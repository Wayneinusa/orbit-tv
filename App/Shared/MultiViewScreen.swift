import SwiftUI

private struct TileIndex: Identifiable {
    let id: Int
}

/// Watch two or four channels at once. One tile has the sound; select another tile to move it.
struct MultiViewScreen: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var router: AppRouter

    @State private var controllers: [PlayerController] = []
    @State private var channels: [Channel?] = [nil, nil, nil, nil]
    @State private var audioIndex = 0
    @State private var tileCount = 4
    @State private var picking: TileIndex?

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                Text("Multi-view").font(.headline).foregroundColor(.white)
                Text("Each screen uses one connection from your TV provider.")
                    .font(.caption)
                    .foregroundColor(Theme.secondaryText)
                    .lineLimit(1)
                Spacer()
                Button {
                    setTileCount(tileCount == 4 ? 2 : 4)
                } label: {
                    Label(tileCount == 4 ? "2 screens" : "4 screens",
                          systemImage: tileCount == 4 ? "rectangle.split.2x1" : "rectangle.split.2x2")
                        .padding(.horizontal, 14)
                        .padding(.vertical, 8)
                }
                .buttonStyle(TileButtonStyle(fill: Theme.raised))
            }
            .padding(.horizontal, Theme.pad)
            .padding(.vertical, Theme.pad * 0.7)

            if controllers.count == 4 {
                VStack(spacing: 8) {
                    HStack(spacing: 8) {
                        tile(0)
                        tile(1)
                    }
                    if tileCount == 4 {
                        HStack(spacing: 8) {
                            tile(2)
                            tile(3)
                        }
                    }
                }
                .padding(Theme.pad)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Spacer()
            }
        }
        .onAppear(perform: resume)
        .onDisappear {
            for controller in controllers { controller.stop() }
        }
        .sheet(item: $picking) { index in
            ChannelPickerSheet { channel in
                assign(channel, to: index.id)
            }
        }
    }

    private func tile(_ index: Int) -> some View {
        MultiViewTile(
            controller: controllers[index],
            channel: channels[index],
            hasAudio: audioIndex == index,
            onActivate: {
                if channels[index] == nil {
                    picking = TileIndex(id: index)
                } else {
                    setAudio(index)
                }
            },
            onChange: { picking = TileIndex(id: index) },
            onFullScreen: {
                if let channel = channels[index] {
                    for controller in controllers { controller.stop() }
                    router.section = .live
                    router.watch(channel)
                }
            },
            onClose: {
                controllers[index].stop()
                channels[index] = nil
            }
        )
    }

    private func resume() {
        if controllers.isEmpty {
            controllers = (0..<4).map { _ in PlayerController(settings: settings) }
        }
        for index in 0..<4 {
            if index < tileCount, let channel = channels[index], let item = model.playable(channel) {
                controllers[index].play(item)
            }
        }
        setAudio(audioIndex)
    }

    private func assign(_ channel: Channel, to index: Int) {
        guard index < controllers.count, let item = model.playable(channel) else { return }
        channels[index] = channel
        controllers[index].play(item)
        setAudio(index)
    }

    private func setAudio(_ index: Int) {
        audioIndex = index
        for (position, controller) in controllers.enumerated() {
            controller.muted = position != index
        }
    }

    private func setTileCount(_ count: Int) {
        tileCount = count
        if count == 2 {
            for index in 2..<controllers.count { controllers[index].stop() }
            if audioIndex > 1 { setAudio(0) }
        } else {
            for index in 2..<controllers.count {
                if let channel = channels[index], let item = model.playable(channel) { controllers[index].play(item) }
            }
            setAudio(audioIndex)
        }
    }
}

struct MultiViewTile: View {
    @ObservedObject var controller: PlayerController
    let channel: Channel?
    let hasAudio: Bool
    let onActivate: () -> Void
    let onChange: () -> Void
    let onFullScreen: () -> Void
    let onClose: () -> Void

    var body: some View {
        Button(action: onActivate) {
            ZStack {
                Color.black
                if let channel = channel {
                    PlayerSurface(controller: controller)
                    if controller.status == .loading {
                        ProgressView()
                    } else if controller.status == .failed || controller.status == .ended {
                        Text("Not responding").font(.caption).foregroundColor(Theme.secondaryText)
                    }
                    VStack {
                        Spacer()
                        HStack(spacing: 6) {
                            Image(systemName: hasAudio ? "speaker.wave.2.fill" : "speaker.slash.fill").font(.caption2)
                            Text(channel.name).font(.caption.weight(.semibold)).lineLimit(1)
                            Spacer()
                        }
                        .foregroundColor(.white)
                        .padding(6)
                        .background(Color.black.opacity(0.55))
                    }
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "plus.circle").font(.system(size: Theme.isTV ? 54 : 32))
                        Text("Add channel").font(.footnote)
                    }
                    .foregroundColor(Theme.secondaryText)
                }
            }
            .clipShape(RoundedRectangle(cornerRadius: Theme.corner - 3, style: .continuous))
            .padding(4)
        }
        .buttonStyle(TileButtonStyle(selected: hasAudio && channel != nil, fill: Theme.panel))
        .contextMenu {
            if channel != nil {
                Button("Change channel", action: onChange)
                Button("Watch full screen", action: onFullScreen)
                Button("Close this screen", role: .destructive, action: onClose)
            }
        }
    }
}

/// Searchable channel list used when filling a multi-view tile.
struct ChannelPickerSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var router: AppRouter
    @Environment(\.dismiss) private var dismiss
    let onPick: (Channel) -> Void

    @State private var query = ""

    private var results: [Channel] {
        let text = query.trimmingCharacters(in: .whitespaces)
        if text.isEmpty { return Array(model.channels(in: router.selectedGroup).prefix(300)) }
        return Array(model.channels.filter { $0.name.range(of: text, options: .caseInsensitive) != nil }.prefix(300))
    }

    var body: some View {
        VStack(spacing: 10) {
            HStack {
                Text("Choose a channel").font(.title3.bold()).foregroundColor(.white)
                Spacer()
                Button {
                    dismiss()
                } label: {
                    Text("Cancel").padding(.horizontal, 14).padding(.vertical, 8)
                }
                .buttonStyle(TileButtonStyle(fill: Theme.raised))
            }
            EntryField(title: "Search all channels (empty shows \(router.selectedGroup))", prompt: "Channel name", text: $query)
            TimelineView(.periodic(from: Date(), by: 60)) { context in
                ScrollView {
                    LazyVStack(spacing: 4) {
                        ForEach(results) { channel in
                            ChannelRow(channel: channel, now: context.date, isPlaying: false) {
                                onPick(channel)
                                dismiss()
                            }
                        }
                    }
                }
            }
        }
        .padding(Theme.pad * 1.5)
        .background(Theme.background.ignoresSafeArea())
    }
}
