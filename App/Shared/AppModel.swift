import SwiftUI

/// The library: playlists and everything loaded from them, plus favourites and history.
@MainActor
final class AppModel: ObservableObject {
    let settings: AppSettings

    @Published private(set) var playlists: [Playlist] = []
    @Published private(set) var channels: [Channel] = []
    @Published private(set) var movies: [Movie] = []
    @Published private(set) var series: [Series] = []
    @Published private(set) var groups: [String] = []
    @Published private(set) var movieGroups: [String] = []
    @Published private(set) var seriesGroups: [String] = []
    @Published private(set) var favorites: [String] = []
    @Published private(set) var recents: [String] = []
    @Published private(set) var didBootstrap = false
    @Published var busyText = ""
    @Published var errorText = ""

    private var contents: [UUID: PlaylistContent] = [:]
    private var byGroup: [String: [Channel]] = [:]
    private var byID: [String: Channel] = [:]
    private var favoriteSet = Set<String>()

    init(settings: AppSettings) {
        self.settings = settings
    }

    // MARK: Start-up

    func bootstrap() async {
        guard !didBootstrap else { return }
        let defaults = UserDefaults.standard
        if let data = defaults.data(forKey: "playlists"),
           let saved = try? JSONDecoder().decode([Playlist].self, from: data) {
            playlists = saved
        }
        favorites = defaults.stringArray(forKey: "favorites") ?? []
        favoriteSet = Set(favorites)
        recents = defaults.stringArray(forKey: "recents") ?? []

        if !playlists.isEmpty { busyText = "Loading library…" }
        for playlist in playlists {
            if let cached = await AppModel.readCache(playlist.id) { contents[playlist.id] = cached }
        }
        rebuild()
        busyText = ""
        didBootstrap = true

        let maxAge = Double(max(1, settings.playlistRefreshHours)) * 3600
        for playlist in playlists {
            let age = playlist.lastRefreshed.map { Date().timeIntervalSince($0) } ?? Double.infinity
            if contents[playlist.id] == nil || age > maxAge {
                _ = await refresh(playlist.id)
            }
        }
    }

    // MARK: Playlists

    func add(_ playlist: Playlist) async -> Bool {
        playlists.append(playlist)
        let loaded = await refresh(playlist.id)
        if !loaded {
            playlists.removeAll { $0.id == playlist.id }
        }
        savePlaylists()
        return loaded
    }

    func update(_ playlist: Playlist) async -> Bool {
        guard let index = playlists.firstIndex(where: { $0.id == playlist.id }) else { return false }
        playlists[index] = playlist
        savePlaylists()
        return await refresh(playlist.id)
    }

    func remove(_ id: UUID) {
        playlists.removeAll { $0.id == id }
        contents[id] = nil
        try? FileManager.default.removeItem(at: AppModel.cacheURL(id))
        savePlaylists()
        rebuild()
    }

    func refreshAll() async {
        for playlist in playlists { _ = await refresh(playlist.id) }
    }

    @discardableResult
    func refresh(_ id: UUID) async -> Bool {
        guard let playlist = playlists.first(where: { $0.id == id }) else { return false }
        busyText = "Loading \(playlist.name)…"
        errorText = ""
        defer { busyText = "" }
        do {
            let loaded = try await ContentLoader.load(playlist: playlist, liveFormat: settings.liveFormat)
            if loaded.content.channels.isEmpty && loaded.content.movies.isEmpty && loaded.content.series.isEmpty {
                throw CoreError("The playlist is empty.")
            }
            contents[id] = loaded.content
            if let index = playlists.firstIndex(where: { $0.id == id }) {
                playlists[index].lastRefreshed = Date()
                if !loaded.timeZone.isEmpty { playlists[index].serverTimeZone = loaded.timeZone }
            }
            savePlaylists()
            rebuild()
            AppModel.writeCache(loaded.content, id: id)
            return true
        } catch {
            errorText = "\(playlist.name): \(error.localizedDescription)"
            return false
        }
    }

    private func savePlaylists() {
        if let data = try? JSONEncoder().encode(playlists) {
            UserDefaults.standard.set(data, forKey: "playlists")
        }
    }

    private func rebuild() {
        var allChannels: [Channel] = []
        var allMovies: [Movie] = []
        var allSeries: [Series] = []
        for playlist in playlists {
            guard let content = contents[playlist.id] else { continue }
            allChannels.append(contentsOf: content.channels)
            allMovies.append(contentsOf: content.movies)
            allSeries.append(contentsOf: content.series)
        }
        if playlists.count > 1 {
            for index in allChannels.indices { allChannels[index].number = index + 1 }
        }

        var order: [String] = []
        var grouped: [String: [Channel]] = [:]
        var lookup: [String: Channel] = [:]
        for channel in allChannels {
            if grouped[channel.group] == nil { order.append(channel.group) }
            grouped[channel.group, default: []].append(channel)
            lookup[channel.id] = channel
        }
        byGroup = grouped
        byID = lookup
        channels = allChannels
        movies = allMovies
        series = allSeries
        groups = order
        movieGroups = AppModel.orderedUnique(allMovies.map { $0.group })
        seriesGroups = AppModel.orderedUnique(allSeries.map { $0.group })
    }

    private static func orderedUnique(_ values: [String]) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for value in values where !seen.contains(value) {
            seen.insert(value)
            result.append(value)
        }
        return result
    }

    // MARK: Lookups

    /// Groups as shown in the sidebar: the built-in ones first, then the playlist's own minus hidden ones.
    var visibleGroups: [String] {
        var result: [String] = []
        if !favorites.isEmpty { result.append(ChannelGroup.favorites) }
        if !recents.isEmpty { result.append(ChannelGroup.recent) }
        result.append(ChannelGroup.all)
        let hidden = Set(settings.hiddenGroups)
        result.append(contentsOf: groups.filter { !hidden.contains($0) })
        return result
    }

    func channels(in group: String) -> [Channel] {
        switch group {
        case ChannelGroup.all:
            let hidden = Set(settings.hiddenGroups)
            if hidden.isEmpty { return channels }
            return channels.filter { !hidden.contains($0.group) }
        case ChannelGroup.favorites:
            return favorites.compactMap { byID[$0] }
        case ChannelGroup.recent:
            return recents.compactMap { byID[$0] }
        default:
            return byGroup[group] ?? []
        }
    }

    func count(in group: String) -> Int {
        switch group {
        case ChannelGroup.all: return channels.count
        case ChannelGroup.favorites: return favorites.count
        case ChannelGroup.recent: return recents.count
        default: return byGroup[group]?.count ?? 0
        }
    }

    func channel(id: String) -> Channel? { byID[id] }

    func playlist(id: UUID) -> Playlist? { playlists.first { $0.id == id } }

    func xtreamClient(for playlistID: UUID) -> XtreamClient? {
        guard let playlist = playlist(id: playlistID),
              case .xtream(let server, let username, let password) = playlist.source else { return nil }
        return XtreamClient(server: server, username: username, password: password,
                            userAgent: playlist.userAgent, liveFormat: settings.liveFormat)
    }

    /// Guide addresses advertised by the playlists plus any the user typed in.
    var epgSources: [String] {
        var result: [String] = []
        for playlist in playlists {
            let custom = playlist.epgURL.trimmingCharacters(in: .whitespacesAndNewlines)
            if !custom.isEmpty && !result.contains(custom) { result.append(custom) }
            for url in contents[playlist.id]?.epgURLs ?? [] where !url.isEmpty && !result.contains(url) {
                result.append(url)
            }
        }
        return result
    }

    /// The channel above or below `channel` inside `group` (wraps around).
    func neighbour(of channel: Channel, in group: String, offset: Int) -> Channel? {
        var list = channels(in: group)
        if !list.contains(where: { $0.id == channel.id }) { list = channels(in: ChannelGroup.all) }
        guard let index = list.firstIndex(where: { $0.id == channel.id }), !list.isEmpty else { return nil }
        let next = (index + offset % list.count + list.count) % list.count
        return list[next]
    }

    // MARK: Favourites and history

    func isFavorite(_ channel: Channel) -> Bool { favoriteSet.contains(channel.id) }

    func toggleFavorite(_ channel: Channel) {
        if favoriteSet.contains(channel.id) {
            favoriteSet.remove(channel.id)
            favorites.removeAll { $0 == channel.id }
        } else {
            favoriteSet.insert(channel.id)
            favorites.append(channel.id)
        }
        UserDefaults.standard.set(favorites, forKey: "favorites")
    }

    func noteWatched(_ channel: Channel) {
        if recents.first == channel.id { return }
        var updated = recents.filter { $0 != channel.id }
        updated.insert(channel.id, at: 0)
        if updated.count > 30 { updated = Array(updated.prefix(30)) }
        recents = updated
        UserDefaults.standard.set(updated, forKey: "recents")
    }

    // MARK: Things to play

    func userAgent(for playlistID: UUID, channel: Channel? = nil) -> String {
        if let channel = channel, !channel.userAgent.isEmpty { return channel.userAgent }
        if let playlist = playlist(id: playlistID), !playlist.userAgent.isEmpty { return playlist.userAgent }
        return settings.effectiveUserAgent
    }

    func playable(_ channel: Channel) -> PlayableItem? {
        guard let url = URL(string: channel.url) else { return nil }
        return PlayableItem(id: channel.id, kind: .live, title: channel.name, url: url,
                            userAgent: userAgent(for: channel.playlistID, channel: channel), channel: channel)
    }

    func playable(_ movie: Movie) -> PlayableItem? {
        guard let url = URL(string: movie.url) else { return nil }
        return PlayableItem(id: movie.id, kind: .movie, title: movie.name, subtitle: movie.year, url: url,
                            userAgent: userAgent(for: movie.playlistID), resumeKey: movie.id)
    }

    func playable(_ episode: Episode, of show: Series) -> PlayableItem? {
        guard let url = URL(string: episode.url) else { return nil }
        return PlayableItem(id: episode.id, kind: .episode, title: show.name,
                            subtitle: "S\(episode.season) E\(episode.number) · \(episode.title)", url: url,
                            userAgent: userAgent(for: show.playlistID), resumeKey: episode.id)
    }

    func playableCatchup(_ channel: Channel, programme: Programme) -> PlayableItem? {
        guard let address = Catchup.url(channel: channel, playlist: playlist(id: channel.playlistID),
                                        start: programme.start, stop: programme.stop, now: Date()),
              let url = URL(string: address) else { return nil }
        return PlayableItem(id: channel.id + ":" + String(Int(programme.start.timeIntervalSince1970)),
                            kind: .catchup, title: programme.title,
                            subtitle: channel.name + " · " + Format.range(programme), url: url,
                            userAgent: userAgent(for: channel.playlistID, channel: channel), channel: channel)
    }

    // MARK: Disk cache (re-downloadable, so it lives in Caches)

    nonisolated static func cacheURL(_ id: UUID) -> URL {
        let folder = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Library", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder.appendingPathComponent(id.uuidString + ".json")
    }

    nonisolated static func readCache(_ id: UUID) async -> PlaylistContent? {
        await Task.detached(priority: .userInitiated) { () -> PlaylistContent? in
            guard let data = try? Data(contentsOf: AppModel.cacheURL(id)) else { return nil }
            return try? JSONDecoder().decode(PlaylistContent.self, from: data)
        }.value
    }

    nonisolated static func writeCache(_ content: PlaylistContent, id: UUID) {
        Task.detached(priority: .utility) {
            if let data = try? JSONEncoder().encode(content) {
                try? data.write(to: AppModel.cacheURL(id), options: .atomic)
            }
        }
    }
}
