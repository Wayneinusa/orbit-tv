import SwiftUI

/// Live TV: groups on the left, channels in the middle, preview and programme info on the right.
struct LiveTVView: View {
    var body: some View {
        GeometryReader { geometry in
            let wide = geometry.size.width > 820
            HStack(spacing: 0) {
                GroupColumn()
                    .frame(width: max(170, geometry.size.width * (wide ? 0.2 : 0.32)))
                ChannelColumn(hasPreview: wide)
                    .frame(maxWidth: .infinity)
                if wide {
                    PreviewPane()
                        .frame(width: geometry.size.width * 0.4)
                }
            }
        }
    }
}

struct GroupColumn: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var router: AppRouter

    var body: some View {
        ScrollView {
            LazyVStack(spacing: 4) {
                ForEach(model.visibleGroups, id: \.self) { group in
                    Button {
                        router.selectedGroup = group
                    } label: {
                        HStack(spacing: 8) {
                            if let symbol = GroupColumn.symbol(for: group) {
                                Image(systemName: symbol).font(.caption)
                            }
                            Text(group).font(.subheadline).lineLimit(1)
                            Spacer(minLength: 4)
                            Text("\(model.count(in: group))")
                                .font(.caption2)
                                .foregroundColor(Theme.secondaryText)
                        }
                        .padding(.horizontal, 12)
                        .frame(height: Theme.rowHeight * 0.78)
                    }
                    .buttonStyle(TileButtonStyle(selected: router.selectedGroup == group))
                }
            }
            .padding(8)
        }
        .background(Theme.panel.opacity(0.55))
        #if os(tvOS)
        .focusSection()
        #endif
    }

    static func symbol(for group: String) -> String? {
        switch group {
        case ChannelGroup.favorites: return "star.fill"
        case ChannelGroup.recent: return "clock"
        case ChannelGroup.all: return "square.grid.2x2"
        default: return nil
        }
    }
}

struct ChannelColumn: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var router: AppRouter
    @EnvironmentObject private var player: PlayerController
    @EnvironmentObject private var recordings: RecordingManager
    let hasPreview: Bool

    @State private var recordTarget: Channel?

    var body: some View {
        let list = model.channels(in: router.selectedGroup)
        VStack(spacing: 0) {
            SectionHeader(title: router.selectedGroup, subtitle: "\(list.count) channels")
            if list.isEmpty {
                EmptyState(symbol: "tv.slash", title: "No channels here",
                           message: "Pick another group, or refresh the playlist in Settings.")
            } else {
                TimelineView(.periodic(from: Date(), by: 30)) { context in
                    ScrollViewReader { proxy in
                        ScrollView {
                            LazyVStack(spacing: 4) {
                                ForEach(list) { channel in
                                    ChannelRow(channel: channel, now: context.date,
                                               isPlaying: player.item?.id == channel.id) {
                                        if hasPreview {
                                            router.preview(channel)
                                        } else {
                                            router.watch(channel)
                                        }
                                    }
                                    .id(channel.id)
                                    .contextMenu {
                                        Button(model.isFavorite(channel) ? "Remove from Favorites" : "Add to Favorites") {
                                            model.toggleFavorite(channel)
                                        }
                                        Button("Watch full screen") { router.watch(channel) }
                                        if let active = recordings.activeRecording(channelID: channel.id) {
                                            Button("Stop recording") { recordings.stop(active.id) }
                                        } else {
                                            Button("Record…") { recordTarget = channel }
                                        }
                                    }
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
        }
        .sheet(item: $recordTarget) { channel in
            RecordSheet(channel: channel)
        }
    }
}

struct ChannelRow: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var recordings: RecordingManager
    let channel: Channel
    let now: Date
    let isPlaying: Bool
    let action: () -> Void

    var body: some View {
        let programme = epg.current(for: channel, at: now)
        Button(action: action) {
            HStack(spacing: 12) {
                Text("\(channel.number)")
                    .font(.caption.monospacedDigit())
                    .foregroundColor(Theme.secondaryText)
                    .frame(width: Theme.isTV ? 70 : 38, alignment: .trailing)
                ChannelLogo(channel: channel)
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 6) {
                        Text(channel.name).font(.subheadline.weight(.semibold)).lineLimit(1)
                        if model.isFavorite(channel) {
                            Image(systemName: "star.fill").font(.caption2).foregroundColor(.yellow)
                        }
                        if recordings.activeRecording(channelID: channel.id) != nil {
                            Circle().fill(Theme.live).frame(width: 8, height: 8)
                        }
                        if channel.catchup != nil {
                            Image(systemName: "clock.arrow.circlepath").font(.caption2).foregroundColor(Theme.secondaryText)
                        }
                    }
                    if let programme = programme {
                        Text(programme.title).font(.caption).foregroundColor(Theme.secondaryText).lineLimit(1)
                        ThinProgressBar(value: programme.progress(at: now))
                    }
                }
                Spacer(minLength: 0)
                if isPlaying {
                    Image(systemName: "play.fill").font(.caption).foregroundColor(.white)
                }
            }
            .padding(.horizontal, 10)
            .frame(height: Theme.rowHeight)
        }
        .buttonStyle(TileButtonStyle(selected: isPlaying))
    }
}

// MARK: - Preview pane

struct PreviewPane: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var router: AppRouter
    @EnvironmentObject private var player: PlayerController
    @EnvironmentObject private var recordings: RecordingManager

    @State private var recordTarget: Channel?

    var body: some View {
        VStack(alignment: .leading, spacing: Theme.pad) {
            ZStack {
                Color.black
                if player.item != nil && !router.fullscreen {
                    PlayerSurface(controller: player)
                    if player.status == .loading {
                        ProgressView()
                    } else if player.status == .failed || player.status == .ended {
                        Text("This channel is not responding").font(.footnote).foregroundColor(Theme.secondaryText)
                    }
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "tv").font(.system(size: Theme.isTV ? 60 : 36))
                        Text("Select a channel to preview").font(.footnote)
                    }
                    .foregroundColor(Theme.faintText)
                }
            }
            .aspectRatio(16.0 / 9.0, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: Theme.corner, style: .continuous))
            .onTapGesture {
                if player.item != nil { router.fullscreen = true }
            }

            if let channel = player.item?.channel {
                TimelineView(.periodic(from: Date(), by: 30)) { context in
                    details(channel, now: context.date)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(Theme.pad)
        .sheet(item: $recordTarget) { channel in
            RecordSheet(channel: channel)
        }
    }

    @ViewBuilder
    private func details(_ channel: Channel, now: Date) -> some View {
        let current = epg.current(for: channel, at: now)
        let upcoming = epg.upcoming(for: channel, after: now, limit: 6).filter { $0.start > now }
        let active = recordings.activeRecording(channelID: channel.id)

        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                ChannelLogo(channel: channel)
                VStack(alignment: .leading, spacing: 2) {
                    Text(channel.name).font(.headline).foregroundColor(.white).lineLimit(1)
                    Text(channel.group).font(.caption).foregroundColor(Theme.secondaryText).lineLimit(1)
                }
            }

            HStack(spacing: 8) {
                IconButton(symbol: "arrow.up.left.and.arrow.down.right", label: "Full screen") {
                    router.fullscreen = true
                }
                IconButton(symbol: model.isFavorite(channel) ? "star.fill" : "star",
                           label: "Favorite", active: model.isFavorite(channel)) {
                    model.toggleFavorite(channel)
                }
                IconButton(symbol: active == nil ? "record.circle" : "stop.circle.fill",
                           label: active == nil ? "Record" : "Stop rec.", tint: Theme.live) {
                    if let active = active {
                        recordings.stop(active.id)
                    } else {
                        recordTarget = channel
                    }
                }
            }

            if let current = current {
                Text(current.title).font(.subheadline.weight(.semibold)).foregroundColor(.white).lineLimit(2)
                Text(Format.range(current)).font(.caption).foregroundColor(Theme.secondaryText)
                ThinProgressBar(value: current.progress(at: now))
                if !current.desc.isEmpty {
                    Text(current.desc).font(.caption).foregroundColor(Theme.secondaryText).lineLimit(Theme.isTV ? 4 : 5)
                }
            } else {
                Text("No programme information").font(.caption).foregroundColor(Theme.faintText)
            }

            if !upcoming.isEmpty {
                Text("UP NEXT").font(.caption2.weight(.bold)).foregroundColor(Theme.faintText).padding(.top, 6)
                ForEach(upcoming.prefix(Theme.isTV ? 3 : 5)) { programme in
                    HStack(alignment: .firstTextBaseline, spacing: 10) {
                        Text(Format.time.string(from: programme.start))
                            .font(.caption.monospacedDigit())
                            .foregroundColor(Theme.secondaryText)
                        Text(programme.title).font(.caption).foregroundColor(.white).lineLimit(1)
                    }
                }
            }
        }
    }
}

// MARK: - "Record…" sheet

struct RecordSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var recordings: RecordingManager
    @Environment(\.dismiss) private var dismiss
    let channel: Channel

    var body: some View {
        let now = Date()
        let current = epg.current(for: channel, at: now)
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Text("Record \(channel.name)").font(.title3.bold()).foregroundColor(.white)
                Text("If the stream drops, recording reconnects and carries on by itself until the end time. "
                     + (settings.autoConvertMP4 ? "The file is converted to MP4 when it finishes." : "The file is saved as .ts; convert it to MP4 from Recordings."))
                    .font(.footnote)
                    .foregroundColor(Theme.secondaryText)

                if let current = current {
                    option("This programme: \(current.title)", detail: "until " + Format.time.string(from: current.stop)) {
                        start(title: current.title, end: current.stop.addingTimeInterval(Double(settings.padAfterMinutes) * 60))
                    }
                }
                ForEach([30, 60, 120, 180, 240], id: \.self) { minutes in
                    option(minutes < 60 ? "\(minutes) minutes" : "\(minutes / 60) hour\(minutes == 60 ? "" : "s")",
                           detail: "until " + Format.time.string(from: now.addingTimeInterval(Double(minutes) * 60))) {
                        start(title: current?.title ?? channel.name, end: Date().addingTimeInterval(Double(minutes) * 60))
                    }
                }
                Button {
                    dismiss()
                } label: {
                    Text("Cancel").frame(maxWidth: .infinity).padding(.vertical, 12)
                }
                .buttonStyle(TileButtonStyle(fill: Theme.raised))
            }
            .padding(Theme.pad * 2)
        }
        .background(Theme.background.ignoresSafeArea())
    }

    private func option(_ title: String, detail: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Image(systemName: "record.circle").foregroundColor(Theme.live)
                Text(title).lineLimit(1)
                Spacer()
                Text(detail).font(.caption).foregroundColor(Theme.secondaryText)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
        }
        .buttonStyle(TileButtonStyle(fill: Theme.panel))
    }

    private func start(title: String, end: Date) {
        recordings.recordNow(channel: channel, title: title, end: end,
                             userAgent: model.userAgent(for: channel.playlistID, channel: channel))
        dismiss()
    }
}
