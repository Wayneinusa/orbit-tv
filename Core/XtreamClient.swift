import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Xtream servers are loose about JSON types (numbers arrive as strings and the other way round),
/// so values are read through this forgiving wrapper instead of Codable.
struct LooseJSON {
    let value: Any?

    init(_ value: Any?) { self.value = value }

    subscript(key: String) -> LooseJSON {
        LooseJSON((value as? [String: Any])?[key])
    }

    var string: String {
        if let text = value as? String { return text }
        if let number = value as? NSNumber { return number.stringValue }
        return ""
    }

    var int: Int? {
        if let number = value as? NSNumber { return number.intValue }
        if let text = value as? String {
            if let parsed = Int(text) { return parsed }
            if let parsed = Double(text) { return Int(parsed) }
        }
        return nil
    }

    var array: [LooseJSON] {
        if let items = value as? [Any] { return items.map { LooseJSON($0) } }
        return []
    }

    var dictionary: [String: LooseJSON] {
        guard let items = value as? [String: Any] else { return [:] }
        return items.mapValues { LooseJSON($0) }
    }
}

struct XtreamAccount: Sendable {
    var status: String = ""
    var expires: Date?
    var activeConnections: Int = 0
    var maxConnections: Int = 0
    var timeZone: String = ""
}

struct XtreamVODInfo: Sendable {
    var plot: String = ""
    var year: String = ""
    var genre: String = ""
    var duration: String = ""
    var rating: String = ""
}

struct XtreamClient: Sendable {
    let server: String
    let username: String
    let password: String
    var userAgent: String = ""
    /// "ts" or "m3u8"
    var liveFormat: String = "ts"

    init(server: String, username: String, password: String, userAgent: String = "", liveFormat: String = "ts") {
        self.server = XtreamClient.normalize(server)
        self.username = username
        self.password = password
        self.userAgent = userAgent
        self.liveFormat = liveFormat
    }

    static func normalize(_ server: String) -> String {
        var text = server.trimmingCharacters(in: .whitespacesAndNewlines)
        if !text.lowercased().hasPrefix("http://") && !text.lowercased().hasPrefix("https://") {
            text = "http://" + text
        }
        // People often paste the full player_api / get.php link.
        for marker in ["/player_api.php", "/get.php", "/xmltv.php"] {
            if let range = text.range(of: marker) { text = String(text[text.startIndex..<range.lowerBound]) }
        }
        while text.hasSuffix("/") { text.removeLast() }
        return text
    }

    // MARK: Addresses

    private func encoded(_ text: String) -> String {
        text.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? text
    }

    func apiURL(action: String?, extra: [String: String] = [:]) -> URL? {
        guard var components = URLComponents(string: server + "/player_api.php") else { return nil }
        var items = [URLQueryItem(name: "username", value: username), URLQueryItem(name: "password", value: password)]
        if let action = action { items.append(URLQueryItem(name: "action", value: action)) }
        for (key, value) in extra { items.append(URLQueryItem(name: key, value: value)) }
        components.queryItems = items
        return components.url
    }

    var xmltvURL: String {
        var components = URLComponents(string: server + "/xmltv.php")
        components?.queryItems = [URLQueryItem(name: "username", value: username), URLQueryItem(name: "password", value: password)]
        return components?.url?.absoluteString ?? ""
    }

    func liveURL(streamID: Int) -> String {
        "\(server)/live/\(encoded(username))/\(encoded(password))/\(streamID).\(liveFormat)"
    }

    func movieURL(streamID: Int, container: String) -> String {
        "\(server)/movie/\(encoded(username))/\(encoded(password))/\(streamID).\(container.isEmpty ? "mp4" : container)"
    }

    func episodeURL(episodeID: String, container: String) -> String {
        "\(server)/series/\(encoded(username))/\(encoded(password))/\(episodeID).\(container.isEmpty ? "mp4" : container)"
    }

    /// Catch-up ("timeshift") address. `start` must already be formatted in the server's time zone.
    func timeshiftURL(streamID: Int, startText: String, minutes: Int) -> String {
        "\(server)/timeshift/\(encoded(username))/\(encoded(password))/\(minutes)/\(startText)/\(streamID).ts"
    }

    // MARK: Requests

    private func fetch(action: String?, extra: [String: String] = [:]) async throws -> LooseJSON {
        guard let url = apiURL(action: action, extra: extra) else { throw CoreError("The server address is not valid.") }
        var request = URLRequest(url: url)
        request.timeoutInterval = 60
        if !userAgent.isEmpty { request.setValue(userAgent, forHTTPHeaderField: "User-Agent") }
        let (data, response) = try await URLSession.shared.data(for: request)
        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            throw CoreError("The server answered with error \(http.statusCode).")
        }
        guard let object = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else {
            throw CoreError("The server did not return valid data. Check the address, username and password.")
        }
        return LooseJSON(object)
    }

    func account() async throws -> XtreamAccount {
        let json = try await fetch(action: nil)
        let user = json["user_info"]
        if user.value == nil { throw CoreError("Login failed. Check the username and password.") }
        if let auth = user["auth"].int, auth == 0 { throw CoreError("Login failed. Check the username and password.") }
        var result = XtreamAccount()
        result.status = user["status"].string
        if let seconds = user["exp_date"].int, seconds > 0 { result.expires = Date(timeIntervalSince1970: TimeInterval(seconds)) }
        result.activeConnections = user["active_cons"].int ?? 0
        result.maxConnections = user["max_connections"].int ?? 0
        result.timeZone = json["server_info"]["timezone"].string
        return result
    }

    private func categories(action: String) async -> (names: [String: String], order: [String: Int]) {
        var names: [String: String] = [:]
        var order: [String: Int] = [:]
        if let json = try? await fetch(action: action) {
            for (index, item) in json.array.enumerated() {
                let id = item["category_id"].string
                names[id] = item["category_name"].string
                order[id] = index
            }
        }
        return (names, order)
    }

    /// Downloads live channels, movies and series lists.
    func loadAll(playlistID: UUID) async throws -> (content: PlaylistContent, timeZone: String) {
        let accountInfo = try await self.account()
        let prefix = String(playlistID.uuidString.prefix(8))
        var content = PlaylistContent()
        content.epgURLs = [xmltvURL]

        // Live
        let liveCategories = await categories(action: "get_live_categories")
        let live = try await fetch(action: "get_live_streams")
        var channels: [(order: Int, channel: Channel)] = []
        for (index, item) in live.array.enumerated() {
            guard let streamID = item["stream_id"].int else { continue }
            let categoryID = item["category_id"].string
            let archiveDays = item["tv_archive_duration"].int ?? 0
            let hasArchive = (item["tv_archive"].int ?? 0) == 1
            let channel = Channel(
                id: "\(prefix):l:\(streamID)",
                playlistID: playlistID,
                number: item["num"].int ?? (index + 1),
                name: item["name"].string,
                group: liveCategories.names[categoryID] ?? ChannelGroup.uncategorized,
                logo: item["stream_icon"].string,
                url: liveURL(streamID: streamID),
                epgID: item["epg_channel_id"].string.lowercased(),
                catchup: hasArchive ? CatchupInfo(mode: .xtream, days: max(1, archiveDays)) : nil,
                xtreamID: streamID,
                userAgent: userAgent
            )
            channels.append((liveCategories.order[categoryID] ?? Int.max, channel))
        }
        // Keep the provider's category order, then the provider's numbering.
        content.channels = channels.enumerated()
            .sorted { lhs, rhs in
                if lhs.element.order != rhs.element.order { return lhs.element.order < rhs.element.order }
                return lhs.offset < rhs.offset
            }
            .map { $0.element.channel }
        for index in content.channels.indices { content.channels[index].number = index + 1 }

        // Movies (a provider may not offer any; that is not an error)
        let vodCategories = await categories(action: "get_vod_categories")
        if let vod = try? await fetch(action: "get_vod_streams") {
            for item in vod.array {
                guard let streamID = item["stream_id"].int else { continue }
                content.movies.append(Movie(
                    id: "\(prefix):m:\(streamID)",
                    playlistID: playlistID,
                    name: item["name"].string,
                    group: vodCategories.names[item["category_id"].string] ?? ChannelGroup.uncategorized,
                    poster: item["stream_icon"].string,
                    url: movieURL(streamID: streamID, container: item["container_extension"].string),
                    rating: item["rating"].string,
                    year: item["year"].string,
                    plot: "",
                    xtreamID: streamID
                ))
            }
        }

        // Series
        let seriesCategories = await categories(action: "get_series_categories")
        if let shows = try? await fetch(action: "get_series") {
            for item in shows.array {
                guard let seriesID = item["series_id"].int else { continue }
                content.series.append(Series(
                    id: "\(prefix):s:\(seriesID)",
                    playlistID: playlistID,
                    name: item["name"].string,
                    group: seriesCategories.names[item["category_id"].string] ?? ChannelGroup.uncategorized,
                    poster: item["cover"].string,
                    plot: item["plot"].string,
                    rating: item["rating"].string,
                    year: String(item["releaseDate"].string.prefix(4)),
                    xtreamID: seriesID
                ))
            }
        }
        return (content, accountInfo.timeZone)
    }

    func episodes(seriesID: Int, idPrefix: String) async throws -> [Episode] {
        let json = try await fetch(action: "get_series_info", extra: ["series_id": String(seriesID)])
        var result: [Episode] = []

        func add(_ item: LooseJSON, fallbackSeason: Int) {
            let episodeID = item["id"].string
            guard !episodeID.isEmpty else { return }
            let number = item["episode_num"].int ?? (result.count + 1)
            let season = item["season"].int ?? fallbackSeason
            var title = item["title"].string
            if title.isEmpty { title = "Episode \(number)" }
            result.append(Episode(
                id: "\(idPrefix):e:\(episodeID)",
                season: season,
                number: number,
                title: title,
                url: episodeURL(episodeID: episodeID, container: item["container_extension"].string),
                plot: item["info"]["plot"].string,
                image: item["info"]["movie_image"].string
            ))
        }

        let episodes = json["episodes"]
        if episodes.value is [String: Any] {
            for (seasonKey, list) in episodes.dictionary {
                for item in list.array { add(item, fallbackSeason: Int(seasonKey) ?? 1) }
            }
        } else {
            // Some panels return an array of seasons instead of a dictionary.
            for (index, list) in episodes.array.enumerated() {
                for item in list.array { add(item, fallbackSeason: index + 1) }
            }
        }
        result.sort { ($0.season, $0.number) < ($1.season, $1.number) }
        return result
    }

    func movieInfo(streamID: Int) async throws -> XtreamVODInfo {
        let json = try await fetch(action: "get_vod_info", extra: ["vod_id": String(streamID)])
        let info = json["info"]
        var result = XtreamVODInfo()
        result.plot = info["plot"].string.isEmpty ? info["description"].string : info["plot"].string
        result.year = String(info["releasedate"].string.prefix(4))
        result.genre = info["genre"].string
        result.duration = info["duration"].string
        result.rating = info["rating"].string
        return result
    }
}
