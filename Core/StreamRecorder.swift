import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Saves a live stream to a file and keeps it going.
///
/// Whenever the stream stops for any reason (server hiccup, Wi-Fi drop, timeout, HTTP error) the recorder
/// reconnects on its own and appends to the same file. It only gives up when the end time is reached,
/// `stop()` is called, or the disk can no longer be written.
///
/// MPEG-TS is used as the capture format on purpose: it is the one container that stays playable when
/// it is cut and joined at arbitrary points, which is exactly what a reconnect does.
final class StreamRecorder: NSObject, URLSessionDataDelegate, @unchecked Sendable {

    enum State: String, Sendable {
        case idle, connecting, recording, reconnecting, finished, stopped, failed
    }

    struct Progress: Sendable {
        var state: State = .idle
        var bytes: Int64 = 0
        var reconnects = 0
        var message = ""
    }

    private let sourceURL: URL
    private let fileURL: URL
    private let headers: [String: String]
    private let endDate: Date
    private let queue = DispatchQueue(label: "orbit.recorder")

    private var session: URLSession?
    private var task: URLSessionDataTask?
    private var handle: FileHandle?
    private var watchdog: DispatchSourceTimer?

    private var progress = Progress()
    private var done = false
    private var attempt = 0
    private var retryPending = false
    private var pendingError = ""
    private var lastDataAt = Date()
    private var lastReportAt = Date.distantPast

    private var hlsMode: Bool
    private var hlsURL: URL
    private var hlsLastSequence = -1
    private var hlsWroteInit = false

    /// Called on a background queue.
    var onProgress: (@Sendable (Progress) -> Void)?

    init(url: URL, fileURL: URL, headers: [String: String], endDate: Date) {
        self.sourceURL = url
        self.fileURL = fileURL
        self.headers = headers
        self.endDate = endDate
        self.hlsURL = url
        self.hlsMode = url.pathExtension.lowercased() == "m3u8"
        super.init()
    }

    // MARK: Control

    func start() {
        queue.async { [self] in
            guard session == nil, !done else { return }

            let manager = FileManager.default
            try? manager.createDirectory(at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            if !manager.fileExists(atPath: fileURL.path) {
                manager.createFile(atPath: fileURL.path, contents: nil)
            }
            guard let opened = try? FileHandle(forWritingTo: fileURL) else {
                finish(.failed, message: "The recording file could not be created.")
                return
            }
            // Appending is what makes "resume after the app was closed" work.
            let existing = (try? opened.seekToEnd()) ?? 0
            handle = opened
            progress.bytes = Int64(existing)

            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 20       // no data for 20 s counts as a dropped stream
            configuration.timeoutIntervalForResource = 60 * 60 * 24 * 7
            configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
            configuration.urlCache = nil
            let delegateQueue = OperationQueue()
            delegateQueue.maxConcurrentOperationCount = 1
            delegateQueue.underlyingQueue = queue
            session = URLSession(configuration: configuration, delegate: self, delegateQueue: delegateQueue)

            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + 5.0, repeating: 5.0)
            timer.setEventHandler { [weak self] in self?.checkHealth() }
            timer.resume()
            watchdog = timer

            connect()
        }
    }

    func stop() {
        queue.async { [self] in
            finish(.stopped, message: "")
        }
    }

    // MARK: Connection loop (all on `queue`)

    private func request(for url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        for (name, value) in headers where !value.isEmpty { request.setValue(value, forHTTPHeaderField: name) }
        return request
    }

    private func connect() {
        retryPending = false
        guard !done, let session = session else { return }
        if Date() >= endDate {
            finish(.finished, message: "")
            return
        }
        if progress.state != .recording {
            progress.state = progress.reconnects == 0 ? .connecting : .reconnecting
            report(force: true)
        }
        pendingError = ""
        if hlsMode {
            pollPlaylist()
            return
        }
        let newTask = session.dataTask(with: request(for: sourceURL))
        task = newTask
        newTask.resume()
    }

    private func retry(_ reason: String) {
        guard !done, !retryPending else { return }
        if Date() >= endDate {
            finish(.finished, message: "")
            return
        }
        retryPending = true
        attempt += 1
        progress.reconnects += 1
        progress.state = .reconnecting
        progress.message = reason
        report(force: true)
        // 1, 2, 4, 8, then every 15 seconds. `attempt` returns to zero as soon as data flows again.
        let delay = min(15.0, pow(2.0, Double(min(attempt, 6) - 1)))
        queue.asyncAfter(deadline: .now() + delay) { [weak self] in self?.connect() }
    }

    private func checkHealth() {
        guard !done else { return }
        if Date() >= endDate {
            finish(.finished, message: "")
            return
        }
        // Belt and braces: URLSession's own timeout normally fires first.
        if progress.state == .recording, !hlsMode, Date().timeIntervalSince(lastDataAt) > 45 {
            pendingError = "No data received"
            task?.cancel()
        }
    }

    private func append(_ data: Data) {
        guard !done, let handle = handle, !data.isEmpty else { return }
        do {
            try handle.write(contentsOf: data)
        } catch {
            finish(.failed, message: "Could not write to storage: \(error.localizedDescription)")
            return
        }
        progress.bytes += Int64(data.count)
        lastDataAt = Date()
        attempt = 0
        if progress.state != .recording {
            progress.state = .recording
            report(force: true)
        } else {
            report(force: false)
        }
    }

    private func finish(_ state: State, message: String) {
        guard !done else { return }
        done = true
        watchdog?.cancel()
        watchdog = nil
        task?.cancel()
        task = nil
        try? handle?.synchronize()
        try? handle?.close()
        handle = nil
        session?.invalidateAndCancel()
        session = nil
        progress.state = state
        if !message.isEmpty { progress.message = message }
        report(force: true)
    }

    private func report(force: Bool) {
        let now = Date()
        if !force && now.timeIntervalSince(lastReportAt) < 1 { return }
        lastReportAt = now
        onProgress?(progress)
    }

    // MARK: Plain HTTP stream

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard dataTask === task, !done else {
            completionHandler(.cancel)
            return
        }
        if let http = response as? HTTPURLResponse {
            if !(200...299).contains(http.statusCode) {
                pendingError = "Server answered \(http.statusCode)"
                completionHandler(.cancel)
                return
            }
            let type = (http.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased()
            let finalExtension = response.url?.pathExtension.lowercased() ?? ""
            if type.contains("mpegurl") || finalExtension == "m3u8" {
                // The address turned out to be an HLS playlist: switch to segment downloading.
                hlsMode = true
                hlsURL = response.url ?? sourceURL
                completionHandler(.cancel)
                return
            }
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard dataTask === task else { return }
        append(data)
    }

    func urlSession(_ session: URLSession, task finishedTask: URLSessionTask, didCompleteWithError error: Error?) {
        guard finishedTask === task else { return }
        task = nil
        guard !done else { return }
        if hlsMode {
            connect()
            return
        }
        var reason = pendingError
        if reason.isEmpty { reason = error?.localizedDescription ?? "The stream ended" }
        retry(reason)
    }

    // MARK: HLS

    private func pollPlaylist() {
        guard !done, let session = session else { return }
        let playlistURL = hlsURL
        let playlistTask = session.dataTask(with: request(for: playlistURL)) { [weak self] data, response, error in
            guard let self = self, !self.done else { return }
            let status = (response as? HTTPURLResponse)?.statusCode ?? 200
            guard error == nil, (200...299).contains(status), let data = data,
                  let text = String(data: data, encoding: .utf8), text.contains("#EXTM3U") else {
                self.retry(error?.localizedDescription ?? "Playlist could not be loaded (\(status))")
                return
            }
            let playlist = HLSParser.parse(text, baseURL: response?.url ?? playlistURL)
            if playlist.isMaster {
                guard let best = playlist.variants.max(by: { $0.bandwidth < $1.bandwidth }) else {
                    self.retry("The playlist lists no streams")
                    return
                }
                self.hlsURL = best.url
                self.pollPlaylist()
                return
            }
            if playlist.encrypted {
                self.finish(.failed, message: "This stream is encrypted and cannot be recorded.")
                return
            }
            self.process(playlist)
        }
        playlistTask.resume()
    }

    private func process(_ playlist: HLSPlaylist) {
        // A sequence number far below ours means the server restarted its numbering.
        let lastAvailable = playlist.mediaSequence + playlist.segments.count - 1
        if hlsLastSequence > lastAvailable + 2 { hlsLastSequence = -1 }

        var pending: [(sequence: Int, url: URL)] = []
        for (index, url) in playlist.segments.enumerated() {
            let sequence = playlist.mediaSequence + index
            if sequence > hlsLastSequence { pending.append((sequence, url)) }
        }
        // On the first pass of a live stream start near "now" rather than at the back of the buffer.
        if hlsLastSequence < 0 && !playlist.endList && pending.count > 3 {
            pending = Array(pending.suffix(3))
        }

        if let initSegment = playlist.initSegment, !hlsWroteInit, progress.bytes == 0 {
            guard let session = session else { return }
            session.dataTask(with: request(for: initSegment)) { [weak self] data, _, error in
                guard let self = self, !self.done else { return }
                guard let data = data, error == nil, !data.isEmpty else {
                    self.retry(error?.localizedDescription ?? "Stream header could not be loaded")
                    return
                }
                self.hlsWroteInit = true
                self.append(data)
                self.download(pending, index: 0, playlist: playlist)
            }.resume()
            return
        }
        download(pending, index: 0, playlist: playlist)
    }

    private func download(_ pending: [(sequence: Int, url: URL)], index: Int, playlist: HLSPlaylist) {
        guard !done, let session = session else { return }
        if Date() >= endDate {
            finish(.finished, message: "")
            return
        }
        if index >= pending.count {
            if playlist.endList {
                finish(.finished, message: "")
                return
            }
            let wait = max(1.0, Double(playlist.targetDuration) / 2)
            queue.asyncAfter(deadline: .now() + wait) { [weak self] in self?.pollPlaylist() }
            return
        }
        let item = pending[index]
        session.dataTask(with: request(for: item.url)) { [weak self] data, response, error in
            guard let self = self, !self.done else { return }
            let status = (response as? HTTPURLResponse)?.statusCode ?? 200
            guard let data = data, error == nil, (200...299).contains(status), !data.isEmpty else {
                self.retry(error?.localizedDescription ?? "A stream segment could not be loaded (\(status))")
                return
            }
            self.append(data)
            self.hlsLastSequence = item.sequence
            self.download(pending, index: index + 1, playlist: playlist)
        }.resume()
    }
}
