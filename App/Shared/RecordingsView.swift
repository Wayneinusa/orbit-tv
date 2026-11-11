import SwiftUI

struct RecordingsView: View {
    @EnvironmentObject private var recordings: RecordingManager
    @EnvironmentObject private var router: AppRouter
    @EnvironmentObject private var settings: AppSettings

    @State private var target: Recording?

    private var sorted: [Recording] {
        func rank(_ status: Recording.Status) -> Int {
            switch status {
            case .recording: return 0
            case .converting: return 1
            case .scheduled: return 2
            case .completed, .failed: return 3
            }
        }
        return recordings.recordings.sorted { lhs, rhs in
            let left = rank(lhs.status)
            let right = rank(rhs.status)
            if left != right { return left < right }
            if lhs.status == .scheduled { return lhs.start < rhs.start }
            return lhs.start > rhs.start
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            SectionHeader(title: "Recordings", subtitle: "Free space: " + Format.bytes(recordings.freeSpace))
            if recordings.recordings.isEmpty {
                EmptyState(symbol: "record.circle", title: "No recordings yet",
                           message: "Hold a channel and choose Record, press Record while watching, or pick a future programme in the Guide.")
            } else {
                ScrollView {
                    LazyVStack(spacing: 6) {
                        ForEach(sorted) { recording in
                            HStack(spacing: 8) {
                                Button {
                                    target = recording
                                } label: {
                                    RecordingRow(recording: recording)
                                }
                                .buttonStyle(TileButtonStyle(fill: Theme.panel))
                                #if os(iOS)
                                if recording.status == .completed, let url = recordings.fileURL(recording) {
                                    ShareLink(item: url) {
                                        Image(systemName: "square.and.arrow.up")
                                            .font(.title3)
                                            .foregroundColor(.white)
                                            .frame(width: 48, height: Theme.rowHeight)
                                    }
                                }
                                #endif
                            }
                        }
                    }
                    .padding(.horizontal, Theme.pad)
                    .padding(.bottom, 20)
                }
            }
            Text(storageNote)
                .font(.caption2)
                .foregroundColor(Theme.faintText)
                .multilineTextAlignment(.center)
                .padding(8)
        }
        .confirmationDialog(
            target?.title ?? "Recording",
            isPresented: Binding(get: { target != nil }, set: { shown in if !shown { target = nil } }),
            titleVisibility: .visible,
            presenting: target
        ) { recording in
            if recordings.fileURL(recording) != nil {
                Button(recording.status == .recording ? "Play what has been recorded so far" : "Play") {
                    play(recording)
                }
            }
            if recording.status == .recording {
                Button("Stop recording") { recordings.stop(recording.id) }
            }
            if recording.status == .scheduled {
                Button("Cancel this booking") { recordings.stop(recording.id) }
            }
            if recording.status == .completed && recording.hasTS && !recording.hasMP4 {
                Button("Convert to MP4") { recordings.convert(recording.id) }
            }
            Button("Delete", role: .destructive) { recordings.delete(recording.id) }
        }
    }

    private var storageNote: String {
        #if os(tvOS)
        return "Apple TV gives apps no permanent storage. Recordings are kept in the app's cache and tvOS may remove them when space runs low, so watch them soon."
        #else
        return "Recordings are saved in the Files app under On My iPad › Orbit TV › Recordings."
        #endif
    }

    private func play(_ recording: Recording) {
        guard let url = recordings.fileURL(recording) else { return }
        let item = PlayableItem(id: recording.id.uuidString, kind: .recording, title: recording.title,
                                subtitle: recording.channelName + " · " + Format.dayTime.string(from: recording.start),
                                url: url, resumeKey: "rec:" + recording.id.uuidString)
        router.play(item)
    }
}

struct RecordingRow: View {
    let recording: Recording

    private var symbol: String {
        switch recording.status {
        case .scheduled: return "calendar.badge.clock"
        case .recording: return "record.circle.fill"
        case .converting: return "arrow.triangle.2.circlepath"
        case .completed: return "checkmark.circle.fill"
        case .failed: return "exclamationmark.triangle.fill"
        }
    }

    private var tint: Color {
        switch recording.status {
        case .scheduled: return Theme.accent
        case .recording: return Theme.live
        case .converting: return .orange
        case .completed: return Theme.good
        case .failed: return .yellow
        }
    }

    private var statusText: String {
        switch recording.status {
        case .scheduled:
            return "Scheduled"
        case .recording:
            return "Recording until " + Format.time.string(from: recording.end)
        case .converting:
            return recording.convertProgress > 0 ? "Converting to MP4 · \(Int(recording.convertProgress * 100))%" : "Waiting to convert to MP4"
        case .completed:
            return recording.hasMP4 ? "MP4" : "TS"
        case .failed:
            return "Failed"
        }
    }

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .font(.title3)
                .foregroundColor(tint)
                .frame(width: Theme.isTV ? 60 : 36)
            VStack(alignment: .leading, spacing: 3) {
                Text(recording.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                Text(recording.channelName + " · " + Format.dayTime.string(from: recording.start))
                    .font(.caption)
                    .foregroundColor(Theme.secondaryText)
                    .lineLimit(1)
                HStack(spacing: 8) {
                    Text(statusText).font(.caption2.weight(.semibold)).foregroundColor(tint)
                    if recording.bytes > 0 {
                        Text(Format.bytes(recording.bytes)).font(.caption2).foregroundColor(Theme.secondaryText)
                    }
                    if recording.reconnects > 0 {
                        Text("resumed ×\(recording.reconnects)").font(.caption2).foregroundColor(Theme.secondaryText)
                    }
                }
                if !recording.message.isEmpty {
                    Text(recording.message).font(.caption2).foregroundColor(Theme.secondaryText).lineLimit(2)
                }
                if recording.status == .converting {
                    ThinProgressBar(value: recording.convertProgress, color: .orange)
                }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .frame(minHeight: Theme.rowHeight)
    }
}
