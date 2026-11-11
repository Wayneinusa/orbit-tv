import SwiftUI
import UIKit

#if canImport(VLCKitSPM)
import VLCKitSPM
#endif
#if canImport(MobileVLCKit)
import MobileVLCKit
#elseif canImport(TVVLCKit)
import TVVLCKit
#elseif canImport(VLCKit)
import VLCKit
#endif

/// Schedules, runs, resumes and converts recordings.
///
/// How "never stops" is achieved:
///  1. `StreamRecorder` reconnects by itself whenever the stream drops, appending to the same .ts file.
///  2. Every few seconds `tick()` checks that each recording that should be running has a live recorder.
///     If the app was closed, suspended or crashed in the middle, the recording picks up again as soon as
///     the app is running, for as long as its end time has not passed.
///  3. When a recording ends, the .ts is converted to .mp4 (automatically, or on demand).
@MainActor
final class RecordingManager: ObservableObject {
    @Published private(set) var recordings: [Recording] = []

    private let settings: AppSettings
    private var recorders: [UUID: StreamRecorder] = [:]
    private var timer: Timer?
    private var converter: MP4Converter?
    private var convertingID: UUID?
    private var lastSave = Date()

    init(settings: AppSettings) {
        self.settings = settings
        if let data = UserDefaults.standard.data(forKey: "recordings"),
           let saved = try? JSONDecoder().decode([Recording].self, from: data) {
            recordings = saved
        }
        // Anything that claims to be converting was interrupted by the app closing.
        for index in recordings.indices where recordings[index].status == .converting {
            recordings[index].status = .completed
            recordings[index].convertProgress = 0
        }
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
        Task { @MainActor [weak self] in self?.tick() }
    }

    // MARK: Files

    /// iPad: the app's Documents folder, visible in the Files app.
    /// Apple TV: apps have no permanent storage, so recordings live in the cache folder.
    nonisolated static var folder: URL {
        #if os(tvOS)
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        #else
        let base = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        #endif
        let folder = base.appendingPathComponent("Recordings", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    func tsURL(_ recording: Recording) -> URL {
        RecordingManager.folder.appendingPathComponent(recording.baseName + ".ts")
    }

    func mp4URL(_ recording: Recording) -> URL {
        RecordingManager.folder.appendingPathComponent(recording.baseName + ".mp4")
    }

    /// The best file to play or share: the MP4 when it exists, otherwise the raw capture.
    func fileURL(_ recording: Recording) -> URL? {
        let manager = FileManager.default
        if recording.hasMP4, manager.fileExists(atPath: mp4URL(recording).path) { return mp4URL(recording) }
        if manager.fileExists(atPath: tsURL(recording).path) { return tsURL(recording) }
        return nil
    }

    var freeSpace: Int64 {
        let values = try? RecordingManager.folder.resourceValues(forKeys: [.volumeAvailableCapacityKey])
        return Int64(values?.volumeAvailableCapacity ?? 0)
    }

    private func fileSize(_ url: URL) -> Int64 {
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        return (attributes?[.size] as? NSNumber)?.int64Value ?? 0
    }

    // MARK: Creating recordings

    func activeRecording(channelID: String) -> Recording? {
        recordings.first { $0.channelID == channelID && $0.status == .recording }
    }

    /// True when a booking exists for the programme that starts at `start` on this channel.
    func isScheduled(channelID: String, start: Date) -> Bool {
        let padding = Double(settings.padBeforeMinutes) * 60
        return recordings.contains { recording in
            recording.channelID == channelID && recording.status == .scheduled
                && abs(recording.start.addingTimeInterval(padding).timeIntervalSince(start)) < 120
        }
    }

    /// Starts recording right away, until `end`.
    func recordNow(channel: Channel, title: String, end: Date, userAgent: String) {
        let start = Date()
        var recording = Recording(
            channelID: channel.id, channelName: channel.name, title: title, url: channel.url, userAgent: userAgent,
            start: start, end: max(end, start.addingTimeInterval(60)),
            baseName: uniqueBaseName(title: title, channel: channel.name, start: start)
        )
        recording.status = .scheduled
        recordings.insert(recording, at: 0)
        save()
        tick()
    }

    /// Books a future programme. Padding from Settings is added on both sides.
    func schedule(channel: Channel, programme: Programme, userAgent: String) {
        let start = programme.start.addingTimeInterval(-Double(settings.padBeforeMinutes) * 60)
        let end = programme.stop.addingTimeInterval(Double(settings.padAfterMinutes) * 60)
        let recording = Recording(
            channelID: channel.id, channelName: channel.name, title: programme.title, url: channel.url, userAgent: userAgent,
            start: start, end: end,
            baseName: uniqueBaseName(title: programme.title, channel: channel.name, start: programme.start)
        )
        recordings.insert(recording, at: 0)
        save()
        tick()
    }

    private func uniqueBaseName(title: String, channel: String, start: Date) -> String {
        let base = Recording.makeBaseName(title: title, channel: channel, start: start)
        var candidate = base
        var suffix = 2
        let manager = FileManager.default
        while recordings.contains(where: { $0.baseName == candidate })
                || manager.fileExists(atPath: RecordingManager.folder.appendingPathComponent(candidate + ".ts").path)
                || manager.fileExists(atPath: RecordingManager.folder.appendingPathComponent(candidate + ".mp4").path) {
            candidate = base + "_" + String(suffix)
            suffix += 1
        }
        return candidate
    }

    // MARK: Controlling recordings

    func stop(_ id: UUID) {
        guard let index = recordings.firstIndex(where: { $0.id == id }) else { return }
        if let recorder = recorders[id] {
            recorder.stop()     // completion arrives through the progress callback
        } else if recordings[index].status == .recording {
            finalize(id, message: "")
        } else if recordings[index].status == .scheduled {
            recordings.remove(at: index)
            save()
        }
    }

    func delete(_ id: UUID) {
        guard let index = recordings.firstIndex(where: { $0.id == id }) else { return }
        recorders[id]?.onProgress = nil
        recorders[id]?.stop()
        recorders[id] = nil
        if convertingID == id {
            converter?.cancel()
            converter = nil
            convertingID = nil
        }
        let recording = recordings[index]
        try? FileManager.default.removeItem(at: tsURL(recording))
        try? FileManager.default.removeItem(at: mp4URL(recording))
        recordings.remove(at: index)
        save()
    }

    /// Queues a finished recording for conversion to MP4.
    func convert(_ id: UUID) {
        guard let index = recordings.firstIndex(where: { $0.id == id }) else { return }
        guard recordings[index].status == .completed, recordings[index].hasTS, !recordings[index].hasMP4 else { return }
        recordings[index].status = .converting
        recordings[index].convertProgress = 0
        recordings[index].message = "Waiting to convert"
        save()
        startNextConversion()
    }

    // MARK: Housekeeping

    func tick() {
        let now = Date()
        var changed = false
        for recording in recordings {
            switch recording.status {
            case .scheduled:
                if now >= recording.end {
                    update(recording.id) {
                        $0.status = .failed
                        $0.message = "Missed: the app was not running at the scheduled time."
                    }
                    changed = true
                } else if now >= recording.start {
                    startRecorder(recording.id)
                    changed = true
                }
            case .recording:
                if recorders[recording.id] == nil {
                    if now < recording.end {
                        startRecorder(recording.id)     // resume after the app was closed or suspended
                    } else {
                        finalize(recording.id, message: "")
                    }
                    changed = true
                }
            default:
                break
            }
        }
        if !recorders.isEmpty && freeSpace > 0 && freeSpace < 200_000_000 {
            for id in Array(recorders.keys) {
                update(id) { $0.message = "Stopped: storage is almost full." }
                recorders[id]?.stop()
            }
        }
        UIApplication.shared.isIdleTimerDisabled = !recorders.isEmpty || convertingID != nil || keepAwakeForPlayback
        if changed || now.timeIntervalSince(lastSave) > 30 { save() }
        startNextConversion()
    }

    /// Set by the player screens so the display stays on while something is playing.
    var keepAwakeForPlayback = false {
        didSet { UIApplication.shared.isIdleTimerDisabled = keepAwakeForPlayback || !recorders.isEmpty || convertingID != nil }
    }

    private func update(_ id: UUID, _ change: (inout Recording) -> Void) {
        guard let index = recordings.firstIndex(where: { $0.id == id }) else { return }
        change(&recordings[index])
    }

    private func startRecorder(_ id: UUID) {
        guard recorders[id] == nil, let index = recordings.firstIndex(where: { $0.id == id }) else { return }
        let recording = recordings[index]
        guard let url = URL(string: recording.url) else {
            update(id) {
                $0.status = .failed
                $0.message = "The stream address is not valid."
            }
            return
        }
        var headers: [String: String] = [:]
        if !recording.userAgent.isEmpty { headers["User-Agent"] = recording.userAgent }

        let recorder = StreamRecorder(url: url, fileURL: tsURL(recording), headers: headers, endDate: recording.end)
        recorder.onProgress = { [weak self] progress in
            Task { @MainActor in self?.apply(progress, to: id) }
        }
        recorders[id] = recorder
        update(id) {
            $0.status = .recording
            $0.hasTS = true
            if $0.message.isEmpty { $0.message = "Connecting…" }
        }
        recorder.start()
    }

    private func apply(_ progress: StreamRecorder.Progress, to id: UUID) {
        guard recordings.contains(where: { $0.id == id }) else { return }
        update(id) {
            $0.bytes = progress.bytes
            $0.reconnects = progress.reconnects
        }
        switch progress.state {
        case .connecting:
            update(id) { $0.message = "Connecting…" }
        case .recording:
            update(id) { $0.message = "" }
        case .reconnecting:
            update(id) { $0.message = "Reconnecting: " + progress.message }
        case .finished, .stopped:
            recorders[id] = nil
            finalize(id, message: "")
        case .failed:
            recorders[id] = nil
            finalize(id, message: progress.message)
        case .idle:
            break
        }
    }

    private func finalize(_ id: UUID, message: String) {
        guard let index = recordings.firstIndex(where: { $0.id == id }) else { return }
        let size = fileSize(tsURL(recordings[index]))
        recordings[index].bytes = size
        if size > 200_000 {
            recordings[index].status = .completed
            recordings[index].hasTS = true
            var note = message
            if note.isEmpty && recordings[index].reconnects > 0 {
                note = "Resumed automatically \(recordings[index].reconnects) time(s)."
            }
            recordings[index].message = note
            if settings.autoConvertMP4 {
                recordings[index].status = .converting
                recordings[index].convertProgress = 0
            }
        } else {
            recordings[index].status = .failed
            recordings[index].hasTS = false
            recordings[index].message = message.isEmpty ? "No video was received from the channel." : message
            try? FileManager.default.removeItem(at: tsURL(recordings[index]))
        }
        save()
        startNextConversion()
    }

    private func save() {
        lastSave = Date()
        if let data = try? JSONEncoder().encode(recordings) {
            UserDefaults.standard.set(data, forKey: "recordings")
        }
    }

    // MARK: MP4 conversion (one at a time)

    private func startNextConversion() {
        guard convertingID == nil else { return }
        guard let next = recordings.last(where: { $0.status == .converting }) else { return }
        let id = next.id
        let source = tsURL(next)
        let destination = mp4URL(next)
        guard FileManager.default.fileExists(atPath: source.path) else {
            update(id) {
                $0.status = .completed
                $0.message = "The original capture is missing."
            }
            return
        }
        convertingID = id
        update(id) { $0.message = "Converting to MP4…" }

        let job = MP4Converter(source: source, destination: destination)
        converter = job
        job.start(
            progress: { [weak self] fraction in
                self?.update(id) { $0.convertProgress = fraction }
            },
            completion: { [weak self] succeeded in
                self?.conversionFinished(id, succeeded: succeeded)
            }
        )
    }

    private func conversionFinished(_ id: UUID, succeeded: Bool) {
        converter = nil
        convertingID = nil
        if let index = recordings.firstIndex(where: { $0.id == id }) {
            recordings[index].status = .completed
            recordings[index].convertProgress = 0
            if succeeded {
                recordings[index].hasMP4 = true
                recordings[index].bytes = fileSize(mp4URL(recordings[index]))
                if recordings[index].message.hasPrefix("Converting") { recordings[index].message = "" }
                if !settings.keepTS {
                    try? FileManager.default.removeItem(at: tsURL(recordings[index]))
                    recordings[index].hasTS = false
                }
            } else {
                try? FileManager.default.removeItem(at: mp4URL(recordings[index]))
                recordings[index].message = "MP4 conversion did not complete. The .ts recording was kept and still plays."
            }
        }
        save()
        startNextConversion()
    }
}

// MARK: - TS to MP4

/// Re-packages an MPEG-TS capture as MP4 without re-encoding (fast, no quality loss), using VLC's
/// stream output: the file is "played" into an MP4 writer instead of onto the screen.
@MainActor
final class MP4Converter {
    private let source: URL
    private let destination: URL
    private var player: VLCMediaPlayer?
    private var timer: Timer?
    private var startedAt = Date()
    private var lastFraction: Double = -1
    private var lastChangeAt = Date()
    private var sawActivity = false
    private var progress: ((Double) -> Void)?
    private var completion: ((Bool) -> Void)?

    init(source: URL, destination: URL) {
        self.source = source
        self.destination = destination
    }

    func start(progress: @escaping (Double) -> Void, completion: @escaping (Bool) -> Void) {
        self.progress = progress
        self.completion = completion
        try? FileManager.default.removeItem(at: destination)

        let created: VLCMedia? = VLCMedia(url: source)
        guard let media = created else {
            finish(false)
            return
        }
        // File names are plain ASCII (see Recording.makeBaseName), so the path needs no escaping here.
        media.addOption(":sout=#std{access=file,mux=mp4,dst=\(destination.path)}")
        media.addOption(":no-sout-all")

        let newPlayer = VLCMediaPlayer()
        newPlayer.media = media
        player = newPlayer
        startedAt = Date()
        lastChangeAt = Date()
        newPlayer.play()

        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.poll() }
        }
    }

    func cancel() {
        completion = nil
        finish(false)
    }

    private func poll() {
        guard let player = player else { return }
        let fraction = Double(player.position)
        if fraction != lastFraction {
            lastFraction = fraction
            lastChangeAt = Date()
            progress?(min(1, max(0, fraction)))
        }
        if player.isPlaying || fraction > 0 { sawActivity = true }

        let state = player.state
        let elapsed = Date().timeIntervalSince(startedAt)
        if state == .error {
            finish(false)
        } else if state == .ended || (state == .stopped && (sawActivity || elapsed > 10)) {
            finish(true)
        } else if Date().timeIntervalSince(lastChangeAt) > 90 {
            finish(false)       // no progress for a minute and a half: give up rather than hang forever
        }
    }

    private func finish(_ engineSucceeded: Bool) {
        timer?.invalidate()
        timer = nil
        if let player = player {
            player.stop()
            self.player = nil
        }
        let callback = completion
        completion = nil
        progress = nil
        guard let callback = callback else { return }
        guard engineSucceeded else {
            callback(false)
            return
        }
        // Give VLC a moment to write the MP4 index, then trust the result only if a sensibly sized file exists.
        let sourceURL = source
        let destinationURL = destination
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
            let manager = FileManager.default
            let sourceSize = ((try? manager.attributesOfItem(atPath: sourceURL.path))?[.size] as? NSNumber)?.int64Value ?? 0
            let outputSize = ((try? manager.attributesOfItem(atPath: destinationURL.path))?[.size] as? NSNumber)?.int64Value ?? 0
            callback(outputSize > 100_000 && outputSize > sourceSize / 4)
        }
    }
}
