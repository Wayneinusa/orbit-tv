import SwiftUI

/// Programme guide: downloads XMLTV sources, keeps the result in memory and caches it on disk.
@MainActor
final class EPGModel: ObservableObject {
    @Published private(set) var data = EPGData()
    @Published private(set) var isRefreshing = false
    @Published private(set) var statusText = ""

    // MARK: Lookups

    func current(for channel: Channel, at date: Date) -> Programme? { data.current(for: channel, at: date) }
    func next(for channel: Channel, after date: Date) -> Programme? { data.next(for: channel, after: date) }
    func programmes(for channel: Channel, from: Date, to: Date) -> [Programme] {
        data.programmes(for: channel, from: from, to: to)
    }

    func upcoming(for channel: Channel, after date: Date, limit: Int) -> [Programme] {
        Array(data.list(for: channel).filter { $0.stop > date }.prefix(limit))
    }

    var hasData: Bool { !data.programmes.isEmpty }

    // MARK: Loading

    func loadCache() async {
        let cached = await Task.detached(priority: .userInitiated) { () -> EPGData? in
            guard let raw = try? Data(contentsOf: EPGModel.cacheURL) else { return nil }
            return try? PropertyListDecoder().decode(EPGData.self, from: raw)
        }.value
        if let cached = cached, data.programmes.isEmpty {
            data = cached
            if let updated = cached.updated {
                statusText = "Guide updated " + Format.dayTime.string(from: updated)
            }
        }
    }

    func refreshIfNeeded(model: AppModel, settings: AppSettings) async {
        let maxAge = Double(max(1, settings.epgRefreshHours)) * 3600
        // A changed list of guide addresses (for example after adding a playlist) also forces an update.
        let sourceKey = model.epgSources.joined(separator: "|")
        let previousKey = UserDefaults.standard.string(forKey: "guideSources") ?? ""
        if let updated = data.updated, Date().timeIntervalSince(updated) < maxAge, hasData, sourceKey == previousKey { return }
        await refresh(model: model, settings: settings)
    }

    func refresh(model: AppModel, settings: AppSettings) async {
        guard !isRefreshing else { return }
        let sources = model.epgSources
        let channels = model.channels
        guard !sources.isEmpty, !channels.isEmpty else {
            statusText = sources.isEmpty ? "No guide source. Add an XMLTV address in the playlist settings." : ""
            return
        }
        isRefreshing = true
        defer { isRefreshing = false }

        let ids = Set(channels.map { $0.epgID }.filter { !$0.isEmpty })
        let names = Set(channels.map { $0.name.lowercased() })
        let start = Date().addingTimeInterval(-Double(max(0, settings.epgPastDays)) * 86400)
        let end = Date().addingTimeInterval(Double(max(1, settings.epgFutureDays)) * 86400)

        var merged = EPGData()
        var failures = 0
        for (index, source) in sources.enumerated() {
            statusText = "Updating guide (\(index + 1) of \(sources.count))…"
            if let part = await EPGModel.fetch(source: source, ids: ids, names: names, start: start, end: end) {
                merged.merge(part)
            } else {
                failures += 1
            }
        }

        if merged.programmes.isEmpty {
            statusText = failures > 0 ? "The guide could not be downloaded." : "The guide has no listings for these channels."
            return
        }
        merged.updated = Date()
        data = merged
        UserDefaults.standard.set(sources.joined(separator: "|"), forKey: "guideSources")
        statusText = "Guide updated " + Format.dayTime.string(from: Date())
        let snapshot = merged
        Task.detached(priority: .utility) {
            let encoder = PropertyListEncoder()
            encoder.outputFormat = .binary
            if let raw = try? encoder.encode(snapshot) {
                try? raw.write(to: EPGModel.cacheURL, options: .atomic)
            }
        }
    }

    nonisolated static var cacheURL: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appendingPathComponent("guide.plist")
    }

    nonisolated static func fetch(source: String, ids: Set<String>, names: Set<String>, start: Date, end: Date) async -> EPGData? {
        guard let url = URL(string: source.trimmingCharacters(in: .whitespacesAndNewlines)) else { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = 180
        request.setValue(ContentLoader.defaultUserAgent, forHTTPHeaderField: "User-Agent")
        guard let downloaded = try? await URLSession.shared.download(for: request) else { return nil }
        let tempURL = downloaded.0
        if let http = downloaded.1 as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            try? FileManager.default.removeItem(at: tempURL)
            return nil
        }
        return await Task.detached(priority: .utility) { () -> EPGData? in
            let manager = FileManager.default
            let unpacked = manager.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".xml")
            defer {
                try? manager.removeItem(at: unpacked)
                try? manager.removeItem(at: tempURL)
            }
            var fileURL = tempURL
            if Gzip.isGzipped(tempURL) {
                do {
                    try Gzip.gunzip(from: tempURL, to: unpacked)
                    fileURL = unpacked
                } catch {
                    return nil
                }
            }
            let parser = XMLTVParser(wantedIDs: ids, wantedNames: names, windowStart: start, windowEnd: end)
            let result = parser.parse(fileURL: fileURL)
            if result.programmes.isEmpty { return nil }
            return result
        }.value
    }
}
