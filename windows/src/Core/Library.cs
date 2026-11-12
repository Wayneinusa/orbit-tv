using System.IO.Compression;
using System.Text.Json;

namespace SuperiorIptv;

/// <summary>The library: playlists and everything loaded from them, plus favourites, history and the guide.</summary>
public sealed class Library
{
    private readonly object gate = new();
    private readonly Dictionary<string, PlaylistContent> contents = new();
    private Dictionary<string, List<Channel>> byGroup = new();
    private Dictionary<string, Channel> byId = new();
    private HashSet<string> favoriteSet = new();

    public Settings Settings { get; }
    public List<Playlist> Playlists { get; private set; } = new();
    public List<Channel> Channels { get; private set; } = new();
    public List<Movie> Movies { get; private set; } = new();
    public List<Series> Series { get; private set; } = new();
    public List<string> Groups { get; private set; } = new();
    public List<string> MovieGroups { get; private set; } = new();
    public List<string> SeriesGroups { get; private set; } = new();
    public List<string> Favorites { get; private set; } = new();
    public List<string> Recents { get; private set; } = new();

    public EpgData Epg { get; private set; } = new();
    public volatile bool EpgRefreshing;
    public volatile string EpgStatus = "";
    public volatile string BusyText = "";
    public volatile string ErrorText = "";

    /// <summary>Raised (possibly on a background thread) when the library or guide changed.</summary>
    public event Action Changed;

    public Library(Settings settings)
    {
        Settings = settings;
    }

    private void RaiseChanged() => Changed?.Invoke();

    // ---- Start-up

    public void LoadFromDisk()
    {
        Playlists = Storage.Load<List<Playlist>>(Storage.File("playlists.json")) ?? new List<Playlist>();
        Favorites = Storage.Load<List<string>>(Storage.File("favorites.json")) ?? new List<string>();
        favoriteSet = new HashSet<string>(Favorites);
        Recents = Storage.Load<List<string>>(Storage.File("recents.json")) ?? new List<string>();
        foreach (Playlist playlist in Playlists)
        {
            var cached = LoadCompressed<PlaylistContent>(ContentCachePath(playlist.Id));
            if (cached != null) contents[playlist.Id] = cached;
        }
        Rebuild();
        var guide = LoadCompressed<EpgData>(EpgCachePath);
        if (guide != null)
        {
            Epg = guide;
            if (guide.Updated != null) EpgStatus = "Guide updated " + guide.Updated.Value.ToLocalTime().ToString("g");
        }
        RaiseChanged();
    }

    /// <summary>Reloads playlists that are missing or older than the refresh interval, then the guide.</summary>
    public async Task RefreshStaleAsync()
    {
        double maxAge = Math.Max(1, Settings.PlaylistRefreshHours);
        foreach (Playlist playlist in Playlists.ToList())
        {
            bool missing;
            lock (gate) missing = !contents.ContainsKey(playlist.Id);
            double age = playlist.LastRefreshed == null ? double.MaxValue : (DateTime.UtcNow - playlist.LastRefreshed.Value).TotalHours;
            if (missing || age > maxAge) await RefreshAsync(playlist.Id).ConfigureAwait(false);
        }
        await RefreshEpgIfNeededAsync().ConfigureAwait(false);
    }

    // ---- Playlists

    public async Task<bool> AddAsync(Playlist playlist)
    {
        Playlists.Add(playlist);
        bool loaded = await RefreshAsync(playlist.Id).ConfigureAwait(false);
        if (!loaded) Playlists.RemoveAll(p => p.Id == playlist.Id);
        SavePlaylists();
        RaiseChanged();
        return loaded;
    }

    public async Task<bool> UpdateAsync(Playlist playlist)
    {
        int index = Playlists.FindIndex(p => p.Id == playlist.Id);
        if (index < 0) return false;
        Playlists[index] = playlist;
        SavePlaylists();
        return await RefreshAsync(playlist.Id).ConfigureAwait(false);
    }

    public void Remove(string id)
    {
        Playlists.RemoveAll(p => p.Id == id);
        lock (gate) contents.Remove(id);
        try { File.Delete(ContentCachePath(id)); } catch (Exception) { }
        SavePlaylists();
        Rebuild();
        RaiseChanged();
    }

    public async Task RefreshAllAsync()
    {
        foreach (Playlist playlist in Playlists.ToList()) await RefreshAsync(playlist.Id).ConfigureAwait(false);
    }

    public async Task<bool> RefreshAsync(string id)
    {
        Playlist playlist = Playlists.FirstOrDefault(p => p.Id == id);
        if (playlist == null) return false;
        BusyText = $"Loading {playlist.Name}…";
        ErrorText = "";
        RaiseChanged();
        try
        {
            var (content, timeZone) = await LoadContentAsync(playlist, Settings.LiveFormat).ConfigureAwait(false);
            if (content.Channels.Count == 0 && content.Movies.Count == 0 && content.Series.Count == 0)
            {
                throw new UserException("The playlist is empty.");
            }
            lock (gate) contents[id] = content;
            playlist.LastRefreshed = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(timeZone)) playlist.ServerTimeZone = timeZone;
            SavePlaylists();
            Rebuild();
            SaveCompressed(ContentCachePath(id), content);
            return true;
        }
        catch (Exception ex)
        {
            ErrorText = $"{playlist.Name}: {Describe(ex)}";
            return false;
        }
        finally
        {
            BusyText = "";
            RaiseChanged();
        }
    }

    public static string Describe(Exception ex)
    {
        if (ex is UserException) return ex.Message;
        if (ex is TaskCanceledException || ex is OperationCanceledException) return "The server took too long to answer.";
        if (ex is HttpRequestException) return "Could not connect: " + ex.Message;
        return ex.Message;
    }

    public static async Task<(PlaylistContent Content, string TimeZone)> LoadContentAsync(Playlist playlist, string liveFormat)
    {
        if (playlist.Kind == PlaylistKind.Xtream)
        {
            var client = new XtreamClient(playlist.Server, playlist.Username, playlist.Password, playlist.UserAgent, liveFormat);
            return await client.LoadAllAsync(playlist.Id).ConfigureAwait(false);
        }

        string address = (playlist.Url ?? "").Trim().Trim('"');
        if (address.Length == 0) throw new UserException("The playlist address is empty.");
        if (Net.IsLocalPath(address) && !File.Exists(Net.LocalPath(address))) throw new UserException("The playlist file was not found.");
        string text = await Net.GetStringAsync(address, playlist.UserAgent, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
        if (!text.Contains("#EXTINF")) throw new UserException("That address did not return an M3U playlist.");
        PlaylistContent content = await Task.Run(() => M3UParser.Parse(text, playlist.Id)).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(playlist.UserAgent))
        {
            foreach (Channel channel in content.Channels)
            {
                if (string.IsNullOrEmpty(channel.UserAgent)) channel.UserAgent = playlist.UserAgent;
            }
        }
        return (content, "");
    }

    private void SavePlaylists() => Storage.Save(Storage.File("playlists.json"), Playlists);

    private void Rebuild()
    {
        var allChannels = new List<Channel>();
        var allMovies = new List<Movie>();
        var allSeries = new List<Series>();
        lock (gate)
        {
            foreach (Playlist playlist in Playlists)
            {
                if (!contents.TryGetValue(playlist.Id, out PlaylistContent content)) continue;
                allChannels.AddRange(content.Channels);
                allMovies.AddRange(content.Movies);
                allSeries.AddRange(content.Series);
            }
        }
        if (Playlists.Count > 1)
        {
            for (int i = 0; i < allChannels.Count; i++) allChannels[i].Number = i + 1;
        }

        var order = new List<string>();
        var grouped = new Dictionary<string, List<Channel>>();
        var lookup = new Dictionary<string, Channel>();
        foreach (Channel channel in allChannels)
        {
            if (!grouped.TryGetValue(channel.Group, out var list))
            {
                list = new List<Channel>();
                grouped[channel.Group] = list;
                order.Add(channel.Group);
            }
            list.Add(channel);
            lookup[channel.Id] = channel;
        }
        byGroup = grouped;
        byId = lookup;
        Channels = allChannels;
        Movies = allMovies;
        Series = allSeries;
        Groups = order;
        MovieGroups = allMovies.Select(m => m.Group).Distinct().ToList();
        SeriesGroups = allSeries.Select(s => s.Group).Distinct().ToList();
    }

    // ---- Lookups

    /// <summary>Groups as shown in the sidebar: the built-in ones first, then the playlist's own minus hidden ones.</summary>
    public List<string> VisibleGroups()
    {
        var result = new List<string>();
        if (Favorites.Count > 0) result.Add(ChannelGroups.Favorites);
        if (Recents.Count > 0) result.Add(ChannelGroups.Recent);
        result.Add(ChannelGroups.All);
        var hidden = new HashSet<string>(Settings.HiddenGroups);
        result.AddRange(Groups.Where(g => !hidden.Contains(g)));
        return result;
    }

    public List<Channel> ChannelsIn(string group)
    {
        switch (group)
        {
            case ChannelGroups.All:
                if (Settings.HiddenGroups.Count == 0) return Channels;
                var hidden = new HashSet<string>(Settings.HiddenGroups);
                return Channels.Where(c => !hidden.Contains(c.Group)).ToList();
            case ChannelGroups.Favorites:
                return Favorites.Select(ChannelById).Where(c => c != null).ToList();
            case ChannelGroups.Recent:
                return Recents.Select(ChannelById).Where(c => c != null).ToList();
            default:
                return byGroup.TryGetValue(group ?? "", out var list) ? list : new List<Channel>();
        }
    }

    public int CountIn(string group) => group switch
    {
        ChannelGroups.All => Channels.Count,
        ChannelGroups.Favorites => Favorites.Count,
        ChannelGroups.Recent => Recents.Count,
        _ => byGroup.TryGetValue(group ?? "", out var list) ? list.Count : 0
    };

    public Channel ChannelById(string id) => id != null && byId.TryGetValue(id, out Channel channel) ? channel : null;

    public Playlist PlaylistById(string id) => Playlists.FirstOrDefault(p => p.Id == id);

    public XtreamClient XtreamFor(string playlistId)
    {
        Playlist playlist = PlaylistById(playlistId);
        if (playlist == null || playlist.Kind != PlaylistKind.Xtream) return null;
        return new XtreamClient(playlist.Server, playlist.Username, playlist.Password, playlist.UserAgent, Settings.LiveFormat);
    }

    /// <summary>The channel above or below <paramref name="channel"/> inside <paramref name="group"/> (wraps around).</summary>
    public Channel Neighbour(Channel channel, string group, int offset)
    {
        List<Channel> list = ChannelsIn(group);
        int index = list.FindIndex(c => c.Id == channel.Id);
        if (index < 0)
        {
            list = ChannelsIn(ChannelGroups.All);
            index = list.FindIndex(c => c.Id == channel.Id);
        }
        if (index < 0 || list.Count == 0) return null;
        int next = ((index + offset) % list.Count + list.Count) % list.Count;
        return list[next];
    }

    // ---- Favourites and history

    public bool IsFavorite(Channel channel) => channel != null && favoriteSet.Contains(channel.Id);

    public void ToggleFavorite(Channel channel)
    {
        if (!favoriteSet.Remove(channel.Id))
        {
            favoriteSet.Add(channel.Id);
            Favorites.Add(channel.Id);
        }
        else
        {
            Favorites.Remove(channel.Id);
        }
        Storage.Save(Storage.File("favorites.json"), Favorites);
        RaiseChanged();
    }

    public void NoteWatched(Channel channel)
    {
        if (Recents.Count > 0 && Recents[0] == channel.Id) return;
        Recents.Remove(channel.Id);
        Recents.Insert(0, channel.Id);
        if (Recents.Count > 30) Recents.RemoveRange(30, Recents.Count - 30);
        Storage.Save(Storage.File("recents.json"), Recents);
    }

    // ---- Things to play

    public string UserAgentFor(string playlistId, Channel channel = null)
    {
        if (channel != null && !string.IsNullOrEmpty(channel.UserAgent)) return channel.UserAgent;
        Playlist playlist = PlaylistById(playlistId);
        if (playlist != null && !string.IsNullOrEmpty(playlist.UserAgent)) return playlist.UserAgent;
        return Settings.EffectiveUserAgent;
    }

    public PlayableItem Playable(Channel channel) => new()
    {
        Id = channel.Id,
        Kind = PlayKind.Live,
        Title = channel.Name,
        Url = channel.Url,
        UserAgent = UserAgentFor(channel.PlaylistId, channel),
        Channel = channel
    };

    public PlayableItem Playable(Movie movie) => new()
    {
        Id = movie.Id,
        Kind = PlayKind.Movie,
        Title = movie.Name,
        Subtitle = movie.Year,
        Url = movie.Url,
        UserAgent = UserAgentFor(movie.PlaylistId),
        ResumeKey = movie.Id,
        DownloadUrl = movie.Url,
        DownloadName = movie.Name
    };

    public PlayableItem Playable(Episode episode, Series show) => new()
    {
        Id = episode.Id,
        Kind = PlayKind.Episode,
        Title = show.Name,
        Subtitle = EpisodeLabel(episode),
        Url = episode.Url,
        UserAgent = UserAgentFor(show.PlaylistId),
        ResumeKey = episode.Id,
        DownloadUrl = episode.Url,
        DownloadName = show.Name
    };

    public static string EpisodeLabel(Episode episode) => $"S{episode.Season:00}E{episode.Number:00} {episode.Title}".Trim();

    public PlayableItem PlayableCatchup(Channel channel, Programme programme)
    {
        string address = Catchup.Url(channel, PlaylistById(channel.PlaylistId), programme.Start, programme.Stop, DateTime.UtcNow);
        if (address == null) return null;
        return new PlayableItem
        {
            Id = channel.Id + ":" + Catchup.Epoch(programme.Start),
            Kind = PlayKind.Catchup,
            Title = programme.Title,
            Subtitle = channel.Name + " · " + programme.Start.ToLocalTime().ToString("ddd HH:mm"),
            Url = address,
            UserAgent = UserAgentFor(channel.PlaylistId, channel),
            Channel = channel
        };
    }

    // ---- Guide

    /// <summary>Guide addresses advertised by the playlists plus any the user typed in.</summary>
    public List<string> EpgSources()
    {
        var result = new List<string>();
        foreach (Playlist playlist in Playlists)
        {
            string custom = (playlist.EpgUrl ?? "").Trim();
            if (custom.Length > 0 && !result.Contains(custom)) result.Add(custom);
            PlaylistContent content;
            lock (gate) contents.TryGetValue(playlist.Id, out content);
            if (content == null) continue;
            foreach (string url in content.EpgUrls)
            {
                if (!string.IsNullOrEmpty(url) && !result.Contains(url)) result.Add(url);
            }
        }
        return result;
    }

    public async Task RefreshEpgIfNeededAsync()
    {
        double maxAge = Math.Max(1, Settings.EpgRefreshHours);
        string sourceKey = string.Join("|", EpgSources());
        string previousKey = Storage.Load<List<string>>(Storage.File("guide-sources.json"))?.FirstOrDefault() ?? "";
        bool fresh = Epg.Updated != null && (DateTime.UtcNow - Epg.Updated.Value).TotalHours < maxAge && Epg.Programmes.Count > 0;
        if (fresh && sourceKey == previousKey) return;
        await RefreshEpgAsync().ConfigureAwait(false);
    }

    public async Task RefreshEpgAsync()
    {
        if (EpgRefreshing) return;
        List<string> sources = EpgSources();
        List<Channel> channels = Channels;
        if (sources.Count == 0 || channels.Count == 0)
        {
            EpgStatus = sources.Count == 0 ? "No guide source. Add an XMLTV address in the playlist settings." : "";
            RaiseChanged();
            return;
        }
        EpgRefreshing = true;
        try
        {
            var ids = new HashSet<string>(channels.Select(c => c.EpgId).Where(id => !string.IsNullOrEmpty(id)));
            var names = new HashSet<string>(channels.Select(c => c.Name.ToLowerInvariant()));
            DateTime start = DateTime.UtcNow.AddDays(-Math.Max(0, Settings.EpgPastDays));
            DateTime end = DateTime.UtcNow.AddDays(Math.Max(1, Settings.EpgFutureDays));

            var merged = new EpgData();
            int failures = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                EpgStatus = $"Updating guide ({i + 1} of {sources.Count})…";
                RaiseChanged();
                EpgData part = await FetchEpgAsync(sources[i], ids, names, start, end).ConfigureAwait(false);
                if (part != null) merged.Merge(part);
                else failures++;
            }
            if (merged.Programmes.Count == 0)
            {
                EpgStatus = failures > 0 ? "The guide could not be downloaded." : "The guide has no listings for these channels.";
                return;
            }
            merged.Updated = DateTime.UtcNow;
            Epg = merged;
            EpgStatus = "Guide updated " + DateTime.Now.ToString("g");
            Storage.Save(Storage.File("guide-sources.json"), new List<string> { string.Join("|", sources) });
            SaveCompressed(EpgCachePath, merged);
        }
        finally
        {
            EpgRefreshing = false;
            RaiseChanged();
        }
    }

    public static async Task<EpgData> FetchEpgAsync(string source, ISet<string> ids, ISet<string> names, DateTime start, DateTime end)
    {
        string temp = null;
        try
        {
            string path;
            if (Net.IsLocalPath(source))
            {
                path = Net.LocalPath(source);
            }
            else
            {
                temp = Path.Combine(Path.GetTempPath(), "siptv-guide-" + Guid.NewGuid().ToString("N") + ".tmp");
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                using HttpRequestMessage request = Net.Get(source, "");
                using HttpResponseMessage response = await Net.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                using (FileStream file = File.Create(temp))
                {
                    await response.Content.CopyToAsync(file, cts.Token).ConfigureAwait(false);
                }
                path = temp;
            }
            EpgData result = await Task.Run(() => XmltvParser.ParseFile(path, ids, names, start, end)).ConfigureAwait(false);
            return result.Programmes.Count == 0 ? null : result;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (temp != null)
            {
                try { File.Delete(temp); } catch (Exception) { }
            }
        }
    }

    // ---- Disk cache

    private static string ContentCachePath(string id) => Path.Combine(Storage.CacheFolder, "playlist-" + id + ".json.gz");
    private static string EpgCachePath => Path.Combine(Storage.CacheFolder, "guide.json.gz");

    private static readonly JsonSerializerOptions CacheOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private static T LoadCompressed<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using FileStream file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<T>(gzip, CacheOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void SaveCompressed<T>(string path, T value)
    {
        Task.Run(() =>
        {
            try
            {
                string temp = path + ".tmp";
                using (FileStream file = File.Create(temp))
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                {
                    JsonSerializer.Serialize(gzip, value, CacheOptions);
                }
                File.Move(temp, path, true);
            }
            catch (Exception)
            {
            }
        });
    }
}
