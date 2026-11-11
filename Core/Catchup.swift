import Foundation

/// Builds catch-up ("watch a programme that already aired") addresses.
enum Catchup {

    static func isAvailable(channel: Channel, programme: Programme, now: Date) -> Bool {
        guard let info = channel.catchup else { return false }
        guard programme.start < now else { return false }
        return programme.start > now.addingTimeInterval(-Double(info.days) * 86400)
    }

    static func url(channel: Channel, playlist: Playlist?, start: Date, stop: Date, now: Date) -> String? {
        guard let info = channel.catchup else { return nil }
        let end = min(stop, now)
        let durationSeconds = max(60, Int(end.timeIntervalSince(start)))

        switch info.mode {
        case .xtream:
            guard let playlist = playlist, case .xtream(let server, let username, let password) = playlist.source,
                  let streamID = channel.xtreamID else { return nil }
            let client = XtreamClient(server: server, username: username, password: password)
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.dateFormat = "yyyy-MM-dd:HH-mm"
            formatter.timeZone = TimeZone(identifier: playlist.serverTimeZone) ?? TimeZone.current
            // Round up so the end of the programme is not cut off.
            let minutes = (durationSeconds + 59) / 60
            return client.timeshiftURL(streamID: streamID, startText: formatter.string(from: start), minutes: minutes)

        case .shift:
            let separator = channel.url.contains("?") ? "&" : "?"
            return channel.url + separator + "utc=\(Int(start.timeIntervalSince1970))&lutc=\(Int(now.timeIntervalSince1970))"

        case .append:
            return channel.url + fill(info.source, start: start, end: end, now: now, duration: durationSeconds)

        case .template:
            return fill(info.source, start: start, end: end, now: now, duration: durationSeconds)

        case .flussonic:
            return flussonic(channel.url, start: start, duration: durationSeconds)
        }
    }

    /// Replaces the placeholders IPTV playlists use in `catchup-source`.
    static func fill(_ template: String, start: Date, end: Date, now: Date, duration: Int) -> String {
        let startEpoch = Int(start.timeIntervalSince1970)
        let endEpoch = Int(end.timeIntervalSince1970)
        let nowEpoch = Int(now.timeIntervalSince1970)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC") ?? TimeZone.current
        let parts = calendar.dateComponents([.year, .month, .day, .hour, .minute, .second], from: start)

        func two(_ value: Int?) -> String { String(format: "%02d", value ?? 0) }

        let replacements: [(String, String)] = [
            ("${start}", String(startEpoch)), ("{utc}", String(startEpoch)),
            ("${end}", String(endEpoch)), ("{utcend}", String(endEpoch)),
            ("${timestamp}", String(nowEpoch)), ("{lutc}", String(nowEpoch)),
            ("${duration}", String(duration)), ("{duration}", String(duration)),
            ("${offset}", String(max(0, nowEpoch - startEpoch))), ("{offset}", String(max(0, nowEpoch - startEpoch))),
            ("{Y}", String(parts.year ?? 1970)), ("{m}", two(parts.month)), ("{d}", two(parts.day)),
            ("{H}", two(parts.hour)), ("{M}", two(parts.minute)), ("{S}", two(parts.second))
        ]
        var result = template
        for (placeholder, value) in replacements {
            result = result.replacingOccurrences(of: placeholder, with: value)
        }
        return result
    }

    static func flussonic(_ live: String, start: Date, duration: Int) -> String? {
        var base = live
        var query = ""
        if let question = base.firstIndex(of: "?") {
            query = String(base[question...])
            base = String(base[base.startIndex..<question])
        }
        guard let slash = base.lastIndex(of: "/") else { return nil }
        let folder = String(base[base.startIndex..<slash])
        let file = String(base[base.index(after: slash)...])
        let epoch = Int(start.timeIntervalSince1970)

        if file.hasSuffix(".m3u8") {
            let name = String(file.dropLast(".m3u8".count))
            return "\(folder)/\(name)-\(epoch)-\(duration).m3u8\(query)"
        }
        return "\(folder)/timeshift_abs-\(epoch).ts\(query)"
    }
}
