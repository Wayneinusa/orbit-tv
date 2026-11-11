import SwiftUI
import UIKit

// MARK: - Look and feel

enum Theme {
    static let background = Color(red: 0.047, green: 0.059, blue: 0.082)
    static let panel = Color(red: 0.082, green: 0.102, blue: 0.137)
    static let raised = Color(red: 0.125, green: 0.153, blue: 0.2)
    static let accent = Color(red: 0.22, green: 0.55, blue: 0.98)
    static let live = Color(red: 0.94, green: 0.27, blue: 0.27)
    static let good = Color(red: 0.25, green: 0.78, blue: 0.47)
    static let secondaryText = Color.white.opacity(0.62)
    static let faintText = Color.white.opacity(0.38)

    #if os(tvOS)
    static let isTV = true
    static let rowHeight: CGFloat = 92
    static let railWidth: CGFloat = 120
    static let corner: CGFloat = 14
    static let pad: CGFloat = 24
    static let logoSize: CGFloat = 64
    static let posterWidth: CGFloat = 230
    static let guideChannelWidth: CGFloat = 360
    static let guideHours: Double = 3
    static let overlayWidth: CGFloat = 640
    #else
    static let isTV = false
    static let rowHeight: CGFloat = 58
    static let railWidth: CGFloat = 76
    static let corner: CGFloat = 10
    static let pad: CGFloat = 12
    static let logoSize: CGFloat = 40
    static let posterWidth: CGFloat = 132
    static let guideChannelWidth: CGFloat = 200
    static let guideHours: Double = 2.5
    static let overlayWidth: CGFloat = 380
    #endif
}

/// Button look used for every row, tile and icon so the iPad (touch) and Apple TV (focus) behave alike.
struct TileButtonStyle: ButtonStyle {
    var selected = false
    var fill: Color = .clear

    func makeBody(configuration: Configuration) -> some View {
        TileButtonBody(configuration: configuration, selected: selected, fill: fill)
    }
}

private struct TileButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let selected: Bool
    let fill: Color
    @Environment(\.isFocused) private var isFocused
    @Environment(\.isEnabled) private var isEnabled

    private var background: Color {
        if isFocused { return Theme.accent }
        if configuration.isPressed { return Theme.accent.opacity(0.55) }
        if selected { return Theme.accent.opacity(0.28) }
        return fill
    }

    var body: some View {
        configuration.label
            .foregroundColor(.white)
            .background(RoundedRectangle(cornerRadius: Theme.corner, style: .continuous).fill(background))
            .overlay(
                RoundedRectangle(cornerRadius: Theme.corner, style: .continuous)
                    .stroke(selected && !isFocused ? Theme.accent : Color.clear, lineWidth: 1.5)
            )
            .scaleEffect(isFocused ? 1.03 : 1)
            .opacity(isEnabled ? 1 : 0.4)
            .animation(.easeOut(duration: 0.12), value: isFocused)
    }
}

// MARK: - Small shared views

final class ImageCache {
    static let shared: NSCache<NSString, UIImage> = {
        let cache = NSCache<NSString, UIImage>()
        cache.countLimit = 600
        return cache
    }()
}

/// Remote logo or poster with an in-memory cache and a down-scaled copy to keep long lists light.
struct RemoteImage: View {
    let url: String
    var maxPixel: CGFloat = 200
    var fill = false

    @State private var image: UIImage?

    var body: some View {
        ZStack {
            if let image = image {
                Image(uiImage: image)
                    .resizable()
                    .aspectRatio(contentMode: fill ? .fill : .fit)
            } else {
                Color.clear
            }
        }
        .task(id: url) { await load() }
    }

    private func load() async {
        let address = url
        if address.isEmpty {
            image = nil
            return
        }
        if let cached = ImageCache.shared.object(forKey: address as NSString) {
            image = cached
            return
        }
        image = nil
        guard let target = URL(string: address) else { return }
        var request = URLRequest(url: target)
        request.timeoutInterval = 20
        guard let result = try? await URLSession.shared.data(for: request),
              let full = UIImage(data: result.0) else { return }
        let longest = max(full.size.width, full.size.height)
        var output = full
        if longest > maxPixel, longest > 0 {
            let scale = maxPixel / longest
            let size = CGSize(width: max(1, full.size.width * scale), height: max(1, full.size.height * scale))
            if let small = await full.byPreparingThumbnail(ofSize: size) { output = small }
        }
        ImageCache.shared.setObject(output, forKey: address as NSString)
        if !Task.isCancelled { image = output }
    }
}

struct ChannelLogo: View {
    let channel: Channel
    var size: CGFloat = Theme.logoSize

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.white.opacity(0.06))
            if channel.logo.isEmpty {
                Text(String(channel.name.prefix(2)).uppercased())
                    .font(.system(size: size * 0.36, weight: .bold))
                    .foregroundColor(Theme.secondaryText)
            } else {
                RemoteImage(url: channel.logo, maxPixel: 160).padding(4)
            }
        }
        .frame(width: size * 1.3, height: size)
    }
}

struct ThinProgressBar: View {
    var value: Double
    var color: Color = Theme.accent
    var height: CGFloat = 3

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.15))
                Capsule().fill(color).frame(width: max(0, min(1, value)) * geometry.size.width)
            }
        }
        .frame(height: height)
    }
}

struct SectionHeader: View {
    let title: String
    var subtitle: String = ""

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title).font(.headline).foregroundColor(.white)
            if !subtitle.isEmpty {
                Text(subtitle).font(.caption).foregroundColor(Theme.secondaryText)
            }
            Spacer()
        }
        .padding(.horizontal, Theme.pad)
        .padding(.vertical, Theme.pad * 0.7)
    }
}

struct EmptyState: View {
    let symbol: String
    let title: String
    var message: String = ""

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: symbol).font(.system(size: Theme.isTV ? 72 : 44)).foregroundColor(Theme.faintText)
            Text(title).font(.headline).foregroundColor(.white)
            if !message.isEmpty {
                Text(message).font(.subheadline).foregroundColor(Theme.secondaryText).multilineTextAlignment(.center)
            }
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Labelled icon button used in toolbars and the player's control bar.
struct IconButton: View {
    let symbol: String
    let label: String
    var active = false
    var tint: Color = .white
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(systemName: symbol)
                    .font(.system(size: Theme.isTV ? 30 : 20, weight: .semibold))
                    .foregroundColor(tint)
                Text(label).font(.caption2).lineLimit(1)
            }
            .frame(minWidth: Theme.isTV ? 130 : 64)
            .padding(.vertical, Theme.isTV ? 14 : 8)
            .padding(.horizontal, 6)
        }
        .buttonStyle(TileButtonStyle(selected: active, fill: Color.white.opacity(0.08)))
    }
}

// MARK: - Formatting

enum Format {
    static let time: DateFormatter = {
        let formatter = DateFormatter()
        formatter.timeStyle = .short
        formatter.dateStyle = .none
        return formatter
    }()

    static let day: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "EEE d MMM"
        return formatter
    }()

    static let dayTime: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return formatter
    }()

    static func range(_ programme: Programme) -> String {
        time.string(from: programme.start) + " – " + time.string(from: programme.stop)
    }

    static func bytes(_ count: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: count, countStyle: .file)
    }

    static func clock(milliseconds: Int) -> String {
        let total = max(0, milliseconds / 1000)
        let hours = total / 3600
        let minutes = (total % 3600) / 60
        let seconds = total % 60
        if hours > 0 { return String(format: "%d:%02d:%02d", hours, minutes, seconds) }
        return String(format: "%d:%02d", minutes, seconds)
    }
}
