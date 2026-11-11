import XCTest
@testable import IPTVCore

final class CoreTests: XCTestCase {

    // MARK: M3U

    func testParsesChannelsMoviesAndSeries() {
        let text = """
        #EXTM3U url-tvg="http://example.com/guide.xml.gz"
        #EXTINF:-1 tvg-id="News.One" tvg-logo="http://example.com/a.png" group-title="News, World" catchup="shift" catchup-days="3",News One, HD
        http://example.com/live/1.ts
        #EXTINF:-1 tvg-chno="50" group-title="Sports",Sports 50
        #EXTVLCOPT:http-user-agent=TestAgent
        http://example.com/live/2.m3u8
        #EXTINF:-1 group-title="Films",A Film (2020)
        http://example.com/movie/u/p/10.mkv
        #EXTINF:-1 group-title="Shows",Some Show S01 E02
        http://example.com/series/u/p/20.mp4
        #EXTINF:-1 group-title="Shows",Some Show S01 E01
        http://example.com/series/u/p/21.mp4
        """
        let content = M3UParser.parse(text, playlistID: UUID())

        XCTAssertEqual(content.epgURLs, ["http://example.com/guide.xml.gz"])
        XCTAssertEqual(content.channels.count, 2)
        XCTAssertEqual(content.channels[0].name, "News One, HD")
        XCTAssertEqual(content.channels[0].group, "News, World")
        XCTAssertEqual(content.channels[0].epgID, "news.one")
        XCTAssertEqual(content.channels[0].catchup?.mode, .shift)
        XCTAssertEqual(content.channels[0].catchup?.days, 3)
        XCTAssertEqual(content.channels[1].number, 50)
        XCTAssertEqual(content.channels[1].userAgent, "TestAgent")
        XCTAssertEqual(content.movies.count, 1)
        XCTAssertEqual(content.series.count, 1)
        XCTAssertEqual(content.series[0].name, "Some Show")
        XCTAssertEqual(content.series[0].episodes.map { $0.number }, [1, 2])
    }

    func testChannelIDsAreStableBetweenRuns() {
        let id = UUID()
        let text = "#EXTM3U\n#EXTINF:-1,One\nhttp://example.com/1.ts\n"
        let first = M3UParser.parse(text, playlistID: id).channels.first?.id
        let second = M3UParser.parse(text, playlistID: id).channels.first?.id
        XCTAssertNotNil(first)
        XCTAssertEqual(first, second)
    }

    // MARK: XMLTV

    func testXMLTVDates() {
        XCTAssertEqual(XMLTVDate.parse("19700101000000 +0000")?.timeIntervalSince1970, 0)
        XCTAssertEqual(XMLTVDate.parse("20240229120000 +0000")?.timeIntervalSince1970, 1709208000)
        XCTAssertEqual(XMLTVDate.parse("20240229130000 +0100")?.timeIntervalSince1970, 1709208000)
        XCTAssertEqual(XMLTVDate.parse("20240229070000 -0500")?.timeIntervalSince1970, 1709208000)
        XCTAssertEqual(XMLTVDate.parse("202402291200")?.timeIntervalSince1970, 1709208000)
        XCTAssertNil(XMLTVDate.parse("garbage"))
    }

    func testXMLTVParsingAndLookup() {
        let xml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <tv>
          <channel id="News.One"><display-name>News One</display-name></channel>
          <channel id="other"><display-name>Sports 50</display-name></channel>
          <channel id="unused"><display-name>Unused</display-name></channel>
          <programme start="20240229120000 +0000" stop="20240229130000 +0000" channel="News.One"><title>Midday</title><desc>News &amp; weather</desc></programme>
          <programme start="20240229130000 +0000" stop="20240229140000 +0000" channel="News.One"><title>Afternoon</title></programme>
          <programme start="20240229120000 +0000" stop="20240229140000 +0000" channel="other"><title>Match</title></programme>
          <programme start="20240229120000 +0000" stop="20240229140000 +0000" channel="unused"><title>Skipped</title></programme>
        </tv>
        """
        let noon = Date(timeIntervalSince1970: 1709208000)
        let parser = XMLTVParser(wantedIDs: ["news.one"], wantedNames: ["sports 50"],
                                 windowStart: noon.addingTimeInterval(-86400), windowEnd: noon.addingTimeInterval(86400))
        let data = parser.parse(data: Data(xml.utf8))

        XCTAssertEqual(data.programmes["news.one"]?.count, 2)
        XCTAssertEqual(data.programmes["other"]?.count, 1)
        XCTAssertNil(data.programmes["unused"])

        let id = UUID()
        let news = Channel(id: "a", playlistID: id, number: 1, name: "Whatever", group: "g", logo: "", url: "http://x/1", epgID: "news.one")
        let sports = Channel(id: "b", playlistID: id, number: 2, name: "Sports 50", group: "g", logo: "", url: "http://x/2", epgID: "")
        XCTAssertEqual(data.current(for: news, at: noon.addingTimeInterval(60))?.title, "Midday")
        XCTAssertEqual(data.current(for: news, at: noon.addingTimeInterval(60))?.desc, "News & weather")
        XCTAssertEqual(data.next(for: news, after: noon.addingTimeInterval(60))?.title, "Afternoon")
        XCTAssertEqual(data.current(for: sports, at: noon.addingTimeInterval(60))?.title, "Match")
        XCTAssertNil(data.current(for: news, at: noon.addingTimeInterval(3 * 3600)))
    }

    // MARK: Catch-up

    func testCatchupAddresses() {
        let id = UUID()
        let start = Date(timeIntervalSince1970: 1709208000)
        let stop = start.addingTimeInterval(3600)
        let now = start.addingTimeInterval(7200)

        var channel = Channel(id: "a", playlistID: id, number: 1, name: "n", group: "g", logo: "",
                              url: "http://host/ch/index.m3u8?token=1", epgID: "",
                              catchup: CatchupInfo(mode: .shift, days: 3))
        XCTAssertEqual(Catchup.url(channel: channel, playlist: nil, start: start, stop: stop, now: now),
                       "http://host/ch/index.m3u8?token=1&utc=1709208000&lutc=1709215200")

        channel.catchup = CatchupInfo(mode: .flussonic, days: 3)
        XCTAssertEqual(Catchup.url(channel: channel, playlist: nil, start: start, stop: stop, now: now),
                       "http://host/ch/index-1709208000-3600.m3u8?token=1")

        channel.catchup = CatchupInfo(mode: .template, days: 3, source: "http://host/a?s=${start}&d={duration}&h={H}")
        XCTAssertEqual(Catchup.url(channel: channel, playlist: nil, start: start, stop: stop, now: now),
                       "http://host/a?s=1709208000&d=3600&h=12")

        let playlist = Playlist(name: "x", source: .xtream(server: "host:8080/", username: "u", password: "p"), serverTimeZone: "UTC")
        channel.catchup = CatchupInfo(mode: .xtream, days: 3)
        channel.xtreamID = 77
        XCTAssertEqual(Catchup.url(channel: channel, playlist: playlist, start: start, stop: stop, now: now),
                       "http://host:8080/timeshift/u/p/60/2024-02-29:12-00/77.ts")

        let programme = Programme(start: start, stop: stop, title: "t", desc: "")
        XCTAssertTrue(Catchup.isAvailable(channel: channel, programme: programme, now: now))
        XCTAssertFalse(Catchup.isAvailable(channel: channel, programme: programme, now: start.addingTimeInterval(5 * 86400)))
    }

    // MARK: HLS

    func testHLSPlaylists() {
        let base = URL(string: "http://host/live/master.m3u8")!
        let master = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360
        low/index.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720
        http://cdn/high/index.m3u8
        """
        let parsedMaster = HLSParser.parse(master, baseURL: base)
        XCTAssertTrue(parsedMaster.isMaster)
        XCTAssertEqual(parsedMaster.variants.count, 2)
        XCTAssertEqual(parsedMaster.variants[0].url.absoluteString, "http://host/live/low/index.m3u8")
        XCTAssertEqual(parsedMaster.variants.max { $0.bandwidth < $1.bandwidth }?.url.absoluteString, "http://cdn/high/index.m3u8")

        let media = """
        #EXTM3U
        #EXT-X-TARGETDURATION:4
        #EXT-X-MEDIA-SEQUENCE:120
        #EXTINF:4.0,
        seg120.ts
        #EXTINF:4.0,
        seg121.ts
        """
        let parsedMedia = HLSParser.parse(media, baseURL: base)
        XCTAssertFalse(parsedMedia.isMaster)
        XCTAssertEqual(parsedMedia.targetDuration, 4)
        XCTAssertEqual(parsedMedia.mediaSequence, 120)
        XCTAssertEqual(parsedMedia.segments.map { $0.lastPathComponent }, ["seg120.ts", "seg121.ts"])
        XCTAssertFalse(parsedMedia.endList)
        XCTAssertFalse(parsedMedia.encrypted)
    }

    // MARK: Recording names

    func testRecordingFileNamesArePlainASCII() {
        let name = Recording.makeBaseName(title: "Évening News: Live!", channel: "BBC One HD", start: Date(timeIntervalSince1970: 0))
        XCTAssertTrue(name.contains("BBC-One-HD"))
        XCTAssertTrue(name.unicodeScalars.allSatisfy { $0.isASCII })
        XCTAssertFalse(name.contains(" "))
    }
}
