import SwiftUI

// MARK: - Shared category list + poster grid

struct PosterBrowser<Item: Identifiable>: View {
    let title: String
    let groups: [String]
    let items: [Item]
    let group: (Item) -> String
    let name: (Item) -> String
    let poster: (Item) -> String
    let onSelect: (Item) -> Void

    @State private var selected = ""

    var body: some View {
        let shown = selected.isEmpty ? items : items.filter { group($0) == selected }
        HStack(spacing: 0) {
            ScrollView {
                LazyVStack(spacing: 4) {
                    categoryButton(label: "All", value: "")
                    ForEach(groups, id: \.self) { entry in
                        categoryButton(label: entry, value: entry)
                    }
                }
                .padding(8)
            }
            .frame(width: Theme.isTV ? 380 : 210)
            .background(Theme.panel.opacity(0.55))

            VStack(spacing: 0) {
                SectionHeader(title: selected.isEmpty ? title : selected, subtitle: "\(shown.count) titles")
                ScrollView {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: Theme.posterWidth), spacing: 14)], spacing: 14) {
                        ForEach(shown) { item in
                            Button {
                                onSelect(item)
                            } label: {
                                PosterTile(name: name(item), poster: poster(item))
                            }
                            .buttonStyle(TileButtonStyle())
                        }
                    }
                    .padding(Theme.pad)
                }
            }
        }
    }

    private func categoryButton(label: String, value: String) -> some View {
        Button {
            selected = value
        } label: {
            Text(label)
                .font(.subheadline)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 12)
                .frame(height: Theme.rowHeight * 0.78)
        }
        .buttonStyle(TileButtonStyle(selected: selected == value))
    }
}

struct PosterTile: View {
    let name: String
    let poster: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Color.white.opacity(0.06)
                .aspectRatio(2.0 / 3.0, contentMode: .fit)
                .overlay(
                    ZStack {
                        Image(systemName: "film").font(.title).foregroundColor(Theme.faintText)
                        RemoteImage(url: poster, maxPixel: 420, fill: true)
                    }
                )
                .clipShape(RoundedRectangle(cornerRadius: Theme.corner, style: .continuous))
            Text(name)
                .font(.caption)
                .lineLimit(2)
                .multilineTextAlignment(.leading)
                .frame(maxWidth: .infinity, minHeight: Theme.isTV ? 60 : 32, alignment: .topLeading)
        }
        .padding(6)
    }
}

// MARK: - Movies

struct MoviesView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selected: Movie?

    var body: some View {
        if model.movies.isEmpty {
            EmptyState(symbol: "film", title: "No movies", message: "This playlist does not include a movie library.")
        } else {
            PosterBrowser(title: "All movies", groups: model.movieGroups, items: model.movies,
                          group: { $0.group }, name: { $0.name }, poster: { $0.poster },
                          onSelect: { selected = $0 })
                .sheet(item: $selected) { movie in
                    MovieDetailView(movie: movie)
                }
        }
    }
}

struct MovieDetailView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var router: AppRouter
    @Environment(\.dismiss) private var dismiss
    let movie: Movie

    @State private var info = XtreamVODInfo()

    var body: some View {
        let resumeAt = ResumeStore.position(for: movie.id)
        let facts = [movie.year.isEmpty ? info.year : movie.year, info.genre, info.duration,
                     movie.rating.isEmpty ? "" : "★ " + movie.rating].filter { !$0.isEmpty }
        ScrollView {
            HStack(alignment: .top, spacing: Theme.pad * 2) {
                PosterTile(name: "", poster: movie.poster)
                    .frame(width: Theme.isTV ? 340 : 190)
                VStack(alignment: .leading, spacing: 12) {
                    Text(movie.name).font(.title2.bold()).foregroundColor(.white)
                    if !facts.isEmpty {
                        Text(facts.joined(separator: " · ")).font(.subheadline).foregroundColor(Theme.secondaryText)
                    }
                    if !info.plot.isEmpty {
                        Text(info.plot).font(.body).foregroundColor(.white.opacity(0.85))
                    }
                    if resumeAt > 0 {
                        DetailButton(symbol: "play.fill", title: "Resume from " + Format.clock(milliseconds: resumeAt)) {
                            play()
                        }
                        DetailButton(symbol: "gobackward", title: "Play from the beginning") {
                            ResumeStore.save(0, for: movie.id)
                            play()
                        }
                    } else {
                        DetailButton(symbol: "play.fill", title: "Play") { play() }
                    }
                    DetailButton(symbol: "xmark", title: "Close") { dismiss() }
                }
            }
            .padding(Theme.pad * 2)
        }
        .background(Theme.background.ignoresSafeArea())
        .task {
            guard let id = movie.xtreamID, let client = model.xtreamClient(for: movie.playlistID) else { return }
            if let loaded = try? await client.movieInfo(streamID: id) { info = loaded }
        }
    }

    private func play() {
        guard let item = model.playable(movie) else { return }
        dismiss()
        router.play(item)
    }
}

struct DetailButton: View {
    let symbol: String
    let title: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: symbol)
                Text(title).fontWeight(.semibold).lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
        }
        .buttonStyle(TileButtonStyle(fill: Theme.panel))
    }
}

// MARK: - Series

struct SeriesView: View {
    @EnvironmentObject private var model: AppModel
    @State private var selected: Series?

    var body: some View {
        if model.series.isEmpty {
            EmptyState(symbol: "rectangle.stack", title: "No series", message: "This playlist does not include a series library.")
        } else {
            PosterBrowser(title: "All series", groups: model.seriesGroups, items: model.series,
                          group: { $0.group }, name: { $0.name }, poster: { $0.poster },
                          onSelect: { selected = $0 })
                .sheet(item: $selected) { show in
                    SeriesDetailView(show: show)
                }
        }
    }
}

struct SeriesDetailView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var router: AppRouter
    @Environment(\.dismiss) private var dismiss
    let show: Series

    @State private var episodes: [Episode] = []
    @State private var season = 1
    @State private var isLoading = true

    var body: some View {
        let seasons = Array(Set(episodes.map { $0.season })).sorted()
        let visible = episodes.filter { $0.season == season }
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                HStack(alignment: .top, spacing: Theme.pad * 2) {
                    PosterTile(name: "", poster: show.poster)
                        .frame(width: Theme.isTV ? 260 : 150)
                    VStack(alignment: .leading, spacing: 10) {
                        Text(show.name).font(.title2.bold()).foregroundColor(.white)
                        if !show.year.isEmpty || !show.rating.isEmpty {
                            Text([show.year, show.rating.isEmpty ? "" : "★ " + show.rating].filter { !$0.isEmpty }.joined(separator: " · "))
                                .font(.subheadline)
                                .foregroundColor(Theme.secondaryText)
                        }
                        if !show.plot.isEmpty {
                            Text(show.plot).font(.footnote).foregroundColor(.white.opacity(0.85)).lineLimit(8)
                        }
                        DetailButton(symbol: "xmark", title: "Close") { dismiss() }
                    }
                }

                if isLoading {
                    HStack(spacing: 10) {
                        ProgressView()
                        Text("Loading episodes…").font(.footnote).foregroundColor(Theme.secondaryText)
                    }
                } else if episodes.isEmpty {
                    Text("No episodes were found for this series.").font(.footnote).foregroundColor(Theme.secondaryText)
                } else {
                    if seasons.count > 1 {
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack(spacing: 8) {
                                ForEach(seasons, id: \.self) { number in
                                    Button {
                                        season = number
                                    } label: {
                                        Text("Season \(number)").padding(.horizontal, 16).padding(.vertical, 10)
                                    }
                                    .buttonStyle(TileButtonStyle(selected: season == number, fill: Theme.panel))
                                }
                            }
                            .padding(.vertical, Theme.isTV ? 14 : 2)
                            .padding(.horizontal, Theme.isTV ? 14 : 0)
                        }
                    }
                    ForEach(visible) { episode in
                        Button {
                            guard let item = model.playable(episode, of: show) else { return }
                            dismiss()
                            router.play(item)
                        } label: {
                            HStack(spacing: 12) {
                                Text("\(episode.number)")
                                    .font(.headline.monospacedDigit())
                                    .foregroundColor(Theme.secondaryText)
                                    .frame(width: Theme.isTV ? 60 : 34)
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(episode.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                                    if !episode.plot.isEmpty {
                                        Text(episode.plot).font(.caption).foregroundColor(Theme.secondaryText).lineLimit(2)
                                    }
                                }
                                Spacer(minLength: 0)
                                if ResumeStore.position(for: episode.id) > 0 {
                                    Image(systemName: "clock").font(.caption).foregroundColor(Theme.secondaryText)
                                }
                                Image(systemName: "play.fill").font(.caption)
                            }
                            .padding(.horizontal, 12)
                            .padding(.vertical, 10)
                        }
                        .buttonStyle(TileButtonStyle(fill: Theme.panel))
                    }
                }
            }
            .padding(Theme.pad * 2)
        }
        .background(Theme.background.ignoresSafeArea())
        .task { await load() }
    }

    private func load() async {
        if !show.episodes.isEmpty {
            episodes = show.episodes
        } else if let id = show.xtreamID, let client = model.xtreamClient(for: show.playlistID) {
            let prefix = String(show.id.prefix(8))
            episodes = (try? await client.episodes(seriesID: id, idPrefix: prefix)) ?? []
        }
        season = episodes.first?.season ?? 1
        isLoading = false
    }
}
