using System.Text.Json.Serialization;

namespace SuperiorIptv;

public enum PlaylistKind { M3U, Xtream }

public sealed class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public PlaylistKind Kind { get; set; }
    /// <summary>M3U web address or a path to an .m3u file on this PC.</summary>
    public string Url { get; set; } = "";
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Extra XMLTV guide address. Empty means "use whatever the playlist advertises".</summary>
    public string EpgUrl { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string ServerTimeZone { get; set; } = "";
    public DateTime? LastRefreshed { get; set; }
}

public enum CatchupMode { Xtream, Shift, Append, Template, Flussonic }

public sealed class CatchupInfo
{
    public CatchupMode Mode { get; set; }
    public int Days { get; set; }
    public string Source { get; set; } = "";
}

public sealed class Channel
{
    public string Id { get; set; } = "";
    public string PlaylistId { get; set; } = "";
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Logo { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Lower-cased XMLTV channel id ("" when the playlist gives none).</summary>
    public string EpgId { get; set; } = "";
    public CatchupInfo Catchup { get; set; }
    public int? XtreamId { get; set; }
    public string UserAgent { get; set; } = "";
}

public sealed class Movie
{
    public string Id { get; set; } = "";
    public string PlaylistId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Poster { get; set; } = "";
    public string Url { get; set; } = "";
    public string Rating { get; set; } = "";
    public string Year { get; set; } = "";
    public string Plot { get; set; } = "";
    public int? XtreamId { get; set; }
}

public sealed class Episode
{
    public string Id { get; set; } = "";
    public int Season { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Image { get; set; } = "";
}

public sealed class Series
{
    public string Id { get; set; } = "";
    public string PlaylistId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Group { get; set; } = "";
    public string Poster { get; set; } = "";
    public string Plot { get; set; } = "";
    public string Rating { get; set; } = "";
    public string Year { get; set; } = "";
    public int? XtreamId { get; set; }
    /// <summary>Filled for M3U playlists; Xtream playlists load episodes on demand.</summary>
    public List<Episode> Episodes { get; set; } = new();
}

/// <summary>Everything one playlist contributes to the library.</summary>
public sealed class PlaylistContent
{
    public List<Channel> Channels { get; set; } = new();
    public List<Movie> Movies { get; set; } = new();
    public List<Series> Series { get; set; } = new();
    public List<string> EpgUrls { get; set; } = new();
}

/// <summary>One guide entry. Times are UTC.</summary>
public sealed class Programme
{
    public DateTime Start { get; set; }
    public DateTime Stop { get; set; }
    public string Title { get; set; } = "";
    public string Desc { get; set; } = "";

    public bool IsLive(DateTime utcNow) => Start <= utcNow && utcNow < Stop;

    public double Progress(DateTime utcNow)
    {
        double total = (Stop - Start).TotalSeconds;
        if (total <= 0) return 0;
        return Math.Clamp((utcNow - Start).TotalSeconds / total, 0, 1);
    }
}

public static class ChannelGroups
{
    public const string All = "All channels";
    public const string Favorites = "Favorites";
    public const string Recent = "Recent";
    public const string Uncategorized = "Uncategorized";
}

public static class StableHash
{
    /// <summary>FNV-1a. String.GetHashCode is re-seeded on every launch, so it cannot be used for saved ids.</summary>
    public static string Hex(string text)
    {
        ulong hash = 0xcbf29ce484222325;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 0x100000001b3;
        }
        return hash.ToString("x");
    }
}

/// <summary>What the player is asked to play.</summary>
public enum PlayKind { Live, Catchup, Movie, Episode, Recording, Download }

public sealed class PlayableItem
{
    public string Id { get; set; } = "";
    public PlayKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    /// <summary>Web address or a local file path.</summary>
    public string Url { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public Channel Channel { get; set; }
    /// <summary>Key under which the playback position is remembered.</summary>
    public string ResumeKey { get; set; } = "";
    /// <summary>Set for movies and episodes that can be downloaded.</summary>
    public string DownloadUrl { get; set; } = "";
    public string DownloadName { get; set; } = "";

    [JsonIgnore] public bool IsLive => Kind == PlayKind.Live;
    [JsonIgnore] public bool IsLocalFile => !Url.Contains("://");
}

public sealed class UserException : Exception
{
    public UserException(string message) : base(message) { }
}
