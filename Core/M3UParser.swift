import Foundation

/// Parses extended M3U playlists (the "#EXTINF" flavour used by IPTV providers).
enum M3UParser {

    static func parse(_ text: String, playlistID: UUID) -> PlaylistContent {
        var content = PlaylistContent()
        let prefix = String(playlistID.uuidString.prefix(8))

        var pendingAttributes: [String: String] = [:]
        var pendingName = ""
        var pendingGroup = ""
        var pendingUserAgent = ""
        var hasPending = false
        var nextNumber = 1
        var seriesIndex: [String: Int] = [:]
        var seenIDs = Set<String>()

        text.enumerateLines { rawLine, _ in
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { return }

            if line.hasPrefix("#EXTM3U") {
                let attributes = parseAttributes(line)
                for key in ["url-tvg", "x-tvg-url", "tvg-url"] {
                    if let value = attributes[key] {
                        for part in value.split(separator: ",") {
                            let url = part.trimmingCharacters(in: .whitespaces)
                            if !url.isEmpty && !content.epgURLs.contains(url) { content.epgURLs.append(url) }
                        }
                    }
                }
                return
            }
            if line.hasPrefix("#EXTINF") {
                let (header, name) = splitInfoLine(line)
                pendingAttributes = parseAttributes(header)
                pendingName = name
                pendingGroup = ""
                pendingUserAgent = ""
                hasPending = true
                return
            }
            if line.hasPrefix("#EXTGRP:") {
                pendingGroup = String(line.dropFirst("#EXTGRP:".count)).trimmingCharacters(in: .whitespaces)
                return
            }
            if line.hasPrefix("#EXTVLCOPT:") {
                let option = String(line.dropFirst("#EXTVLCOPT:".count))
                if option.lowercased().hasPrefix("http-user-agent=") {
                    pendingUserAgent = String(option.dropFirst("http-user-agent=".count))
                }
                return
            }
            if line.hasPrefix("#") { return }

            // Anything else is a stream address.
            defer {
                hasPending = false
                pendingAttributes = [:]
            }
            let address = line
            guard address.contains("://") else { return }

            var name = pendingName
            if name.isEmpty { name = pendingAttributes["tvg-name"] ?? "" }
            if name.isEmpty { name = URL(string: address)?.lastPathComponent ?? address }

            var group = pendingAttributes["group-title"] ?? ""
            if group.isEmpty { group = pendingGroup }
            if group.isEmpty { group = ChannelGroup.uncategorized }

            let logo = pendingAttributes["tvg-logo"] ?? pendingAttributes["logo"] ?? ""
            var id = prefix + ":" + StableHash.hex(address)
            if seenIDs.contains(id) { id += "-" + String(seenIDs.count) }
            seenIDs.insert(id)

            switch classify(address: address, name: name) {
            case .live:
                var number = nextNumber
                if let text = pendingAttributes["tvg-chno"] ?? pendingAttributes["channel-number"],
                   let parsed = Int(text.trimmingCharacters(in: .whitespaces)), parsed > 0 {
                    number = parsed
                }
                nextNumber = max(nextNumber, number) + 1
                let channel = Channel(
                    id: id,
                    playlistID: playlistID,
                    number: number,
                    name: name,
                    group: group,
                    logo: logo,
                    url: address,
                    epgID: (pendingAttributes["tvg-id"] ?? "").lowercased(),
                    catchup: catchupInfo(pendingAttributes),
                    xtreamID: nil,
                    userAgent: pendingUserAgent
                )
                content.channels.append(channel)

            case .movie:
                content.movies.append(Movie(id: id, playlistID: playlistID, name: name, group: group, poster: logo, url: address))

            case .episode(let show, let season, let number):
                let key = group + "\u{1}" + show.lowercased()
                let episode = Episode(id: id, season: season, number: number, title: name, url: address, image: logo)
                if let index = seriesIndex[key] {
                    content.series[index].episodes.append(episode)
                } else {
                    seriesIndex[key] = content.series.count
                    content.series.append(Series(
                        id: prefix + ":s:" + StableHash.hex(key),
                        playlistID: playlistID,
                        name: show,
                        group: group,
                        poster: logo,
                        episodes: [episode]
                    ))
                }
            }
            _ = hasPending
        }

        for index in content.series.indices {
            content.series[index].episodes.sort { ($0.season, $0.number) < ($1.season, $1.number) }
        }
        return content
    }

    // MARK: - Line helpers

    /// Splits `#EXTINF:-1 key="value",Display name` into its attribute part and its display name.
    /// The separator is the first comma that is not inside a quoted value.
    static func splitInfoLine(_ line: String) -> (header: String, name: String) {
        var inQuotes = false
        var index = line.startIndex
        while index < line.endIndex {
            let character = line[index]
            if character == "\"" {
                inQuotes.toggle()
            } else if character == "," && !inQuotes {
                let header = String(line[line.startIndex..<index])
                let name = String(line[line.index(after: index)...]).trimmingCharacters(in: .whitespaces)
                return (header, name)
            }
            index = line.index(after: index)
        }
        return (line, "")
    }

    private static let attributeRegex = try? NSRegularExpression(pattern: "([A-Za-z0-9_-]+)=\"([^\"]*)\"")

    static func parseAttributes(_ header: String) -> [String: String] {
        guard let regex = attributeRegex else { return [:] }
        var result: [String: String] = [:]
        let source = header as NSString
        let range = NSRange(location: 0, length: source.length)
        for match in regex.matches(in: header, range: range) where match.numberOfRanges == 3 {
            let key = source.substring(with: match.range(at: 1)).lowercased()
            let value = source.substring(with: match.range(at: 2)).trimmingCharacters(in: .whitespaces)
            result[key] = value
        }
        return result
    }

    static func catchupInfo(_ attributes: [String: String]) -> CatchupInfo? {
        let kind = (attributes["catchup"] ?? attributes["catchup-type"] ?? "").lowercased()
        let dayText = attributes["catchup-days"] ?? attributes["timeshift"] ?? attributes["tvg-rec"] ?? ""
        var days = Int(dayText) ?? 0
        let source = attributes["catchup-source"] ?? ""
        if kind.isEmpty && days <= 0 { return nil }
        if days <= 0 { days = 7 }

        let mode: CatchupInfo.Mode
        switch kind {
        case "xc": mode = .xtream
        case "append": mode = .append
        case "default": mode = source.isEmpty ? .shift : .template
        case "flussonic", "fs", "flussonic-hls", "flussonic-ts": mode = .flussonic
        default: mode = source.isEmpty ? .shift : .template
        }
        return CatchupInfo(mode: mode, days: days, source: source)
    }

    // MARK: - Classification

    enum ItemKind: Equatable {
        case live
        case movie
        case episode(show: String, season: Int, number: Int)
    }

    private static let videoExtensions: Set<String> = ["mp4", "mkv", "avi", "mov", "m4v", "wmv", "flv", "mpg", "mpeg", "webm"]
    private static let episodeRegex = try? NSRegularExpression(
        pattern: "^(.*?)[\\s._-]+S(\\d{1,2})[\\s._-]*E(\\d{1,3})",
        options: [.caseInsensitive]
    )

    static func classify(address: String, name: String) -> ItemKind {
        let lower = address.lowercased()
        var path = lower
        if let question = path.firstIndex(of: "?") { path = String(path[path.startIndex..<question]) }
        var fileExtension = ""
        if let dot = path.lastIndex(of: "."), let slash = path.lastIndex(of: "/"), dot > slash {
            fileExtension = String(path[path.index(after: dot)...])
        }
        let looksLikeFile = videoExtensions.contains(fileExtension)
        let isSeriesPath = path.contains("/series/")
        let isMoviePath = path.contains("/movie/")
        guard looksLikeFile || isSeriesPath || isMoviePath else { return .live }

        if let episode = episodeParts(name) { return episode }
        return .movie
    }

    static func episodeParts(_ name: String) -> ItemKind? {
        guard let regex = episodeRegex else { return nil }
        let source = name as NSString
        guard let match = regex.firstMatch(in: name, range: NSRange(location: 0, length: source.length)),
              match.numberOfRanges == 4 else { return nil }
        let show = source.substring(with: match.range(at: 1)).trimmingCharacters(in: .whitespaces)
        let season = Int(source.substring(with: match.range(at: 2))) ?? 1
        let number = Int(source.substring(with: match.range(at: 3))) ?? 1
        if show.isEmpty { return nil }
        return .episode(show: show, season: season, number: number)
    }
}
