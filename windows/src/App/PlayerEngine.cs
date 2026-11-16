using System.Collections.Concurrent;
using Vlc = LibVLCSharp.Shared;

namespace SuperiorIptv.Win;

public enum PlayerStatus { Idle, Loading, Playing, Paused, Ended, Failed }

public sealed class TrackOption
{
    public int Id;
    public string Name = "";
}

/// <summary>
/// One video player built on the VLC engine. The main window owns one; multi-view creates one per tile.
///
/// VLC is used because IPTV streams are mostly raw MPEG-TS over HTTP in many codecs, which Windows' own
/// player does not handle well. Starting and stopping streams can block for a moment on a slow server, so
/// those commands run on a private worker thread and never freeze the window.
/// </summary>
public sealed class PlayerEngine : IDisposable
{
    private static readonly Dictionary<string, Vlc.LibVLC> Libraries = new();
    private static readonly object LibraryGate = new();

    /// <summary>
    /// When not empty, these VLC options are used in place of the subtitle size setting. Only the
    /// self-test sets this, to measure what each option does to subtitles and captions.
    /// </summary>
    public static string[] TestOptions = Array.Empty<string>();

    /// <summary>
    /// The shared VLC instance for the current settings (created on first use). The subtitle text size can
    /// only be given to VLC when an instance is created, so there is one instance per size that was used.
    /// </summary>
    public static Vlc.LibVLC Library(Settings settings)
    {
        int scale = Math.Clamp(settings?.SubtitleScale ?? 100, 10, 500);
        string[] sizeOptions = TestOptions.Length > 0 ? TestOptions : new[] { "--sub-text-scale=" + scale };
        string key = string.Join(" ", sizeOptions);
        lock (LibraryGate)
        {
            if (!Libraries.TryGetValue(key, out Vlc.LibVLC instance))
            {
                Vlc.Core.Initialize();
                var options = new List<string> { "--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--quiet" };
                options.AddRange(sizeOptions);
                if (settings != null && !settings.HardwareDecoding) options.Add("--avcodec-hw=none");
                instance = new Vlc.LibVLC(options.ToArray());
                Libraries[key] = instance;
            }
            return instance;
        }
    }

    public static readonly float[] Speeds = { 0.5f, 1f, 1.25f, 1.5f, 2f, 3f, 4f, 6f, 8f, 10f, 16f };

    private readonly Settings settings;
    private readonly ResumeStore resume;
    // Replaced (on the window's thread) when a setting that VLC only reads at start-up has changed.
    private volatile Vlc.MediaPlayer player;
    private Vlc.LibVLC library;
    private readonly BlockingCollection<Action> commands = new();
    private readonly Thread worker;
    private Control surface;
    private IntPtr surfaceHandle;

    private bool userStopped = true;
    private long lastTime = -1;
    private DateTime lastProgressAt = DateTime.UtcNow;
    private DateTime startedAt = DateTime.UtcNow;
    private DateTime? retryAt;
    private long pendingSeek;
    private int aspectIndex;
    private int ticks;
    private bool muted;
    private int volume = 100;

    public PlayableItem Item { get; private set; }
    public PlayerStatus Status { get; private set; } = PlayerStatus.Idle;
    public long TimeMs { get; private set; }
    public long DurationMs { get; private set; }
    public int Reconnects { get; private set; }
    public float Rate { get; private set; } = 1f;
    public string AspectLabel { get; private set; } = "Original";
    public List<TrackOption> AudioTracks { get; private set; } = new();
    public List<TrackOption> SubtitleTracks { get; private set; } = new();
    public int CurrentAudio { get; private set; } = -1;
    public int CurrentSubtitle { get; private set; } = -1;

    /// <summary>Turn on to have <see cref="StatsText"/> kept up to date (costs a little work twice a second).</summary>
    public bool CollectStats;
    /// <summary>One line about the stream being played, for example "1920×1080 · 29.97 fps · 4.8 Mbps · H264".</summary>
    public string StatsText { get; private set; } = "";
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    /// <summary>Current bitrate of the stream in kilobits per second (smoothed), 0 when not known yet.</summary>
    public double BitrateKbps { get; private set; }

    public PlayerEngine(Settings settings, ResumeStore resume)
    {
        this.settings = settings;
        this.resume = resume;
        library = Library(settings);
        player = NewPlayer(library);
        volume = settings?.Volume ?? 100;
        worker = new Thread(Work) { IsBackground = true, Name = "player" };
        worker.Start();
    }

    private static Vlc.MediaPlayer NewPlayer(Vlc.LibVLC instance) => new(instance)
    {
        // Let the window receive mouse and keyboard input instead of VLC's own video window.
        EnableMouseInput = false,
        EnableKeyInput = false
    };

    /// <summary>Moves to the VLC instance that matches the current settings, if that is a different one.</summary>
    private void UseCurrentLibrary()
    {
        Vlc.LibVLC wanted = Library(settings);
        if (ReferenceEquals(wanted, library)) return;
        Vlc.MediaPlayer old = player;
        library = wanted;
        player = NewPlayer(wanted);
        IntPtr handle = surfaceHandle;
        // The old player is let go on the worker thread, after any command that may still be using it.
        Enqueue(() =>
        {
            try
            {
                old.Stop();
            }
            catch (Exception)
            {
            }
            old.Dispose();
            if (handle != IntPtr.Zero) player.Hwnd = handle;
        });
    }

    private void Enqueue(Action command)
    {
        try
        {
            commands.Add(command);
        }
        catch (InvalidOperationException)
        {
            // The player was already shut down.
        }
    }

    private void Work()
    {
        foreach (Action command in commands.GetConsumingEnumerable())
        {
            try
            {
                command();
            }
            catch (Exception)
            {
                // A failed command shows up as a player that is not playing; the poll loop handles that.
            }
        }
    }

    /// <summary>Tells VLC which control to draw the picture into.</summary>
    public void AttachTo(Control control)
    {
        surface = control;
        IntPtr handle = control.Handle;
        surfaceHandle = handle;
        Enqueue(() => player.Hwnd = handle);
    }

    public double Progress => DurationMs > 0 ? Math.Clamp((double)TimeMs / DurationMs, 0, 1) : 0;

    public bool CanSeek => DurationMs > 0 && Item != null && !Item.IsLive;

    /// <summary>Speed control is offered for everything except live channels.</summary>
    public bool CanChangeSpeed => Item != null && !Item.IsLive;

    public bool Muted
    {
        get => muted;
        set
        {
            muted = value;
            ApplyVolume();
        }
    }

    public int Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 100);
            ApplyVolume();
        }
    }

    private void ApplyVolume()
    {
        bool mute = muted;
        int level = volume;
        Enqueue(() =>
        {
            player.Mute = mute;
            player.Volume = level;
        });
    }

    // ---- Transport

    public void Play(PlayableItem item)
    {
        SaveResumePoint();
        Item = item;
        userStopped = false;
        Reconnects = 0;
        retryAt = null;
        Rate = 1f;
        pendingSeek = resume?.Get(item.ResumeKey) ?? 0;
        StartCurrent();
    }

    private void StartCurrent()
    {
        PlayableItem item = Item;
        if (item == null) return;
        UseCurrentLibrary();
        Vlc.LibVLC instance = library;
        Status = PlayerStatus.Loading;
        TimeMs = 0;
        DurationMs = 0;
        lastTime = -1;
        lastProgressAt = DateTime.UtcNow;
        startedAt = DateTime.UtcNow;
        AudioTracks = new List<TrackOption>();
        SubtitleTracks = new List<TrackOption>();
        StatsText = "";
        VideoWidth = VideoHeight = 0;
        BitrateKbps = 0;
        int buffer = Math.Max(300, settings?.BufferMs ?? 1500);
        bool mute = muted;
        int level = volume;
        string aspect = AspectValue();

        Enqueue(() =>
        {
            Vlc.Media media = item.IsLocalFile
                ? new Vlc.Media(instance, item.Url, Vlc.FromType.FromPath)
                : new Vlc.Media(instance, item.Url, Vlc.FromType.FromLocation);
            using (media)
            {
                if (item.IsLocalFile)
                {
                    media.AddOption(":file-caching=1000");
                }
                else
                {
                    media.AddOption(":network-caching=" + buffer);
                    media.AddOption(":http-reconnect");
                    if (!string.IsNullOrEmpty(item.UserAgent)) media.AddOption(":http-user-agent=" + item.UserAgent);
                }
                player.Play(media);
            }
            player.Mute = mute;
            player.Volume = level;
            player.AspectRatio = aspect;
            player.SetRate(1f);
        });
    }

    /// <summary>
    /// Starts what is playing again, at the place it had reached, so that a changed setting (such as the
    /// subtitle size) takes effect straight away.
    /// </summary>
    public void Reload()
    {
        PlayableItem item = Item;
        if (item == null) return;
        if (!item.IsLive && TimeMs > 0) pendingSeek = TimeMs;
        Rate = 1f;
        StartCurrent();
    }

    public void Stop()
    {
        SaveResumePoint();
        userStopped = true;
        retryAt = null;
        bool hadItem = Item != null;
        Item = null;
        Status = PlayerStatus.Idle;
        TimeMs = 0;
        DurationMs = 0;
        Rate = 1f;
        if (hadItem) Enqueue(() => player.Stop());
    }

    public void TogglePause()
    {
        if (Item == null) return;
        if (Status == PlayerStatus.Paused)
        {
            Enqueue(() => player.SetPause(false));
            lastProgressAt = DateTime.UtcNow;
        }
        else if (Status == PlayerStatus.Playing)
        {
            Enqueue(() => player.SetPause(true));
        }
        else if (Status == PlayerStatus.Ended || Status == PlayerStatus.Failed)
        {
            StartCurrent();
        }
    }

    public void SeekBy(int seconds)
    {
        if (!CanSeek) return;
        long target = Math.Clamp(TimeMs + seconds * 1000L, 0, Math.Max(0, DurationMs - 2000));
        TimeMs = target;
        lastProgressAt = DateTime.UtcNow;
        Enqueue(() => player.Time = target);
    }

    public void SeekTo(double fraction)
    {
        if (!CanSeek) return;
        float position = (float)Math.Clamp(fraction, 0, 0.999);
        TimeMs = (long)(position * DurationMs);
        lastProgressAt = DateTime.UtcNow;
        Enqueue(() => player.Position = position);
    }

    /// <summary>Playback speed (1 = normal). Not available for live channels.</summary>
    public void SetRate(float rate)
    {
        if (!CanChangeSpeed) return;
        Rate = rate;
        lastProgressAt = DateTime.UtcNow;
        Enqueue(() => player.SetRate(rate));
    }

    /// <summary>Moves to the next faster (or slower) speed in the list.</summary>
    public void StepRate(int direction)
    {
        if (!CanChangeSpeed) return;
        int index = Array.FindIndex(Speeds, s => Math.Abs(s - Rate) < 0.01f);
        if (index < 0) index = Array.IndexOf(Speeds, 1f);
        index = Math.Clamp(index + direction, 0, Speeds.Length - 1);
        SetRate(Speeds[index]);
    }

    // ---- Tracks and picture shape

    public void SelectAudio(int id)
    {
        CurrentAudio = id;
        Enqueue(() => player.SetAudioTrack(id));
    }

    public void SelectSubtitle(int id)
    {
        CurrentSubtitle = id;
        Enqueue(() => player.SetSpu(id));
    }

    public void CycleAspect()
    {
        aspectIndex = (aspectIndex + 1) % 4;
        string aspect = AspectValue();
        Enqueue(() => player.AspectRatio = aspect);
    }

    private string AspectValue()
    {
        switch (aspectIndex)
        {
            case 1:
                AspectLabel = "16:9";
                return "16:9";
            case 2:
                AspectLabel = "4:3";
                return "4:3";
            case 3:
                AspectLabel = "Stretch";
                if (surface != null && surface.Width > 1 && surface.Height > 1) return surface.Width + ":" + surface.Height;
                return null;
            default:
                AspectLabel = "Original";
                return null;
        }
    }

    private void ReadTracks()
    {
        try
        {
            // VLC adds a "Disable" entry with id -1 to both lists. It is not a real audio track, and for
            // subtitles it is only useful when there is at least one subtitle to switch off.
            AudioTracks = player.AudioTrackDescription.Where(t => t.Id != -1)
                .Select(t => new TrackOption { Id = t.Id, Name = t.Name ?? "Track" }).ToList();
            List<TrackOption> subtitles = player.SpuDescription.Select(t => new TrackOption { Id = t.Id, Name = t.Name ?? "Track" }).ToList();
            SubtitleTracks = subtitles.Any(t => t.Id != -1) ? subtitles : new List<TrackOption>();
            CurrentAudio = player.AudioTrack;
            CurrentSubtitle = player.Spu;
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Reads the picture size, frame rate, codec and bitrate of what is playing.</summary>
    private void ReadStats()
    {
        try
        {
            uint width = 0, height = 0;
            player.Size(0, ref width, ref height);
            double fps = 0;
            string codec = "";
            double kbps = 0;
            using (Vlc.Media media = player.Media)
            {
                if (media != null)
                {
                    foreach (Vlc.MediaTrack track in media.Tracks)
                    {
                        if (track.TrackType != Vlc.TrackType.Video) continue;
                        if (width == 0 || height == 0)
                        {
                            width = track.Data.Video.Width;
                            height = track.Data.Video.Height;
                        }
                        if (track.Data.Video.FrameRateDen > 0) fps = (double)track.Data.Video.FrameRateNum / track.Data.Video.FrameRateDen;
                        codec = FourCc(track.Codec);
                        break;
                    }
                    // VLC reports bitrates in bytes per microsecond; 8000 turns that into kilobits per second.
                    Vlc.MediaStats stats = media.Statistics;
                    double measured = stats.DemuxBitrate > 0 ? stats.DemuxBitrate : stats.InputBitrate;
                    kbps = measured * 8000;
                }
            }
            if (kbps > 0) BitrateKbps = BitrateKbps <= 0 ? kbps : BitrateKbps * 0.7 + kbps * 0.3;
            VideoWidth = (int)width;
            VideoHeight = (int)height;

            var parts = new List<string>();
            if (width > 0 && height > 0) parts.Add($"{width}×{height}");
            if (fps > 1 && fps < 400) parts.Add(fps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " fps");
            if (BitrateKbps > 0)
            {
                parts.Add(BitrateKbps >= 1000
                    ? (BitrateKbps / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Mbps"
                    : BitrateKbps.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " kbps");
            }
            if (codec.Length > 0) parts.Add(codec);
            StatsText = string.Join(" · ", parts);
        }
        catch (Exception)
        {
            // Statistics are optional; never let them disturb playback.
        }
    }

    /// <summary>VLC names codecs with four packed characters, for example "h264".</summary>
    private static string FourCc(uint code)
    {
        var text = new System.Text.StringBuilder(4);
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((code >> (8 * i)) & 0xFF);
            if (c > 32 && c < 127) text.Append(c);
        }
        return text.ToString().ToUpperInvariant();
    }

    // ---- Polling (called about twice a second from the window's timer)

    public void Poll()
    {
        PlayableItem item = Item;
        if (item == null || userStopped) return;
        ticks++;

        long time, length;
        Vlc.VLCState state;
        bool playing;
        try
        {
            time = Math.Max(0, player.Time);
            length = Math.Max(0, player.Length);
            state = player.State;
            playing = player.IsPlaying;
        }
        catch (Exception)
        {
            return;
        }

        if (time != lastTime)
        {
            lastTime = time;
            lastProgressAt = DateTime.UtcNow;
        }
        TimeMs = time;
        DurationMs = length;

        PlayerStatus status;
        double sinceStart = (DateTime.UtcNow - startedAt).TotalSeconds;
        if (state == Vlc.VLCState.Error) status = PlayerStatus.Failed;
        else if (state == Vlc.VLCState.Ended) status = PlayerStatus.Ended;
        else if (state == Vlc.VLCState.Paused) status = PlayerStatus.Paused;
        else if (state == Vlc.VLCState.Stopped) status = sinceStart > 3 ? PlayerStatus.Ended : PlayerStatus.Loading;
        else if (playing && time > 0) status = PlayerStatus.Playing;
        else status = PlayerStatus.Loading;

        // Resume a movie, recording or download where the viewer left off.
        if (status == PlayerStatus.Playing && pendingSeek > 0 && length > 0)
        {
            long target = pendingSeek;
            pendingSeek = 0;
            if (target < length - 30_000) Enqueue(() => player.Time = target);
        }

        if (status == PlayerStatus.Playing && ticks % 4 == 0) ReadTracks();
        if (CollectStats && (status == PlayerStatus.Playing || status == PlayerStatus.Paused) && ticks % 2 == 0) ReadStats();
        if (status == PlayerStatus.Playing && ticks % 20 == 0) SaveResumePoint();
        if (status == PlayerStatus.Ended && !item.IsLive && !string.IsNullOrEmpty(item.ResumeKey)) resume?.Set(item.ResumeKey, 0);

        // Live channels drop all the time; put them back on air without the viewer doing anything.
        if (item.IsLive && (settings?.AutoReconnect ?? true) && status != PlayerStatus.Paused)
        {
            bool stalled = (DateTime.UtcNow - lastProgressAt).TotalSeconds > 20;
            if (status == PlayerStatus.Failed || status == PlayerStatus.Ended || stalled)
            {
                retryAt ??= DateTime.UtcNow.AddSeconds(Math.Min(10, 1 + Reconnects * 2));
                status = PlayerStatus.Loading;
            }
            else if (status == PlayerStatus.Playing && sinceStart > 30)
            {
                Reconnects = 0;
            }
            if (retryAt != null && DateTime.UtcNow >= retryAt)
            {
                retryAt = null;
                Reconnects++;
                StartCurrent();
                return;
            }
        }
        Status = status;
    }

    private void SaveResumePoint()
    {
        PlayableItem item = Item;
        if (item == null || resume == null || string.IsNullOrEmpty(item.ResumeKey) || DurationMs <= 0) return;
        if (TimeMs > 30_000 && TimeMs < DurationMs - 60_000) resume.Set(item.ResumeKey, TimeMs);
        else if (TimeMs >= DurationMs - 60_000) resume.Set(item.ResumeKey, 0);
    }

    public void Dispose()
    {
        SaveResumePoint();
        userStopped = true;
        Enqueue(() =>
        {
            player.Stop();
            player.Dispose();
        });
        commands.CompleteAdding();
        worker.Join(TimeSpan.FromSeconds(3));
    }
}

/// <summary>
/// Re-packages an MPEG-TS capture as MP4 without re-encoding (fast, no quality loss), using VLC's stream
/// output: the file is "played" into an MP4 writer instead of onto the screen.
/// </summary>
public sealed class Mp4Converter : IMp4Converter
{
    private readonly Settings settings;

    /// <summary>What VLC reported during the most recent conversion (warnings, errors and writer messages).</summary>
    public static string LastLog { get; private set; } = "";

    public Mp4Converter(Settings settings)
    {
        this.settings = settings;
    }

    public Task<bool> ConvertAsync(string tsPath, string mp4Path, Action<double> progress, CancellationToken token)
    {
        return Task.Run(() => Convert(tsPath, mp4Path, progress, token));
    }

    private bool Convert(string tsPath, string mp4Path, Action<double> progress, CancellationToken token)
    {
        try
        {
            if (File.Exists(mp4Path)) File.Delete(mp4Path);
        }
        catch (Exception)
        {
            return false;
        }

        Vlc.LibVLC library = PlayerEngine.Library(settings);
        // Forward slashes work for Windows paths and need no escaping inside VLC's option syntax.
        string destination = mp4Path.Replace('\\', '/').Replace("'", "\\'");
        bool engineSucceeded = false;

        var messages = new List<string>();
        void OnLog(object sender, Vlc.LogEventArgs e)
        {
            bool relevant = e.Level >= Vlc.LogLevel.Warning
                            || (e.Module != null && (e.Module.Contains("mux") || e.Module.Contains("stream_out") || e.Module.Contains("sout")));
            if (!relevant) return;
            lock (messages)
            {
                if (messages.Count < 60) messages.Add($"{e.Level} {e.Module}: {e.Message}");
            }
        }
        library.Log += OnLog;
        try
        {

        using (var player = new Vlc.MediaPlayer(library))
        using (var media = new Vlc.Media(library, tsPath, Vlc.FromType.FromPath))
        {
            media.AddOption(":sout=#std{access=file,mux=mp4,dst='" + destination + "'}");
            media.AddOption(":no-sout-all");
            if (!player.Play(media)) return false;

            DateTime started = DateTime.UtcNow, lastChange = DateTime.UtcNow;
            float lastPosition = -1;
            bool sawActivity = false;
            while (true)
            {
                Thread.Sleep(300);
                if (token.IsCancellationRequested) break;
                float position = player.Position;
                if (position != lastPosition)
                {
                    lastPosition = position;
                    lastChange = DateTime.UtcNow;
                    progress?.Invoke(Math.Clamp(position, 0, 1));
                }
                if (player.IsPlaying || position > 0) sawActivity = true;

                Vlc.VLCState state = player.State;
                double elapsed = (DateTime.UtcNow - started).TotalSeconds;
                if (state == Vlc.VLCState.Error) break;
                if (state == Vlc.VLCState.Ended || (state == Vlc.VLCState.Stopped && (sawActivity || elapsed > 10)))
                {
                    engineSucceeded = true;
                    break;
                }
                // No progress for a minute and a half: give up rather than hang forever.
                if ((DateTime.UtcNow - lastChange).TotalSeconds > 90) break;
            }
            player.Stop();
        }
        }
        finally
        {
            library.Log -= OnLog;
            lock (messages) LastLog = string.Join(Environment.NewLine, messages);
        }

        if (!engineSucceeded || token.IsCancellationRequested)
        {
            try { File.Delete(mp4Path); } catch (Exception) { }
            return false;
        }

        // Trust the result only if a sensibly sized file actually exists.
        try
        {
            long sourceSize = new FileInfo(tsPath).Length;
            long outputSize = File.Exists(mp4Path) ? new FileInfo(mp4Path).Length : 0;
            LastLog = $"source {sourceSize} bytes, output {outputSize} bytes" + Environment.NewLine + LastLog;
            return outputSize > 50_000 && outputSize > sourceSize / 4;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
