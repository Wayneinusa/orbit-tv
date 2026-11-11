import SwiftUI

/// Full-screen playback for live TV, catch-up, movies, episodes and recordings.
///
/// Apple TV remote: up/down change channel, left opens the channel list, select shows the controls,
/// Menu goes back one step. For films: left/right skip 10 seconds.
/// iPad: tap for controls, swipe up/down to change channel, swipe right for the channel list.
struct PlayerScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var router: AppRouter
    @EnvironmentObject private var player: PlayerController
    @EnvironmentObject private var recordings: RecordingManager

    @State private var showControls = false
    @State private var showChannels = false
    @State private var showInfo = true
    @State private var infoToken = 0
    @State private var controlsToken = 0
    @State private var showAudio = false
    @State private var showSubtitles = false
    @State private var recordTarget: Channel?
    @State private var scrubValue: Double?

    private var isLive: Bool { player.item?.isLive ?? false }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            PlayerSurface(controller: player).ignoresSafeArea()
            inputLayer
            statusOverlay

            VStack(spacing: 10) {
                Spacer()
                if showInfo || showControls { infoBanner }
                if showControls { controlBar }
            }
            .padding(Theme.pad * 1.5)

            if showChannels { channelOverlay }
        }
        #if os(tvOS)
        .onExitCommand { back() }
        .onPlayPauseCommand {
            player.togglePause()
            flashInfo()
        }
        #endif
        .task(id: infoToken) {
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            if !Task.isCancelled { showInfo = false }
        }
        .task(id: controlsToken) {
            try? await Task.sleep(nanoseconds: 7_000_000_000)
            if !Task.isCancelled && !Theme.isTV { showControls = false }
        }
        .onChange(of: player.item?.id) { _ in flashInfo() }
        .onChange(of: player.status) { status in
            if status == .ended && !isLive { router.closePlayer() }
        }
        .confirmationDialog("Audio track", isPresented: $showAudio, titleVisibility: .visible) {
            ForEach(player.audioTracks) { track in
                Button(track.name + (track.id == player.currentAudio ? "  ✓" : "")) { player.selectAudio(track.id) }
            }
        }
        .confirmationDialog("Subtitles", isPresented: $showSubtitles, titleVisibility: .visible) {
            ForEach(player.subtitleTracks) { track in
                Button(track.name + (track.id == player.currentSubtitle ? "  ✓" : "")) { player.selectSubtitle(track.id) }
            }
        }
        .sheet(item: $recordTarget) { channel in
            RecordSheet(channel: channel)
        }
        #if os(iOS)
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
        #endif
    }

    // MARK: Input

    @ViewBuilder
    private var inputLayer: some View {
        #if os(tvOS)
        Button {
            showControls = true
            flashInfo()
        } label: {
            Color.clear
        }
        .buttonStyle(InvisibleButtonStyle())
        .disabled(showControls || showChannels)
        .onMoveCommand { direction in
            switch direction {
            case .up:
                if isLive { router.zap(1) } else { showControls = true }
            case .down:
                if isLive { router.zap(-1) } else { showControls = true }
            case .left:
                if isLive { showChannels = true } else { skip(-10) }
            case .right:
                if isLive { flashInfo() } else { skip(10) }
            @unknown default:
                break
            }
        }
        #else
        Color.clear
            .contentShape(Rectangle())
            .onTapGesture {
                if showChannels {
                    showChannels = false
                } else {
                    showControls.toggle()
                    touch()
                }
            }
            .gesture(
                DragGesture(minimumDistance: 40).onEnded { value in
                    let horizontal = value.translation.width
                    let vertical = value.translation.height
                    if abs(vertical) > abs(horizontal) {
                        if isLive { router.zap(vertical < 0 ? 1 : -1) }
                    } else if horizontal > 0 {
                        if isLive { showChannels = true }
                    } else {
                        showChannels = false
                    }
                }
            )
        #endif
    }

    private func back() {
        if showChannels {
            showChannels = false
        } else if showControls {
            showControls = false
        } else {
            router.closePlayer()
        }
    }

    private func flashInfo() {
        showInfo = true
        infoToken += 1
    }

    private func touch() {
        controlsToken += 1
    }

    private func skip(_ seconds: Int) {
        player.seek(bySeconds: seconds)
        flashInfo()
    }

    // MARK: Overlays

    @ViewBuilder
    private var statusOverlay: some View {
        if player.status == .loading {
            VStack(spacing: 12) {
                ProgressView()
                if player.reconnects > 0 {
                    Text("Reconnecting…").font(.footnote).foregroundColor(.white)
                }
            }
            .padding(24)
            .background(RoundedRectangle(cornerRadius: Theme.corner).fill(Color.black.opacity(0.5)))
            .allowsHitTesting(false)
        } else if player.status == .failed {
            VStack(spacing: 8) {
                Image(systemName: "exclamationmark.triangle").font(.title)
                Text("This stream could not be played").font(.subheadline)
            }
            .foregroundColor(.white)
            .padding(24)
            .background(RoundedRectangle(cornerRadius: Theme.corner).fill(Color.black.opacity(0.6)))
            .allowsHitTesting(false)
        } else if player.status == .paused {
            Image(systemName: "pause.fill")
                .font(.system(size: 54))
                .foregroundColor(.white.opacity(0.8))
                .allowsHitTesting(false)
        }
    }

    private var infoBanner: some View {
        TimelineView(.periodic(from: Date(), by: 30)) { context in
            HStack(alignment: .center, spacing: 16) {
                if let item = player.item, item.isLive, let channel = item.channel {
                    Text("\(channel.number)").font(.title2.bold()).foregroundColor(.white)
                    ChannelLogo(channel: channel, size: Theme.logoSize * 1.15)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(channel.name).font(.headline).foregroundColor(.white).lineLimit(1)
                        if let current = epg.current(for: channel, at: context.date) {
                            Text(current.title).font(.subheadline).foregroundColor(.white).lineLimit(1)
                            HStack(spacing: 8) {
                                Text(Format.time.string(from: current.start))
                                ThinProgressBar(value: current.progress(at: context.date), height: 4)
                                Text(Format.time.string(from: current.stop))
                            }
                            .font(.caption.monospacedDigit())
                            .foregroundColor(Theme.secondaryText)
                            if let next = epg.next(for: channel, after: context.date) {
                                Text("Next: \(next.title) · \(Format.time.string(from: next.start))")
                                    .font(.caption)
                                    .foregroundColor(Theme.secondaryText)
                                    .lineLimit(1)
                            }
                        }
                    }
                } else if let item = player.item {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(item.title).font(.headline).foregroundColor(.white).lineLimit(1)
                        if !item.subtitle.isEmpty {
                            Text(item.subtitle).font(.subheadline).foregroundColor(Theme.secondaryText).lineLimit(1)
                        }
                        if player.durationMs > 0 {
                            HStack(spacing: 8) {
                                Text(Format.clock(milliseconds: player.timeMs))
                                ThinProgressBar(value: player.progress, height: 4)
                                Text(Format.clock(milliseconds: player.durationMs))
                            }
                            .font(.caption.monospacedDigit())
                            .foregroundColor(Theme.secondaryText)
                        }
                    }
                }
                Spacer(minLength: 12)
                if let channel = player.item?.channel, recordings.activeRecording(channelID: channel.id) != nil {
                    HStack(spacing: 6) {
                        Circle().fill(Theme.live).frame(width: 10, height: 10)
                        Text("REC").font(.caption.weight(.bold)).foregroundColor(.white)
                    }
                }
                Text(Format.time.string(from: context.date)).font(.title3.monospacedDigit()).foregroundColor(.white)
            }
            .padding(Theme.pad * 1.2)
            .background(RoundedRectangle(cornerRadius: Theme.corner, style: .continuous).fill(Color.black.opacity(0.74)))
        }
        .allowsHitTesting(false)
    }

    private var controlBar: some View {
        VStack(spacing: 8) {
            #if os(iOS)
            if player.canSeek {
                Slider(
                    value: Binding(get: { scrubValue ?? player.progress }, set: { scrubValue = $0 }),
                    onEditingChanged: { editing in
                        touch()
                        if !editing, let value = scrubValue {
                            player.seek(toFraction: value)
                            scrubValue = nil
                        }
                    }
                )
                .tint(Theme.accent)
            }
            #endif
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: Theme.isTV ? 16 : 8) {
                    #if os(iOS)
                    IconButton(symbol: "chevron.backward", label: "Back") { router.closePlayer() }
                    #endif
                    if isLive {
                        IconButton(symbol: "list.bullet", label: "Channels") {
                            showControls = false
                            showChannels = true
                        }
                        IconButton(symbol: "chevron.up", label: "Next") {
                            router.zap(1)
                            touch()
                        }
                        IconButton(symbol: "chevron.down", label: "Previous") {
                            router.zap(-1)
                            touch()
                        }
                        if let channel = player.item?.channel {
                            IconButton(symbol: model.isFavorite(channel) ? "star.fill" : "star", label: "Favorite",
                                       active: model.isFavorite(channel)) {
                                model.toggleFavorite(channel)
                                touch()
                            }
                            recordButton(channel)
                        }
                    } else {
                        IconButton(symbol: "gobackward.10", label: "-10 s") {
                            skip(-10)
                            touch()
                        }
                        IconButton(symbol: player.status == .paused ? "play.fill" : "pause.fill",
                                   label: player.status == .paused ? "Play" : "Pause") {
                            player.togglePause()
                            touch()
                        }
                        IconButton(symbol: "goforward.10", label: "+10 s") {
                            skip(10)
                            touch()
                        }
                        IconButton(symbol: "goforward.60", label: "+1 min") {
                            skip(60)
                            touch()
                        }
                    }
                    if player.audioTracks.count > 1 {
                        IconButton(symbol: "waveform", label: "Audio") { showAudio = true }
                    }
                    if !player.subtitleTracks.isEmpty {
                        IconButton(symbol: "captions.bubble", label: "Subtitles") { showSubtitles = true }
                    }
                    IconButton(symbol: "aspectratio", label: player.aspectLabel) {
                        player.cycleAspect()
                        touch()
                    }
                }
                .padding(.horizontal, 8)
                .padding(.vertical, Theme.isTV ? 16 : 6)
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: Theme.corner, style: .continuous).fill(Color.black.opacity(0.74)))
    }

    private func recordButton(_ channel: Channel) -> some View {
        let active = recordings.activeRecording(channelID: channel.id)
        return IconButton(symbol: active == nil ? "record.circle" : "stop.circle.fill",
                          label: active == nil ? "Record" : "Stop rec.", tint: Theme.live) {
            if let active = active {
                recordings.stop(active.id)
            } else {
                recordTarget = channel
            }
            touch()
        }
    }

    private var channelOverlay: some View {
        let groups = model.visibleGroups
        let list = model.channels(in: router.selectedGroup)
        return HStack(spacing: 0) {
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    Button {
                        stepGroup(-1, in: groups)
                    } label: {
                        Image(systemName: "chevron.left").padding(10)
                    }
                    .buttonStyle(TileButtonStyle(fill: Theme.raised))
                    Text(router.selectedGroup).font(.headline).foregroundColor(.white).lineLimit(1)
                        .frame(maxWidth: .infinity)
                    Button {
                        stepGroup(1, in: groups)
                    } label: {
                        Image(systemName: "chevron.right").padding(10)
                    }
                    .buttonStyle(TileButtonStyle(fill: Theme.raised))
                }
                .padding(Theme.pad)

                TimelineView(.periodic(from: Date(), by: 30)) { context in
                    ScrollViewReader { proxy in
                        ScrollView {
                            LazyVStack(spacing: 4) {
                                ForEach(list) { channel in
                                    ChannelRow(channel: channel, now: context.date,
                                               isPlaying: player.item?.id == channel.id) {
                                        router.watch(channel)
                                        showChannels = false
                                    }
                                    .id(channel.id)
                                }
                            }
                            .padding(.horizontal, 8)
                            .padding(.bottom, 20)
                        }
                        .onAppear {
                            if let id = player.item?.channel?.id { proxy.scrollTo(id, anchor: .center) }
                        }
                    }
                }
            }
            .frame(width: Theme.overlayWidth)
            .background(Theme.background.opacity(0.95).ignoresSafeArea())
            Spacer(minLength: 0)
        }
    }

    private func stepGroup(_ offset: Int, in groups: [String]) {
        guard !groups.isEmpty else { return }
        let index = groups.firstIndex(of: router.selectedGroup) ?? 0
        router.selectedGroup = groups[(index + offset + groups.count) % groups.count]
    }
}

/// A button that draws nothing: on Apple TV it exists only to receive remote presses over the video.
struct InvisibleButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
    }
}
