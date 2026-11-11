import SwiftUI

struct GuideSelection: Identifiable {
    let channel: Channel
    let programme: Programme
    var id: String { channel.id + "@" + String(programme.id) }
}

/// TV guide grid: one row per channel, time running left to right, paged in blocks of a few hours.
struct GuideView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var router: AppRouter
    @EnvironmentObject private var settings: AppSettings

    @State private var windowStart = GuideView.slot(for: Date())
    @State private var selection: GuideSelection?
    @State private var showGroups = false

    static func slot(for date: Date) -> Date {
        let seconds = (date.timeIntervalSince1970 / 1800).rounded(.down) * 1800
        return Date(timeIntervalSince1970: seconds)
    }

    private var windowLength: TimeInterval { Theme.guideHours * 3600 }
    private var pageStep: TimeInterval { windowLength - 1800 }

    var body: some View {
        let list = model.channels(in: router.selectedGroup)
        let windowEnd = windowStart.addingTimeInterval(windowLength)
        VStack(spacing: 0) {
            header(windowEnd: windowEnd)
            ruler
            if list.isEmpty {
                EmptyState(symbol: "calendar", title: "No channels in this group")
            } else {
                TimelineView(.periodic(from: Date(), by: 60)) { context in
                    ScrollView {
                        LazyVStack(spacing: 3) {
                            ForEach(list) { channel in
                                GuideRow(channel: channel, windowStart: windowStart, windowEnd: windowEnd, now: context.date,
                                         onChannel: { router.watch(channel) },
                                         onProgramme: { programme in
                                             selection = GuideSelection(channel: channel, programme: programme)
                                         })
                            }
                        }
                        .padding(.horizontal, 8)
                        .padding(.bottom, 20)
                    }
                }
            }
        }
        .sheet(item: $selection) { item in
            ProgrammeDetailView(channel: item.channel, programme: item.programme)
        }
        .sheet(isPresented: $showGroups) {
            GroupPickerSheet()
        }
    }

    private func header(windowEnd: Date) -> some View {
        HStack(spacing: 10) {
            Button {
                showGroups = true
            } label: {
                HStack(spacing: 6) {
                    Image(systemName: "line.3.horizontal.decrease.circle")
                    Text(router.selectedGroup).lineLimit(1)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 8)
            }
            .buttonStyle(TileButtonStyle(fill: Theme.raised))

            VStack(alignment: .leading, spacing: 2) {
                Text(Format.day.string(from: windowStart)).font(.subheadline.weight(.semibold)).foregroundColor(.white)
                Text(epg.statusText).font(.caption2).foregroundColor(Theme.secondaryText).lineLimit(1)
            }
            Spacer(minLength: 8)

            pageButton("chevron.left") { windowStart = windowStart.addingTimeInterval(-pageStep) }
            Button {
                windowStart = GuideView.slot(for: Date())
            } label: {
                Text("Now").padding(.horizontal, 14).padding(.vertical, 8)
            }
            .buttonStyle(TileButtonStyle(fill: Theme.raised))
            pageButton("chevron.right") { windowStart = windowStart.addingTimeInterval(pageStep) }
            pageButton(epg.isRefreshing ? "hourglass" : "arrow.clockwise") {
                Task { await epg.refresh(model: model, settings: settings) }
            }
            .disabled(epg.isRefreshing)
        }
        .padding(.horizontal, Theme.pad)
        .padding(.vertical, Theme.pad * 0.7)
    }

    private func pageButton(_ symbol: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).padding(.horizontal, 14).padding(.vertical, 8)
        }
        .buttonStyle(TileButtonStyle(fill: Theme.raised))
    }

    /// Half-hour marks above the grid.
    private var ruler: some View {
        let slots = Int(Theme.guideHours * 2)
        return HStack(spacing: 3) {
            Color.clear.frame(width: Theme.guideChannelWidth)
            HStack(spacing: 0) {
                ForEach(0..<slots, id: \.self) { index in
                    Text(Format.time.string(from: windowStart.addingTimeInterval(Double(index) * 1800)))
                        .font(.caption.monospacedDigit())
                        .foregroundColor(Theme.secondaryText)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .padding(.horizontal, 8)
        .frame(height: Theme.isTV ? 40 : 24)
    }
}

// MARK: - One channel's row

struct GuideRow: View {
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var recordings: RecordingManager
    let channel: Channel
    let windowStart: Date
    let windowEnd: Date
    let now: Date
    let onChannel: () -> Void
    let onProgramme: (Programme) -> Void

    private struct Cell: Identifiable {
        let programme: Programme
        let gap: CGFloat
        let width: CGFloat
        var id: Double { programme.id }
    }

    private var rowHeight: CGFloat { Theme.rowHeight * 0.9 }

    var body: some View {
        HStack(spacing: 3) {
            Button(action: onChannel) {
                HStack(spacing: 8) {
                    Text("\(channel.number)")
                        .font(.caption2.monospacedDigit())
                        .foregroundColor(Theme.secondaryText)
                        .frame(width: Theme.isTV ? 56 : 30, alignment: .trailing)
                    ChannelLogo(channel: channel, size: Theme.logoSize * 0.8)
                    Text(channel.name).font(.caption.weight(.semibold)).lineLimit(2)
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 6)
                .frame(width: Theme.guideChannelWidth, height: rowHeight)
            }
            .buttonStyle(TileButtonStyle(fill: Theme.panel))

            GeometryReader { geometry in
                let cells = layout(width: geometry.size.width)
                HStack(spacing: 0) {
                    if cells.isEmpty {
                        Text("No programme information")
                            .font(.caption)
                            .foregroundColor(Theme.faintText)
                            .padding(.horizontal, 10)
                            .frame(width: geometry.size.width, height: rowHeight, alignment: .leading)
                            .background(RoundedRectangle(cornerRadius: Theme.corner).fill(Theme.panel.opacity(0.5)))
                    } else {
                        ForEach(cells) { cell in
                            Button {
                                onProgramme(cell.programme)
                            } label: {
                                cellLabel(cell.programme)
                                    .frame(width: cell.width, height: rowHeight, alignment: .leading)
                            }
                            .buttonStyle(TileButtonStyle(fill: fill(for: cell.programme)))
                            .padding(.leading, cell.gap)
                        }
                    }
                }
                .frame(width: geometry.size.width, alignment: .leading)
                .clipped()
            }
            .frame(height: rowHeight)
        }
    }

    private func layout(width: CGFloat) -> [Cell] {
        let total = windowEnd.timeIntervalSince(windowStart)
        guard total > 0, width > 0 else { return [] }
        let items = epg.programmes(for: channel, from: windowStart, to: windowEnd)
        var cells: [Cell] = []
        var cursor: CGFloat = 0
        let spacing: CGFloat = 3
        for programme in items {
            let start = max(programme.start, windowStart)
            let stop = min(programme.stop, windowEnd)
            let x = CGFloat(start.timeIntervalSince(windowStart) / total) * width
            let end = CGFloat(stop.timeIntervalSince(windowStart) / total) * width
            let cellWidth = end - max(x, cursor) - spacing
            if cellWidth < 6 { continue }
            let gap = max(0, x - cursor)
            cells.append(Cell(programme: programme, gap: gap, width: cellWidth))
            cursor = max(x, cursor) + cellWidth + spacing
        }
        return cells
    }

    private func fill(for programme: Programme) -> Color {
        if programme.isLive(at: now) { return Theme.raised }
        if programme.stop <= now { return Theme.panel.opacity(0.45) }
        return Theme.panel
    }

    private func cellLabel(_ programme: Programme) -> some View {
        let isPast = programme.stop <= now
        let hasCatchup = isPast && Catchup.isAvailable(channel: channel, programme: programme, now: now)
        let isBooked = recordings.isScheduled(channelID: channel.id, start: programme.start)
        return VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 5) {
                if isBooked { Circle().fill(Theme.live).frame(width: 8, height: 8) }
                if hasCatchup { Image(systemName: "clock.arrow.circlepath").font(.caption2) }
                Text(programme.title)
                    .font(.caption.weight(.semibold))
                    .lineLimit(1)
            }
            Text(Format.time.string(from: programme.start))
                .font(.caption2.monospacedDigit())
                .foregroundColor(Theme.secondaryText)
            if programme.isLive(at: now) {
                ThinProgressBar(value: programme.progress(at: now))
            }
        }
        .opacity(isPast && !hasCatchup ? 0.55 : 1)
        .padding(.horizontal, 8)
    }
}

// MARK: - Programme details

struct ProgrammeDetailView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var router: AppRouter
    @EnvironmentObject private var recordings: RecordingManager
    @EnvironmentObject private var settings: AppSettings
    @Environment(\.dismiss) private var dismiss
    let channel: Channel
    let programme: Programme

    var body: some View {
        let now = Date()
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 12) {
                    ChannelLogo(channel: channel)
                    Text(channel.name).font(.headline).foregroundColor(Theme.secondaryText)
                }
                Text(programme.title).font(.title2.bold()).foregroundColor(.white)
                Text(Format.day.string(from: programme.start) + " · " + Format.range(programme))
                    .font(.subheadline)
                    .foregroundColor(Theme.secondaryText)
                if !programme.desc.isEmpty {
                    Text(programme.desc).font(.body).foregroundColor(.white.opacity(0.85))
                }

                if programme.isLive(at: now) {
                    actionButton("play.fill", "Watch live") {
                        dismiss()
                        router.watch(channel)
                    }
                    if Catchup.isAvailable(channel: channel, programme: programme, now: now) {
                        actionButton("backward.end.fill", "Watch from the start") { playCatchup() }
                    }
                    if let active = recordings.activeRecording(channelID: channel.id) {
                        actionButton("stop.circle.fill", "Stop recording") {
                            recordings.stop(active.id)
                            dismiss()
                        }
                    } else {
                        actionButton("record.circle", "Record until it ends") {
                            recordings.recordNow(
                                channel: channel, title: programme.title,
                                end: programme.stop.addingTimeInterval(Double(settings.padAfterMinutes) * 60),
                                userAgent: model.userAgent(for: channel.playlistID, channel: channel)
                            )
                            dismiss()
                        }
                    }
                } else if programme.start > now {
                    if recordings.isScheduled(channelID: channel.id, start: programme.start) {
                        Label("Recording is scheduled", systemImage: "checkmark.circle.fill")
                            .foregroundColor(Theme.good)
                    } else {
                        actionButton("record.circle", "Schedule recording") {
                            recordings.schedule(channel: channel, programme: programme,
                                                userAgent: model.userAgent(for: channel.playlistID, channel: channel))
                            dismiss()
                        }
                        Text("Keep Orbit TV open at the scheduled time. Apple does not let apps start recording while they are closed.")
                            .font(.caption)
                            .foregroundColor(Theme.secondaryText)
                    }
                } else if Catchup.isAvailable(channel: channel, programme: programme, now: now) {
                    actionButton("clock.arrow.circlepath", "Watch (catch-up)") { playCatchup() }
                } else {
                    Text("This programme has ended and the channel does not offer catch-up.")
                        .font(.footnote)
                        .foregroundColor(Theme.secondaryText)
                }

                actionButton("xmark", "Close") { dismiss() }
            }
            .padding(Theme.pad * 2)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Theme.background.ignoresSafeArea())
    }

    private func playCatchup() {
        guard let item = model.playableCatchup(channel, programme: programme) else { return }
        dismiss()
        router.play(item)
    }

    private func actionButton(_ symbol: String, _ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: symbol)
                Text(title).fontWeight(.semibold)
                Spacer()
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
        }
        .buttonStyle(TileButtonStyle(fill: Theme.panel))
    }
}

struct GroupPickerSheet: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var router: AppRouter
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            LazyVStack(spacing: 4) {
                Text("Channel group").font(.title3.bold()).foregroundColor(.white)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.bottom, 8)
                ForEach(model.visibleGroups, id: \.self) { group in
                    Button {
                        router.selectedGroup = group
                        dismiss()
                    } label: {
                        HStack {
                            Text(group).lineLimit(1)
                            Spacer()
                            Text("\(model.count(in: group))").font(.caption).foregroundColor(Theme.secondaryText)
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 12)
                    }
                    .buttonStyle(TileButtonStyle(selected: router.selectedGroup == group, fill: Theme.panel))
                }
            }
            .padding(Theme.pad * 2)
        }
        .background(Theme.background.ignoresSafeArea())
    }
}
