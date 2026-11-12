namespace SuperiorIptv.Win;

/// <summary>Everything the app keeps alive for its whole lifetime, shared by all screens.</summary>
public sealed class AppServices : IDisposable
{
    public Settings Settings { get; }
    public Library Library { get; }
    public ResumeStore Resume { get; }
    public RecordingManager Recordings { get; }
    public DownloadManager Downloads { get; }
    public PlayerEngine Player { get; }

    public string SelectedGroup = ChannelGroups.All;

    // Navigation, provided by the main window.
    public Action<Channel> Preview = _ => { };
    public Action<Channel> Watch = _ => { };
    public Action<PlayableItem> PlayItem = _ => { };
    public Action<string> Toast = _ => { };
    public Action<string> GoTo = _ => { };

    public AppServices()
    {
        Settings = Settings.Load();
        Library = new Library(Settings);
        Resume = new ResumeStore();
        Player = new PlayerEngine(Settings, Resume);
        Recordings = new RecordingManager(Settings, new Mp4Converter(Settings));
        Downloads = new DownloadManager(Storage.File("downloads.json"), () => Settings.EffectiveDownloadsFolder, () => Settings.MaxDownloads);
    }

    // ---- Playing

    /// <summary>The finished download for a movie or episode, if there is one on disk.</summary>
    public DownloadItem LocalCopy(string sourceId)
    {
        DownloadItem item = Downloads.FindBySource(sourceId);
        return item != null && item.State == DownloadState.Completed && File.Exists(item.FilePath) ? item : null;
    }

    public void PlayMovie(Movie movie)
    {
        PlayableItem item = Library.Playable(movie);
        DownloadItem local = LocalCopy(movie.Id);
        if (local != null)
        {
            item.Url = local.FilePath;
            item.DownloadUrl = "";
        }
        PlayItem(item);
    }

    public void PlayEpisode(Episode episode, Series show)
    {
        PlayableItem item = Library.Playable(episode, show);
        DownloadItem local = LocalCopy(episode.Id);
        if (local != null)
        {
            item.Url = local.FilePath;
            item.DownloadUrl = "";
        }
        PlayItem(item);
    }

    public void PlayDownload(DownloadItem download)
    {
        if (!File.Exists(download.FilePath))
        {
            Toast("That file is not finished yet or was moved.");
            return;
        }
        PlayItem(new PlayableItem
        {
            Id = download.Id,
            Kind = PlayKind.Download,
            Title = download.Title,
            Subtitle = download.Subtitle,
            Url = download.FilePath,
            ResumeKey = string.IsNullOrEmpty(download.SourceId) ? "dl:" + download.Id : download.SourceId
        });
    }

    public void PlayRecording(Recording recording)
    {
        string path = recording.PlayablePath;
        if (path == null)
        {
            Toast("The recording file was not found.");
            return;
        }
        PlayItem(new PlayableItem
        {
            Id = recording.Id,
            Kind = PlayKind.Recording,
            Title = recording.Title,
            Subtitle = recording.ChannelName + " · " + recording.Start.ToLocalTime().ToString("g"),
            Url = path,
            ResumeKey = "rec:" + recording.Id
        });
    }

    // ---- Downloading

    public void Download(Movie movie)
    {
        string title = string.IsNullOrEmpty(movie.Year) || movie.Name.Contains(movie.Year) ? movie.Name : $"{movie.Name} ({movie.Year})";
        Downloads.Add(movie.Id, title, "", movie.Url, Library.UserAgentFor(movie.PlaylistId));
    }

    public void Download(Episode episode, Series show)
    {
        Downloads.Add(episode.Id, show.Name, Library.EpisodeLabel(episode), episode.Url, Library.UserAgentFor(show.PlaylistId));
    }

    /// <summary>Queues whatever is playing right now (a movie or an episode).</summary>
    public bool DownloadCurrent()
    {
        PlayableItem item = Player.Item;
        if (item == null || string.IsNullOrEmpty(item.DownloadUrl)) return false;
        Downloads.Add(item.Id, item.DownloadName, item.Kind == PlayKind.Episode ? item.Subtitle : "", item.DownloadUrl, item.UserAgent);
        return true;
    }

    // ---- Recording

    public void RecordNow(Channel channel, string title, DateTime endUtc)
    {
        Recordings.RecordNow(channel, title, endUtc, Library.UserAgentFor(channel.PlaylistId, channel));
        Toast($"Recording {channel.Name} until {Theme.Time(endUtc)}");
    }

    public void Dispose()
    {
        Settings.Save();
        Downloads.Dispose();
        Recordings.Dispose();
        Player.Dispose();
    }
}
