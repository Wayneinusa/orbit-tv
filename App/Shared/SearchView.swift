import SwiftUI

/// One search box across channels, movies, series and upcoming programmes.
struct SearchView: View {
    @EnvironmentObject private var model: AppModel
    @EnvironmentObject private var epg: EPGModel
    @EnvironmentObject private var router: AppRouter

    @State private var query = ""
    @State private var programmeHits: [GuideSelection] = []
    @State private var selectedMovie: Movie?
    @State private var selectedSeries: Series?
    @State private var selectedProgramme: GuideSelection?

    private var text: String { query.trimmingCharacters(in: .whitespaces) }

    var body: some View {
        let needle = text
        let active = needle.count >= 2
        let channelHits = active ? Array(model.channels.filter { SearchView.matches($0.name, needle) }.prefix(40)) : []
        let movieHits = active ? Array(model.movies.filter { SearchView.matches($0.name, needle) }.prefix(40)) : []
        let seriesHits = active ? Array(model.series.filter { SearchView.matches($0.name, needle) }.prefix(40)) : []

        VStack(spacing: 0) {
            EntryField(title: "Search channels, movies, series and the guide", prompt: "Type at least two letters", text: $query)
                .padding(Theme.pad)

            if !active {
                EmptyState(symbol: "magnifyingglass", title: "Search your library")
            } else if channelHits.isEmpty && movieHits.isEmpty && seriesHits.isEmpty && programmeHits.isEmpty {
                EmptyState(symbol: "magnifyingglass", title: "Nothing found for “\(needle)”")
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 4) {
                        if !channelHits.isEmpty {
                            SectionHeader(title: "Channels", subtitle: "\(channelHits.count)")
                            ForEach(channelHits) { channel in
                                ChannelRow(channel: channel, now: Date(), isPlaying: false) {
                                    router.watch(channel)
                                }
                            }
                        }
                        if !programmeHits.isEmpty {
                            SectionHeader(title: "On now and coming up", subtitle: "\(programmeHits.count)")
                            ForEach(programmeHits) { hit in
                                resultRow(symbol: "calendar", title: hit.programme.title,
                                          detail: hit.channel.name + " · " + Format.day.string(from: hit.programme.start) + " " + Format.range(hit.programme)) {
                                    selectedProgramme = hit
                                }
                            }
                        }
                        if !movieHits.isEmpty {
                            SectionHeader(title: "Movies", subtitle: "\(movieHits.count)")
                            ForEach(movieHits) { movie in
                                resultRow(symbol: "film", title: movie.name, detail: movie.group) {
                                    selectedMovie = movie
                                }
                            }
                        }
                        if !seriesHits.isEmpty {
                            SectionHeader(title: "Series", subtitle: "\(seriesHits.count)")
                            ForEach(seriesHits) { show in
                                resultRow(symbol: "rectangle.stack", title: show.name, detail: show.group) {
                                    selectedSeries = show
                                }
                            }
                        }
                    }
                    .padding(.horizontal, 8)
                    .padding(.bottom, 20)
                }
            }
        }
        .task(id: query) { await searchGuide() }
        .sheet(item: $selectedMovie) { movie in
            MovieDetailView(movie: movie)
        }
        .sheet(item: $selectedSeries) { show in
            SeriesDetailView(show: show)
        }
        .sheet(item: $selectedProgramme) { hit in
            ProgrammeDetailView(channel: hit.channel, programme: hit.programme)
        }
    }

    static func matches(_ value: String, _ needle: String) -> Bool {
        value.range(of: needle, options: [.caseInsensitive, .diacriticInsensitive]) != nil
    }

    private func resultRow(symbol: String, title: String, detail: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: symbol).foregroundColor(Theme.secondaryText).frame(width: Theme.isTV ? 60 : 36)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.subheadline.weight(.semibold)).lineLimit(1)
                    Text(detail).font(.caption).foregroundColor(Theme.secondaryText).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 10)
            .frame(height: Theme.rowHeight)
        }
        .buttonStyle(TileButtonStyle())
    }

    /// The guide can hold hundreds of thousands of entries, so it is searched off the main thread,
    /// and only after the user pauses typing.
    private func searchGuide() async {
        let needle = text
        guard needle.count >= 2 else {
            programmeHits = []
            return
        }
        try? await Task.sleep(nanoseconds: 400_000_000)
        if Task.isCancelled { return }

        let data = epg.data
        let channels = model.channels
        let now = Date()
        let hits = await Task.detached(priority: .userInitiated) { () -> [GuideSelection] in
            var found: [GuideSelection] = []
            for channel in channels {
                for programme in data.list(for: channel) where programme.stop > now {
                    if programme.title.range(of: needle, options: [.caseInsensitive, .diacriticInsensitive]) != nil {
                        found.append(GuideSelection(channel: channel, programme: programme))
                        if found.count >= 40 { return found }
                    }
                }
            }
            return found
        }.value
        if !Task.isCancelled { programmeHits = hits }
    }
}
