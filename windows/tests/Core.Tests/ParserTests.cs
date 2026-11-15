using System.IO.Compression;
using System.Text;

namespace SuperiorIptv.Tests;

public class ParserTests
{
    [Fact]
    public void ParsesChannelsMoviesAndSeries()
    {
        string text = string.Join("\n", new[]
        {
            "#EXTM3U url-tvg=\"http://example.com/guide.xml.gz\"",
            "#EXTINF:-1 tvg-id=\"News.One\" tvg-logo=\"http://example.com/a.png\" group-title=\"News, World\" catchup=\"shift\" catchup-days=\"3\",News One, HD",
            "http://example.com/live/1.ts",
            "#EXTINF:-1 tvg-chno=\"50\" group-title=\"Sports\",Sports 50",
            "#EXTVLCOPT:http-user-agent=TestAgent",
            "http://example.com/live/2.m3u8",
            "#EXTINF:-1 group-title=\"Films\",A Film (2020)",
            "http://example.com/movie/u/p/10.mkv",
            "#EXTINF:-1 group-title=\"Shows\",Some Show S01 E02",
            "http://example.com/series/u/p/20.mp4",
            "#EXTINF:-1 group-title=\"Shows\",Some Show S01 E01",
            "http://example.com/series/u/p/21.mp4"
        });
        PlaylistContent content = M3UParser.Parse(text, "abcdefgh12345678");

        Assert.Equal(new[] { "http://example.com/guide.xml.gz" }, content.EpgUrls);
        Assert.Equal(2, content.Channels.Count);
        Assert.Equal("News One, HD", content.Channels[0].Name);
        Assert.Equal("News, World", content.Channels[0].Group);
        Assert.Equal("news.one", content.Channels[0].EpgId);
        Assert.Equal(CatchupMode.Shift, content.Channels[0].Catchup.Mode);
        Assert.Equal(3, content.Channels[0].Catchup.Days);
        Assert.Equal(50, content.Channels[1].Number);
        Assert.Equal("TestAgent", content.Channels[1].UserAgent);
        Assert.Single(content.Movies);
        Assert.Single(content.Series);
        Assert.Equal("Some Show", content.Series[0].Name);
        Assert.Equal(new[] { 1, 2 }, content.Series[0].Episodes.Select(e => e.Number).ToArray());
    }

    [Fact]
    public void ChannelIdsAreStableBetweenRuns()
    {
        string text = "#EXTM3U\n#EXTINF:-1,One\nhttp://example.com/1.ts\n";
        string first = M3UParser.Parse(text, "abcdefgh").Channels[0].Id;
        string second = M3UParser.Parse(text, "abcdefgh").Channels[0].Id;
        Assert.Equal(first, second);
    }

    [Fact]
    public void XmltvDates()
    {
        DateTime noon = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc), XmltvDate.Parse("19700101000000 +0000"));
        Assert.Equal(noon, XmltvDate.Parse("20240229120000 +0000"));
        Assert.Equal(noon, XmltvDate.Parse("20240229130000 +0100"));
        Assert.Equal(noon, XmltvDate.Parse("20240229070000 -0500"));
        Assert.Equal(noon, XmltvDate.Parse("202402291200"));
        Assert.Null(XmltvDate.Parse("garbage"));
        Assert.Null(XmltvDate.Parse("20230230120000 +0000"));
    }

    private const string GuideXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE tv SYSTEM ""xmltv.dtd"">
<tv>
  <channel id=""News.One""><display-name>News One</display-name></channel>
  <channel id=""other""><display-name>Sports 50</display-name></channel>
  <channel id=""unused""><display-name>Unused</display-name></channel>
  <programme start=""20240229120000 +0000"" stop=""20240229130000 +0000"" channel=""News.One""><title>Midday</title><desc>News &amp; weather</desc></programme>
  <programme start=""20240229130000 +0000"" stop=""20240229140000 +0000"" channel=""News.One""><title>Afternoon</title></programme>
  <programme start=""20240229120000 +0000"" stop=""20240229140000 +0000"" channel=""other""><title>Match</title></programme>
  <programme start=""20240229120000 +0000"" stop=""20240229140000 +0000"" channel=""unused""><title>Skipped</title></programme>
  <programme start=""20240310120000 +0000"" stop=""20240310140000 +0000"" channel=""News.One""><title>Too late</title></programme>
</tv>";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void XmltvParsingAndLookup(bool gzip)
    {
        DateTime noon = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        byte[] raw = Encoding.UTF8.GetBytes(GuideXml);
        if (gzip)
        {
            using var packed = new MemoryStream();
            using (var zip = new GZipStream(packed, CompressionLevel.Fastest, true)) zip.Write(raw, 0, raw.Length);
            raw = packed.ToArray();
        }
        EpgData data = XmltvParser.Parse(new MemoryStream(raw), new HashSet<string> { "news.one" }, new HashSet<string> { "sports 50" },
            noon.AddDays(-1), noon.AddDays(1));

        Assert.Equal(2, data.Programmes["news.one"].Count);
        Assert.Single(data.Programmes["other"]);
        Assert.False(data.Programmes.ContainsKey("unused"));

        var news = new Channel { Id = "a", Name = "Whatever", EpgId = "news.one" };
        var sports = new Channel { Id = "b", Name = "Sports 50", EpgId = "" };
        Assert.Equal("Midday", data.Current(news, noon.AddMinutes(1)).Title);
        Assert.Equal("News & weather", data.Current(news, noon.AddMinutes(1)).Desc);
        Assert.Equal("Afternoon", data.Next(news, noon.AddMinutes(1)).Title);
        Assert.Equal("Match", data.Current(sports, noon.AddMinutes(1)).Title);
        Assert.Null(data.Current(news, noon.AddHours(3)));
        Assert.Equal(2, data.Between(news, noon, noon.AddHours(2)).Count);
    }

    private static EpgData Listings(string channelId, params string[] titles)
    {
        var data = new EpgData();
        var list = new List<Programme>();
        DateTime at = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (string title in titles)
        {
            list.Add(new Programme { Start = at, Stop = at.AddHours(1), Title = title });
            at = at.AddHours(1);
        }
        data.Programmes[channelId] = list;
        return data;
    }

    [Fact]
    public void AdditionalGuideFillsInAndCanBeChosenPerChannel()
    {
        DateTime noon = new DateTime(2026, 3, 1, 12, 30, 0, DateTimeKind.Utc);
        var sources = new Dictionary<string, EpgData>
        {
            ["http://primary"] = Listings("one", "Primary show"),
            ["http://extra"] = new EpgData()
        };
        sources["http://extra"].Programmes["one"] = Listings("one", "Extra show").Programmes["one"];
        sources["http://extra"].Programmes["two"] = Listings("two", "Only in extra").Programmes["two"];
        var playlists = new Dictionary<string, PlaylistGuide>
        {
            ["p"] = new PlaylistGuide { Primary = { "http://primary" }, Additional = { "http://extra" } }
        };
        var one = new Channel { Id = "c1", PlaylistId = "p", Name = "One", EpgId = "one" };
        var two = new Channel { Id = "c2", PlaylistId = "p", Name = "Two", EpgId = "two" };
        var three = new Channel { Id = "c3", PlaylistId = "p", Name = "Three", EpgId = "three" };
        var stranger = new Channel { Id = "c4", PlaylistId = "other", Name = "Stranger", EpgId = "one" };

        var book = new GuideBook(sources, playlists, new Dictionary<string, GuideChoice>(), null);
        Assert.True(book.HasListings);
        Assert.Equal("Primary show", book.Current(one, noon).Title);      // the primary guide wins when it has listings
        Assert.False(book.UsesAdditional(one));
        Assert.Equal("Only in extra", book.Current(two, noon).Title);     // the additional guide fills the gap
        Assert.True(book.UsesAdditional(two));
        Assert.Null(book.Current(three, noon));                           // neither has it
        Assert.Null(book.Current(stranger, noon));                        // a channel of a playlist without guides
        Assert.True(book.HasAdditional(one));
        Assert.Equal(GuideChoice.Automatic, book.ChoiceFor(one));

        var choices = new Dictionary<string, GuideChoice> { ["c1"] = GuideChoice.Additional, ["c2"] = GuideChoice.Primary };
        book = new GuideBook(sources, playlists, choices, null);
        Assert.Equal("Extra show", book.Current(one, noon).Title);        // chosen for this channel
        Assert.Null(book.Current(two, noon));                             // primary only, and the primary has nothing
        Assert.Single(book.Between(one, noon, noon.AddMinutes(10)));

        choices = new Dictionary<string, GuideChoice> { ["c1"] = GuideChoice.None };
        book = new GuideBook(sources, playlists, choices, null);
        Assert.Empty(book.ListFor(one));
    }

    [Fact]
    public void TimesAreWrittenInTwelveHourFormat()
    {
        var afternoon = new DateTime(2026, 11, 14, 16, 36, 0);
        var morning = new DateTime(2026, 11, 14, 0, 5, 0);
        TimeText.TwentyFourHour = false;
        Assert.Equal("4:36 PM", TimeText.Time(afternoon));
        Assert.Equal("12:05 AM", TimeText.Time(morning));
        Assert.Equal("Sat 4:36 PM", TimeText.DayTime(afternoon));
        Assert.Equal("Sat, Nov 14, 4:36 PM", TimeText.DateAndTime(afternoon));
        TimeText.TwentyFourHour = true;
        Assert.Equal("16:36", TimeText.Time(afternoon));
        TimeText.TwentyFourHour = false;
    }

    [Fact]
    public void CatchupAddresses()
    {
        DateTime start = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        DateTime stop = start.AddHours(1);
        DateTime now = start.AddHours(2);
        var channel = new Channel
        {
            Id = "a", Name = "n", Url = "http://host/ch/index.m3u8?token=1",
            Catchup = new CatchupInfo { Mode = CatchupMode.Shift, Days = 3 }
        };
        Assert.Equal("http://host/ch/index.m3u8?token=1&utc=1709208000&lutc=1709215200", Catchup.Url(channel, null, start, stop, now));

        channel.Catchup = new CatchupInfo { Mode = CatchupMode.Flussonic, Days = 3 };
        Assert.Equal("http://host/ch/index-1709208000-3600.m3u8?token=1", Catchup.Url(channel, null, start, stop, now));

        channel.Catchup = new CatchupInfo { Mode = CatchupMode.Template, Days = 3, Source = "http://host/a?s=${start}&d={duration}&h={H}" };
        Assert.Equal("http://host/a?s=1709208000&d=3600&h=12", Catchup.Url(channel, null, start, stop, now));

        var playlist = new Playlist { Kind = PlaylistKind.Xtream, Server = "host:8080/", Username = "u", Password = "p", ServerTimeZone = "UTC" };
        channel.Catchup = new CatchupInfo { Mode = CatchupMode.Xtream, Days = 3 };
        channel.XtreamId = 77;
        Assert.Equal("http://host:8080/timeshift/u/p/60/2024-02-29:12-00/77.ts", Catchup.Url(channel, playlist, start, stop, now));

        var programme = new Programme { Start = start, Stop = stop, Title = "t" };
        Assert.True(Catchup.IsAvailable(channel, programme, now));
        Assert.False(Catchup.IsAvailable(channel, programme, start.AddDays(5)));
    }

    [Fact]
    public void HlsPlaylists()
    {
        var baseUrl = new Uri("http://host/live/master.m3u8");
        string master = "#EXTM3U\n#EXT-X-STREAM-INF:AVERAGE-BANDWIDTH=700000,BANDWIDTH=800000,RESOLUTION=640x360\nlow/index.m3u8\n" +
                        "#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720\nhttp://cdn/high/index.m3u8\n";
        HlsPlaylist parsedMaster = HlsPlaylist.Parse(master, baseUrl);
        Assert.True(parsedMaster.IsMaster);
        Assert.Equal(2, parsedMaster.Variants.Count);
        Assert.Equal(800000, parsedMaster.Variants[0].Bandwidth);
        Assert.Equal("http://host/live/low/index.m3u8", parsedMaster.Variants[0].Url.AbsoluteUri);
        Assert.Equal("http://cdn/high/index.m3u8", parsedMaster.Variants.OrderByDescending(v => v.Bandwidth).First().Url.AbsoluteUri);

        string media = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:120\n#EXTINF:4.0,\nseg120.ts\n#EXTINF:4.0,\nseg121.ts\n";
        HlsPlaylist parsedMedia = HlsPlaylist.Parse(media, baseUrl);
        Assert.False(parsedMedia.IsMaster);
        Assert.Equal(4, parsedMedia.TargetDuration);
        Assert.Equal(120, parsedMedia.MediaSequence);
        Assert.Equal(new[] { "http://host/live/seg120.ts", "http://host/live/seg121.ts" }, parsedMedia.Segments.Select(s => s.AbsoluteUri).ToArray());
        Assert.False(parsedMedia.EndList);
        Assert.False(parsedMedia.Encrypted);
    }

    [Fact]
    public void FileNamesAreSafeForWindows()
    {
        string name = Storage.SafeFileName("What? A \"Movie\": Part 1/2 <final>.");
        Assert.DoesNotContain(name, c => "<>:\"/\\|?*".Contains(c));
        Assert.False(name.EndsWith('.'));
        Assert.Equal("video", Storage.SafeFileName("???"));
        Assert.Equal(".mkv", DownloadManager.ExtensionOf("http://host/movie/u/p/10.mkv?token=abc"));
        Assert.Equal(".mp4", DownloadManager.ExtensionOf("http://host/movie/u/p/10"));
    }
}
