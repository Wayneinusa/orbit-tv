namespace SuperiorIptv.Win;

/// <summary>Base class of the screens shown next to the navigation rail.</summary>
public abstract class PageBase : Control
{
    protected readonly AppServices App;
    protected int HeaderHeight => Theme.S(58);

    protected PageBase(AppServices app)
    {
        App = app;
        BackColor = Theme.Background;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>The page became visible.</summary>
    public virtual void OnShown() { }

    /// <summary>The page was replaced by another one.</summary>
    public virtual void OnHidden() { }

    /// <summary>Called about twice a second while the page is visible.</summary>
    public virtual void OnTick() { }

    /// <summary>Playlists, favourites or the guide changed.</summary>
    public virtual void OnLibraryChanged() { }

    protected abstract void Arrange();

    /// <summary>Lays the page out again (called when it becomes visible).</summary>
    public void Relayout()
    {
        if (Width > 0 && Height > 0) Arrange();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width > 0 && Height > 0) Arrange();
    }

    protected void DrawHeader(Graphics g, int x, string title, string subtitle)
    {
        int titleWidth = Theme.MeasureWidth(g, title, Theme.Ui(14f, FontStyle.Bold));
        Theme.DrawText(g, title, Theme.Ui(14f, FontStyle.Bold), Theme.Text, new Rectangle(x, 0, titleWidth + Theme.S(4), HeaderHeight));
        if (!string.IsNullOrEmpty(subtitle))
        {
            Theme.DrawText(g, subtitle, Theme.Ui(9.5f), Theme.Secondary, new Rectangle(x + titleWidth + Theme.S(14), Theme.S(3), Math.Max(10, Width - x - titleWidth - Theme.S(20)), HeaderHeight));
        }
    }

    /// <summary>Places buttons right-aligned in the header, last one at the far right.</summary>
    protected void PlaceHeaderButtons(params FlatButton[] buttons)
    {
        int x = Width - Theme.S(14);
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            FlatButton button = buttons[i];
            if (!button.Shown) continue;
            int width = button.PreferredWidth();
            x -= width;
            button.SetBounds(x, Theme.S(11), width, Theme.S(36));
            x -= Theme.S(8);
        }
    }
}

/// <summary>Dialogs that several screens open.</summary>
public static class Sheets
{
    /// <summary>"Record…" options for a channel.</summary>
    public static void Record(IWin32Window owner, AppServices app, Channel channel)
    {
        DateTime now = DateTime.UtcNow;
        Programme current = app.Library.Epg.Current(channel, now);
        string note = "If the stream drops, recording reconnects and carries on by itself until the end time. "
                      + (app.Settings.AutoConvertMp4 ? "The file is converted to MP4 when it finishes." : "The file is saved as .ts; convert it to MP4 from Recordings.");
        var dialog = new ActionDialog("Record " + channel.Name, "", note);
        if (current != null)
        {
            DateTime end = current.Stop.AddMinutes(app.Settings.PadAfterMinutes);
            dialog.AddAction($"This programme: {current.Title} (until {Theme.Time(current.Stop)})", Theme.Glyph.Record,
                () => app.RecordNow(channel, current.Title, end), Theme.Live);
        }
        foreach (int minutes in new[] { 30, 60, 120, 180, 240 })
        {
            string label = minutes < 60 ? $"{minutes} minutes" : minutes == 60 ? "1 hour" : $"{minutes / 60} hours";
            int captured = minutes;
            dialog.AddAction($"{label} (until {Theme.Time(now.AddMinutes(minutes))})", Theme.Glyph.Record,
                () => app.RecordNow(channel, current?.Title ?? channel.Name, DateTime.UtcNow.AddMinutes(captured)), Theme.Live);
        }
        dialog.AddClose("Cancel");
        using (dialog) dialog.ShowDialog(owner);
    }

    /// <summary>Details of a guide entry with whatever can be done with it right now.</summary>
    public static void Programme(IWin32Window owner, AppServices app, Channel channel, Programme programme)
    {
        DateTime now = DateTime.UtcNow;
        string when = programme.Start.ToLocalTime().ToString("ddd d MMM") + " · " + Theme.Range(programme);
        var dialog = new ActionDialog(programme.Title, channel.Name + " · " + when, programme.Desc);

        if (programme.IsLive(now))
        {
            dialog.AddAction("Watch live", Theme.Glyph.Play, () => app.Watch(channel));
            if (Catchup.IsAvailable(channel, programme, now)) dialog.AddAction("Watch from the start", Theme.Glyph.Catchup, () => PlayCatchup(app, channel, programme));
            Recording active = app.Recordings.ActiveFor(channel.Id);
            if (active != null)
            {
                dialog.AddAction("Stop recording", Theme.Glyph.Stop, () => app.Recordings.Stop(active.Id), Theme.Live);
            }
            else
            {
                dialog.AddAction("Record until it ends", Theme.Glyph.Record,
                    () => app.RecordNow(channel, programme.Title, programme.Stop.AddMinutes(app.Settings.PadAfterMinutes)), Theme.Live);
            }
        }
        else if (programme.Start > now)
        {
            if (app.Recordings.IsScheduled(channel.Id, programme.Start))
            {
                dialog.AddNote("A recording is scheduled for this programme.", Theme.Good);
            }
            else
            {
                dialog.AddAction("Schedule recording", Theme.Glyph.Record, () =>
                {
                    app.Recordings.Schedule(channel, programme, app.Library.UserAgentFor(channel.PlaylistId, channel));
                    app.Toast("Recording scheduled: " + programme.Title);
                }, Theme.Live);
                dialog.AddNote("Keep Superior IPTV Player running at the scheduled time (it can be minimised).");
            }
        }
        else if (Catchup.IsAvailable(channel, programme, now))
        {
            dialog.AddAction("Watch (catch-up)", Theme.Glyph.Catchup, () => PlayCatchup(app, channel, programme));
        }
        else
        {
            dialog.AddNote("This programme has ended and the channel does not offer catch-up.");
        }
        dialog.AddClose();
        using (dialog) dialog.ShowDialog(owner);
    }

    private static void PlayCatchup(AppServices app, Channel channel, Programme programme)
    {
        PlayableItem item = app.Library.PlayableCatchup(channel, programme);
        if (item == null) app.Toast("Catch-up is not available for this programme.");
        else app.PlayItem(item);
    }

    /// <summary>Movie details: play, resume, download.</summary>
    public static async void Movie(Control owner, AppServices app, Movie movie)
    {
        var info = new XtreamVodInfo();
        if (movie.XtreamId != null)
        {
            XtreamClient client = app.Library.XtreamFor(movie.PlaylistId);
            if (client != null)
            {
                owner.Cursor = Cursors.WaitCursor;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    info = await client.MovieInfoAsync(movie.XtreamId.Value, cts.Token);
                }
                catch (Exception)
                {
                    // Details are optional; the movie can still be played and downloaded.
                }
                owner.Cursor = Cursors.Default;
                if (owner.IsDisposed) return;
            }
        }

        string year = string.IsNullOrEmpty(movie.Year) ? info.Year : movie.Year;
        string rating = string.IsNullOrEmpty(movie.Rating) ? info.Rating : movie.Rating;
        string facts = string.Join(" · ", new[] { year, info.Genre, info.Duration, string.IsNullOrEmpty(rating) ? "" : "★ " + rating }.Where(s => !string.IsNullOrEmpty(s)));
        var dialog = new ActionDialog(movie.Name, facts, string.IsNullOrEmpty(info.Plot) ? movie.Plot : info.Plot, movie.Poster);

        long resumeAt = app.Resume.Get(movie.Id);
        DownloadItem download = app.Downloads.FindBySource(movie.Id);
        bool local = app.LocalCopy(movie.Id) != null;
        if (resumeAt > 0)
        {
            dialog.AddAction("Resume from " + Theme.Clock(resumeAt), Theme.Glyph.Play, () => app.PlayMovie(movie));
            dialog.AddAction("Play from the beginning", Theme.Glyph.Refresh, () =>
            {
                app.Resume.Set(movie.Id, 0);
                app.PlayMovie(movie);
            });
        }
        else
        {
            dialog.AddAction(local ? "Play (downloaded copy)" : "Play", Theme.Glyph.Play, () => app.PlayMovie(movie));
        }

        if (download == null)
        {
            dialog.AddAction("Download", Theme.Glyph.Download, () =>
            {
                app.Download(movie);
                app.Toast("Added to Downloads: " + movie.Name);
            });
        }
        else if (download.State == DownloadState.Completed)
        {
            dialog.AddNote("Downloaded to " + download.FilePath, Theme.Good);
        }
        else
        {
            dialog.AddAction($"Downloading ({download.State}, {download.Fraction:P0}): open Downloads", Theme.Glyph.Download, () => app.GoTo("downloads"));
        }
        dialog.AddClose();
        using (dialog) dialog.ShowDialog(owner);
    }

    public static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else if (Directory.Exists(Path.GetDirectoryName(path))) OpenFolder(Path.GetDirectoryName(path));
        }
        catch (Exception)
        {
        }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
}
