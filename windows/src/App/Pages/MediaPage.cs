namespace SuperiorIptv.Win;

/// <summary>Movies or series: categories on the left, posters on the right. Movies can be ticked and downloaded together.</summary>
public sealed class MediaPage : PageBase
{
    private readonly bool movies;
    private readonly VirtualList categories = new() { RowHeight = Theme.S(42), Back = Theme.Panel };
    private readonly VirtualList grid = new() { TileWidth = Theme.S(168), RowHeight = Theme.S(300) };
    private readonly FlatButton select, download, selectAll;
    private List<string> categoryNames = new();
    private List<Movie> movieList = new();
    private List<Series> seriesList = new();
    private string category = "";
    private int ticks;

    public MediaPage(AppServices app, bool movies) : base(app)
    {
        this.movies = movies;
        grid.EmptyText = movies ? "This playlist does not include a movie library." : "This playlist does not include a series library.";
        categories.DrawItem = DrawCategory;
        categories.Activated += index =>
        {
            category = index == 0 ? "" : categoryNames[index - 1];
            LoadItems(true);
        };
        grid.DrawItem = DrawTile;
        grid.Activated += index =>
        {
            if (movies) Sheets.Movie(this, App, movieList[index]);
            else using (var dialog = new SeriesDialog(App, seriesList[index])) dialog.ShowDialog(this);
        };
        grid.ContextRequested += (index, point) =>
        {
            if (!movies) return;
            Movie movie = movieList[index];
            Painters.ShowMenu(this, point,
                ("Play", () => App.PlayMovie(movie)),
                ("Download", () =>
                {
                    App.Download(movie);
                    App.Toast("Added to Downloads: " + movie.Name);
                }),
                ("Details…", () => Sheets.Movie(this, App, movie)));
        };
        grid.CheckedChanged += UpdateButtons;

        select = new FlatButton("Select", Theme.Glyph.Select, (_, _) => ToggleSelect()) { Shown = movies };
        selectAll = new FlatButton("Select all", "", (_, _) =>
        {
            for (int i = 0; i < grid.Count; i++) grid.Checked.Add(i);
            grid.Invalidate();
            UpdateButtons();
        }) { Shown = false };
        download = new FlatButton("Download selected", Theme.Glyph.Download, (_, _) => DownloadChecked()) { Shown = false, Fill = Theme.Accent };
        Controls.AddRange(new Control[] { categories, grid, select, selectAll, download });
    }

    private int ItemCount => movies ? movieList.Count : seriesList.Count;

    protected override void Arrange()
    {
        int side = Math.Clamp(Width / 5, Theme.S(180), Theme.S(280));
        categories.SetBounds(0, 0, side, Height);
        grid.SetBounds(side, HeaderHeight, Width - side, Height - HeaderHeight);
        PlaceHeaderButtons(selectAll, download, select);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Prepare(e.Graphics);
        e.Graphics.Clear(Theme.Background);
        string title = category.Length == 0 ? (movies ? "All movies" : "All series") : category;
        string detail = grid.CheckMode ? "Tick the movies you want, then choose Download selected" : ItemCount + " titles";
        DrawHeader(e.Graphics, categories.Right + Theme.S(16), title, detail);
    }

    private void DrawCategory(Graphics g, int index, Rectangle bounds, ItemState state)
    {
        string name = index == 0 ? "All" : categoryNames[index - 1];
        bool active = index == 0 ? category.Length == 0 : categoryNames[index - 1] == category;
        Rectangle r = Painters.RowBackground(g, bounds, active, state.Hover);
        Theme.DrawText(g, name, Theme.Ui(10f), Theme.Text, new Rectangle(r.X + Theme.S(10), r.Y, r.Width - Theme.S(16), r.Height));
    }

    private void DrawTile(Graphics g, int index, Rectangle bounds, ItemState state)
    {
        if (movies)
        {
            Movie movie = movieList[index];
            string badge = "";
            DownloadItem item = App.Downloads.FindBySource(movie.Id);
            if (item != null) badge = item.State == DownloadState.Completed ? "DOWNLOADED" : item.State == DownloadState.Paused ? "PAUSED" : $"{item.Fraction:P0}";
            Painters.PosterTile(g, grid, bounds, state, movie.Name, movie.Poster, grid.CheckMode, badge);
        }
        else
        {
            Series show = seriesList[index];
            Painters.PosterTile(g, grid, bounds, state, show.Name, show.Poster, false, "");
        }
    }

    private void LoadItems(bool resetScroll)
    {
        categoryNames = movies ? App.Library.MovieGroups : App.Library.SeriesGroups;
        if (category.Length > 0 && !categoryNames.Contains(category)) category = "";
        categories.Count = categoryNames.Count + 1;
        if (movies) movieList = category.Length == 0 ? App.Library.Movies : App.Library.Movies.Where(m => m.Group == category).ToList();
        else seriesList = category.Length == 0 ? App.Library.Series : App.Library.Series.Where(s => s.Group == category).ToList();
        grid.Checked.Clear();
        grid.Count = ItemCount;
        if (resetScroll) grid.ScrollToTop();
        UpdateButtons();
        Invalidate();
    }

    private void ToggleSelect()
    {
        grid.CheckMode = !grid.CheckMode;
        grid.Checked.Clear();
        select.Selected = grid.CheckMode;
        select.Caption = grid.CheckMode ? "Cancel" : "Select";
        grid.Invalidate();
        UpdateButtons();
        Invalidate();
    }

    private void UpdateButtons()
    {
        download.Shown = selectAll.Shown = grid.CheckMode;
        download.Enabled = grid.Checked.Count > 0;
        download.Caption = grid.Checked.Count > 0 ? $"Download selected ({grid.Checked.Count})" : "Download selected";
        Arrange();
    }

    private void DownloadChecked()
    {
        List<Movie> picked = grid.Checked.OrderBy(i => i).Select(i => movieList[i]).ToList();
        foreach (Movie movie in picked) App.Download(movie);
        App.Toast(picked.Count == 1 ? "Added 1 movie to Downloads" : $"Added {picked.Count} movies to Downloads");
        ToggleSelect();
    }

    public override void OnShown() => LoadItems(false);

    public override void OnLibraryChanged() => LoadItems(false);

    public override void OnTick()
    {
        ticks++;
        // Keep the download badges on the posters up to date.
        if (movies && ticks % 4 == 0 && App.Downloads.ActiveCount > 0) grid.Invalidate();
    }
}

/// <summary>Recordings: running, scheduled and finished.</summary>
public sealed class RecordingsPage : PageBase
{
    private readonly VirtualList list = new() { RowHeight = Theme.S(84), EmptyText = "No recordings yet. Right-click a channel and choose Record, press Record while watching, or pick a future programme in the Guide." };
    private readonly FlatButton openFolder;
    private List<Recording> items = new();

    public RecordingsPage(AppServices app) : base(app)
    {
        list.DrawItem = DrawRow;
        list.Activated += index => ShowMenu(items[index], Cursor.Position);
        list.ContextRequested += (index, point) => ShowMenu(items[index], point);
        openFolder = new FlatButton("Open folder", Theme.Glyph.Folder, (_, _) => Sheets.OpenFolder(App.Settings.EffectiveRecordingsFolder));
        Controls.Add(list);
        Controls.Add(openFolder);
    }

    protected override void Arrange()
    {
        list.SetBounds(0, HeaderHeight, Width, Height - HeaderHeight);
        PlaceHeaderButtons(openFolder);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Prepare(e.Graphics);
        e.Graphics.Clear(Theme.Background);
        long free = Storage.FreeSpace(App.Settings.EffectiveRecordingsFolder);
        DrawHeader(e.Graphics, Theme.S(18), "Recordings", free >= 0 ? "Free space: " + Theme.Bytes(free) : App.Settings.EffectiveRecordingsFolder);
    }

    private static int Rank(RecordingStatus status) => status switch
    {
        RecordingStatus.Recording => 0,
        RecordingStatus.Converting => 1,
        RecordingStatus.Scheduled => 2,
        _ => 3
    };

    private void Reload()
    {
        items = App.Recordings.Snapshot()
            .OrderBy(r => Rank(r.Status))
            .ThenBy(r => r.Status == RecordingStatus.Scheduled ? r.Start.Ticks : -r.Start.Ticks)
            .ToList();
        list.Count = items.Count;
    }

    private void DrawRow(Graphics g, int index, Rectangle bounds, ItemState state)
    {
        Recording item = items[index];
        Rectangle r = Painters.RowBackground(g, bounds, state.Selected, state.Hover, Theme.Panel);
        (string glyph, Color color, string status) = item.Status switch
        {
            RecordingStatus.Scheduled => (Theme.Glyph.Clock, Theme.Accent, "Scheduled for " + item.Start.ToLocalTime().ToString("ddd HH:mm")),
            RecordingStatus.Recording => (Theme.Glyph.Record, Theme.Live, "Recording until " + Theme.Time(item.End)),
            RecordingStatus.Converting => (Theme.Glyph.Refresh, Theme.Warn, item.ConvertProgress > 0 ? $"Converting to MP4 · {item.ConvertProgress:P0}" : "Waiting to convert to MP4"),
            RecordingStatus.Completed => (Theme.Glyph.Check, Theme.Good, item.HasMp4 ? "MP4" : "TS"),
            _ => (Theme.Glyph.Warning, Theme.Warn, "Failed")
        };
        Theme.DrawGlyph(g, glyph, 15f, color, new Rectangle(r.X + Theme.S(6), r.Y, Theme.S(44), r.Height));
        int x = r.X + Theme.S(58), width = r.Width - Theme.S(110);
        Theme.DrawText(g, item.Title, Theme.Ui(10.5f, FontStyle.Bold), Theme.Text, new Rectangle(x, r.Y + Theme.S(6), width, Theme.S(22)));
        Theme.DrawText(g, item.ChannelName + " · " + item.Start.ToLocalTime().ToString("g"), Theme.Ui(9f), Theme.Secondary, new Rectangle(x, r.Y + Theme.S(28), width, Theme.S(18)));

        string line = status;
        if (item.Bytes > 0) line += " · " + Theme.Bytes(item.Bytes);
        if (item.Reconnects > 0) line += $" · resumed ×{item.Reconnects}";
        if (!string.IsNullOrEmpty(item.Message)) line += " · " + item.Message;
        Theme.DrawText(g, line, Theme.Ui(8.5f, FontStyle.Bold), color, new Rectangle(x, r.Y + Theme.S(48), width, Theme.S(18)));
        if (item.Status == RecordingStatus.Converting)
        {
            Theme.DrawProgress(g, new Rectangle(x, r.Bottom - Theme.S(10), width, Theme.S(4)), item.ConvertProgress, Theme.Warn);
        }
        Theme.DrawGlyph(g, Theme.Glyph.More, 11f, Theme.Secondary, new Rectangle(r.Right - Theme.S(44), r.Y, Theme.S(36), r.Height));
    }

    private void ShowMenu(Recording item, Point screen)
    {
        var entries = new List<(string, Action)>();
        if (item.PlayablePath != null)
        {
            entries.Add((item.Status == RecordingStatus.Recording ? "Play what has been recorded so far" : "Play", () => App.PlayRecording(item)));
        }
        if (item.Status == RecordingStatus.Recording) entries.Add(("Stop recording", () => App.Recordings.Stop(item.Id)));
        if (item.Status == RecordingStatus.Scheduled) entries.Add(("Cancel this booking", () => App.Recordings.Stop(item.Id)));
        if (item.Status == RecordingStatus.Completed && item.HasTs && !item.HasMp4) entries.Add(("Convert to MP4", () => App.Recordings.Convert(item.Id)));
        if (item.PlayablePath != null) entries.Add(("Show in folder", () => Sheets.ShowInFolder(item.PlayablePath)));
        entries.Add(("-", null));
        entries.Add(("Delete", () =>
        {
            if (MessageBox.Show(this, $"Delete “{item.Title}” and its file?", Storage.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
            {
                App.Recordings.Delete(item.Id);
                Reload();
            }
        }));
        Painters.ShowMenu(this, screen, entries.ToArray());
    }

    public override void OnShown() => Reload();

    public override void OnTick()
    {
        Reload();
        list.Invalidate();
        Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
    }
}

/// <summary>Downloads: what is downloading, queued, paused and finished, with pause, resume and stop.</summary>
public sealed class DownloadsPage : PageBase
{
    private readonly VirtualList list = new() { RowHeight = Theme.S(84), EmptyText = "Nothing here yet. Open a movie or an episode and choose Download, or use Select in Movies to queue several at once." };
    private readonly FlatButton pauseAll, resumeAll, openFolder;
    private List<DownloadItem> items = new();

    public DownloadsPage(AppServices app) : base(app)
    {
        list.DrawItem = DrawRow;
        list.Activated += index => ShowMenu(items[index], Cursor.Position);
        list.ContextRequested += (index, point) => ShowMenu(items[index], point);
        pauseAll = new FlatButton("Pause all", Theme.Glyph.Pause, (_, _) => App.Downloads.PauseAll());
        resumeAll = new FlatButton("Resume all", Theme.Glyph.Play, (_, _) => App.Downloads.ResumeAll());
        openFolder = new FlatButton("Open folder", Theme.Glyph.Folder, (_, _) => Sheets.OpenFolder(App.Settings.EffectiveDownloadsFolder));
        Controls.AddRange(new Control[] { list, pauseAll, resumeAll, openFolder });
    }

    protected override void Arrange()
    {
        list.SetBounds(0, HeaderHeight, Width, Height - HeaderHeight);
        PlaceHeaderButtons(pauseAll, resumeAll, openFolder);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Prepare(e.Graphics);
        e.Graphics.Clear(Theme.Background);
        int active = items.Count(i => i.State == DownloadState.Downloading);
        int waiting = items.Count(i => i.State == DownloadState.Queued);
        string detail = items.Count == 0 ? "" : $"{active} downloading · {waiting} waiting · {items.Count(i => i.State == DownloadState.Completed)} finished";
        DrawHeader(e.Graphics, Theme.S(18), "Downloads", detail);
    }

    private void Reload()
    {
        items = App.Downloads.Snapshot();
        list.Count = items.Count;
    }

    private void DrawRow(Graphics g, int index, Rectangle bounds, ItemState state)
    {
        DownloadItem item = items[index];
        Rectangle r = Painters.RowBackground(g, bounds, state.Selected, state.Hover, Theme.Panel);
        (string glyph, Color color) = item.State switch
        {
            DownloadState.Downloading => (Theme.Glyph.Download, Theme.Accent),
            DownloadState.Queued => (Theme.Glyph.Clock, Theme.Secondary),
            DownloadState.Paused => (Theme.Glyph.Pause, Theme.Warn),
            DownloadState.Completed => (Theme.Glyph.Check, Theme.Good),
            _ => (Theme.Glyph.Warning, Theme.Warn)
        };
        Theme.DrawGlyph(g, glyph, 15f, color, new Rectangle(r.X + Theme.S(6), r.Y, Theme.S(44), r.Height));
        int x = r.X + Theme.S(58), width = r.Width - Theme.S(110);
        string title = string.IsNullOrEmpty(item.Subtitle) ? item.Title : item.Title + " · " + item.Subtitle;
        Theme.DrawText(g, title, Theme.Ui(10.5f, FontStyle.Bold), Theme.Text, new Rectangle(x, r.Y + Theme.S(6), width, Theme.S(22)));

        string size = item.TotalBytes > 0 ? $"{Theme.Bytes(item.ReceivedBytes)} of {Theme.Bytes(item.TotalBytes)}" : Theme.Bytes(item.ReceivedBytes);
        string line = item.State switch
        {
            DownloadState.Downloading => string.IsNullOrEmpty(item.Message)
                ? $"{item.Fraction:P0} · {size} · {Theme.Bytes((long)item.BytesPerSecond)}/s{Remaining(item)}"
                : $"{item.Fraction:P0} · {size} · {item.Message}",
            DownloadState.Queued => "Waiting to start" + (item.ReceivedBytes > 0 ? $" · {size}" : ""),
            DownloadState.Paused => $"Paused · {item.Fraction:P0} · {size}",
            DownloadState.Completed => $"Finished · {Theme.Bytes(item.TotalBytes)} · {Path.GetFileName(item.FilePath)}",
            _ => "Failed · " + item.Message
        };
        if (item.Resumes > 0 && item.State != DownloadState.Failed) line += $" · resumed ×{item.Resumes}";
        Theme.DrawText(g, line, Theme.Ui(8.5f), item.State == DownloadState.Failed ? Theme.Warn : Theme.Secondary, new Rectangle(x, r.Y + Theme.S(30), width, Theme.S(18)));
        if (item.State != DownloadState.Completed && item.State != DownloadState.Failed)
        {
            Theme.DrawProgress(g, new Rectangle(x, r.Y + Theme.S(56), width, Theme.S(5)), item.Fraction, color);
        }
        Theme.DrawGlyph(g, Theme.Glyph.More, 11f, Theme.Secondary, new Rectangle(r.Right - Theme.S(44), r.Y, Theme.S(36), r.Height));
    }

    private static string Remaining(DownloadItem item)
    {
        if (item.TotalBytes <= 0 || item.BytesPerSecond < 1000) return "";
        double seconds = (item.TotalBytes - item.ReceivedBytes) / item.BytesPerSecond;
        if (seconds < 60) return " · under a minute left";
        if (seconds < 3600) return $" · about {(int)(seconds / 60)} min left";
        return $" · about {seconds / 3600:0.#} h left";
    }

    private void ShowMenu(DownloadItem item, Point screen)
    {
        var entries = new List<(string, Action)>();
        if (item.State == DownloadState.Completed) entries.Add(("Play", () => App.PlayDownload(item)));
        if (item.State == DownloadState.Downloading || item.State == DownloadState.Queued) entries.Add(("Pause", () => App.Downloads.Pause(item.Id)));
        if (item.State == DownloadState.Paused) entries.Add(("Resume", () => App.Downloads.Resume(item.Id)));
        if (item.State == DownloadState.Failed) entries.Add(("Try again", () => App.Downloads.Resume(item.Id)));
        if (item.State == DownloadState.Completed) entries.Add(("Show in folder", () => Sheets.ShowInFolder(item.FilePath)));
        entries.Add(("-", null));
        if (item.State == DownloadState.Completed)
        {
            entries.Add(("Remove from this list (keep the file)", () => App.Downloads.Remove(item.Id, false)));
            entries.Add(("Delete the file", () =>
            {
                if (MessageBox.Show(this, $"Delete “{Path.GetFileName(item.FilePath)}” from this PC?", Storage.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                {
                    App.Downloads.Remove(item.Id, true);
                }
            }));
        }
        else
        {
            entries.Add(("Stop and remove", () => App.Downloads.Remove(item.Id, true)));
        }
        Painters.ShowMenu(this, screen, entries.ToArray());
    }

    public override void OnShown() => Reload();

    public override void OnTick()
    {
        Reload();
        list.Invalidate();
        Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
    }
}
