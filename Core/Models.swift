import Foundation

// MARK: - Playlists

enum PlaylistSource: Codable, Hashable, Sendable {
    case m3u(url: String)
    case xtream(server: String, username: String, password: String)
}

struct Playlist: Codable, Hashable, Identifiable, Sendable {
    var id: UUID = UUID()
    var name: String
    var source: PlaylistSource
    /// Extra XMLTV guide address. Empty means "use whatever the playlist advertises".
    var epgURL: String = ""
    /// Custom HTTP User-Agent. Empty means the app default.
    var userAgent: String = ""
    /// Time zone identifier reported by an Xtream server (used for catch-up links).
    var serverTimeZone: String = ""
    var lastRefreshed: Date?

    var isXtream: Bool {
        if case .xtream = source { return true }
        return false
    }
}

// MARK: - Live channels

struct CatchupInfo: Codable, Hashable, Sendable {
    enum Mode: String, Codable, Sendable {
        case xtream      // Xtream Codes "timeshift" endpoint
        case shift       // ?utc=...&lutc=...
        case append      // catchup-source appended to the live address
        case template    // catchup-source is the complete address
        case flussonic   // Flussonic DVR naming
    }
    var mode: Mode
    var days: Int
    var source: String = ""
}

struct Channel: Codable, Hashable, Identifiable, Sendable {
    var id: String
    var playlistID: UUID
    var number: Int
    var name: String
    var group: String
    var logo: String
    var url: String
    /// Lower-cased XMLTV channel id ("" when the playlist gives none).
    var epgID: String
    var catchup: CatchupInfo?
    var xtreamID: Int?
    var userAgent: String = ""
}

// MARK: - Video on demand

struct Movie: Codable, Hashable, Identifiable, Sendable {
    var id: String
    var playlistID: UUID
    var name: String
    var group: String
    var poster: String
    var url: String
    var rating: String = ""
    var year: String = ""
    var plot: String = ""
    var xtreamID: Int?
}

struct Episode: Codable, Hashable, Identifiable, Sendable {
    var id: String
    var season: Int
    var number: Int
    var title: String
    var url: String
    var plot: String = ""
    var image: String = ""
}

struct Series: Codable, Hashable, Identifiable, Sendable {
    var id: String
    var playlistID: UUID
    var name: String
    var group: String
    var poster: String
    var plot: String = ""
    var rating: String = ""
    var year: String = ""
    var xtreamID: Int?
    /// Filled for M3U playlists; Xtream playlists load episodes on demand.
    var episodes: [Episode] = []
}

/// Everything one playlist contributes to the library.
struct PlaylistContent: Codable, Sendable {
    var channels: [Channel] = []
    var movies: [Movie] = []
    var series: [Series] = []
    var epgURLs: [String] = []
}

// MARK: - Programme guide

struct Programme: Codable, Hashable, Identifiable, Sendable {
    var start: Date
    var stop: Date
    var title: String
    var desc: String

    var id: Double { start.timeIntervalSince1970 }
    var duration: TimeInterval { stop.timeIntervalSince(start) }

    func isLive(at date: Date) -> Bool { start <= date && date < stop }

    func progress(at date: Date) -> Double {
        let total = duration
        guard total > 0 else { return 0 }
        return min(1, max(0, date.timeIntervalSince(start) / total))
    }
}

// MARK: - Helpers

enum StableHash {
    /// FNV-1a. Swift's own hashing is re-seeded on every launch, so it cannot be used for saved ids.
    static func hex(_ text: String) -> String {
        var hash: UInt64 = 0xcbf29ce484222325
        for byte in text.utf8 {
            hash ^= UInt64(byte)
            hash = hash &* 0x100000001b3
        }
        return String(hash, radix: 16)
    }
}

enum ChannelGroup {
    static let all = "All channels"
    static let favorites = "Favorites"
    static let recent = "Recent"
    static let uncategorized = "Uncategorized"
}

struct CoreError: LocalizedError, Sendable {
    var message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}
