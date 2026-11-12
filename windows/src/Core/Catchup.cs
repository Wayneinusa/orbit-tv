namespace SuperiorIptv;

/// <summary>Builds catch-up ("watch a programme that already aired") addresses.</summary>
public static class Catchup
{
    public static bool IsAvailable(Channel channel, Programme programme, DateTime utcNow)
    {
        if (channel.Catchup == null) return false;
        if (programme.Start >= utcNow) return false;
        return programme.Start > utcNow.AddDays(-channel.Catchup.Days);
    }

    public static string Url(Channel channel, Playlist playlist, DateTime start, DateTime stop, DateTime utcNow)
    {
        CatchupInfo info = channel.Catchup;
        if (info == null) return null;
        DateTime end = stop < utcNow ? stop : utcNow;
        int duration = Math.Max(60, (int)(end - start).TotalSeconds);

        switch (info.Mode)
        {
            case CatchupMode.Xtream:
                if (playlist == null || playlist.Kind != PlaylistKind.Xtream || channel.XtreamId == null) return null;
                var client = new XtreamClient(playlist.Server, playlist.Username, playlist.Password);
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(start, DateTimeKind.Utc), FindZone(playlist.ServerTimeZone));
                // Round up so the end of the programme is not cut off.
                int minutes = (duration + 59) / 60;
                return client.TimeshiftUrl(channel.XtreamId.Value, local.ToString("yyyy-MM-dd':'HH-mm", System.Globalization.CultureInfo.InvariantCulture), minutes);

            case CatchupMode.Shift:
                string separator = channel.Url.Contains('?') ? "&" : "?";
                return $"{channel.Url}{separator}utc={Epoch(start)}&lutc={Epoch(utcNow)}";

            case CatchupMode.Append:
                return channel.Url + Fill(info.Source, start, end, utcNow, duration);

            case CatchupMode.Template:
                return Fill(info.Source, start, end, utcNow, duration);

            case CatchupMode.Flussonic:
                return Flussonic(channel.Url, start, duration);
        }
        return null;
    }

    public static long Epoch(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static TimeZoneInfo FindZone(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception) { }
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>Replaces the placeholders IPTV playlists use in <c>catchup-source</c>.</summary>
    public static string Fill(string template, DateTime start, DateTime end, DateTime utcNow, int duration)
    {
        long startEpoch = Epoch(start), endEpoch = Epoch(end), nowEpoch = Epoch(utcNow);
        string offset = Math.Max(0, nowEpoch - startEpoch).ToString();
        var replacements = new (string, string)[]
        {
            ("${start}", startEpoch.ToString()), ("{utc}", startEpoch.ToString()),
            ("${end}", endEpoch.ToString()), ("{utcend}", endEpoch.ToString()),
            ("${timestamp}", nowEpoch.ToString()), ("{lutc}", nowEpoch.ToString()),
            ("${duration}", duration.ToString()), ("{duration}", duration.ToString()),
            ("${offset}", offset), ("{offset}", offset),
            ("{Y}", start.Year.ToString("0000")), ("{m}", start.Month.ToString("00")), ("{d}", start.Day.ToString("00")),
            ("{H}", start.Hour.ToString("00")), ("{M}", start.Minute.ToString("00")), ("{S}", start.Second.ToString("00"))
        };
        string result = template ?? "";
        foreach (var (placeholder, value) in replacements) result = result.Replace(placeholder, value, StringComparison.Ordinal);
        return result;
    }

    public static string Flussonic(string live, DateTime start, int duration)
    {
        string address = live, query = "";
        int question = address.IndexOf('?');
        if (question >= 0)
        {
            query = address[question..];
            address = address[..question];
        }
        int slash = address.LastIndexOf('/');
        if (slash < 0) return null;
        string folder = address[..slash];
        string file = address[(slash + 1)..];
        long epoch = Epoch(start);
        if (file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return $"{folder}/{file[..^5]}-{epoch}-{duration}.m3u8{query}";
        }
        return $"{folder}/timeshift_abs-{epoch}.ts{query}";
    }
}

/// <summary>The small part of the HLS playlist format the recorder needs.</summary>
public sealed class HlsPlaylist
{
    public bool IsMaster;
    public List<(int Bandwidth, Uri Url)> Variants = new();
    public int TargetDuration = 6;
    public long MediaSequence;
    public List<Uri> Segments = new();
    public bool EndList;
    public bool Encrypted;
    public Uri InitSegment;

    public static HlsPlaylist Parse(string text, Uri baseUrl)
    {
        var playlist = new HlsPlaylist();
        int? pendingBandwidth = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                playlist.IsMaster = true;
                int.TryParse(Attribute("BANDWIDTH", line), out int bandwidth);
                pendingBandwidth = bandwidth;
            }
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (int.TryParse(line["#EXT-X-TARGETDURATION:".Length..], out int target)) playlist.TargetDuration = target;
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], out playlist.MediaSequence);
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                playlist.EndList = true;
            }
            else if (line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal))
            {
                string method = Attribute("METHOD", line) ?? "NONE";
                if (method != "NONE") playlist.Encrypted = true;
            }
            else if (line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal))
            {
                string uri = Attribute("URI", line);
                if (uri != null && Uri.TryCreate(baseUrl, uri, out Uri map)) playlist.InitSegment = map;
            }
            else if (line.StartsWith('#'))
            {
                continue;
            }
            else if (Uri.TryCreate(baseUrl, line, out Uri url))
            {
                if (pendingBandwidth != null)
                {
                    playlist.Variants.Add((pendingBandwidth.Value, url));
                    pendingBandwidth = null;
                }
                else
                {
                    playlist.Segments.Add(url);
                }
            }
        }
        return playlist;
    }

    /// <summary>Reads <c>NAME=value</c> or <c>NAME="value"</c> from a tag line.</summary>
    public static string Attribute(string name, string line)
    {
        int index = line.IndexOf(":" + name + "=", StringComparison.Ordinal);
        if (index < 0) index = line.IndexOf("," + name + "=", StringComparison.Ordinal);
        if (index < 0) return null;
        string rest = line[(index + name.Length + 2)..];
        if (rest.StartsWith('"'))
        {
            int closing = rest.IndexOf('"', 1);
            return closing < 0 ? null : rest[1..closing];
        }
        int comma = rest.IndexOf(',');
        return comma < 0 ? rest : rest[..comma];
    }
}
