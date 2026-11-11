import SwiftUI

struct SettingsView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var settings: AppSettings
    @EnvironmentObject private var recordings: RecordingManager

    @State private var editing: Playlist?
    @State private var adding = false
    @State private var showGroups = false

    var body: some View {
        NavigationStack {
            Form {
                Section("Playlists") {
                    ForEach(model.playlists) { playlist in
                        Button {
                            editing = playlist
                        } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(playlist.name)
                                    Text(SettingsView.summary(playlist)).font(.caption).foregroundColor(.secondary)
                                }
                                Spacer()
                                Image(systemName: "chevron.right").font(.caption).foregroundColor(.secondary)
                            }
                        }
                    }
                    Button("Add playlist…") { adding = true }
                    Button("Reload all playlists") {
                        Task { await model.refreshAll() }
                    }
                    Picker("Reload automatically", selection: $settings.playlistRefreshHours) {
                        Text("Every 12 hours").tag(12)
                        Text("Every day").tag(24)
                        Text("Every 3 days").tag(72)
                        Text("Every week").tag(168)
                    }
                }

                Section("TV guide") {
                    Text(epg.statusText.isEmpty ? "No guide loaded yet" : epg.statusText)
                        .font(.footnote)
                        .foregroundColor(.secondary)
                    Button(epg.isRefreshing ? "Updating…" : "Update guide now") {
                        Task { await epg.refresh(model: model, settings: settings) }
                    }
                    .disabled(epg.isRefreshing)
                    Picker("Update automatically", selection: $settings.epgRefreshHours) {
                        Text("Every 6 hours").tag(6)
                        Text("Every 12 hours").tag(12)
                        Text("Every day").tag(24)
                        Text("Every 2 days").tag(48)
                    }
                    Picker("Keep past days (for catch-up)", selection: $settings.epgPastDays) {
                        ForEach([0, 1, 2, 3, 7], id: \.self) { days in
                            Text(days == 0 ? "None" : "\(days) day\(days == 1 ? "" : "s")").tag(days)
                        }
                    }
                    Picker("Days ahead", selection: $settings.epgFutureDays) {
                        ForEach([1, 2, 3, 5, 7], id: \.self) { days in
                            Text("\(days) day\(days == 1 ? "" : "s")").tag(days)
                        }
                    }
                }

                Section("Playback") {
                    Picker("Buffer", selection: $settings.bufferMs) {
                        Text("0.5 s (fastest channel change)").tag(500)
                        Text("1 s").tag(1000)
                        Text("1.5 s (recommended)").tag(1500)
                        Text("3 s").tag(3000)
                        Text("5 s").tag(5000)
                        Text("10 s (unstable connections)").tag(10000)
                    }
                    Picker("Xtream stream format", selection: $settings.liveFormat) {
                        Text("MPEG-TS (recommended)").tag("ts")
                        Text("HLS (m3u8)").tag("m3u8")
                    }
                    Toggle("Reconnect automatically when a channel freezes", isOn: $settings.autoReconnect)
                    TextField("User-Agent (optional)", text: $settings.userAgent)
                        .autocorrectionDisabled(true)
                    Text("Changing the stream format takes effect after reloading the playlist.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }

                Section("Recording") {
                    Toggle("Convert recordings to MP4 automatically", isOn: $settings.autoConvertMP4)
                    Toggle("Keep the original .ts file after converting", isOn: $settings.keepTS)
                    Picker("Start early", selection: $settings.padBeforeMinutes) {
                        ForEach([0, 1, 2, 5, 10], id: \.self) { minutes in
                            Text(minutes == 0 ? "Off" : "\(minutes) min").tag(minutes)
                        }
                    }
                    Picker("Finish late", selection: $settings.padAfterMinutes) {
                        ForEach([0, 2, 5, 10, 15, 30], id: \.self) { minutes in
                            Text(minutes == 0 ? "Off" : "\(minutes) min").tag(minutes)
                        }
                    }
                    Text("Free space: " + Format.bytes(recordings.freeSpace))
                        .font(.footnote)
                        .foregroundColor(.secondary)
                    Text("Recordings are captured as .ts so that they can resume after any interruption, then turned into .mp4. If a stream stops, the recorder reconnects by itself until the end time. Keep the app open while recording: Apple suspends apps that are in the background.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }

                Section("Channel groups") {
                    Button("Choose which groups to show…") { showGroups = true }
                    Text("\(settings.hiddenGroups.count) hidden").font(.footnote).foregroundColor(.secondary)
                }

                Section("About") {
                    Text("Orbit TV " + SettingsView.version)
                    Text("Orbit TV is a media player. It contains no channels or content; you add a playlist supplied by your own TV provider.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
            .navigationTitle("Settings")
        }
        .sheet(item: $editing) { playlist in
            PlaylistSheet(playlist: playlist)
        }
        .sheet(isPresented: $adding) {
            PlaylistSheet(playlist: nil)
        }
        .sheet(isPresented: $showGroups) {
            HiddenGroupsSheet()
        }
    }

    static var version: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0"
    }

    static func summary(_ playlist: Playlist) -> String {
        var parts: [String] = []
        switch playlist.source {
        case .xtream(let server, let username, _):
            parts.append("Xtream · " + username + " @ " + XtreamClient.normalize(server))
        case .m3u:
            parts.append("M3U address")
        }
        if let refreshed = playlist.lastRefreshed {
            parts.append("loaded " + Format.dayTime.string(from: refreshed))
        }
        return parts.joined(separator: " · ")
    }
}

/// Add or edit a playlist, with account details for Xtream logins.
struct PlaylistSheet: View {
    @EnvironmentObject private var model: AppModel
    @Environment(\.dismiss) private var dismiss
    let playlist: Playlist?

    @State private var account: XtreamAccount?
    @State private var confirmDelete = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text(playlist == nil ? "Add playlist" : "Edit playlist").font(.title2.bold()).foregroundColor(.white)

                if let account = account {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Account: " + (account.status.isEmpty ? "unknown" : account.status))
                        if let expires = account.expires {
                            Text("Expires: " + Format.dayTime.string(from: expires))
                        }
                        Text("Connections in use: \(account.activeConnections) of \(account.maxConnections)")
                    }
                    .font(.footnote)
                    .foregroundColor(Theme.secondaryText)
                }

                PlaylistEditor(existing: playlist, onDone: { dismiss() })

                if let playlist = playlist {
                    Button {
                        confirmDelete = true
                    } label: {
                        Label("Remove this playlist", systemImage: "trash")
                            .padding(.horizontal, 20)
                            .padding(.vertical, 12)
                    }
                    .buttonStyle(TileButtonStyle(fill: Theme.live.opacity(0.35)))
                    .confirmationDialog("Remove “\(playlist.name)”?", isPresented: $confirmDelete, titleVisibility: .visible) {
                        Button("Remove", role: .destructive) {
                            model.remove(playlist.id)
                            dismiss()
                        }
                    }
                } else {
                    Button {
                        dismiss()
                    } label: {
                        Text("Cancel").padding(.horizontal, 20).padding(.vertical, 12)
                    }
                    .buttonStyle(TileButtonStyle(fill: Theme.raised))
                }
            }
            .padding(Theme.pad * 2)
        }
        .background(Theme.background.ignoresSafeArea())
        .task {
            guard let playlist = playlist, let client = model.xtreamClient(for: playlist.id) else { return }
            account = try? await client.account()
        }
    }
}

struct HiddenGroupsSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var settings: AppSettings
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            LazyVStack(spacing: 4) {
                HStack {
                    Text("Groups to show").font(.title3.bold()).foregroundColor(.white)
                    Spacer()
                    Button {
                        dismiss()
                    } label: {
                        Text("Done").padding(.horizontal, 16).padding(.vertical, 8)
                    }
                    .buttonStyle(TileButtonStyle(fill: Theme.accent.opacity(0.85)))
                }
                .padding(.bottom, 8)

                ForEach(model.groups, id: \.self) { group in
                    let hidden = settings.hiddenGroups.contains(group)
                    Button {
                        if hidden {
                            settings.hiddenGroups.removeAll { $0 == group }
                        } else {
                            settings.hiddenGroups.append(group)
                        }
                    } label: {
                        HStack {
                            Image(systemName: hidden ? "circle" : "checkmark.circle.fill")
                                .foregroundColor(hidden ? Theme.secondaryText : Theme.good)
                            Text(group).lineLimit(1)
                            Spacer()
                            Text("\(model.count(in: group))").font(.caption).foregroundColor(Theme.secondaryText)
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 12)
                    }
                    .buttonStyle(TileButtonStyle(fill: Theme.panel))
                }
            }
            .padding(Theme.pad * 2)
        }
        .background(Theme.background.ignoresSafeArea())
    }
}
