using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace SuperiorIptv;

public enum DownloadState { Queued, Downloading, Paused, Completed, Failed }

public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Id of the movie or episode this file belongs to.</summary>
    public string SourceId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Url { get; set; } = "";
    public string UserAgent { get; set; } = "";
    /// <summary>Where the finished file goes. While downloading, data is kept next to it as ".part".</summary>
    public string FilePath { get; set; } = "";
    public DownloadState State { get; set; } = DownloadState.Queued;
    public long TotalBytes { get; set; } = -1;
    public long ReceivedBytes { get; set; }
    public string Message { get; set; } = "";
    public int Resumes { get; set; }
    public DateTime Added { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public double BytesPerSecond;
    [JsonIgnore] public string PartPath => FilePath + ".part";
    [JsonIgnore] public double Fraction => TotalBytes > 0 ? Math.Clamp((double)ReceivedBytes / TotalBytes, 0, 1) : 0;

    public DownloadItem Clone() => (DownloadItem)MemberwiseClone();
}

/// <summary>
/// Downloads movie and episode files.
///
/// Several files can be queued at once. A download that is interrupted (network drop, server error, the
/// app being closed) resumes by itself from the byte where it stopped, and keeps retrying until it
/// finishes or the user pauses or removes it.
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private sealed class FatalException : Exception
    {
        public FatalException(string message) : base(message) { }
    }

    private readonly object gate = new();
    private readonly List<DownloadItem> items;
    private readonly Dictionary<string, CancellationTokenSource> running = new();
    private readonly string storePath;
    private readonly Func<string> folder;
    private readonly Func<int> maxConcurrent;
    private DateTime lastSave = DateTime.MinValue;
    private bool disposed;

    /// <summary>Seconds without data before a connection counts as dropped.</summary>
    public int IdleTimeoutSeconds = 30;
    /// <summary>Longest pause between automatic resume attempts.</summary>
    public double MaxRetryDelaySeconds = 30;

    /// <summary>Raised (on a background thread) when the list or a state changes.</summary>
    public event Action Changed;

    public DownloadManager(string storePath, Func<string> folder, Func<int> maxConcurrent)
    {
        this.storePath = storePath;
        this.folder = folder;
        this.maxConcurrent = maxConcurrent;
        items = Storage.Load<List<DownloadItem>>(storePath) ?? new List<DownloadItem>();
        foreach (DownloadItem item in items)
        {
            // Anything that was running when the app closed continues automatically.
            if (item.State == DownloadState.Downloading) item.State = DownloadState.Queued;
            if (item.State == DownloadState.Completed && !File.Exists(item.FilePath))
            {
                item.State = DownloadState.Failed;
                item.Message = "The file was moved or deleted.";
            }
        }
    }

    /// <summary>Starts queued downloads. Call once after creating the manager.</summary>
    public void Start() => Pump();

    public List<DownloadItem> Snapshot()
    {
        lock (gate) return items.Select(i => i.Clone()).ToList();
    }

    public DownloadItem FindBySource(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return null;
        lock (gate) return items.FirstOrDefault(i => i.SourceId == sourceId && i.State != DownloadState.Failed)?.Clone();
    }

    public int ActiveCount
    {
        get { lock (gate) return items.Count(i => i.State == DownloadState.Downloading || i.State == DownloadState.Queued); }
    }

    /// <summary>Queues a file. Returns the existing entry if the same item is already queued or done.</summary>
    public DownloadItem Add(string sourceId, string title, string subtitle, string url, string userAgent)
    {
        DownloadItem created;
        lock (gate)
        {
            DownloadItem existing = items.FirstOrDefault(i => i.SourceId == sourceId && i.State != DownloadState.Failed);
            if (existing != null) return existing.Clone();

            string extension = ExtensionOf(url);
            string baseName = Storage.SafeFileName(string.IsNullOrWhiteSpace(subtitle) ? title : title + " - " + subtitle);
            string path = Storage.UniquePath(folder(), baseName, extension, candidate => items.Any(i => string.Equals(i.FilePath, candidate, StringComparison.OrdinalIgnoreCase)));
            created = new DownloadItem
            {
                SourceId = sourceId,
                Title = title,
                Subtitle = subtitle ?? "",
                Url = url,
                UserAgent = userAgent ?? "",
                FilePath = path
            };
            items.Insert(0, created);
            SaveLocked(true);
        }
        Pump();
        Changed?.Invoke();
        return created.Clone();
    }

    public static string ExtensionOf(string url)
    {
        string path = url;
        int question = path.IndexOf('?');
        if (question >= 0) path = path[..question];
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension.Length < 2 || extension.Length > 5) return ".mp4";
        foreach (char c in extension.AsSpan(1))
        {
            if (!char.IsLetterOrDigit(c)) return ".mp4";
        }
        return extension;
    }

    public void Pause(string id)
    {
        CancellationTokenSource source = null;
        lock (gate)
        {
            DownloadItem item = items.FirstOrDefault(i => i.Id == id);
            if (item == null || item.State == DownloadState.Completed) return;
            item.State = DownloadState.Paused;
            item.Message = "";
            item.BytesPerSecond = 0;
            running.Remove(id, out source);
            SaveLocked(true);
        }
        source?.Cancel();
        Pump();
        Changed?.Invoke();
    }

    public void Resume(string id)
    {
        lock (gate)
        {
            DownloadItem item = items.FirstOrDefault(i => i.Id == id);
            if (item == null || item.State == DownloadState.Completed || item.State == DownloadState.Downloading) return;
            item.State = DownloadState.Queued;
            item.Message = "";
            SaveLocked(true);
        }
        Pump();
        Changed?.Invoke();
    }

    public void PauseAll()
    {
        foreach (DownloadItem item in Snapshot())
        {
            if (item.State == DownloadState.Downloading || item.State == DownloadState.Queued) Pause(item.Id);
        }
    }

    public void ResumeAll()
    {
        foreach (DownloadItem item in Snapshot())
        {
            if (item.State == DownloadState.Paused || item.State == DownloadState.Failed) Resume(item.Id);
        }
    }

    /// <summary>Stops a download and takes it off the list. The partial data is always deleted; the finished file only on request.</summary>
    public void Remove(string id, bool deleteFile)
    {
        CancellationTokenSource source = null;
        DownloadItem item;
        lock (gate)
        {
            item = items.FirstOrDefault(i => i.Id == id);
            if (item == null) return;
            items.Remove(item);
            running.Remove(id, out source);
            SaveLocked(true);
        }
        source?.Cancel();
        string partPath = item.PartPath, filePath = item.FilePath;
        bool removeFile = deleteFile;
        Task.Run(async () =>
        {
            // Give the worker a moment to let go of the file.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (File.Exists(partPath)) File.Delete(partPath);
                    if (removeFile && File.Exists(filePath)) File.Delete(filePath);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(300).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break;
                }
            }
        });
        Pump();
        Changed?.Invoke();
    }

    private void SaveLocked(bool force)
    {
        if (!force && (DateTime.UtcNow - lastSave).TotalSeconds < 5) return;
        lastSave = DateTime.UtcNow;
        Storage.Save(storePath, items);
    }

    /// <summary>Starts queued downloads while there are free slots.</summary>
    private void Pump()
    {
        lock (gate)
        {
            if (disposed) return;
            int limit = Math.Clamp(maxConcurrent(), 1, 8);
            foreach (DownloadItem item in items.AsEnumerable().Reverse())   // oldest first
            {
                if (running.Count >= limit) break;
                if (item.State != DownloadState.Queued || running.ContainsKey(item.Id)) continue;
                var source = new CancellationTokenSource();
                running[item.Id] = source;
                item.State = DownloadState.Downloading;
                item.Message = "Connecting…";
                DownloadItem target = item;
                Task.Run(() => RunAsync(target, source));
            }
        }
    }

    private async Task RunAsync(DownloadItem item, CancellationTokenSource source)
    {
        CancellationToken token = source.Token;
        int attempt = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    bool complete = await TransferAsync(item, token, () => attempt = 0).ConfigureAwait(false);
                    if (complete) break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (FatalException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempt++;
                    lock (gate)
                    {
                        item.Resumes++;
                        item.BytesPerSecond = 0;
                        // Our own messages are short and plain; anything longer is technical detail from the network layer.
                        string reason = ex.Message.Length > 0 && ex.Message.Length <= 45 ? ex.Message : "the connection dropped";
                        item.Message = $"Resuming automatically ({char.ToLowerInvariant(reason[0])}{reason[1..]})";
                    }
                    Changed?.Invoke();
                    // 1, 2, 4, 8, 16 seconds, then the maximum; the counter resets as soon as data flows again.
                    double delay = Math.Min(MaxRetryDelaySeconds, Math.Pow(2, Math.Min(attempt, 6) - 1));
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                }
            }

            File.Move(item.PartPath, item.FilePath, true);
            lock (gate)
            {
                item.State = DownloadState.Completed;
                item.Message = "";
                item.BytesPerSecond = 0;
                if (item.TotalBytes <= 0) item.TotalBytes = item.ReceivedBytes;
                running.Remove(item.Id);
                SaveLocked(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Paused or removed: the state was already set by whoever cancelled.
            lock (gate)
            {
                if (running.TryGetValue(item.Id, out var current) && current == source) running.Remove(item.Id);
                SaveLocked(true);
            }
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                item.State = DownloadState.Failed;
                item.Message = ex.Message;
                item.BytesPerSecond = 0;
                if (running.TryGetValue(item.Id, out var current) && current == source) running.Remove(item.Id);
                SaveLocked(true);
            }
        }
        finally
        {
            source.Dispose();
        }
        Changed?.Invoke();
        Pump();
    }

    /// <summary>One connection attempt. Returns true when the whole file has arrived.</summary>
    private async Task<bool> TransferAsync(DownloadItem item, CancellationToken token, Action dataFlowing)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.FilePath));
        long existing = File.Exists(item.PartPath) ? new FileInfo(item.PartPath).Length : 0;

        using HttpRequestMessage request = Net.Get(item.Url, item.UserAgent);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        HttpResponseMessage response;
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
            try
            {
                response = await Net.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("The server did not answer");
            }
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Either the file is already complete, or the partial data no longer matches the server's file.
                long known = response.Content.Headers.ContentRange?.Length ?? item.TotalBytes;
                if (known > 0 && existing == known) return true;
                File.Delete(item.PartPath);
                lock (gate) item.ReceivedBytes = 0;
                throw new IOException("Restarting from the beginning");
            }
            if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
            {
                throw new FatalException("The file is no longer available on the server.");
            }
            if (!response.IsSuccessStatusCode) throw new IOException($"Server answered {(int)response.StatusCode}");

            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The server sent a web page instead of the video");
            }

            // 206 means the server continues where we stopped; 200 means it started over.
            bool resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) existing = 0;
            long? length = response.Content.Headers.ContentLength;
            long total = resumed
                ? (response.Content.Headers.ContentRange?.Length ?? (length.HasValue ? existing + length.Value : -1))
                : (length ?? -1);

            lock (gate)
            {
                item.TotalBytes = total;
                item.ReceivedBytes = existing;
                item.State = DownloadState.Downloading;
                item.Message = "";
            }
            Changed?.Invoke();

            FileStream file;
            try
            {
                file = new FileStream(item.PartPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 32 || (ex.HResult & 0xFFFF) == 33)
            {
                // Sharing violation: a previous attempt has not let go of the file yet. Try again shortly.
                throw new IOException("The file is still in use");
            }
            catch (Exception ex)
            {
                throw new FatalException("Could not write to the downloads folder: " + ex.Message);
            }

            using (file)
            using (Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                byte[] buffer = new byte[1 << 16];
                long windowBytes = 0;
                DateTime windowStart = DateTime.UtcNow;
                while (true)
                {
                    int read;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
                        try
                        {
                            read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            throw new IOException("No data received");
                        }
                    }
                    if (read == 0) break;

                    try
                    {
                        file.Write(buffer, 0, read);
                    }
                    catch (Exception ex)
                    {
                        throw new FatalException("Could not write to the downloads folder: " + ex.Message);
                    }

                    dataFlowing();
                    windowBytes += read;
                    double elapsed = (DateTime.UtcNow - windowStart).TotalSeconds;
                    lock (gate)
                    {
                        item.ReceivedBytes += read;
                        if (elapsed >= 1)
                        {
                            item.BytesPerSecond = windowBytes / elapsed;
                            windowBytes = 0;
                            windowStart = DateTime.UtcNow;
                            SaveLocked(false);
                        }
                    }
                }
                file.Flush(true);
            }

            long received;
            lock (gate) received = item.ReceivedBytes;
            if (total > 0 && received < total) throw new IOException("The connection closed early");
            return true;
        }
    }

    public void Dispose()
    {
        List<CancellationTokenSource> sources;
        lock (gate)
        {
            disposed = true;
            sources = running.Values.ToList();
            running.Clear();
            // Whatever was running continues the next time the app starts.
            foreach (DownloadItem item in items)
            {
                if (item.State == DownloadState.Downloading) item.State = DownloadState.Queued;
            }
            SaveLocked(true);
        }
        foreach (CancellationTokenSource source in sources)
        {
            try { source.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
