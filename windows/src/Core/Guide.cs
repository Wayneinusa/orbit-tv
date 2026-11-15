using System.Collections.Concurrent;

namespace SuperiorIptv;

/// <summary>Which guide source a channel takes its listings from.</summary>
public enum GuideChoice
{
    /// <summary>The primary guide, or the additional one when the primary has nothing for this channel.</summary>
    Automatic,
    Primary,
    Additional,
    /// <summary>Show no listings for this channel.</summary>
    None
}

/// <summary>The guide addresses that belong to one playlist.</summary>
public sealed class PlaylistGuide
{
    public List<string> Primary { get; set; } = new();
    public List<string> Additional { get; set; } = new();
}

/// <summary>
/// The guide as the screens see it. Listings are kept per source (per XMLTV address); for each channel
/// this picks the source that applies: the playlist's primary guide, its additional guide when the primary
/// has no schedule for that channel, or whatever was chosen for that one channel.
///
/// A GuideBook never changes after it is built. The library swaps in a new one when listings, playlists
/// or per-channel choices change, so screens can keep using the one they have while painting.
/// </summary>
public sealed class GuideBook
{
    private static readonly List<Programme> NoProgrammes = new();

    private readonly Dictionary<string, EpgData> sources;
    private readonly Dictionary<string, PlaylistGuide> playlists;
    private readonly Dictionary<string, GuideChoice> choices;
    private readonly ConcurrentDictionary<string, List<Programme>> resolved = new();

    public DateTime? Updated { get; }

    public GuideBook() : this(new(), new(), new(), null) { }

    public GuideBook(Dictionary<string, EpgData> sources, Dictionary<string, PlaylistGuide> playlists,
        Dictionary<string, GuideChoice> choices, DateTime? updated)
    {
        this.sources = sources;
        this.playlists = playlists;
        this.choices = choices;
        Updated = updated;
    }

    /// <summary>True when at least one source has listings.</summary>
    public bool HasListings => sources.Values.Any(s => s.Programmes.Count > 0);

    /// <summary>How many channels the given source has listings for (0 when it was never loaded).</summary>
    public int ChannelsIn(string address) => sources.TryGetValue(address ?? "", out EpgData data) ? data.Programmes.Count : 0;

    public GuideChoice ChoiceFor(Channel channel) =>
        channel != null && choices.TryGetValue(channel.Id, out GuideChoice choice) ? choice : GuideChoice.Automatic;

    /// <summary>Whether the channel's playlist has an additional guide at all.</summary>
    public bool HasAdditional(Channel channel) =>
        channel != null && playlists.TryGetValue(channel.PlaylistId ?? "", out PlaylistGuide guide) && guide.Additional.Count > 0;

    /// <summary>The channel's listings in its playlist's primary guide (false) or additional guide (true).</summary>
    public List<Programme> ListFrom(Channel channel, bool additional)
    {
        if (channel == null || !playlists.TryGetValue(channel.PlaylistId ?? "", out PlaylistGuide guide)) return NoProgrammes;
        foreach (string address in additional ? guide.Additional : guide.Primary)
        {
            if (!sources.TryGetValue(address, out EpgData data)) continue;
            List<Programme> list = data.ListFor(channel);
            if (list.Count > 0) return list;
        }
        return NoProgrammes;
    }

    /// <summary>The listings to show for a channel, after applying the choice made for it.</summary>
    public List<Programme> ListFor(Channel channel)
    {
        if (channel == null) return NoProgrammes;
        if (resolved.TryGetValue(channel.Id, out List<Programme> known)) return known;
        List<Programme> list;
        switch (ChoiceFor(channel))
        {
            case GuideChoice.None:
                list = NoProgrammes;
                break;
            case GuideChoice.Primary:
                list = ListFrom(channel, false);
                break;
            case GuideChoice.Additional:
                list = ListFrom(channel, true);
                break;
            default:
                list = ListFrom(channel, false);
                if (list.Count == 0) list = ListFrom(channel, true);
                break;
        }
        resolved[channel.Id] = list;
        return list;
    }

    /// <summary>True when the listings shown for this channel come from the additional guide.</summary>
    public bool UsesAdditional(Channel channel)
    {
        List<Programme> list = ListFor(channel);
        return list.Count > 0 && ReferenceEquals(list, ListFrom(channel, true)) && !ReferenceEquals(list, ListFrom(channel, false));
    }

    public Programme Current(Channel channel, DateTime utc)
    {
        List<Programme> list = ListFor(channel);
        int index = EpgData.IndexAt(list, utc);
        return index < 0 ? null : list[index];
    }

    public Programme Next(Channel channel, DateTime utc)
    {
        foreach (Programme programme in ListFor(channel))
        {
            if (programme.Start > utc) return programme;
        }
        return null;
    }

    public List<Programme> Between(Channel channel, DateTime from, DateTime to)
    {
        var result = new List<Programme>();
        foreach (Programme programme in ListFor(channel))
        {
            if (programme.Start >= to) break;
            if (programme.Stop > from) result.Add(programme);
        }
        return result;
    }

    public List<Programme> Upcoming(Channel channel, DateTime utc, int limit)
    {
        var result = new List<Programme>();
        foreach (Programme programme in ListFor(channel))
        {
            if (programme.Start <= utc) continue;
            result.Add(programme);
            if (result.Count >= limit) break;
        }
        return result;
    }
}

/// <summary>What is kept on disk between runs: the listings of every source.</summary>
public sealed class GuideCache
{
    public Dictionary<string, EpgData> Sources { get; set; } = new();
    public DateTime? Updated { get; set; }
}
