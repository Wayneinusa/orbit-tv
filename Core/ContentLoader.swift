import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Downloads a playlist and turns it into channels, movies and series.
enum ContentLoader {
    static let defaultUserAgent = "OrbitTV/1.0"

    static func load(playlist: Playlist, liveFormat: String) async throws -> (content: PlaylistContent, timeZone: String) {
        switch playlist.source {
        case .xtream(let server, let username, let password):
            let client = XtreamClient(server: server, username: username, password: password,
                                      userAgent: playlist.userAgent, liveFormat: liveFormat)
            let loaded = try await client.loadAll(playlistID: playlist.id)
            return (loaded.content, loaded.timeZone)

        case .m3u(let address):
            let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let url = URL(string: trimmed), url.scheme != nil else {
                throw CoreError("The playlist address is not valid.")
            }
            var request = URLRequest(url: url)
            request.timeoutInterval = 120
            request.setValue(playlist.userAgent.isEmpty ? defaultUserAgent : playlist.userAgent, forHTTPHeaderField: "User-Agent")
            let (data, response) = try await URLSession.shared.data(for: request)
            if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                throw CoreError("The playlist server answered with error \(http.statusCode).")
            }
            let text = String(data: data, encoding: .utf8) ?? String(decoding: data, as: UTF8.self)
            guard text.contains("#EXTINF") else {
                throw CoreError("That address did not return an M3U playlist.")
            }
            let playlistID = playlist.id
            let userAgent = playlist.userAgent
            let content = await Task.detached(priority: .userInitiated) { () -> PlaylistContent in
                var parsed = M3UParser.parse(text, playlistID: playlistID)
                if !userAgent.isEmpty {
                    for index in parsed.channels.indices where parsed.channels[index].userAgent.isEmpty {
                        parsed.channels[index].userAgent = userAgent
                    }
                }
                return parsed
            }.value
            return (content, "")
        }
    }
}

// MARK: - Recordings

struct Recording: Codable, Hashable, Identifiable, Sendable {
    enum Status: String, Codable, Sendable {
        case scheduled, recording, converting, completed, failed
    }

    var id: UUID = UUID()
    var channelID: String
    var channelName: String
    var title: String
    var url: String
    var userAgent: String
    var start: Date
    var end: Date
    var status: Status = .scheduled
    /// File name without extension, inside the recordings folder.
    var baseName: String
    var hasTS = false
    var hasMP4 = false
    var bytes: Int64 = 0
    var reconnects = 0
    var message = ""
    var convertProgress: Double = 0

    static func makeBaseName(title: String, channel: String, start: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd_HH-mm"
        let allowed = Set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
        // File names stay plain ASCII so they are safe to pass through every tool, including the converter.
        func clean(_ text: String) -> String {
            var result = ""
            var lastWasDash = false
            for character in text {
                if allowed.contains(character) {
                    result.append(character)
                    lastWasDash = false
                } else if !lastWasDash && !result.isEmpty {
                    result.append("-")
                    lastWasDash = true
                }
            }
            while result.hasSuffix("-") { result.removeLast() }
            return String(result.prefix(40))
        }
        var parts = [formatter.string(from: start)]
        let channelPart = clean(channel)
        let titlePart = clean(title)
        if !channelPart.isEmpty { parts.append(channelPart) }
        if !titlePart.isEmpty && titlePart != channelPart { parts.append(titlePart) }
        return parts.joined(separator: "_")
    }
}
