using System.Net;

namespace SuperiorIptv;

/// <summary>One shared HTTP client for the whole app.</summary>
public static class Net
{
    public const string DefaultUserAgent = "SuperiorIPTV/1.0";

    public static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16
        };
        // Long streams are read for hours; time limits are applied per request with cancellation tokens.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static HttpRequestMessage Get(string url, string userAgent)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        return request;
    }

    /// <summary>Downloads text from a web address, or reads it from a local file path.</summary>
    public static async Task<string> GetStringAsync(string source, string userAgent, TimeSpan timeout, CancellationToken token = default)
    {
        if (IsLocalPath(source)) return await File.ReadAllTextAsync(LocalPath(source), token).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        using var request = Get(source, userAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UserException($"The server answered with error {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    public static bool IsLocalPath(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
        return !source.Contains("://");
    }

    public static string LocalPath(string source)
    {
        if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return new Uri(source).LocalPath;
        return source;
    }
}
