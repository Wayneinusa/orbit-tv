import Foundation

/// The small part of the HLS playlist format the recorder needs.
struct HLSPlaylist {
    struct Variant {
        var bandwidth: Int
        var url: URL
    }

    var isMaster = false
    var variants: [Variant] = []
    var targetDuration = 6
    var mediaSequence = 0
    var segments: [URL] = []
    var endList = false
    var encrypted = false
    var initSegment: URL?
}

enum HLSParser {
    static func parse(_ text: String, baseURL: URL) -> HLSPlaylist {
        var playlist = HLSPlaylist()
        var pendingBandwidth: Int?

        func resolve(_ reference: String) -> URL? {
            URL(string: reference, relativeTo: baseURL)?.absoluteURL
        }

        for rawLine in text.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if line.isEmpty { continue }

            if line.hasPrefix("#EXT-X-STREAM-INF") {
                playlist.isMaster = true
                pendingBandwidth = attribute("BANDWIDTH", in: line).flatMap { Int($0) } ?? 0
            } else if line.hasPrefix("#EXT-X-TARGETDURATION:") {
                playlist.targetDuration = Int(line.dropFirst("#EXT-X-TARGETDURATION:".count)) ?? 6
            } else if line.hasPrefix("#EXT-X-MEDIA-SEQUENCE:") {
                playlist.mediaSequence = Int(line.dropFirst("#EXT-X-MEDIA-SEQUENCE:".count)) ?? 0
            } else if line.hasPrefix("#EXT-X-ENDLIST") {
                playlist.endList = true
            } else if line.hasPrefix("#EXT-X-KEY") {
                let method = attribute("METHOD", in: line) ?? "NONE"
                if method != "NONE" { playlist.encrypted = true }
            } else if line.hasPrefix("#EXT-X-MAP") {
                if let uri = attribute("URI", in: line) { playlist.initSegment = resolve(uri) }
            } else if line.hasPrefix("#") {
                continue
            } else if let url = resolve(line) {
                if let bandwidth = pendingBandwidth {
                    playlist.variants.append(HLSPlaylist.Variant(bandwidth: bandwidth, url: url))
                    pendingBandwidth = nil
                } else {
                    playlist.segments.append(url)
                }
            }
        }
        return playlist
    }

    /// Reads `NAME=value` or `NAME="value"` from a tag line.
    static func attribute(_ name: String, in line: String) -> String? {
        guard let range = line.range(of: ":" + name + "=") ?? line.range(of: "," + name + "=") else { return nil }
        let rest = line[range.upperBound...]
        if rest.hasPrefix("\"") {
            let afterQuote = rest.dropFirst()
            guard let closing = afterQuote.firstIndex(of: "\"") else { return nil }
            return String(afterQuote[afterQuote.startIndex..<closing])
        }
        if let comma = rest.firstIndex(of: ",") { return String(rest[rest.startIndex..<comma]) }
        return String(rest)
    }
}
