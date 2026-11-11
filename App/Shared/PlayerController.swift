import SwiftUI
import UIKit

// The VLC playback engine ships under a different module name per platform.
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

struct TrackOption: Identifiable, Equatable {
    let id: Int32
    let name: String
}

/// One video player. The main screen owns one; multi-view creates one per tile.
///
/// VLC is used because IPTV streams are mostly raw MPEG-TS over HTTP, which Apple's own player refuses.
/// State is read by polling twice a second rather than through delegate callbacks, which keeps this
/// file independent of the callback signatures that differ between VLCKit releases.
@MainActor
final class PlayerController: ObservableObject {
    enum Status: Equatable {
        case idle, loading, playing, paused, ended, failed
    }

    @Published private(set) var status: Status = .idle
    @Published private(set) var item: PlayableItem?
    @Published private(set) var timeMs = 0
    @Published private(set) var durationMs = 0
    @Published private(set) var audioTracks: [TrackOption] = []
    @Published private(set) var subtitleTracks: [TrackOption] = []
    @Published private(set) var currentAudio: Int32 = -1
    @Published private(set) var currentSubtitle: Int32 = -1
    @Published private(set) var aspectLabel = "Original"
    @Published private(set) var reconnects = 0
    @Published var muted = false { didSet { applyVolume() } }

    /// The view VLC draws into. It is created once and moved between screens (preview, full screen).
    let videoView = UIView()

    private let settings: AppSettings
    private let player = VLCMediaPlayer()
    private var timer: Timer?
    private var userStopped = true
    private var lastTimeMs = -1
    private var lastProgressAt = Date()
    private var startedAt = Date()
    private var retryAt: Date?
    private var pendingSeekMs = 0
    private var aspectIndex = 0
    private var tickCount = 0

    init(settings: AppSettings) {
        self.settings = settings
        videoView.backgroundColor = .black
        videoView.isUserInteractionEnabled = false
        player.drawable = videoView
    }

    var progress: Double {
        guard durationMs > 0 else { return 0 }
        return min(1, max(0, Double(timeMs) / Double(durationMs)))
    }

    var canSeek: Bool { durationMs > 0 && !(item?.isLive ?? true) }

    // MARK: Transport

    func play(_ newItem: PlayableItem) {
        saveResumePoint()
        item = newItem
        userStopped = false
        reconnects = 0
        retryAt = nil
        pendingSeekMs = ResumeStore.position(for: newItem.resumeKey)
        start()
    }

    private func start() {
        guard let item = item else { return }
        let created: VLCMedia? = VLCMedia(url: item.url)
        guard let media = created else {
            status = .failed
            return
        }
        media.addOption(":network-caching=\(max(300, settings.bufferMs))")
        media.addOption(":http-reconnect")
        if !item.userAgent.isEmpty { media.addOption(":http-user-agent=\(item.userAgent)") }

        player.media = media
        player.play()

        status = .loading
        timeMs = 0
        durationMs = 0
        lastTimeMs = -1
        lastProgressAt = Date()
        startedAt = Date()
        audioTracks = []
        subtitleTracks = []
        applyVolume()
        applyAspect()
        ensureTimer()
    }

    func stop() {
        saveResumePoint()
        userStopped = true
        retryAt = nil
        timer?.invalidate()
        timer = nil
        if item != nil { player.stop() }
        item = nil
        status = .idle
        timeMs = 0
        durationMs = 0
    }

    func togglePause() {
        guard item != nil else { return }
        if status == .paused {
            player.play()
            lastProgressAt = Date()
        } else if status == .playing {
            player.pause()
        } else if status == .ended || status == .failed {
            start()
        }
    }

    func seek(bySeconds seconds: Int) {
        guard canSeek else { return }
        let target = min(max(0, timeMs + seconds * 1000), max(0, durationMs - 2000))
        player.position = Float(Double(target) / Double(durationMs))
        timeMs = target
        lastProgressAt = Date()
    }

    func seek(toFraction fraction: Double) {
        guard canSeek else { return }
        let clamped = min(0.999, max(0, fraction))
        player.position = Float(clamped)
        timeMs = Int(clamped * Double(durationMs))
        lastProgressAt = Date()
    }

    // MARK: Tracks, volume, picture shape

    func selectAudio(_ id: Int32) {
        player.currentAudioTrackIndex = id
        currentAudio = id
    }

    func selectSubtitle(_ id: Int32) {
        player.currentVideoSubTitleIndex = id
        currentSubtitle = id
    }

    private func applyVolume() {
        let audio: VLCAudio? = player.audio
        audio?.volume = muted ? 0 : 100
    }

    func cycleAspect() {
        aspectIndex = (aspectIndex + 1) % 4
        applyAspect()
    }

    private func applyAspect() {
        var ratio = ""
        switch aspectIndex {
        case 1:
            ratio = "16:9"
            aspectLabel = "16:9"
        case 2:
            ratio = "4:3"
            aspectLabel = "4:3"
        case 3:
            let size = videoView.bounds.size
            if size.width > 1 && size.height > 1 { ratio = "\(Int(size.width)):\(Int(size.height))" }
            aspectLabel = "Stretch"
        default:
            aspectLabel = "Original"
        }
        // An empty string returns VLC to the stream's own shape.
        ratio.withCString { pointer in
            player.videoAspectRatio = UnsafeMutablePointer<CChar>(mutating: pointer)
        }
    }

    private func readTracks() {
        let audio = PlayerController.tracks(names: player.audioTrackNames, ids: player.audioTrackIndexes)
        if audio != audioTracks { audioTracks = audio }
        let subtitles = PlayerController.tracks(names: player.videoSubTitlesNames, ids: player.videoSubTitlesIndexes)
        if subtitles != subtitleTracks { subtitleTracks = subtitles }
        let audioIndex = player.currentAudioTrackIndex
        if audioIndex != currentAudio { currentAudio = audioIndex }
        let subtitleIndex = player.currentVideoSubTitleIndex
        if subtitleIndex != currentSubtitle { currentSubtitle = subtitleIndex }
    }

    private static func tracks(names: [Any]?, ids: [Any]?) -> [TrackOption] {
        let nameList = (names ?? []).map { ($0 as? String) ?? "Track" }
        let idList = (ids ?? []).compactMap { ($0 as? NSNumber)?.int32Value }
        var result: [TrackOption] = []
        for (index, id) in idList.enumerated() {
            let name = index < nameList.count ? nameList[index] : "Track \(index + 1)"
            result.append(TrackOption(id: id, name: name))
        }
        return result
    }

    // MARK: Polling

    private func ensureTimer() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.tick() }
        }
    }

    private func tick() {
        guard let item = item, !userStopped else { return }
        tickCount += 1

        let currentTime: VLCTime? = player.time
        let milliseconds = Int(currentTime?.intValue ?? 0)
        let media: VLCMedia? = player.media
        let length: VLCTime? = media?.length
        let total = Int(length?.intValue ?? 0)

        if milliseconds != lastTimeMs {
            lastTimeMs = milliseconds
            lastProgressAt = Date()
        }
        if milliseconds != timeMs { timeMs = milliseconds }
        if total != durationMs { durationMs = total }

        let state = player.state
        var newStatus: Status
        if state == .error {
            newStatus = .failed
        } else if state == .ended {
            newStatus = .ended
        } else if state == .paused {
            newStatus = .paused
        } else if state == .stopped {
            newStatus = Date().timeIntervalSince(startedAt) > 3 ? .ended : .loading
        } else if player.isPlaying && milliseconds > 0 {
            newStatus = .playing
        } else {
            newStatus = .loading
        }

        // Resume a movie or recording where the viewer left off.
        if newStatus == .playing && pendingSeekMs > 0 && total > 0 {
            if pendingSeekMs < total - 30_000 {
                player.position = Float(Double(pendingSeekMs) / Double(total))
            }
            pendingSeekMs = 0
        }

        if newStatus == .playing && tickCount % 4 == 0 { readTracks() }
        if newStatus == .playing && tickCount % 30 == 0 { saveResumePoint() }
        if newStatus == .ended && !item.isLive && !item.resumeKey.isEmpty {
            ResumeStore.save(0, for: item.resumeKey)
        }

        // Live streams drop all the time; put them back on air without the viewer doing anything.
        if item.isLive && settings.autoReconnect && newStatus != .paused {
            let stalled = Date().timeIntervalSince(lastProgressAt) > 20
            if newStatus == .failed || newStatus == .ended || stalled {
                if retryAt == nil {
                    retryAt = Date().addingTimeInterval(min(10, 1 + Double(reconnects) * 2))
                }
                newStatus = .loading
            } else if newStatus == .playing && Date().timeIntervalSince(startedAt) > 30 {
                reconnects = 0
            }
            if let at = retryAt, Date() >= at {
                retryAt = nil
                reconnects += 1
                start()
                return
            }
        }

        if newStatus != status { status = newStatus }
    }

    private func saveResumePoint() {
        guard let item = item, !item.resumeKey.isEmpty, durationMs > 0 else { return }
        if timeMs > 30_000 && timeMs < durationMs - 60_000 {
            ResumeStore.save(timeMs, for: item.resumeKey)
        } else if timeMs >= durationMs - 60_000 {
            ResumeStore.save(0, for: item.resumeKey)
        }
    }
}

// MARK: - Hosting the video view in SwiftUI

/// Shows a player's picture. Only one surface per player should be on screen at a time; whichever
/// appears last takes the video view over.
struct PlayerSurface: UIViewRepresentable {
    let controller: PlayerController

    func makeUIView(context: Context) -> SurfaceHostView {
        let view = SurfaceHostView()
        view.backgroundColor = .black
        view.clipsToBounds = true
        // Touches belong to the SwiftUI layers around the picture, not to the picture itself.
        view.isUserInteractionEnabled = false
        return view
    }

    func updateUIView(_ uiView: SurfaceHostView, context: Context) {
        uiView.host(controller.videoView)
    }
}

final class SurfaceHostView: UIView {
    func host(_ video: UIView) {
        if video.superview !== self {
            video.removeFromSuperview()
            video.frame = bounds
            video.autoresizingMask = [.flexibleWidth, .flexibleHeight]
            addSubview(video)
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        for subview in subviews { subview.frame = bounds }
    }
}
