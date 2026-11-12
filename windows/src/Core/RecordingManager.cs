using System.Text.Json.Serialization;

namespace SuperiorIptv;

public enum RecordingStatus { Scheduled, Recording, Converting, Completed, Failed }

public sealed class Recording
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public RecordingStatus Status { get; set; } = RecordingStatus.Scheduled;
    /// <summary>Full path without extension; ".ts" and ".mp4" are added to it.</summary>
    public string BasePath { get; set; } = "";
    public bool HasTs { get; set; }
    public bool HasMp4 { get; set; }
    public long Bytes { get; set; }
    public int Reconnects { get; set; }
    public string Message { get; set; } = "";
    public double ConvertProgress { get; set; }

    [JsonIgnore] public string TsPath => BasePath + ".ts";
    [JsonIgnore] public string Mp4Path => BasePath + ".mp4";

    /// <summary>The best file to play: the MP4 when it exists, otherwise the raw capture.</summary>
    [JsonIgnore]
    public string PlayablePath
    {
        get
        {
            if (HasMp4 && File.Exists(Mp4Path)) return Mp4Path;
            if (File.Exists(TsPath)) return TsPath;
            return null;
        }
    }

    public Recording Clone() => (Recording)MemberwiseClone();

    public static string MakeBaseName(string title, string channel, DateTime startLocal)
    {
        string name = startLocal.ToString("yyyy-MM-dd HH-mm") + " " + Storage.SafeFileName(channel, 40);
        string cleanTitle = Storage.SafeFileName(title, 60);
        if (!string.IsNullOrWhiteSpace(title) && !string.Equals(cleanTitle, Storage.SafeFileName(channel, 40), StringComparison.OrdinalIgnoreCase))
        {
            name += " - " + cleanTitle;
        }
        return name;
    }
}

/// <summary>Turns a .ts capture into an .mp4 file. Implemented by the app with the VLC engine.</summary>
public interface IMp4Converter
{
    Task<bool> ConvertAsync(string tsPath, string mp4Path, Action<double> progress, CancellationToken token);
}

/// <summary>
/// Schedules, runs, resumes and converts recordings.
///
/// How "never stops" is achieved:
///  1. StreamRecorder reconnects by itself whenever the stream drops, appending to the same .ts file.
///  2. Every few seconds Tick() checks that each recording that should be running has a live recorder.
///     If the app was closed or crashed in the middle, the recording picks up again as soon as the app
///     is running, for as long as its end time has not passed.
///  3. When a recording ends, the .ts is converted to .mp4 (automatically, or on demand).
/// </summary>
public sealed class RecordingManager : IDisposable
{
    private readonly object gate = new();
    private readonly Settings settings;
    private readonly IMp4Converter converter;
    private readonly List<Recording> recordings;
    private readonly Dictionary<string, StreamRecorder> recorders = new();
    private readonly System.Threading.Timer timer;
    private string convertingId;
    private CancellationTokenSource convertCancel;
    private DateTime lastSave = DateTime.UtcNow;
    private bool disposed;

    public event Action Changed;

    public RecordingManager(Settings settings, IMp4Converter converter)
    {
        this.settings = settings;
        this.converter = converter;
        recordings = Storage.Load<List<Recording>>(Storage.File("recordings.json")) ?? new List<Recording>();
        foreach (Recording recording in recordings)
        {
            // A conversion that was running when the app closed starts over.
            if (recording.Status == RecordingStatus.Converting) recording.ConvertProgress = 0;
        }
        timer = new System.Threading.Timer(_ => SafeTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    public List<Recording> Snapshot()
    {
        lock (gate) return recordings.Select(r => r.Clone()).ToList();
    }

    public bool AnyActive
    {
        get { lock (gate) return recorders.Count > 0 || convertingId != null; }
    }

    public Recording ActiveFor(string channelId)
    {
        lock (gate) return recordings.FirstOrDefault(r => r.ChannelId == channelId && r.Status == RecordingStatus.Recording)?.Clone();
    }

    /// <summary>True when a booking exists for the programme that starts at <paramref name="startUtc"/> on this channel.</summary>
    public bool IsScheduled(string channelId, DateTime startUtc)
    {
        double padding = settings.PadBeforeMinutes * 60;
        lock (gate)
        {
            return recordings.Any(r => r.ChannelId == channelId && r.Status == RecordingStatus.Scheduled
                && Math.Abs((r.Start.AddSeconds(padding) - startUtc).TotalSeconds) < 120);
        }
    }

    // ---- Creating recordings

    private string NewBasePath(string title, string channel, DateTime startUtc)
    {
        string folder = settings.EffectiveRecordingsFolder;
        string baseName = Recording.MakeBaseName(title, channel, startUtc.ToLocalTime());
        string candidate = Path.Combine(folder, baseName);
        int suffix = 2;
        while (recordings.Any(r => string.Equals(r.BasePath, candidate, StringComparison.OrdinalIgnoreCase))
               || File.Exists(candidate + ".ts") || File.Exists(candidate + ".mp4"))
        {
            candidate = Path.Combine(folder, $"{baseName} ({suffix})");
            suffix++;
        }
        return candidate;
    }

    /// <summary>Starts recording right away, until <paramref name="endUtc"/>.</summary>
    public void RecordNow(Channel channel, string title, DateTime endUtc, string userAgent)
    {
        DateTime start = DateTime.UtcNow;
        lock (gate)
        {
            recordings.Insert(0, new Recording
            {
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                Title = title,
                Url = channel.Url,
                UserAgent = userAgent ?? "",
                Start = start,
                End = endUtc < start.AddMinutes(1) ? start.AddMinutes(1) : endUtc,
                BasePath = NewBasePath(title, channel.Name, start)
            });
            Save();
        }
        SafeTick();
    }

    /// <summary>Books a future programme. Padding from Settings is added on both sides.</summary>
    public void Schedule(Channel channel, Programme programme, string userAgent)
    {
        lock (gate)
        {
            recordings.Insert(0, new Recording
            {
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                Title = programme.Title,
                Url = channel.Url,
                UserAgent = userAgent ?? "",
                Start = programme.Start.AddMinutes(-settings.PadBeforeMinutes),
                End = programme.Stop.AddMinutes(settings.PadAfterMinutes),
                BasePath = NewBasePath(programme.Title, channel.Name, programme.Start)
            });
            Save();
        }
        SafeTick();
    }

    // ---- Controlling recordings

    /// <summary>Stops a running recording (keeping what was captured) or cancels a booking.</summary>
    public void Stop(string id)
    {
        lock (gate)
        {
            Recording recording = recordings.FirstOrDefault(r => r.Id == id);
            if (recording == null) return;
            if (recorders.TryGetValue(id, out StreamRecorder recorder))
            {
                recorder.Stop();    // Tick() finalizes it once the recorder has let go of the file
            }
            else if (recording.Status == RecordingStatus.Recording)
            {
                FinalizeLocked(recording, "");
            }
            else if (recording.Status == RecordingStatus.Scheduled)
            {
                recordings.Remove(recording);
                Save();
            }
        }
        SafeTick();
    }

    public void Delete(string id)
    {
        Recording removed;
        StreamRecorder recorder;
        lock (gate)
        {
            removed = recordings.FirstOrDefault(r => r.Id == id);
            if (removed == null) return;
            recorders.Remove(id, out recorder);
            if (convertingId == id) convertCancel?.Cancel();
            recordings.Remove(removed);
            Save();
        }
        recorder?.Stop();
        string ts = removed.TsPath, mp4 = removed.Mp4Path;
        Task.Run(async () =>
        {
            if (recorder != null)
            {
                try { await recorder.Completion.ConfigureAwait(false); } catch (Exception) { }
            }
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (File.Exists(ts)) File.Delete(ts);
                    if (File.Exists(mp4)) File.Delete(mp4);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(400).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break;
                }
            }
        });
        Changed?.Invoke();
    }

    /// <summary>Queues a finished recording for conversion to MP4.</summary>
    public void Convert(string id)
    {
        lock (gate)
        {
            Recording recording = recordings.FirstOrDefault(r => r.Id == id);
            if (recording == null || recording.Status != RecordingStatus.Completed || !recording.HasTs || recording.HasMp4) return;
            recording.Status = RecordingStatus.Converting;
            recording.ConvertProgress = 0;
            recording.Message = "Waiting to convert";
            Save();
        }
        SafeTick();
    }

    // ---- Housekeeping

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception)
        {
            // Never let a timer callback take the app down.
        }
    }

    public void Tick()
    {
        bool changed = false;
        lock (gate)
        {
            if (disposed) return;
            DateTime now = DateTime.UtcNow;
            foreach (Recording recording in recordings.ToList())
            {
                recorders.TryGetValue(recording.Id, out StreamRecorder recorder);
                switch (recording.Status)
                {
                    case RecordingStatus.Scheduled:
                        if (now >= recording.End)
                        {
                            recording.Status = RecordingStatus.Failed;
                            recording.Message = "Missed: the app was not running at the scheduled time.";
                            changed = true;
                        }
                        else if (now >= recording.Start)
                        {
                            StartRecorderLocked(recording);
                            changed = true;
                        }
                        break;

                    case RecordingStatus.Recording:
                        if (recorder == null)
                        {
                            // Resume after the app was closed, or finish if the end time has passed meanwhile.
                            if (now < recording.End) StartRecorderLocked(recording);
                            else FinalizeLocked(recording, "");
                            changed = true;
                        }
                        else
                        {
                            long bytes = Interlocked.Read(ref recorder.Bytes);
                            if (bytes != recording.Bytes || recorder.Reconnects != recording.Reconnects) changed = true;
                            recording.Bytes = bytes;
                            recording.Reconnects = recorder.Reconnects;
                            RecorderState state = recorder.State;
                            string message = state switch
                            {
                                RecorderState.Connecting => "Connecting…",
                                RecorderState.Reconnecting => "Reconnecting: " + recorder.Message,
                                _ => ""
                            };
                            if (message != recording.Message)
                            {
                                recording.Message = message;
                                changed = true;
                            }
                            if (!recorder.IsRunning)
                            {
                                recorders.Remove(recording.Id);
                                FinalizeLocked(recording, state == RecorderState.Failed ? recorder.Message : "");
                                changed = true;
                            }
                        }
                        break;
                }
            }

            // Stop before the disk is completely full so the files stay usable.
            if (recorders.Count > 0)
            {
                long free = Storage.FreeSpace(settings.EffectiveRecordingsFolder);
                if (free >= 0 && free < 200_000_000)
                {
                    foreach (StreamRecorder recorder in recorders.Values) recorder.Stop();
                }
            }

            if (changed || (DateTime.UtcNow - lastSave).TotalSeconds > 30) Save();
            StartNextConversionLocked();
        }
        if (changed) Changed?.Invoke();
    }

    private void StartRecorderLocked(Recording recording)
    {
        if (recorders.ContainsKey(recording.Id)) return;
        if (string.IsNullOrWhiteSpace(recording.Url))
        {
            recording.Status = RecordingStatus.Failed;
            recording.Message = "The stream address is not valid.";
            return;
        }
        var recorder = new StreamRecorder(recording.Url, recording.TsPath, recording.UserAgent, recording.End);
        recorders[recording.Id] = recorder;
        recording.Status = RecordingStatus.Recording;
        recording.HasTs = true;
        if (string.IsNullOrEmpty(recording.Message)) recording.Message = "Connecting…";
        recorder.Start();
    }

    private void FinalizeLocked(Recording recording, string message)
    {
        long size = 0;
        try
        {
            if (File.Exists(recording.TsPath)) size = new FileInfo(recording.TsPath).Length;
        }
        catch (Exception) { }
        recording.Bytes = size;
        if (size > 200_000)
        {
            recording.Status = RecordingStatus.Completed;
            recording.HasTs = true;
            string note = message;
            if (string.IsNullOrEmpty(note) && recording.Reconnects > 0) note = $"Resumed automatically {recording.Reconnects} time(s).";
            recording.Message = note ?? "";
            if (settings.AutoConvertMp4 && converter != null)
            {
                recording.Status = RecordingStatus.Converting;
                recording.ConvertProgress = 0;
            }
        }
        else
        {
            recording.Status = RecordingStatus.Failed;
            recording.HasTs = false;
            recording.Message = string.IsNullOrEmpty(message) ? "No video was received from the channel." : message;
            try { File.Delete(recording.TsPath); } catch (Exception) { }
        }
        Save();
    }

    private void Save()
    {
        lastSave = DateTime.UtcNow;
        Storage.Save(Storage.File("recordings.json"), recordings);
    }

    // ---- MP4 conversion (one at a time)

    private void StartNextConversionLocked()
    {
        if (convertingId != null || converter == null) return;
        Recording next = recordings.LastOrDefault(r => r.Status == RecordingStatus.Converting);
        if (next == null) return;
        if (!File.Exists(next.TsPath))
        {
            next.Status = RecordingStatus.Completed;
            next.Message = "The original capture is missing.";
            return;
        }
        string id = next.Id, ts = next.TsPath, mp4 = next.Mp4Path;
        convertingId = id;
        convertCancel = new CancellationTokenSource();
        next.Message = "Converting to MP4…";
        CancellationToken token = convertCancel.Token;

        Task.Run(async () =>
        {
            bool succeeded = false;
            try
            {
                succeeded = await converter.ConvertAsync(ts, mp4, fraction =>
                {
                    lock (gate)
                    {
                        Recording current = recordings.FirstOrDefault(r => r.Id == id);
                        if (current != null) current.ConvertProgress = fraction;
                    }
                }, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                succeeded = false;
            }

            lock (gate)
            {
                convertingId = null;
                Recording current = recordings.FirstOrDefault(r => r.Id == id);
                if (current != null)
                {
                    current.Status = RecordingStatus.Completed;
                    current.ConvertProgress = 0;
                    if (succeeded)
                    {
                        current.HasMp4 = true;
                        try { current.Bytes = new FileInfo(mp4).Length; } catch (Exception) { }
                        if (current.Message.StartsWith("Converting", StringComparison.Ordinal)) current.Message = "";
                        if (!settings.KeepTs)
                        {
                            try
                            {
                                File.Delete(ts);
                                current.HasTs = false;
                            }
                            catch (Exception) { }
                        }
                    }
                    else
                    {
                        try { File.Delete(mp4); } catch (Exception) { }
                        current.Message = "MP4 conversion did not complete. The .ts recording was kept and still plays.";
                    }
                }
                else
                {
                    try { File.Delete(mp4); } catch (Exception) { }
                }
                Save();
            }
            Changed?.Invoke();
            SafeTick();
        });
    }

    public void Dispose()
    {
        List<StreamRecorder> active;
        lock (gate)
        {
            disposed = true;
            active = recorders.Values.ToList();
            recorders.Clear();
            convertCancel?.Cancel();
            Save();
        }
        timer.Dispose();
        foreach (StreamRecorder recorder in active) recorder.Stop();
        // Recordings stay marked "Recording", so they resume on the next launch if their end time has not passed.
        try
        {
            Task.WaitAll(active.Select(r => r.Completion).ToArray(), TimeSpan.FromSeconds(3));
        }
        catch (Exception) { }
    }
}
