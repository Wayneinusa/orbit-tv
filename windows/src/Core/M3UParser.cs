using System.Text.RegularExpressions;

namespace SuperiorIptv;

/// <summary>Parses extended M3U playlists (the "#EXTINF" flavour used by IPTV providers).</summary>
public static class M3UParser
{
    private static readonly Regex AttributeRegex = new("([A-Za-z0-9_-]+)=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex EpisodeRegex = new(@"^(.*?)[\s._-]+S(\d{1,2})[\s._-]*E(\d{1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "mp4", "mkv", "avi", "mov", "m4v", "wmv", "flv", "mpg", "mpeg", "webm" };

    public static PlaylistContent Parse(string text, string playlistId)
    {
        var content = new PlaylistContent();
        string prefix = playlistId.Length > 8 ? playlistId[..8] : playlistId;
        var attributes = new Dictionary<string, string>();
        string pendingName = "", pendingGroup = "", pendingUserAgent = "";
        int nextNumber = 1;
        var seriesIndex = new Dictionary<string, Series>();
        var seenIds = new HashSet<string>();

        using var reader = new StringReader(text);
        string raw;
        while ((raw = reader.ReadLine()) != null)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                var header = ParseAttributes(line);
                foreach (string key in new[] { "url-tvg", "x-tvg-url", "tvg-url" })
                {
                    if (!header.TryGetValue(key, out string value)) continue;
                    foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!content.EpgUrls.Contains(part)) content.EpgUrls.Add(part);
                    }
                }
                continue;
            }
            if (line.StartsWith("#EXTINF", StringComparison.Ordinal))
            {
                var (head, name) = SplitInfoLine(line);
                attributes = ParseAttributes(head);
                pendingName = name;
                pendingGroup = "";
                pendingUserAgent = "";
                continue;
            }
            if (line.StartsWith("#EXTGRP:", StringComparison.Ordinal))
            {
                pendingGroup = line["#EXTGRP:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("#EXTVLCOPT:", StringComparison.Ordinal))
            {
                string option = line["#EXTVLCOPT:".Length..];
                if (option.StartsWith("http-user-agent=", StringComparison.OrdinalIgnoreCase))
                {
                    pendingUserAgent = option["http-user-agent=".Length..];
                }
                continue;
            }
            if (line.StartsWith('#')) continue;

            // Anything else is a stream address.
            string address = line;
            var current = attributes;
            attributes = new Dictionary<string, string>();
            if (!address.Contains("://")) continue;

            string itemName = pendingName;
            if (itemName.Length == 0) itemName = current.GetValueOrDefault("tvg-name", "");
            if (itemName.Length == 0) itemName = address;

            string group = current.GetValueOrDefault("group-title", "");
            if (group.Length == 0) group = pendingGroup;
            if (group.Length == 0) group = ChannelGroups.Uncategorized;

            string logo = current.GetValueOrDefault("tvg-logo", current.GetValueOrDefault("logo", ""));
            string id = prefix + ":" + StableHash.Hex(address);
            if (seenIds.Contains(id)) id += "-" + seenIds.Count;
            seenIds.Add(id);

            var kind = Classify(address, itemName, out string show, out int season, out int episodeNumber);
            if (kind == ItemKind.Live)
            {
                int number = nextNumber;
                string numberText = current.GetValueOrDefault("tvg-chno", current.GetValueOrDefault("channel-number", ""));
                if (int.TryParse(numberText.Trim(), out int parsed) && parsed > 0) number = parsed;
                nextNumber = Math.Max(nextNumber, number) + 1;
                content.Channels.Add(new Channel
                {
                    Id = id,
                    PlaylistId = playlistId,
                    Number = number,
                    Name = itemName,
                    Group = group,
                    Logo = logo,
                    Url = address,
                    EpgId = current.GetValueOrDefault("tvg-id", "").ToLowerInvariant(),
                    Catchup = ParseCatchup(current),
                    UserAgent = pendingUserAgent
                });
            }
            else if (kind == ItemKind.Movie)
            {
                content.Movies.Add(new Movie { Id = id, PlaylistId = playlistId, Name = itemName, Group = group, Poster = logo, Url = address });
            }
            else
            {
                string key = group + "\u0001" + show.ToLowerInvariant();
                if (!seriesIndex.TryGetValue(key, out Series series))
                {
                    series = new Series
                    {
                        Id = prefix + ":s:" + StableHash.Hex(key),
                        PlaylistId = playlistId,
                        Name = show,
                        Group = group,
                        Poster = logo
                    };
                    seriesIndex[key] = series;
                    content.Series.Add(series);
                }
                series.Episodes.Add(new Episode { Id = id, Season = season, Number = episodeNumber, Title = itemName, Url = address, Image = logo });
            }
        }

        foreach (var series in content.Series)
        {
            series.Episodes = series.Episodes.OrderBy(e => e.Season).ThenBy(e => e.Number).ToList();
        }
        return content;
    }

    /// <summary>
    /// Splits <c>#EXTINF:-1 key="value",Display name</c> into its attribute part and its display name.
    /// The separator is the first comma that is not inside a quoted value.
    /// </summary>
    public static (string Header, string Name) SplitInfoLine(string line)
    {
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes) return (line[..i], line[(i + 1)..].Trim());
        }
        return (line, "");
    }

    public static Dictionary<string, string> ParseAttributes(string header)
    {
        var result = new Dictionary<string, string>();
        foreach (Match match in AttributeRegex.Matches(header))
        {
            result[match.Groups[1].Value.ToLowerInvariant()] = match.Groups[2].Value.Trim();
        }
        return result;
    }

    public static CatchupInfo ParseCatchup(Dictionary<string, string> attributes)
    {
        string kind = attributes.GetValueOrDefault("catchup", attributes.GetValueOrDefault("catchup-type", "")).ToLowerInvariant();
        string dayText = attributes.GetValueOrDefault("catchup-days", attributes.GetValueOrDefault("timeshift", attributes.GetValueOrDefault("tvg-rec", "")));
        int.TryParse(dayText, out int days);
        string source = attributes.GetValueOrDefault("catchup-source", "");
        if (kind.Length == 0 && days <= 0) return null;
        if (days <= 0) days = 7;

        CatchupMode mode = kind switch
        {
            "xc" => CatchupMode.Xtream,
            "append" => CatchupMode.Append,
            "flussonic" or "fs" or "flussonic-hls" or "flussonic-ts" => CatchupMode.Flussonic,
            _ => source.Length == 0 ? CatchupMode.Shift : CatchupMode.Template
        };
        return new CatchupInfo { Mode = mode, Days = days, Source = source };
    }

    public enum ItemKind { Live, Movie, Episode }

    public static ItemKind Classify(string address, string name, out string show, out int season, out int episode)
    {
        show = "";
        season = 1;
        episode = 1;
        string path = address.ToLowerInvariant();
        int question = path.IndexOf('?');
        if (question >= 0) path = path[..question];
        string extension = "";
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('/');
        if (dot > slash && dot >= 0) extension = path[(dot + 1)..];

        bool looksLikeFile = VideoExtensions.Contains(extension);
        if (!looksLikeFile && !path.Contains("/series/") && !path.Contains("/movie/")) return ItemKind.Live;

        Match match = EpisodeRegex.Match(name);
        if (match.Success)
        {
            string candidate = match.Groups[1].Value.Trim();
            if (candidate.Length > 0)
            {
                show = candidate;
                int.TryParse(match.Groups[2].Value, out season);
                int.TryParse(match.Groups[3].Value, out episode);
                return ItemKind.Episode;
            }
        }
        return ItemKind.Movie;
    }
}
