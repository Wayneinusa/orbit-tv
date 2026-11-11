import SwiftUI

// MARK: - Settings

/// User preferences. Stored in UserDefaults, which is the only durable storage Apple TV offers apps.
@MainActor
final class AppSettings: ObservableObject {
    @Published var bufferMs: Int { didSet { save(bufferMs, "bufferMs") } }
    @Published var liveFormat: String { didSet { save(liveFormat, "liveFormat") } }
    @Published var autoReconnect: Bool { didSet { save(autoReconnect, "autoReconnect") } }
    @Published var userAgent: String { didSet { save(userAgent, "userAgent") } }
    @Published var epgPastDays: Int { didSet { save(epgPastDays, "epgPastDays") } }
    @Published var epgFutureDays: Int { didSet { save(epgFutureDays, "epgFutureDays") } }
    @Published var epgRefreshHours: Int { didSet { save(epgRefreshHours, "epgRefreshHours") } }
    @Published var playlistRefreshHours: Int { didSet { save(playlistRefreshHours, "playlistRefreshHours") } }
    @Published var autoConvertMP4: Bool { didSet { save(autoConvertMP4, "autoConvertMP4") } }
    @Published var keepTS: Bool { didSet { save(keepTS, "keepTS") } }
    @Published var padBeforeMinutes: Int { didSet { save(padBeforeMinutes, "padBeforeMinutes") } }
    @Published var padAfterMinutes: Int { didSet { save(padAfterMinutes, "padAfterMinutes") } }
    @Published var defaultRecordMinutes: Int { didSet { save(defaultRecordMinutes, "defaultRecordMinutes") } }
    @Published var hiddenGroups: [String] { didSet { save(hiddenGroups, "hiddenGroups") } }

    init() {
        let defaults = UserDefaults.standard
        bufferMs = defaults.object(forKey: "bufferMs") as? Int ?? 1500
        liveFormat = defaults.string(forKey: "liveFormat") ?? "ts"
        autoReconnect = defaults.object(forKey: "autoReconnect") as? Bool ?? true
        userAgent = defaults.string(forKey: "userAgent") ?? ""
        epgPastDays = defaults.object(forKey: "epgPastDays") as? Int ?? 1
        epgFutureDays = defaults.object(forKey: "epgFutureDays") as? Int ?? 3
        epgRefreshHours = defaults.object(forKey: "epgRefreshHours") as? Int ?? 12
        playlistRefreshHours = defaults.object(forKey: "playlistRefreshHours") as? Int ?? 24
        autoConvertMP4 = defaults.object(forKey: "autoConvertMP4") as? Bool ?? true
        keepTS = defaults.object(forKey: "keepTS") as? Bool ?? false
        padBeforeMinutes = defaults.object(forKey: "padBeforeMinutes") as? Int ?? 1
        padAfterMinutes = defaults.object(forKey: "padAfterMinutes") as? Int ?? 5
        defaultRecordMinutes = defaults.object(forKey: "defaultRecordMinutes") as? Int ?? 120
        hiddenGroups = defaults.stringArray(forKey: "hiddenGroups") ?? []
    }

    private func save(_ value: Any, _ key: String) {
        UserDefaults.standard.set(value, forKey: key)
    }

    var effectiveUserAgent: String {
        userAgent.isEmpty ? ContentLoader.defaultUserAgent : userAgent
    }
}

// MARK: - Navigation

@MainActor
final class AppRouter: ObservableObject {
    enum Section: String, CaseIterable, Identifiable {
        case live, guide, movies, series, recordings, multiview, search, settings

        var id: String { rawValue }

        var title: String {
            switch self {
            case .live: return "Live TV"
            case .guide: return "Guide"
            case .movies: return "Movies"
            case .series: return "Series"
            case .recordings: return "Recordings"
            case .multiview: return "Multi-view"
            case .search: return "Search"
            case .settings: return "Settings"
            }
        }

        var symbol: String {
            switch self {
            case .live: return "tv"
            case .guide: return "calendar"
            case .movies: return "film"
            case .series: return "rectangle.stack"
            case .recordings: return "record.circle"
            case .multiview: return "rectangle.split.2x2"
            case .search: return "magnifyingglass"
            case .settings: return "gearshape"
            }
        }
    }

    @Published var section: Section = .live {
        didSet {
            // The live preview only exists on the Live TV screen; do not leave sound playing without a picture.
            if oldValue == .live && section != .live && !fullscreen { player?.stop() }
        }
    }
    @Published var fullscreen = false
    @Published var selectedGroup = ChannelGroup.all

    private weak var model: AppModel?
    private weak var player: PlayerController?

    func attach(model: AppModel, player: PlayerController) {
        self.model = model
        self.player = player
    }

    /// First activation plays the channel in the preview pane; activating it again goes full screen.
    func preview(_ channel: Channel) {
        if let item = player?.item, item.kind == .live, item.id == channel.id {
            fullscreen = true
            return
        }
        start(channel)
    }

    func watch(_ channel: Channel) {
        let alreadyOn = player?.item?.kind == .live && player?.item?.id == channel.id
        if !alreadyOn { start(channel) }
        fullscreen = true
    }

    private func start(_ channel: Channel) {
        guard let model = model, let item = model.playable(channel) else { return }
        player?.play(item)
        model.noteWatched(channel)
    }

    func play(_ item: PlayableItem) {
        player?.play(item)
        fullscreen = true
    }

    func closePlayer() {
        fullscreen = false
        let isLive = player?.item?.isLive ?? false
        if section != .live || !isLive { player?.stop() }
    }

    /// Channel up / down while watching live TV.
    func zap(_ offset: Int) {
        guard let model = model, let item = player?.item, item.isLive, let channel = item.channel,
              let next = model.neighbour(of: channel, in: selectedGroup, offset: offset) else { return }
        start(next)
    }
}

// MARK: - What the player is asked to play

struct PlayableItem: Identifiable, Equatable {
    enum Kind: Equatable {
        case live, catchup, movie, episode, recording
    }

    var id: String
    var kind: Kind
    var title: String
    var subtitle: String = ""
    var url: URL
    var userAgent: String = ""
    var channel: Channel?
    /// Key under which the playback position is remembered (movies, episodes, recordings).
    var resumeKey: String = ""

    var isLive: Bool { kind == .live }
}

/// Remembers where the viewer stopped in movies, episodes and recordings.
enum ResumeStore {
    private static let key = "resumePositions"

    static func position(for resumeKey: String) -> Int {
        guard !resumeKey.isEmpty else { return 0 }
        let all = UserDefaults.standard.dictionary(forKey: key) as? [String: Int] ?? [:]
        return all[resumeKey] ?? 0
    }

    static func save(_ milliseconds: Int, for resumeKey: String) {
        guard !resumeKey.isEmpty else { return }
        var all = UserDefaults.standard.dictionary(forKey: key) as? [String: Int] ?? [:]
        if milliseconds <= 0 {
            all[resumeKey] = nil
        } else {
            all[resumeKey] = milliseconds
        }
        // Keep the store small: Apple TV limits how much an app may keep in its preferences.
        if all.count > 300 {
            for extra in all.keys.sorted().prefix(all.count - 300) { all[extra] = nil }
        }
        UserDefaults.standard.set(all, forKey: key)
    }
}
