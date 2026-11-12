using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SuperiorIptv.Win;

/// <summary>
/// An end-to-end check that the build server runs after compiling:  SuperiorIPTVPlayer.exe --self-test OUT SAMPLES
///
/// It starts a small web server on this PC that behaves like a TV provider (playlist, guide, live streams
/// that drop, movie files that are cut off mid-download), drives the real app through its screens, and
/// writes screenshots plus a PASS/FAIL log to the OUT folder. It never runs for normal users.
/// </summary>
public static class SelfTest
{
    private static StreamWriter log;
    private static int failures;
    private static string outDir = "";
    private static int shot;

    private static void Log(string text)
    {
        try
        {
            log?.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text);
        }
        catch (Exception)
        {
        }
    }

    private static void Check(string name, bool passed, string detail = "")
    {
        if (!passed) failures++;
        Log((passed ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "   [" + detail + "]" : ""));
    }

    public static int Run(string output, string samples)
    {
        outDir = Path.GetFullPath(output);
        Directory.CreateDirectory(outDir);
        Storage.DataFolder = Path.Combine(outDir, "data");
        log = new StreamWriter(Path.Combine(outDir, "selftest.log")) { AutoFlush = true };
        int exitCode = 1;
        try
        {
            Log($"Self-test starting. Scale {Theme.Scale}, screen {Screen.PrimaryScreen.Bounds}, OS {Environment.OSVersion}");
            using var server = new SampleServer(Path.GetFullPath(samples));
            using var app = new AppServices();
            app.Settings.RecordingsFolder = Path.Combine(outDir, "recordings");
            app.Settings.DownloadsFolder = Path.Combine(outDir, "downloads");
            app.Settings.PadAfterMinutes = 0;
            app.Library.LoadFromDisk();
            app.Downloads.Start();
            var form = new MainForm(app) { CloseWithoutAsking = true };
            form.Shown += async (_, _) =>
            {
                try
                {
                    await ScriptAsync(form, app, server);
                    exitCode = failures == 0 ? 0 : 2;
                }
                catch (Exception ex)
                {
                    Log("FAIL  The script stopped with an exception: " + ex);
                    exitCode = 3;
                }
                Log($"Self-test finished with {failures} failure(s).");
                form.Close();
            };
            // A safety net so a hang cannot stall the build forever.
            var watchdog = new System.Windows.Forms.Timer { Interval = 6 * 60 * 1000 };
            watchdog.Tick += (_, _) =>
            {
                Log("FAIL  Timed out after 6 minutes.");
                exitCode = 4;
                Environment.Exit(4);
            };
            watchdog.Start();
            Application.ThreadException += (_, e) => Log("FAIL  Unhandled exception: " + e.Exception);
            Application.Run(form);
        }
        catch (Exception ex)
        {
            Log("FAIL  Start-up exception: " + ex);
            exitCode = 5;
        }
        log.Dispose();
        return exitCode;
    }

    private static async Task Shot(MainForm form, string name, bool screen = false)
    {
        await Task.Delay(700);
        shot++;
        string path = Path.Combine(outDir, $"{shot:00}-{name}.png");
        try
        {
            form.CaptureTo(path);
        }
        catch (Exception ex)
        {
            Log("note  Could not draw " + name + ": " + ex.Message);
        }
        if (!screen) return;
        try
        {
            // The real screen, which (unlike the window's own drawing) includes the video picture.
            Rectangle bounds = form.Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics g = Graphics.FromImage(bitmap)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            bitmap.Save(Path.Combine(outDir, $"{shot:00}-{name}-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            Log("note  Screen capture not available: " + ex.Message);
        }
    }

    private static async Task<bool> Wait(Func<bool> condition, double seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    private static async Task ScriptAsync(MainForm form, AppServices app, SampleServer server)
    {
        Library library = app.Library;
        PlayerEngine player = app.Player;

        // 1. Playlist and guide over HTTP
        bool added = await library.AddAsync(new Playlist { Name = "Sample provider", Kind = PlaylistKind.M3U, Url = server.Base + "/list.m3u" });
        Check("Playlist loads", added, library.ErrorText);
        Check("Channels, movies and series are sorted into their libraries",
            library.Channels.Count == 12 && library.Movies.Count == 6 && library.Series.Count == 2,
            $"{library.Channels.Count} channels, {library.Movies.Count} movies, {library.Series.Count} series");
        await library.RefreshEpgAsync();
        Check("Guide loads", library.Epg.Programmes.Count == 12, library.Epg.Programmes.Count + " channels with listings; " + library.EpgStatus);
        if (library.Channels.Count == 0 || library.Movies.Count == 0) return;
        Channel channel = library.Channels[0];
        library.ToggleFavorite(library.Channels[1]);

        // 2. Live TV: preview, then the full player
        form.GoTo("live");
        await Shot(form, "live-empty");
        app.Preview(channel);
        bool livePlaying = await Wait(() => player.Status == PlayerStatus.Playing, 20);
        Check("Live channel plays in the preview", livePlaying, $"status {player.Status}, time {player.TimeMs} ms");
        await Shot(form, "live-preview", true);
        app.Watch(channel);
        await Task.Delay(1500);
        Check("Player opens for the live channel", form.InPlayer && player.Item != null && player.Item.IsLive);
        Check("Speed control is not offered for live channels", !player.CanChangeSpeed);
        await Shot(form, "player-live", true);
        form.ShowChannelList();
        await Shot(form, "player-channel-list");
        form.HideChannelList();
        form.Zap(1);
        await Task.Delay(600);
        Check("Channel up switches channel", player.Item?.Channel?.Id == library.Channels[1].Id, player.Item?.Channel?.Name ?? "none");
        form.ClosePlayer();

        // 3. Recording from a stream that hangs up every few seconds
        var flaky = new Channel { Id = "selftest:flaky", Name = "Flaky Channel", Url = server.Base + "/flaky/1.ts" };
        app.RecordNow(flaky, "Self-test recording", DateTime.UtcNow.AddSeconds(14));
        form.GoTo("recordings");
        await Task.Delay(6000);
        await Shot(form, "recordings-running");
        bool recorded = await Wait(() => app.Recordings.Snapshot().Any(r => r.Status == RecordingStatus.Completed || r.Status == RecordingStatus.Failed), 90);
        Recording recording = app.Recordings.Snapshot().FirstOrDefault();
        Check("Recording finishes", recorded && recording != null && recording.Status == RecordingStatus.Completed,
            recording == null ? "no recording" : $"{recording.Status}, {recording.Bytes} bytes, {recording.Message}");
        Check("Recording resumed automatically after the stream dropped", recording != null && recording.Reconnects >= 2, $"resumed {recording?.Reconnects} time(s)");
        Check("Recording was converted to MP4", recording != null && recording.HasMp4 && File.Exists(recording.Mp4Path),
            recording == null ? "" : $"mp4 {(File.Exists(recording.Mp4Path) ? new FileInfo(recording.Mp4Path).Length : 0)} bytes, ts kept: {File.Exists(recording.TsPath)}");
        await Shot(form, "recordings-done");

        // 4. Guide, movies, series, search, settings, multi-view
        form.GoTo("guide");
        await Shot(form, "guide");
        form.GoTo("movies");
        await Shot(form, "movies");
        form.GoTo("series");
        await Shot(form, "series");
        form.GoTo("search");
        await Shot(form, "search");
        form.GoTo("multiview");
        await Shot(form, "multiview");
        form.GoTo("settings");
        await Shot(form, "settings");

        // 5. A movie: playback, speed, download while watching
        Movie movie = library.Movies[0];
        app.PlayMovie(movie);
        bool moviePlaying = await Wait(() => player.Status == PlayerStatus.Playing && player.DurationMs > 0, 25);
        Check("Movie plays", moviePlaying, $"status {player.Status}, {player.TimeMs} of {player.DurationMs} ms");
        Check("Speed control is offered for movies", player.CanChangeSpeed);
        player.SetRate(4f);
        await Task.Delay(1500);
        long before = player.TimeMs;
        await Task.Delay(2000);
        player.Poll();
        long advanced = player.TimeMs - before;
        Check("4× speed plays faster than real time", advanced > 4000, $"{advanced} ms of film in 2000 ms");
        form.ShowControls(10);
        await Shot(form, "player-movie-4x", true);
        Check("Download can be started while the movie is open", app.DownloadCurrent() && app.Downloads.FindBySource(movie.Id) != null);
        player.SetRate(1f);
        form.ClosePlayer();

        // 6. Several downloads at once, with dropped connections, pause and resume
        foreach (Movie other in library.Movies.Skip(1).Take(4)) app.Download(other);
        form.GoTo("downloads");
        Check("Several movies can be queued together", app.Downloads.Snapshot().Count == 5, app.Downloads.Snapshot().Count + " in the list");
        await Task.Delay(1200);
        DownloadItem toPause = app.Downloads.Snapshot().FirstOrDefault(d => d.State == DownloadState.Downloading);
        if (toPause != null)
        {
            app.Downloads.Pause(toPause.Id);
            await Task.Delay(500);
            Check("A download can be paused", app.Downloads.Snapshot().First(d => d.Id == toPause.Id).State == DownloadState.Paused);
        }
        else
        {
            Check("A download can be paused", false, "nothing was downloading");
        }
        await Shot(form, "downloads-running");
        if (toPause != null) app.Downloads.Resume(toPause.Id);
        bool allDone = await Wait(() => app.Downloads.Snapshot().All(d => d.State == DownloadState.Completed), 120);
        List<DownloadItem> downloads = app.Downloads.Snapshot();
        Check("All downloads finish", allDone, string.Join(", ", downloads.Select(d => $"{d.State} {d.ReceivedBytes}/{d.TotalBytes}")));
        Check("Downloads resumed automatically after dropped connections", downloads.Sum(d => d.Resumes) >= 2, downloads.Sum(d => d.Resumes) + " automatic resume(s)");
        Check("Downloaded files are complete", downloads.All(d => File.Exists(d.FilePath) && new FileInfo(d.FilePath).Length == server.MovieLength),
            string.Join(", ", downloads.Select(d => File.Exists(d.FilePath) ? new FileInfo(d.FilePath).Length.ToString() : "missing")) + $" (expected {server.MovieLength})");
        await Shot(form, "downloads-done");
        form.GoTo("movies");
        await Shot(form, "movies-downloaded");

        // 7. Playing a downloaded file and the recording, with speed
        DownloadItem finished = downloads.FirstOrDefault(d => d.State == DownloadState.Completed);
        if (finished != null)
        {
            app.PlayDownload(finished);
            bool localPlaying = await Wait(() => player.Status == PlayerStatus.Playing && player.DurationMs > 0, 20);
            Check("Downloaded file plays", localPlaying, $"status {player.Status}, {player.TimeMs} of {player.DurationMs} ms");
            player.SetRate(8f);
            await Task.Delay(1000);
            long start = player.TimeMs;
            await Task.Delay(1500);
            player.Poll();
            Check("8× speed works on a downloaded file", player.TimeMs - start > 5000, $"{player.TimeMs - start} ms of film in 1500 ms");
            form.ClosePlayer();
        }
        recording = app.Recordings.Snapshot().FirstOrDefault();
        if (recording != null && recording.PlayablePath != null)
        {
            app.PlayRecording(recording);
            bool recordingPlaying = await Wait(() => player.Status == PlayerStatus.Playing && player.TimeMs > 0, 20);
            Check("Recording plays", recordingPlaying, $"status {player.Status}, {player.TimeMs} of {player.DurationMs} ms, file {Path.GetFileName(recording.PlayablePath)}");
            Check("Speed control is offered for recordings", player.CanChangeSpeed);
            player.SetRate(2f);
            await Shot(form, "player-recording-2x", true);
            form.ClosePlayer();
        }
        await Task.Delay(500);
    }
}

/// <summary>A pretend TV provider on this PC, used only by the self-test.</summary>
public sealed class SampleServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly byte[] ts, mp4, logo;
    private readonly HashSet<string> droppedOnce = new();
    private volatile bool stopped;

    public string Base { get; }
    public long MovieLength => mp4.Length;

    public SampleServer(string samples)
    {
        ts = File.ReadAllBytes(Path.Combine(samples, "sample.ts"));
        mp4 = File.ReadAllBytes(Path.Combine(samples, "sample.mp4"));
        logo = File.ReadAllBytes(Path.Combine(samples, "logo.png"));
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Base = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        while (!stopped)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync();
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private string Playlist()
    {
        var text = new StringBuilder();
        text.Append($"#EXTM3U url-tvg=\"{Base}/guide.xml\"\n");
        string[] groups = { "News", "Sports", "Entertainment" };
        string[] names = { "World News", "Capital News", "Business Today", "Weather Now", "Sports One", "Sports Two", "Football Live", "Racing", "Comedy Central Stage", "Drama Box", "Nature HD", "Music Mix" };
        for (int i = 0; i < names.Length; i++)
        {
            string catchup = i % 3 == 0 ? " catchup=\"shift\" catchup-days=\"2\"" : "";
            text.Append($"#EXTINF:-1 tvg-id=\"ch{i + 1}\" tvg-logo=\"{Base}/logo/{i + 1}.png\" group-title=\"{groups[i / 4]}\"{catchup},{names[i]}\n");
            text.Append($"{Base}/live/{i + 1}.ts\n");
        }
        string[] movies = { "The Long Road (2021)", "Midnight Harbor (2019)", "Paper Kites (2023)", "Iron Summer (2018)", "Quiet Rivers (2020)", "Last Signal (2022)" };
        for (int i = 0; i < movies.Length; i++)
        {
            // Every movie except the first is cut off once in the middle, to exercise automatic resume.
            string flag = i == 0 ? "" : "?drop=1";
            text.Append($"#EXTINF:-1 tvg-logo=\"{Base}/logo/m{i}.png\" group-title=\"{(i < 3 ? "Drama" : "Adventure")}\",{movies[i]}\n");
            text.Append($"{Base}/movie/user/pass/{i + 1}.mp4{flag}\n");
        }
        for (int i = 1; i <= 4; i++)
        {
            text.Append($"#EXTINF:-1 tvg-logo=\"{Base}/logo/s1.png\" group-title=\"Shows\",Harbor Lights S01 E0{i}\n{Base}/series/user/pass/1{i}.mp4\n");
        }
        for (int i = 1; i <= 2; i++)
        {
            text.Append($"#EXTINF:-1 tvg-logo=\"{Base}/logo/s2.png\" group-title=\"Shows\",Northern Line S02 E0{i}\n{Base}/series/user/pass/2{i}.mp4\n");
        }
        return text.ToString();
    }

    private static string Guide()
    {
        var text = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv>\n");
        string[] titles = { "Morning Report", "The Big Match", "City Stories", "Late Review", "World in Focus", "Kitchen Table", "Open Road", "Night Shift" };
        for (int c = 1; c <= 12; c++) text.Append($"<channel id=\"ch{c}\"><display-name>Channel {c}</display-name></channel>\n");
        DateTime now = DateTime.UtcNow;
        DateTime start = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-4);
        for (int c = 1; c <= 12; c++)
        {
            DateTime at = start;
            int n = c;
            while (at < now.AddHours(8))
            {
                int minutes = (n % 3 + 1) * 30;
                DateTime stop = at.AddMinutes(minutes);
                text.Append($"<programme start=\"{at:yyyyMMddHHmmss} +0000\" stop=\"{stop:yyyyMMddHHmmss} +0000\" channel=\"ch{c}\">");
                text.Append($"<title>{titles[n % titles.Length]}</title><desc>Sample programme {n} for the self-test. It has a description long enough to wrap onto a second line in the details pane.</desc></programme>\n");
                at = stop;
                n++;
            }
        }
        text.Append("</tv>\n");
        return text.ToString();
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(one.AsMemory(0, 1));
                    if (read == 0 || head.Length > 16000) return;
                    head.Append((char)one[0]);
                }
                string[] lines = head.ToString().Split("\r\n");
                string target = lines[0].Split(' ')[1];
                string path = target.Split('?')[0];
                long rangeStart = -1;
                foreach (string line in lines)
                {
                    if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                    long.TryParse(line[(line.IndexOf('=') + 1)..].Split('-')[0], out rangeStart);
                }

                async Task Head(int status, string type, long length, string extra = "")
                {
                    string text = $"HTTP/1.1 {status} OK\r\nConnection: close\r\nContent-Type: {type}\r\nAccept-Ranges: bytes\r\n" +
                                  (length >= 0 ? $"Content-Length: {length}\r\n" : "") + extra + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
                }

                if (path == "/list.m3u")
                {
                    byte[] body = Encoding.UTF8.GetBytes(Playlist());
                    await Head(200, "audio/x-mpegurl", body.Length);
                    await stream.WriteAsync(body);
                }
                else if (path == "/guide.xml")
                {
                    byte[] body = Encoding.UTF8.GetBytes(Guide());
                    await Head(200, "application/xml", body.Length);
                    await stream.WriteAsync(body);
                }
                else if (path.StartsWith("/logo/", StringComparison.Ordinal))
                {
                    await Head(200, "image/png", logo.Length);
                    await stream.WriteAsync(logo);
                }
                else if (path.StartsWith("/live/", StringComparison.Ordinal) || path.StartsWith("/flaky/", StringComparison.Ordinal))
                {
                    // A live stream: the sample clip over and over, sent at its real speed.
                    // The "flaky" one hangs up after about three seconds, every time.
                    bool flaky = path.StartsWith("/flaky/", StringComparison.Ordinal);
                    await Head(200, "video/mp2t", -1);
                    const int slices = 100;                       // the clip lasts 10 seconds
                    int slice = ts.Length / slices / 188 * 188;    // whole transport-stream packets
                    DateTime begun = DateTime.UtcNow;
                    int sent = 0;
                    while (!stopped)
                    {
                        for (int offset = 0; offset + slice <= ts.Length && !stopped; offset += slice)
                        {
                            await stream.WriteAsync(ts.AsMemory(offset, slice));
                            sent++;
                            if (flaky && sent >= 30) return;
                            double ahead = sent * 0.1 - (DateTime.UtcNow - begun).TotalSeconds;
                            // Stay about one second ahead so the player can fill its buffer.
                            if (ahead > 1) await Task.Delay(TimeSpan.FromSeconds(Math.Min(ahead - 1, 0.5)));
                        }
                    }
                }
                else if (path.StartsWith("/movie/", StringComparison.Ordinal) || path.StartsWith("/series/", StringComparison.Ordinal))
                {
                    long start = Math.Max(0, rangeStart);
                    if (start >= mp4.Length)
                    {
                        await Head(416, "video/mp4", 0, $"Content-Range: bytes */{mp4.Length}\r\n");
                        return;
                    }
                    bool drop = false;
                    if (target.Contains("drop=1"))
                    {
                        lock (droppedOnce) drop = droppedOnce.Add(path);
                    }
                    long length = mp4.Length - start;
                    if (rangeStart >= 0) await Head(206, "video/mp4", length, $"Content-Range: bytes {start}-{mp4.Length - 1}/{mp4.Length}\r\n");
                    else await Head(200, "video/mp4", length);
                    long position = start;
                    long cutAt = drop ? mp4.Length * 2 / 5 : long.MaxValue;
                    while (position < mp4.Length && !stopped)
                    {
                        if (position >= cutAt) return;      // the "network drop"
                        int chunk = (int)Math.Min(32 * 1024, mp4.Length - position);
                        await stream.WriteAsync(mp4.AsMemory((int)position, chunk));
                        position += chunk;
                        await Task.Delay(40);               // roughly 800 KB per second
                    }
                }
                else
                {
                    await Head(404, "text/plain", 0);
                }
                await stream.FlushAsync();
            }
        }
        catch (Exception)
        {
            // The client went away.
        }
    }

    public void Dispose()
    {
        stopped = true;
        listener.Stop();
    }
}
