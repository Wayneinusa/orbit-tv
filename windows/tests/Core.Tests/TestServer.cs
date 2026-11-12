using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SuperiorIptv.Tests;

/// <summary>What the test server should answer to one request.</summary>
public sealed class Reply
{
    public int Status = 200;
    public byte[] Body = Array.Empty<byte>();
    public string ContentType = "application/octet-stream";
    /// <summary>Send a Content-Length header (false imitates a live stream of unknown length).</summary>
    public bool SendLength = true;
    /// <summary>Value for a Content-Range header, e.g. "bytes 100-999/1000".</summary>
    public string ContentRange;
    /// <summary>Cut the connection after this many body bytes (-1 = send everything).</summary>
    public int AbortAfter = -1;
}

/// <summary>A tiny HTTP server that can drop connections on purpose, to test automatic resume.</summary>
public sealed class TestServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly Func<string, long?, int, Reply> handler;
    private int requests;
    private volatile bool stopped;

    public int Port { get; }
    public int Requests => requests;
    public List<long?> Ranges { get; } = new();
    public string Base => $"http://127.0.0.1:{Port}";

    public TestServer(Func<string, long?, int, Reply> handler)
    {
        this.handler = handler;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
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

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                NetworkStream stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await stream.ReadAsync(one.AsMemory(0, 1));
                    if (read == 0) return;
                    head.Append((char)one[0]);
                    if (head.Length > 16000) return;
                }
                string[] lines = head.ToString().Split("\r\n");
                string path = lines[0].Split(' ')[1];
                long? rangeStart = null;
                foreach (string line in lines)
                {
                    if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                    string value = line[(line.IndexOf('=') + 1)..];
                    if (long.TryParse(value.Split('-')[0], out long start)) rangeStart = start;
                }
                int number = Interlocked.Increment(ref requests);
                lock (Ranges) Ranges.Add(rangeStart);

                Reply reply = handler(path, rangeStart, number);
                var header = new StringBuilder();
                header.Append($"HTTP/1.1 {reply.Status} Test\r\nConnection: close\r\nContent-Type: {reply.ContentType}\r\n");
                if (reply.SendLength) header.Append($"Content-Length: {reply.Body.Length}\r\n");
                if (reply.ContentRange != null) header.Append($"Content-Range: {reply.ContentRange}\r\n");
                header.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()));

                int toSend = reply.AbortAfter >= 0 ? Math.Min(reply.AbortAfter, reply.Body.Length) : reply.Body.Length;
                int sent = 0;
                while (sent < toSend)
                {
                    int chunk = Math.Min(8192, toSend - sent);
                    await stream.WriteAsync(reply.Body.AsMemory(sent, chunk));
                    sent += chunk;
                }
                // Closing here, before the promised length has been sent, is what a dropped connection
                // looks like to the client. (A graceful close keeps the test deterministic: everything
                // sent so far still arrives.)
                await stream.FlushAsync();
            }
        }
        catch (Exception)
        {
            // The client went away; nothing to do.
        }
    }

    public void Dispose()
    {
        stopped = true;
        listener.Stop();
    }
}

public static class TestEnv
{
    private static readonly object Gate = new();
    private static string root;

    /// <summary>Points the app's data folder at a temporary directory, once per test run.</summary>
    public static string Root
    {
        get
        {
            lock (Gate)
            {
                if (root == null)
                {
                    root = Path.Combine(Path.GetTempPath(), "siptv-tests-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(root);
                    Storage.DataFolder = Path.Combine(root, "data");
                }
                return root;
            }
        }
    }

    public static string NewFolder(string name)
    {
        string folder = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static byte[] RandomBytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static async Task<bool> WaitAsync(Func<bool> condition, double seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
