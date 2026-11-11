import SwiftUI

/// Everything the app keeps alive for its whole lifetime. Created lazily by SwiftUI once the app is running.
@MainActor
final class AppServices: ObservableObject {
    let settings: AppSettings
    let model: AppModel
    let epg: EPGModel
    let player: PlayerController
    let recordings: RecordingManager
    let router: AppRouter

    init() {
        let settings = AppSettings()
        let model = AppModel(settings: settings)
        let player = PlayerController(settings: settings)
        let router = AppRouter()
        router.attach(model: model, player: player)
        self.settings = settings
        self.model = model
        self.epg = EPGModel()
        self.player = player
        self.recordings = RecordingManager(settings: settings)
        self.router = router
    }
}

@main
struct OrbitTVApp: App {
    @StateObject private var services = AppServices()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(services.settings)
                .environmentObject(services.model)
                .environmentObject(services.epg)
                .environmentObject(services.player)
                .environmentObject(services.recordings)
                .environmentObject(services.router)
                .preferredColorScheme(.dark)
                .onChange(of: scenePhase) { phase in
                    // Coming back to the foreground: restart anything that was cut off while suspended.
                    if phase == .active { services.recordings.tick() }
                }
        }
    }
}

struct RootView: View {
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var player: PlayerController
    @EnvironmentObject private var recordings: RecordingManager
    @EnvironmentObject private var router: AppRouter

    var body: some View {
        ZStack {
            Theme.background.ignoresSafeArea()

            if !model.didBootstrap {
                ProgressView()
            } else if model.playlists.isEmpty {
                OnboardingView()
            } else {
                HStack(spacing: 0) {
                    SideRail()
                    sectionView
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                .opacity(router.fullscreen ? 0 : 1)
                .disabled(router.fullscreen)

                if router.fullscreen {
                    PlayerScreen()
                }
            }

            StatusBanner()
        }
        .task {
            await model.bootstrap()
            await epg.loadCache()
            await epg.refreshIfNeeded(model: model, settings: settings)
            // Keep the guide fresh when the app stays open for days (typical on an Apple TV).
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 1_800_000_000_000)
                await epg.refreshIfNeeded(model: model, settings: settings)
            }
        }
        .onChange(of: model.channels.count) { _ in
            guard model.didBootstrap else { return }
            Task { await epg.refreshIfNeeded(model: model, settings: settings) }
        }
        .onReceive(player.$item) { item in
            recordings.keepAwakeForPlayback = item != nil
        }
    }

    @ViewBuilder
    private var sectionView: some View {
        switch router.section {
        case .live: LiveTVView()
        case .guide: GuideView()
        case .movies: MoviesView()
        case .series: SeriesView()
        case .recordings: RecordingsView()
        case .multiview: MultiViewScreen()
        case .search: SearchView()
        case .settings: SettingsView()
        }
    }
}

// MARK: - Navigation rail

struct SideRail: View {
    @EnvironmentObject private var router: AppRouter
    @EnvironmentObject private var recordings: RecordingManager

    var body: some View {
        VStack(spacing: Theme.isTV ? 14 : 6) {
            ForEach(AppRouter.Section.allCases) { section in
                Button {
                    router.section = section
                } label: {
                    VStack(spacing: 4) {
                        ZStack(alignment: .topTrailing) {
                            Image(systemName: section.symbol)
                                .font(.system(size: Theme.isTV ? 30 : 20, weight: .semibold))
                            if section == .recordings && recordings.recordings.contains(where: { $0.status == .recording }) {
                                Circle().fill(Theme.live).frame(width: 9, height: 9).offset(x: 8, y: -4)
                            }
                        }
                        Text(section.title).font(.caption2).lineLimit(1).minimumScaleFactor(0.7)
                    }
                    .frame(width: Theme.railWidth - 16, height: Theme.isTV ? 84 : 54)
                }
                .buttonStyle(TileButtonStyle(selected: router.section == section))
            }
            Spacer(minLength: 0)
            TimelineView(.periodic(from: Date(), by: 20)) { context in
                Text(Format.time.string(from: context.date))
                    .font(.caption)
                    .foregroundColor(Theme.secondaryText)
            }
        }
        .padding(.vertical, Theme.pad)
        .frame(width: Theme.railWidth)
        .background(Theme.panel)
        #if os(tvOS)
        .focusSection()
        #endif
    }
}

/// Thin strip at the top for "loading…" and error messages.
struct StatusBanner: View {
    @EnvironmentObject private var model: AppModel

    var body: some View {
        VStack {
            if !model.busyText.isEmpty {
                HStack(spacing: 10) {
                    ProgressView()
                    Text(model.busyText).font(.footnote)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .background(Capsule().fill(Theme.raised))
            } else if !model.errorText.isEmpty && !model.playlists.isEmpty {
                Button {
                    model.errorText = ""
                } label: {
                    HStack(spacing: 10) {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundColor(.yellow)
                        Text(model.errorText).font(.footnote).lineLimit(2)
                        Image(systemName: "xmark").font(.caption)
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 10)
                }
                .buttonStyle(TileButtonStyle(fill: Theme.raised))
            }
            Spacer()
        }
        .padding(.top, 12)
        .foregroundColor(.white)
    }
}

// MARK: - First run

struct OnboardingView: View {
    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                Image(systemName: "dot.radiowaves.left.and.right")
                    .font(.system(size: Theme.isTV ? 80 : 54))
                    .foregroundColor(Theme.accent)
                Text("Welcome to Orbit TV").font(.largeTitle.bold()).foregroundColor(.white)
                Text("Add the playlist from your TV provider to get started. Orbit TV is a player only and does not include any channels.")
                    .font(.body)
                    .foregroundColor(Theme.secondaryText)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 700)
                PlaylistEditor(existing: nil, onDone: {})
                    .frame(maxWidth: Theme.isTV ? 1100 : 640)
            }
            .padding(40)
            .frame(maxWidth: .infinity)
        }
    }
}

struct PlaylistEditor: View {
    @EnvironmentObject private var model: AppModel
    let existing: Playlist?
    var onDone: () -> Void

    @State private var kind = 0
    @State private var name = ""
    @State private var server = ""
    @State private var username = ""
    @State private var password = ""
    @State private var address = ""
    @State private var epgURL = ""
    @State private var userAgent = ""
    @State private var working = false
    @State private var loaded = false

    var body: some View {
        VStack(alignment: .leading, spacing: Theme.isTV ? 22 : 14) {
            Picker("Playlist type", selection: $kind) {
                Text("Xtream Codes login").tag(0)
                Text("M3U address").tag(1)
            }
            .pickerStyle(.segmented)

            EntryField(title: "Name", prompt: "My TV", text: $name)
            if kind == 0 {
                EntryField(title: "Server", prompt: "http://example.com:8080", text: $server, isAddress: true)
                EntryField(title: "Username", prompt: "", text: $username, isAddress: true)
                EntryField(title: "Password", prompt: "", text: $password, isAddress: true, isSecret: true)
            } else {
                EntryField(title: "M3U address", prompt: "http://example.com/playlist.m3u", text: $address, isAddress: true)
            }
            EntryField(title: "Guide (XMLTV) address, optional", prompt: "Leave empty to use the playlist's own", text: $epgURL, isAddress: true)
            EntryField(title: "User-Agent, optional", prompt: "Leave empty unless your provider requires one", text: $userAgent, isAddress: true)

            if !model.errorText.isEmpty {
                Text(model.errorText).font(.footnote).foregroundColor(.yellow)
            }

            HStack(spacing: 14) {
                Button {
                    Task { await save() }
                } label: {
                    HStack {
                        if working { ProgressView() }
                        Text(working ? "Loading…" : (existing == nil ? "Add playlist" : "Save and reload"))
                            .fontWeight(.semibold)
                    }
                    .padding(.horizontal, 24)
                    .padding(.vertical, 12)
                }
                .buttonStyle(TileButtonStyle(fill: Theme.accent.opacity(0.85)))
                .disabled(working || !isValid)

                if existing != nil {
                    Button {
                        onDone()
                    } label: {
                        Text("Cancel").padding(.horizontal, 24).padding(.vertical, 12)
                    }
                    .buttonStyle(TileButtonStyle(fill: Theme.raised))
                }
            }
        }
        .padding(Theme.pad * 1.5)
        .background(RoundedRectangle(cornerRadius: Theme.corner).fill(Theme.panel))
        .onAppear(perform: fill)
    }

    private var isValid: Bool {
        if kind == 0 { return !server.isEmpty && !username.isEmpty && !password.isEmpty }
        return address.contains("://")
    }

    private func fill() {
        guard !loaded, let playlist = existing else { return }
        loaded = true
        name = playlist.name
        epgURL = playlist.epgURL
        userAgent = playlist.userAgent
        switch playlist.source {
        case .xtream(let savedServer, let savedUser, let savedPassword):
            kind = 0
            server = savedServer
            username = savedUser
            password = savedPassword
        case .m3u(let url):
            kind = 1
            address = url
        }
    }

    private func save() async {
        working = true
        defer { working = false }
        let trimmedName = name.trimmingCharacters(in: .whitespaces)
        let source: PlaylistSource = kind == 0
            ? .xtream(server: server.trimmingCharacters(in: .whitespaces),
                      username: username.trimmingCharacters(in: .whitespaces),
                      password: password.trimmingCharacters(in: .whitespaces))
            : .m3u(url: address.trimmingCharacters(in: .whitespaces))

        var playlist = existing ?? Playlist(name: "", source: source)
        playlist.name = trimmedName.isEmpty ? (kind == 0 ? "Xtream playlist" : "M3U playlist") : trimmedName
        playlist.source = source
        playlist.epgURL = epgURL.trimmingCharacters(in: .whitespaces)
        playlist.userAgent = userAgent.trimmingCharacters(in: .whitespaces)

        let succeeded: Bool
        if existing == nil {
            succeeded = await model.add(playlist)
        } else {
            succeeded = await model.update(playlist)
        }
        if succeeded { onDone() }
    }
}

/// Text entry that behaves on both platforms (the Apple TV shows its full-screen keyboard).
struct EntryField: View {
    let title: String
    let prompt: String
    @Binding var text: String
    var isAddress = false
    var isSecret = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.caption).foregroundColor(Theme.secondaryText)
            field
                .textInputAutocapitalization(capitalization)
                .autocorrectionDisabled(true)
                #if os(iOS)
                .keyboardType(isAddress && !isSecret ? .URL : .default)
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.08)))
                .foregroundColor(.white)
                #endif
        }
    }

    private var capitalization: TextInputAutocapitalization {
        isAddress ? TextInputAutocapitalization.never : TextInputAutocapitalization.words
    }

    @ViewBuilder
    private var field: some View {
        if isSecret {
            SecureField(prompt, text: $text)
        } else {
            TextField(prompt, text: $text)
        }
    }
}
