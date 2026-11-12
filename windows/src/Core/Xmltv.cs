using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace SuperiorIptv;

/// <summary>Programme listings keyed by lower-cased XMLTV channel id.</summary>
public sealed class EpgData
{
    public Dictionary<string, List<Programme>> Programmes { get; set; } = new();
    /// <summary>Lower-cased channel display name -> channel id, for playlists that carry no tvg-id.</summary>
    public Dictionary<string, string> Names { get; set; } = new();
    public DateTime? Updated { get; set; }

    private static readonly List<Programme> Empty = new();

    public void Merge(EpgData other)
    {
        foreach (var pair in other.Programmes)
        {
            if (Programmes.TryGetValue(pair.Key, out var existing) && existing.Count >= pair.Value.Count) continue;
            Programmes[pair.Key] = pair.Value;
        }
        foreach (var pair in other.Names) Names.TryAdd(pair.Key, pair.Value);
    }

    public List<Programme> ListFor(Channel channel)
    {
        if (channel == null) return Empty;
        if (!string.IsNullOrEmpty(channel.EpgId) && Programmes.TryGetValue(channel.EpgId, out var byId)) return byId;
        if (Names.TryGetValue(channel.Name.ToLowerInvariant(), out string id) && Programmes.TryGetValue(id, out var byName)) return byName;
        return Empty;
    }

    /// <summary>Index of the programme on air at <paramref name="utc"/> (binary search; lists are sorted by start time).</summary>
    public static int IndexAt(List<Programme> list, DateTime utc)
    {
        int low = 0, high = list.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            Programme item = list[middle];
            if (utc < item.Start) high = middle - 1;
            else if (utc >= item.Stop) low = middle + 1;
            else return middle;
        }
        return -1;
    }

    public Programme Current(Channel channel, DateTime utc)
    {
        var list = ListFor(channel);
        int index = IndexAt(list, utc);
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

public static class XmltvDate
{
    /// <summary>Parses "20261111063000 +0100" into UTC without the cost of DateTime.ParseExact.</summary>
    public static DateTime? Parse(string text)
    {
        if (text == null || text.Length < 12) return null;

        int Number(int from, int length)
        {
            if (from + length > text.Length) return -1;
            int value = 0;
            for (int i = from; i < from + length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9') return -1;
                value = value * 10 + (c - '0');
            }
            return value;
        }

        int year = Number(0, 4), month = Number(4, 2), day = Number(6, 2), hour = Number(8, 2), minute = Number(10, 2);
        if (year < 1 || month < 1 || month > 12 || day < 1 || day > 31 || hour < 0 || hour > 23 || minute < 0 || minute > 59) return null;
        int second = 0;
        int cursor = 12;
        int parsedSecond = Number(12, 2);
        if (parsedSecond >= 0)
        {
            second = Math.Min(parsedSecond, 59);
            cursor = 14;
        }

        int offsetSeconds = 0;
        while (cursor < text.Length && text[cursor] == ' ') cursor++;
        if (cursor < text.Length && (text[cursor] == '+' || text[cursor] == '-'))
        {
            bool negative = text[cursor] == '-';
            int hours = Number(cursor + 1, 2), minutes = Number(cursor + 3, 2);
            if (hours >= 0 && minutes >= 0) offsetSeconds = (hours * 3600 + minutes * 60) * (negative ? -1 : 1);
        }

        try
        {
            if (day > DateTime.DaysInMonth(year, month)) return null;
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).AddSeconds(-offsetSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>Streaming XMLTV reader. Only keeps programmes inside the window, and only for wanted channels.</summary>
public static class XmltvParser
{
    public static EpgData ParseFile(string path, ISet<string> wantedIds, ISet<string> wantedNames, DateTime windowStart, DateTime windowEnd)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Parse(file, wantedIds, wantedNames, windowStart, windowEnd);
    }

    public static EpgData Parse(Stream input, ISet<string> wantedIds, ISet<string> wantedNames, DateTime windowStart, DateTime windowEnd)
    {
        Stream source = input;
        // Guides are very often gzip files, whatever their name says.
        if (input.CanSeek)
        {
            int first = input.ReadByte(), second = input.ReadByte();
            input.Seek(0, SeekOrigin.Begin);
            if (first == 0x1f && second == 0x8b) source = new GZipStream(input, CompressionMode.Decompress);
        }

        var result = new EpgData();
        var accepted = new HashSet<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false
        };

        using (XmlReader reader = XmlReader.Create(source, settings))
        {
            try
            {
                reader.MoveToContent();
                while (!reader.EOF)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "channel")
                    {
                        string id = (reader.GetAttribute("id") ?? "").ToLowerInvariant();
                        var element = (XElement)XNode.ReadFrom(reader);
                        if (id.Length == 0) continue;
                        if (wantedIds.Contains(id)) accepted.Add(id);
                        foreach (XElement display in element.Elements("display-name"))
                        {
                            string name = display.Value.Trim().ToLowerInvariant();
                            if (name.Length == 0) continue;
                            result.Names.TryAdd(name, id);
                            if (wantedNames.Contains(name)) accepted.Add(id);
                        }
                        continue;
                    }
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
                    {
                        string channel = (reader.GetAttribute("channel") ?? "").ToLowerInvariant();
                        DateTime? start = XmltvDate.Parse(reader.GetAttribute("start"));
                        DateTime? stop = XmltvDate.Parse(reader.GetAttribute("stop"));
                        bool wanted = start != null && (accepted.Contains(channel) || wantedIds.Contains(channel));
                        if (wanted)
                        {
                            stop ??= start.Value.AddMinutes(30);
                            wanted = stop > windowStart && start < windowEnd;
                        }
                        if (!wanted)
                        {
                            reader.Skip();
                            continue;
                        }
                        var element = (XElement)XNode.ReadFrom(reader);
                        string title = element.Element("title")?.Value.Trim() ?? "";
                        string desc = element.Element("desc")?.Value.Trim() ?? "";
                        if (desc.Length > 600) desc = desc[..600] + "…";
                        if (!result.Programmes.TryGetValue(channel, out var list))
                        {
                            list = new List<Programme>();
                            result.Programmes[channel] = list;
                        }
                        list.Add(new Programme { Start = start.Value, Stop = stop.Value, Title = title.Length == 0 ? "No title" : title, Desc = desc });
                        continue;
                    }
                    reader.Read();
                }
            }
            catch (XmlException)
            {
                // A truncated or slightly malformed guide still yields everything read so far.
            }
            catch (InvalidDataException)
            {
            }
        }

        foreach (string key in result.Programmes.Keys.ToList())
        {
            result.Programmes[key] = result.Programmes[key].OrderBy(p => p.Start).ToList();
        }
        result.Updated = DateTime.UtcNow;
        return result;
    }
}
