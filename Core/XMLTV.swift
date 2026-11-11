import Foundation
#if canImport(FoundationXML)
import FoundationXML
#endif
#if canImport(zlib)
import zlib
#endif

// MARK: - Guide data

/// Programme listings keyed by lower-cased XMLTV channel id.
struct EPGData: Codable, Sendable {
    var programmes: [String: [Programme]] = [:]
    /// Lower-cased channel display name -> channel id, for playlists that carry no tvg-id.
    var names: [String: String] = [:]
    var updated: Date?

    mutating func merge(_ other: EPGData) {
        for (key, list) in other.programmes {
            if let existing = programmes[key], existing.count >= list.count { continue }
            programmes[key] = list
        }
        for (name, id) in other.names where names[name] == nil { names[name] = id }
    }

    func key(for channel: Channel) -> String? {
        if !channel.epgID.isEmpty, programmes[channel.epgID] != nil { return channel.epgID }
        if let id = names[channel.name.lowercased()], programmes[id] != nil { return id }
        return nil
    }

    func list(for channel: Channel) -> [Programme] {
        guard let epgKey = key(for: channel) else { return [] }
        return programmes[epgKey] ?? []
    }

    /// Index of the programme on air at `date` (binary search; lists are sorted by start time).
    static func index(in list: [Programme], at date: Date) -> Int? {
        var low = 0
        var high = list.count - 1
        while low <= high {
            let middle = (low + high) / 2
            let item = list[middle]
            if date < item.start {
                high = middle - 1
            } else if date >= item.stop {
                low = middle + 1
            } else {
                return middle
            }
        }
        return nil
    }

    func current(for channel: Channel, at date: Date) -> Programme? {
        let items = list(for: channel)
        guard let index = EPGData.index(in: items, at: date) else { return nil }
        return items[index]
    }

    func next(for channel: Channel, after date: Date) -> Programme? {
        list(for: channel).first { $0.start > date }
    }

    func programmes(for channel: Channel, from: Date, to: Date) -> [Programme] {
        list(for: channel).filter { $0.stop > from && $0.start < to }
    }
}

// MARK: - Dates

enum XMLTVDate {
    /// Parses "20261111063000 +0100" without DateFormatter (which is far too slow for large guides).
    static func parse(_ text: String) -> Date? {
        let bytes = Array(text.utf8)
        guard bytes.count >= 12 else { return nil }

        func number(_ from: Int, _ length: Int) -> Int? {
            guard from + length <= bytes.count else { return nil }
            var value = 0
            for index in from..<(from + length) {
                let byte = bytes[index]
                guard byte >= 48 && byte <= 57 else { return nil }
                value = value * 10 + Int(byte - 48)
            }
            return value
        }

        guard let year = number(0, 4), let month = number(4, 2), let day = number(6, 2),
              let hour = number(8, 2), let minute = number(10, 2) else { return nil }
        var second = 0
        var cursor = 12
        if let parsed = number(12, 2) {
            second = parsed
            cursor = 14
        }

        // Optional time zone: " +HHMM" or " -HHMM"
        var offsetSeconds = 0
        while cursor < bytes.count && bytes[cursor] == 32 { cursor += 1 }
        if cursor < bytes.count, bytes[cursor] == 43 || bytes[cursor] == 45 {
            let negative = bytes[cursor] == 45
            if let hours = number(cursor + 1, 2), let minutes = number(cursor + 3, 2) {
                offsetSeconds = (hours * 3600 + minutes * 60) * (negative ? -1 : 1)
            }
        }

        let days = daysFromCivil(year: year, month: month, day: day)
        let seconds = days * 86400 + hour * 3600 + minute * 60 + second - offsetSeconds
        return Date(timeIntervalSince1970: TimeInterval(seconds))
    }

    /// Days since 1970-01-01 for a Gregorian date (Howard Hinnant's algorithm).
    static func daysFromCivil(year: Int, month: Int, day: Int) -> Int {
        let y = month <= 2 ? year - 1 : year
        let era = (y >= 0 ? y : y - 399) / 400
        let yearOfEra = y - era * 400
        let dayOfYear = (153 * (month + (month > 2 ? -3 : 9)) + 2) / 5 + day - 1
        let dayOfEra = yearOfEra * 365 + yearOfEra / 4 - yearOfEra / 100 + dayOfYear
        return era * 146097 + dayOfEra - 719468
    }
}

// MARK: - Parser

/// Streaming XMLTV reader. Only keeps programmes inside `window`, and only for wanted channels.
final class XMLTVParser: NSObject, XMLParserDelegate {
    private let wantedIDs: Set<String>
    private let wantedNames: Set<String>
    private let windowStart: Date
    private let windowEnd: Date

    private var result = EPGData()
    private var acceptedIDs = Set<String>()

    private var currentChannelID = ""
    private var programmeChannel = ""
    private var programmeStart: Date?
    private var programmeStop: Date?
    private var programmeTitle = ""
    private var programmeDesc = ""
    private var inProgramme = false
    private var capturing = false
    private var buffer = ""

    /// - Parameters:
    ///   - wantedIDs: lower-cased tvg-ids present in the playlists
    ///   - wantedNames: lower-cased channel names, used when a channel has no tvg-id
    init(wantedIDs: Set<String>, wantedNames: Set<String>, windowStart: Date, windowEnd: Date) {
        self.wantedIDs = wantedIDs
        self.wantedNames = wantedNames
        self.windowStart = windowStart
        self.windowEnd = windowEnd
    }

    func parse(fileURL: URL) -> EPGData {
        guard let stream = InputStream(url: fileURL) else { return EPGData() }
        let parser = XMLParser(stream: stream)
        parser.delegate = self
        parser.shouldProcessNamespaces = false
        parser.shouldResolveExternalEntities = false
        _ = parser.parse()
        return finish()
    }

    func parse(data: Data) -> EPGData {
        let parser = XMLParser(data: data)
        parser.delegate = self
        _ = parser.parse()
        return finish()
    }

    private func finish() -> EPGData {
        for key in Array(result.programmes.keys) {
            result.programmes[key]?.sort { $0.start < $1.start }
        }
        result.updated = Date()
        return result
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        switch elementName {
        case "channel":
            currentChannelID = (attributeDict["id"] ?? "").lowercased()
            if wantedIDs.contains(currentChannelID) { acceptedIDs.insert(currentChannelID) }
        case "display-name":
            if !currentChannelID.isEmpty {
                capturing = true
                buffer = ""
            }
        case "programme":
            let channel = (attributeDict["channel"] ?? "").lowercased()
            inProgramme = false
            guard acceptedIDs.contains(channel) || wantedIDs.contains(channel) else { return }
            guard let start = XMLTVDate.parse(attributeDict["start"] ?? "") else { return }
            let stop = XMLTVDate.parse(attributeDict["stop"] ?? "") ?? start.addingTimeInterval(1800)
            guard stop > windowStart && start < windowEnd else { return }
            inProgramme = true
            programmeChannel = channel
            programmeStart = start
            programmeStop = stop
            programmeTitle = ""
            programmeDesc = ""
        case "title", "desc":
            if inProgramme {
                capturing = true
                buffer = ""
            }
        default:
            break
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if capturing { buffer += string }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        switch elementName {
        case "display-name":
            if capturing {
                let name = buffer.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                if !name.isEmpty && !currentChannelID.isEmpty {
                    if result.names[name] == nil { result.names[name] = currentChannelID }
                    if wantedNames.contains(name) { acceptedIDs.insert(currentChannelID) }
                }
            }
            capturing = false
        case "channel":
            currentChannelID = ""
        case "title":
            if inProgramme && capturing && programmeTitle.isEmpty {
                programmeTitle = buffer.trimmingCharacters(in: .whitespacesAndNewlines)
            }
            capturing = false
        case "desc":
            if inProgramme && capturing && programmeDesc.isEmpty {
                let text = buffer.trimmingCharacters(in: .whitespacesAndNewlines)
                programmeDesc = text.count > 300 ? String(text.prefix(300)) + "…" : text
            }
            capturing = false
        case "programme":
            if inProgramme, let start = programmeStart, let stop = programmeStop {
                let programme = Programme(start: start, stop: stop,
                                          title: programmeTitle.isEmpty ? "No title" : programmeTitle,
                                          desc: programmeDesc)
                result.programmes[programmeChannel, default: []].append(programme)
            }
            inProgramme = false
        default:
            break
        }
    }
}

// MARK: - Gzip

enum Gzip {
    static func isGzipped(_ url: URL) -> Bool {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return false }
        defer { try? handle.close() }
        let magic = handle.readData(ofLength: 2)
        return magic.count == 2 && magic[magic.startIndex] == 0x1f && magic[magic.startIndex + 1] == 0x8b
    }

    #if canImport(zlib)
    /// Streams a .gz file to disk without loading it into memory (guides can be hundreds of MB).
    static func gunzip(from source: URL, to destination: URL) throws {
        let manager = FileManager.default
        if manager.fileExists(atPath: destination.path) { try manager.removeItem(at: destination) }
        manager.createFile(atPath: destination.path, contents: nil)
        let input = try FileHandle(forReadingFrom: source)
        let output = try FileHandle(forWritingTo: destination)
        defer {
            try? input.close()
            try? output.close()
        }

        var stream = z_stream()
        // 15 + 32: largest window, automatic gzip/zlib header detection.
        guard inflateInit2_(&stream, 47, ZLIB_VERSION, Int32(MemoryLayout<z_stream>.size)) == Z_OK else {
            throw CoreError("Could not start decompression.")
        }
        defer { inflateEnd(&stream) }

        let chunkSize = 1 << 16
        var outBuffer = [UInt8](repeating: 0, count: chunkSize)
        var finished = false

        while !finished {
            let chunk = input.readData(ofLength: chunkSize)
            if chunk.isEmpty { break }
            var inBuffer = [UInt8](chunk)
            let inCount = inBuffer.count
            try inBuffer.withUnsafeMutableBufferPointer { inPointer in
                stream.next_in = inPointer.baseAddress
                stream.avail_in = uInt(inCount)
                var keepGoing = true
                while keepGoing {
                    let status: Int32 = outBuffer.withUnsafeMutableBufferPointer { outPointer in
                        stream.next_out = outPointer.baseAddress
                        stream.avail_out = uInt(chunkSize)
                        return inflate(&stream, Z_NO_FLUSH)
                    }
                    if status != Z_OK && status != Z_STREAM_END && status != Z_BUF_ERROR {
                        throw CoreError("The compressed guide file is damaged.")
                    }
                    let produced = chunkSize - Int(stream.avail_out)
                    if produced > 0 { output.write(Data(outBuffer[0..<produced])) }
                    if status == Z_STREAM_END {
                        finished = true
                        keepGoing = false
                    } else if status == Z_BUF_ERROR || stream.avail_out != 0 {
                        keepGoing = false
                    }
                }
            }
        }
    }
    #endif
}
