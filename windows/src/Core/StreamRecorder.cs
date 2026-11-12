namespace SuperiorIptv;

public enum RecorderState { Idle, Connecting, Recording, Reconnecting, Finished, Stopped, Failed }

/// <summary>
/// Saves a live stream to a file and keeps it going.
///
/// Whenever the stream stops for any reason (server hiccup, network drop, timeout, HTTP error) the recorder
/// reconnects on its own and appends to the same file. It only gives up when the end time is reached,
/// Stop() is called, or the disk can no longer be written.
///
/// MPEG-TS is the capture format on purpose: it is the one container that stays playable when it is cut
/// and joined at arbitrary points, which is exactly what a reconnect does.
/// </summary>
public sealed class StreamRecorder
{
    private sealed class FatalException : Exception
    {
        public FatalException(string message) : base(message) { }
    }

    private readonly string url;
    private readonly string filePath;
    private readonly string userAgent;
    private readonly DateTime endUtc;
    private readonly CancellationTokenSource cancel = new();
    private volatile bool stopRequested;
    private Task task;
    private int attempt;

    private Uri hlsUrl;
    private long hlsLastSequence = -1;
    private bool hlsWroteInit;

    public volatile RecorderState State = RecorderState.Idle;
    public long Bytes;
    public int Reconnects;
    public volatile string Message = "";

    /// <summary>Seconds without data before a connection counts as dropped.</summary>
    public int IdleTimeoutSeconds = 20;
    /// <summary>Longest pause between reconnect attempts.</summary>
    public double MaxRetryDelaySeconds = 15;

    public StreamRecorder(string url, string filePath, string userAgent, DateTime endUtc)
    {
        this.url = url;
        this.filePath = filePath;
        this.userAgent = userAgent;
        this.endUtc = endUtc;
    }

    public Task Completion => task ?? Task.CompletedTask;

    public bool IsRunning => task != null && !task.IsCompleted;

    public void Start()
    {
        if (task != null) return;
        State = RecorderState.Connecting;
        task = Task.Run(RunAsync);
    }

    public void Stop()
    {
        stopRequested = true;
        try { cancel.Cancel(); } catch (ObjectDisposedException) { }
    }

    private bool Expired => DateTime.UtcNow >= endUtc;

    private async Task RunAsync()
    {
        FileStream file = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            // Appending is what makes "resume after the app was closed" work.
            file = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16);
            Interlocked.Exchange(ref Bytes, file.Length);
        }
        catch (Exception ex)
        {
            Message = "The recording file could not be created: " + ex.Message;
            State = RecorderState.Failed;
            return;
        }

        bool hls = LooksLikeHls(url);
        if (hls) hlsUrl = new Uri(url);

        try
        {
            while (!stopRequested && !Expired)
            {
                string reason;
                try
                {
                    if (hls)
                    {
                        bool ended = await RunHlsAsync(file).ConfigureAwait(false);
                        if (ended) break;
                        reason = "The stream ended";
                    }
                    else
                    {
                        bool switchToHls = await RunHttpAsync(file).ConfigureAwait(false);
                        if (switchToHls)
                        {
                            hls = true;
                            continue;
                        }
                        reason = "The stream ended";
                    }
                }
                catch (FatalException ex)
                {
                    Message = ex.Message;
                    State = RecorderState.Failed;
                    return;
                }
                catch (OperationCanceledException) when (stopRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    reason = Shorten(ex.Message);
                }

                if (stopRequested || Expired) break;
                attempt++;
                Interlocked.Increment(ref Reconnects);
                Message = reason;
                State = RecorderState.Reconnecting;
                // 1, 2, 4, 8 seconds, then the maximum. "attempt" returns to zero as soon as data flows again.
                double delay = Math.Min(MaxRetryDelaySeconds, Math.Pow(2, Math.Min(attempt, 6) - 1));
                double untilEnd = (endUtc - DateTime.UtcNow).TotalSeconds;
                if (untilEnd <= 0) break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, untilEnd)), cancel.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            State = stopRequested ? RecorderState.Stopped : RecorderState.Finished;
        }
        finally
        {
            try
            {
                file.Flush(true);
            }
            catch (Exception) { }
            file.Dispose();
        }
    }

    private static bool LooksLikeHls(string address)
    {
        string path = address;
        int question = path.IndexOf('?');
        if (question >= 0) path = path[..question];
        return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static string Shorten(string text) => string.IsNullOrEmpty(text) ? "Connection lost" : (text.Length > 140 ? text[..140] : text);

    private void Write(FileStream file, byte[] buffer, int count)
    {
        try
        {
            file.Write(buffer, 0, count);
        }
        catch (Exception ex)
        {
            throw new FatalException("Could not write to storage: " + ex.Message);
        }
        Interlocked.Add(ref Bytes, count);
        attempt = 0;
        if (State != RecorderState.Recording)
        {
            Message = "";
            State = RecorderState.Recording;
        }
    }

    /// <summary>A token that fires on Stop(), at the end time, or after <paramref name="seconds"/> of waiting.</summary>
    private CancellationTokenSource Deadline(int seconds)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
        double untilEnd = (endUtc - DateTime.UtcNow).TotalSeconds;
        source.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.05, Math.Min(seconds, untilEnd + 0.5))));
        return source;
    }

    /// <summary>Records a plain HTTP stream. Returns true when the address turned out to be an HLS playlist.</summary>
    private async Task<bool> RunHttpAsync(FileStream file)
    {
        using HttpRequestMessage request = Net.Get(url, userAgent);
        HttpResponseMessage response;
        using (CancellationTokenSource connect = Deadline(IdleTimeoutSeconds))
        {
            try
            {
                response = await Net.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stopRequested)
            {
                throw new IOException("The server did not answer");
            }
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw new IOException($"Server answered {(int)response.StatusCode}");
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            Uri finalUrl = response.RequestMessage?.RequestUri ?? new Uri(url);
            if (type.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || LooksLikeHls(finalUrl.AbsolutePath))
            {
                hlsUrl = finalUrl;
                return true;
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(cancel.Token).ConfigureAwait(false);
            byte[] buffer = new byte[1 << 16];
            while (!stopRequested && !Expired)
            {
                int read;
                using (CancellationTokenSource idle = Deadline(IdleTimeoutSeconds))
                {
                    try
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stopRequested)
                    {
                        if (Expired) return false;
                        throw new IOException("No data received");
                    }
                }
                if (read == 0) return false;
                Write(file, buffer, read);
            }
            return false;
        }
    }

    private async Task<byte[]> GetBytesAsync(Uri address, int seconds)
    {
        using CancellationTokenSource deadline = Deadline(seconds);
        try
        {
            using HttpRequestMessage request = Net.Get(address.AbsoluteUri, userAgent);
            using HttpResponseMessage response = await Net.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new IOException($"Server answered {(int)response.StatusCode}");
            return await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stopRequested)
        {
            throw new IOException("The server did not answer");
        }
    }

    /// <summary>Records an HLS stream segment by segment. Returns true when the playlist says the stream is over.</summary>
    private async Task<bool> RunHlsAsync(FileStream file)
    {
        while (!stopRequested && !Expired)
        {
            byte[] raw = await GetBytesAsync(hlsUrl, IdleTimeoutSeconds).ConfigureAwait(false);
            string text = System.Text.Encoding.UTF8.GetString(raw);
            if (!text.Contains("#EXTM3U")) throw new IOException("The playlist could not be read");
            HlsPlaylist playlist = HlsPlaylist.Parse(text, hlsUrl);

            if (playlist.IsMaster)
            {
                if (playlist.Variants.Count == 0) throw new IOException("The playlist lists no streams");
                hlsUrl = playlist.Variants.OrderByDescending(v => v.Bandwidth).First().Url;
                continue;
            }
            if (playlist.Encrypted) throw new FatalException("This stream is encrypted and cannot be recorded.");

            // A sequence number far below ours means the server restarted its numbering.
            long lastAvailable = playlist.MediaSequence + playlist.Segments.Count - 1;
            if (hlsLastSequence > lastAvailable + 2) hlsLastSequence = -1;

            var pending = new List<(long Sequence, Uri Url)>();
            for (int i = 0; i < playlist.Segments.Count; i++)
            {
                long sequence = playlist.MediaSequence + i;
                if (sequence > hlsLastSequence) pending.Add((sequence, playlist.Segments[i]));
            }
            // On the first pass of a live stream start near "now" rather than at the back of the buffer.
            if (hlsLastSequence < 0 && !playlist.EndList && pending.Count > 3) pending = pending.Skip(pending.Count - 3).ToList();

            if (playlist.InitSegment != null && !hlsWroteInit && Interlocked.Read(ref Bytes) == 0)
            {
                byte[] init = await GetBytesAsync(playlist.InitSegment, IdleTimeoutSeconds).ConfigureAwait(false);
                Write(file, init, init.Length);
                hlsWroteInit = true;
            }

            foreach (var (sequence, segmentUrl) in pending)
            {
                if (stopRequested || Expired) return false;
                byte[] data = await GetBytesAsync(segmentUrl, Math.Max(IdleTimeoutSeconds, 30)).ConfigureAwait(false);
                if (data.Length == 0) throw new IOException("A stream segment was empty");
                Write(file, data, data.Length);
                hlsLastSequence = sequence;
            }

            if (playlist.EndList) return true;
            double wait = Math.Max(1.0, playlist.TargetDuration / 2.0);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(wait), cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }
}
