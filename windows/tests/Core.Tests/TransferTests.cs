namespace SuperiorIptv.Tests;

/// <summary>Checks that downloads and recordings really do carry on after a dropped connection.</summary>
public class TransferTests
{
    /// <summary>Serves <paramref name="data"/> with Range support, cutting the first few connections short.</summary>
    private static TestServer FileServer(byte[] data, int dropFirst, int dropAfterBytes, bool supportRange = true)
    {
        return new TestServer((path, rangeStart, number) =>
        {
            long start = supportRange ? (rangeStart ?? 0) : 0;
            if (start > data.Length) return new Reply { Status = 416, ContentRange = $"bytes */{data.Length}" };
            if (start == data.Length && data.Length > 0) return new Reply { Status = 416, ContentRange = $"bytes */{data.Length}" };
            byte[] body = data.AsSpan((int)start).ToArray();
            var reply = new Reply { Body = body, ContentType = "video/mp4" };
            if (supportRange && rangeStart != null)
            {
                reply.Status = 206;
                reply.ContentRange = $"bytes {start}-{data.Length - 1}/{data.Length}";
            }
            if (number <= dropFirst) reply.AbortAfter = dropAfterBytes;
            return reply;
        });
    }

    private static DownloadManager NewManager(string folder)
    {
        return new DownloadManager(Path.Combine(folder, "downloads.json"), () => Path.Combine(folder, "files"), () => 2)
        {
            IdleTimeoutSeconds = 5,
            MaxRetryDelaySeconds = 0.2
        };
    }

    [Fact]
    public async Task DownloadResumesAutomaticallyAfterDroppedConnections()
    {
        byte[] data = TestEnv.RandomBytes(400_000, 1);
        using TestServer server = FileServer(data, dropFirst: 3, dropAfterBytes: 60_000);
        string folder = TestEnv.NewFolder("dl-resume");
        using DownloadManager manager = NewManager(folder);
        manager.Start();

        DownloadItem added = manager.Add("movie-1", "Test Movie", "", server.Base + "/movie/u/p/1.mp4", "");
        bool done = await TestEnv.WaitAsync(() => manager.Snapshot()[0].State == DownloadState.Completed, 30);
        DownloadItem item = manager.Snapshot()[0];

        Assert.True(done, $"state {item.State}: {item.Message}");
        Assert.Equal(data, File.ReadAllBytes(item.FilePath));
        Assert.False(File.Exists(item.PartPath));
        Assert.True(item.Resumes >= 3, $"resumes {item.Resumes}");
        Assert.Equal(data.Length, item.TotalBytes);
        // Every retry asked the server to continue from where the previous attempt stopped.
        lock (server.Ranges) Assert.Contains(server.Ranges, r => r.HasValue && r.Value > 0);
        Assert.EndsWith(".mp4", item.FilePath);
        Assert.Equal(added.Id, item.Id);
    }

    [Fact]
    public async Task DownloadStartsOverWhenServerCannotResume()
    {
        byte[] data = TestEnv.RandomBytes(200_000, 2);
        using TestServer server = FileServer(data, dropFirst: 2, dropAfterBytes: 50_000, supportRange: false);
        string folder = TestEnv.NewFolder("dl-norange");
        using DownloadManager manager = NewManager(folder);
        manager.Start();

        manager.Add("movie-2", "No Range", "", server.Base + "/file.mkv", "");
        bool done = await TestEnv.WaitAsync(() => manager.Snapshot()[0].State == DownloadState.Completed, 30);
        DownloadItem item = manager.Snapshot()[0];

        Assert.True(done, $"state {item.State}: {item.Message}");
        Assert.Equal(data, File.ReadAllBytes(item.FilePath));
    }

    [Fact]
    public async Task PauseAndResumeContinueFromTheSamePlace()
    {
        byte[] data = TestEnv.RandomBytes(300_000, 3);
        // The first connection drops after 80 KB; the download is paused while it waits to retry.
        using var server = new TestServer((path, rangeStart, number) =>
        {
            long start = rangeStart ?? 0;
            var reply = new Reply { Body = data.AsSpan((int)start).ToArray() };
            if (rangeStart != null)
            {
                reply.Status = 206;
                reply.ContentRange = $"bytes {start}-{data.Length - 1}/{data.Length}";
            }
            if (number == 1) reply.AbortAfter = 80_000;
            return reply;
        });
        string folder = TestEnv.NewFolder("dl-pause");
        using DownloadManager manager = NewManager(folder);
        manager.MaxRetryDelaySeconds = 60;   // long enough that only an explicit resume can finish it
        manager.Start();

        DownloadItem added = manager.Add("movie-3", "Pause Me", "", server.Base + "/file.mp4", "");
        Assert.True(await TestEnv.WaitAsync(() => manager.Snapshot()[0].ReceivedBytes >= 80_000, 15));
        manager.Pause(added.Id);
        Assert.Equal(DownloadState.Paused, manager.Snapshot()[0].State);
        await Task.Delay(300);
        Assert.Equal(DownloadState.Paused, manager.Snapshot()[0].State);
        Assert.True(File.Exists(added.PartPath));

        manager.MaxRetryDelaySeconds = 0.2;
        manager.Resume(added.Id);
        bool done = await TestEnv.WaitAsync(() => manager.Snapshot()[0].State == DownloadState.Completed, 30);
        DownloadItem item = manager.Snapshot()[0];
        Assert.True(done, $"state {item.State}: {item.Message}");
        Assert.Equal(data, File.ReadAllBytes(item.FilePath));
    }

    [Fact]
    public async Task UnfinishedDownloadContinuesAfterAppRestart()
    {
        byte[] data = TestEnv.RandomBytes(300_000, 4);
        using TestServer server = FileServer(data, dropFirst: 1, dropAfterBytes: 100_000);
        string folder = TestEnv.NewFolder("dl-restart");

        string id;
        using (DownloadManager first = NewManager(folder))
        {
            first.MaxRetryDelaySeconds = 60;
            first.Start();
            id = first.Add("movie-4", "Restart", "", server.Base + "/file.mp4", "").Id;
            Assert.True(await TestEnv.WaitAsync(() => first.Snapshot()[0].ReceivedBytes >= 100_000, 15));
        }   // "closing the app"

        using DownloadManager second = NewManager(folder);
        Assert.Equal(DownloadState.Queued, second.Snapshot()[0].State);
        second.Start();
        bool done = await TestEnv.WaitAsync(() => second.Snapshot()[0].State == DownloadState.Completed, 30);
        DownloadItem item = second.Snapshot()[0];
        Assert.True(done, $"state {item.State}: {item.Message}");
        Assert.Equal(id, item.Id);
        Assert.Equal(data, File.ReadAllBytes(item.FilePath));
    }

    [Fact]
    public async Task SeveralDownloadsCanBeQueuedAndRemoved()
    {
        byte[] data = TestEnv.RandomBytes(120_000, 5);
        using TestServer server = FileServer(data, dropFirst: 0, dropAfterBytes: 0);
        string folder = TestEnv.NewFolder("dl-multi");
        using DownloadManager manager = NewManager(folder);
        manager.Start();

        for (int i = 0; i < 5; i++) manager.Add("ep-" + i, "Show", "S01E0" + i, server.Base + $"/series/u/p/{i}.mkv", "");
        // Adding the same item twice does not create a duplicate.
        manager.Add("ep-0", "Show", "S01E00", server.Base + "/series/u/p/0.mkv", "");
        Assert.Equal(5, manager.Snapshot().Count);

        bool done = await TestEnv.WaitAsync(() => manager.Snapshot().All(i => i.State == DownloadState.Completed), 30);
        Assert.True(done);
        List<DownloadItem> items = manager.Snapshot();
        Assert.Equal(5, items.Select(i => i.FilePath).Distinct().Count());
        foreach (DownloadItem item in items) Assert.Equal(data.Length, new FileInfo(item.FilePath).Length);

        manager.Remove(items[0].Id, deleteFile: true);
        Assert.Equal(4, manager.Snapshot().Count);
        Assert.True(await TestEnv.WaitAsync(() => !File.Exists(items[0].FilePath), 5));
    }

    [Fact]
    public async Task MissingFileFailsInsteadOfRetryingForever()
    {
        using var server = new TestServer((path, rangeStart, number) => new Reply { Status = 404 });
        string folder = TestEnv.NewFolder("dl-404");
        using DownloadManager manager = NewManager(folder);
        manager.Start();
        manager.Add("gone", "Gone", "", server.Base + "/missing.mp4", "");
        Assert.True(await TestEnv.WaitAsync(() => manager.Snapshot()[0].State == DownloadState.Failed, 15));
    }

    [Fact]
    public async Task RecordingReconnectsEveryTimeTheStreamStops()
    {
        byte[] chunk = TestEnv.RandomBytes(50_000, 6);
        // A "live stream" that hangs up after every 50 KB, with one refused connection in between.
        using var server = new TestServer((path, rangeStart, number) =>
        {
            if (number == 3) return new Reply { Status = 503 };
            return new Reply { Body = chunk, SendLength = false, ContentType = "video/mp2t" };
        });
        string folder = TestEnv.NewFolder("rec");
        string file = Path.Combine(folder, "capture.ts");

        var recorder = new StreamRecorder(server.Base + "/live/u/p/1.ts", file, "", DateTime.UtcNow.AddSeconds(4))
        {
            IdleTimeoutSeconds = 3,
            MaxRetryDelaySeconds = 0.2
        };
        recorder.Start();
        await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RecorderState.Finished, recorder.State);
        Assert.True(recorder.Reconnects >= 3, $"reconnects {recorder.Reconnects}");
        long length = new FileInfo(file).Length;
        Assert.True(length >= 3 * chunk.Length, $"length {length}");
        Assert.Equal(length, Interlocked.Read(ref recorder.Bytes));
    }

    [Fact]
    public async Task RecordingAppendsToTheSameFileWhenRestarted()
    {
        byte[] chunk = TestEnv.RandomBytes(30_000, 7);
        using var server = new TestServer((path, rangeStart, number) => new Reply { Body = chunk, SendLength = false });
        string folder = TestEnv.NewFolder("rec-append");
        string file = Path.Combine(folder, "capture.ts");

        var first = new StreamRecorder(server.Base + "/live.ts", file, "", DateTime.UtcNow.AddSeconds(30)) { MaxRetryDelaySeconds = 0.2 };
        first.Start();
        Assert.True(await TestEnv.WaitAsync(() => Interlocked.Read(ref first.Bytes) >= chunk.Length, 15));
        first.Stop();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RecorderState.Stopped, first.State);
        long before = new FileInfo(file).Length;

        var second = new StreamRecorder(server.Base + "/live.ts", file, "", DateTime.UtcNow.AddSeconds(2)) { MaxRetryDelaySeconds = 0.2 };
        second.Start();
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(new FileInfo(file).Length > before);
    }

    [Fact]
    public async Task RecordsHlsStreamsSegmentBySegment()
    {
        byte[] one = TestEnv.RandomBytes(20_000, 8), two = TestEnv.RandomBytes(25_000, 9), three = TestEnv.RandomBytes(15_000, 10);
        using var server = new TestServer((path, rangeStart, number) =>
        {
            if (path.EndsWith("master.m3u8")) return Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nmedia.m3u8\n");
            if (path.EndsWith("media.m3u8")) return Text("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:7\n#EXTINF:2,\na.ts\n#EXTINF:2,\nb.ts\n#EXTINF:2,\nc.ts\n#EXT-X-ENDLIST\n");
            if (path.EndsWith("a.ts")) return new Reply { Body = one };
            if (path.EndsWith("b.ts")) return new Reply { Body = two };
            if (path.EndsWith("c.ts")) return new Reply { Body = three };
            return new Reply { Status = 404 };
        });
        string file = Path.Combine(TestEnv.NewFolder("rec-hls"), "capture.ts");

        var recorder = new StreamRecorder(server.Base + "/live/master.m3u8", file, "", DateTime.UtcNow.AddSeconds(30));
        recorder.Start();
        await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(RecorderState.Finished, recorder.State);
        Assert.Equal(one.Concat(two).Concat(three).ToArray(), File.ReadAllBytes(file));
    }

    private static Reply Text(string text) => new()
    {
        Body = System.Text.Encoding.UTF8.GetBytes(text),
        ContentType = "application/vnd.apple.mpegurl"
    };
}
