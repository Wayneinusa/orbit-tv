namespace SuperiorIptv.Win;

/// <summary>Watch two or four channels at once. One tile has the sound; click another tile to move it.</summary>
public sealed class MultiViewPage : PageBase
{
    private sealed class Tile
    {
        public Panel Video;
        public PlayerEngine Engine;
        public Channel Channel;
        public Rectangle Bounds;
        public Rectangle Caption;
    }

    private readonly Tile[] tiles = new Tile[4];
    private readonly FlatButton layout;
    private int tileCount = 4;
    private int audioIndex;
    private bool active;

    public MultiViewPage(AppServices app) : base(app)
    {
        for (int i = 0; i < tiles.Length; i++)
        {
            int index = i;
            var video = new Panel { BackColor = Color.Black, Visible = false };
            video.MouseDown += (_, e) => TileClicked(index, e.Button, video.PointToScreen(e.Location));
            tiles[i] = new Tile { Video = video };
            Controls.Add(video);
        }
        layout = new FlatButton("2 screens", Theme.Glyph.Multi, (_, _) => SetTileCount(tileCount == 4 ? 2 : 4));
        Controls.Add(layout);
    }

    protected override void Arrange()
    {
        PlaceHeaderButtons(layout);
        int pad = Theme.S(10), caption = Theme.S(30);
        var area = new Rectangle(pad, HeaderHeight, Width - pad * 2, Height - HeaderHeight - pad);
        int columns = 2, rows = tileCount == 4 ? 2 : 1;
        int cellWidth = (area.Width - pad) / columns, cellHeight = (area.Height - pad * (rows - 1)) / rows;
        for (int i = 0; i < tiles.Length; i++)
        {
            Tile tile = tiles[i];
            if (i >= tileCount)
            {
                tile.Bounds = Rectangle.Empty;
                tile.Video.Visible = false;
                continue;
            }
            tile.Bounds = new Rectangle(area.X + i % columns * (cellWidth + pad), area.Y + i / columns * (cellHeight + pad), cellWidth, cellHeight);
            tile.Caption = new Rectangle(tile.Bounds.X, tile.Bounds.Bottom - caption, tile.Bounds.Width, caption);
            tile.Video.SetBounds(tile.Bounds.X + 2, tile.Bounds.Y + 2, tile.Bounds.Width - 4, tile.Bounds.Height - caption - 2);
            tile.Video.Visible = tile.Channel != null;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(Theme.Background);
        DrawHeader(g, Theme.S(18), "Multi-view", "Each screen uses one connection from your TV provider. Click a screen to move the sound; right-click for more.");
        for (int i = 0; i < tileCount; i++)
        {
            Tile tile = tiles[i];
            bool hasAudio = i == audioIndex && tile.Channel != null;
            Theme.FillRound(g, Theme.Panel, tile.Bounds, Theme.S(8));
            if (hasAudio) Theme.StrokeRound(g, Theme.Accent, tile.Bounds, Theme.S(8), 2f);
            if (tile.Channel == null)
            {
                Theme.DrawGlyph(g, Theme.Glyph.Add, 26f, Theme.Secondary, new Rectangle(tile.Bounds.X, tile.Bounds.Y, tile.Bounds.Width, tile.Bounds.Height - Theme.S(30)));
                Theme.DrawText(g, "Add channel", Theme.Ui(10f), Theme.Secondary, tile.Caption, HorizontalAlignment.Center);
                continue;
            }
            string status = tile.Engine?.Status switch
            {
                PlayerStatus.Loading => "  ·  loading…",
                PlayerStatus.Failed or PlayerStatus.Ended => "  ·  not responding",
                _ => ""
            };
            Theme.DrawGlyph(g, hasAudio ? Theme.Glyph.Volume : Theme.Glyph.Mute, 9.5f, hasAudio ? Theme.Accent : Theme.Secondary,
                new Rectangle(tile.Caption.X + Theme.S(6), tile.Caption.Y, Theme.S(24), tile.Caption.Height));
            Theme.DrawText(g, tile.Channel.Name + status, Theme.Ui(9.5f, FontStyle.Bold), Theme.Text,
                new Rectangle(tile.Caption.X + Theme.S(34), tile.Caption.Y, tile.Caption.Width - Theme.S(40), tile.Caption.Height));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        for (int i = 0; i < tileCount; i++)
        {
            if (tiles[i].Bounds.Contains(e.Location)) TileClicked(i, e.Button, PointToScreen(e.Location));
        }
    }

    private void TileClicked(int index, MouseButtons button, Point screen)
    {
        Tile tile = tiles[index];
        if (tile.Channel == null)
        {
            Pick(index);
            return;
        }
        if (button == MouseButtons.Right)
        {
            Painters.ShowMenu(this, screen,
                ("Change channel…", () => Pick(index)),
                ("Watch full screen", () =>
                {
                    Channel channel = tile.Channel;
                    StopAll();
                    App.GoTo("live");
                    App.Watch(channel);
                }),
                ("Close this screen", () =>
                {
                    tile.Engine?.Stop();
                    tile.Channel = null;
                    Arrange();
                }));
            return;
        }
        SetAudio(index);
    }

    private void Pick(int index)
    {
        using var dialog = new ChannelPickerDialog(App);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Picked == null) return;
        Tile tile = tiles[index];
        tile.Channel = dialog.Picked;
        StartTile(tile);
        SetAudio(index);
        Arrange();
    }

    private void StartTile(Tile tile)
    {
        if (tile.Channel == null) return;
        if (tile.Engine == null)
        {
            tile.Engine = new PlayerEngine(App.Settings, null);
            tile.Engine.AttachTo(tile.Video);
        }
        tile.Engine.Play(App.Library.Playable(tile.Channel));
    }

    private void SetAudio(int index)
    {
        audioIndex = index;
        for (int i = 0; i < tiles.Length; i++)
        {
            if (tiles[i].Engine != null) tiles[i].Engine.Muted = i != index;
        }
        Invalidate();
    }

    private void SetTileCount(int count)
    {
        tileCount = count;
        layout.Caption = count == 4 ? "2 screens" : "4 screens";
        for (int i = 0; i < tiles.Length; i++)
        {
            if (i >= count) tiles[i].Engine?.Stop();
            else if (active && tiles[i].Channel != null && tiles[i].Engine?.Item == null) StartTile(tiles[i]);
        }
        if (audioIndex >= count) SetAudio(0);
        Arrange();
    }

    private void StopAll()
    {
        foreach (Tile tile in tiles) tile.Engine?.Stop();
    }

    public override void OnShown()
    {
        active = true;
        for (int i = 0; i < tileCount; i++) StartTile(tiles[i]);
        SetAudio(audioIndex);
        Arrange();
    }

    public override void OnHidden()
    {
        active = false;
        StopAll();
    }

    public override void OnTick()
    {
        foreach (Tile tile in tiles) tile.Engine?.Poll();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Tile tile in tiles) tile?.Engine?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>One search box across channels, movies, series and upcoming programmes.</summary>
public sealed class SearchPage : PageBase
{
    private sealed class Result
    {
        public string Section;
        public string Glyph = "";
        public string Title = "";
        public string Detail = "";
        public Action Open;
    }

    private readonly TextBox box = Theme.Style(new TextBox());
    private readonly VirtualList list = new() { RowHeight = Theme.S(56), EmptyText = "Type at least two letters to search channels, movies, series and the guide." };
    private readonly System.Windows.Forms.Timer delay = new() { Interval = 350 };
    private List<Result> results = new();
    private int generation;

    public SearchPage(AppServices app) : base(app)
    {
        box.PlaceholderText = "Search channels, movies, series and the guide";
        box.Font = Theme.Ui(12f);
        box.TextChanged += (_, _) =>
        {
            delay.Stop();
            delay.Start();
        };
        delay.Tick += async (_, _) =>
        {
            delay.Stop();
            await RunAsync();
        };
        list.DrawItem = (g, index, bounds, state) =>
        {
            Result result = results[index];
            if (result.Section != null) Painters.SectionRow(g, bounds, result.Section);
            else Painters.TwoLineRow(g, bounds, state, result.Glyph, Theme.Secondary, result.Title, result.Detail);
        };
        list.Activated += index => results[index].Open?.Invoke();
        Controls.Add(box);
        Controls.Add(list);
    }

    protected override void Arrange()
    {
        box.SetBounds(Theme.S(18), Theme.S(14), Math.Min(Width - Theme.S(36), Theme.S(700)), Theme.S(32));
        list.SetBounds(0, HeaderHeight, Width, Height - HeaderHeight);
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Theme.Background);

    public override void OnShown() => box.Focus();

    private async Task RunAsync()
    {
        string needle = box.Text.Trim();
        int run = ++generation;
        if (needle.Length < 2)
        {
            results = new List<Result>();
            list.EmptyText = "Type at least two letters to search channels, movies, series and the guide.";
            list.Count = 0;
            return;
        }

        Library library = App.Library;
        List<Channel> allChannels = library.Channels;
        EpgData guide = library.Epg;
        DateTime now = DateTime.UtcNow;
        bool Match(string value) => value != null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

        // The guide can hold hundreds of thousands of entries, so search runs off the window's thread.
        List<Result> found = await Task.Run(() =>
        {
            var output = new List<Result>();
            List<Channel> channels = allChannels.Where(c => Match(c.Name)).Take(40).ToList();
            if (channels.Count > 0) output.Add(new Result { Section = $"Channels ({channels.Count})" });
            foreach (Channel channel in channels)
            {
                output.Add(new Result { Glyph = Theme.Glyph.Tv, Title = channel.Name, Detail = channel.Group, Open = () => App.Watch(channel) });
            }

            var programmes = new List<Result>();
            foreach (Channel channel in allChannels)
            {
                foreach (Programme programme in guide.ListFor(channel))
                {
                    if (programme.Stop <= now || !Match(programme.Title)) continue;
                    Channel owner = channel;
                    Programme hit = programme;
                    programmes.Add(new Result
                    {
                        Glyph = Theme.Glyph.Guide,
                        Title = programme.Title,
                        Detail = channel.Name + " · " + programme.Start.ToLocalTime().ToString("ddd d MMM") + " " + Theme.Range(programme),
                        Open = () => Sheets.Programme(this, App, owner, hit)
                    });
                    if (programmes.Count >= 40) break;
                }
                if (programmes.Count >= 40) break;
            }
            if (programmes.Count > 0) output.Add(new Result { Section = $"On now and coming up ({programmes.Count})" });
            output.AddRange(programmes);

            List<Movie> movies = library.Movies.Where(m => Match(m.Name)).Take(40).ToList();
            if (movies.Count > 0) output.Add(new Result { Section = $"Movies ({movies.Count})" });
            foreach (Movie movie in movies)
            {
                output.Add(new Result { Glyph = Theme.Glyph.Movie, Title = movie.Name, Detail = movie.Group, Open = () => Sheets.Movie(this, App, movie) });
            }

            List<Series> shows = library.Series.Where(s => Match(s.Name)).Take(40).ToList();
            if (shows.Count > 0) output.Add(new Result { Section = $"Series ({shows.Count})" });
            foreach (Series show in shows)
            {
                output.Add(new Result
                {
                    Glyph = Theme.Glyph.Series,
                    Title = show.Name,
                    Detail = show.Group,
                    Open = () =>
                    {
                        using var dialog = new SeriesDialog(App, show);
                        dialog.ShowDialog(this);
                    }
                });
            }
            return output;
        });

        if (run != generation || IsDisposed) return;
        results = found;
        list.EmptyText = $"Nothing found for “{needle}”.";
        list.Count = results.Count;
        list.ScrollToTop();
    }
}

/// <summary>Settings: playlists, guide, playback, recording, downloads.</summary>
public sealed class SettingsPage : PageBase
{
    private readonly Panel body = new() { AutoScroll = true, BackColor = Theme.Background };
    private readonly VirtualList playlists = new() { RowHeight = Theme.S(52), Back = Theme.Panel, EmptyText = "No playlists yet." };
    private readonly Label guideStatus = Theme.Caption("");
    private readonly Label recordingFolder = Theme.Caption("");
    private readonly Label downloadFolder = Theme.Caption("");
    private readonly Label groupsStatus = Theme.Caption("");
    private int y;

    public SettingsPage(AppServices app) : base(app)
    {
        Controls.Add(body);
        Settings s = App.Settings;
        y = Theme.S(10);

        Section("Playlists");
        playlists.DrawItem = (g, index, bounds, state) =>
        {
            Playlist playlist = App.Library.Playlists[index];
            string detail = playlist.Kind == PlaylistKind.Xtream ? "Xtream · " + playlist.Username + " @ " + XtreamClient.Normalize(playlist.Server) : "M3U · " + playlist.Url;
            if (playlist.LastRefreshed != null) detail += " · loaded " + playlist.LastRefreshed.Value.ToLocalTime().ToString("g");
            Painters.TwoLineRow(g, bounds, state, Theme.Glyph.List, Theme.Secondary, playlist.Name, detail);
        };
        playlists.Activated += index => EditPlaylist(App.Library.Playlists[index]);
        Place(playlists, Theme.S(720), Theme.S(170));
        Buttons(
            new FlatButton("Add playlist…", Theme.Glyph.Add, (_, _) => EditPlaylist(null)) { Fill = Theme.Accent },
            new FlatButton("Reload all playlists", Theme.Glyph.Refresh, async (_, _) => await App.Library.RefreshAllAsync()));
        Combo("Reload playlists automatically", new[] { ("Every 12 hours", 12), ("Every day", 24), ("Every 3 days", 72), ("Every week", 168) },
            s.PlaylistRefreshHours, value => s.PlaylistRefreshHours = value);

        Section("TV guide");
        Place(guideStatus, Theme.S(720), Theme.S(22));
        Buttons(new FlatButton("Update guide now", Theme.Glyph.Refresh, async (_, _) => await App.Library.RefreshEpgAsync()));
        Combo("Update the guide automatically", new[] { ("Every 6 hours", 6), ("Every 12 hours", 12), ("Every day", 24), ("Every 2 days", 48) },
            s.EpgRefreshHours, value => s.EpgRefreshHours = value);
        Combo("Keep past days (for catch-up)", new[] { ("None", 0), ("1 day", 1), ("2 days", 2), ("3 days", 3), ("7 days", 7) },
            s.EpgPastDays, value => s.EpgPastDays = value);
        Combo("Days ahead", new[] { ("1 day", 1), ("2 days", 2), ("3 days", 3), ("5 days", 5), ("7 days", 7) },
            s.EpgFutureDays, value => s.EpgFutureDays = value);

        Section("Playback");
        Combo("Buffer", new[] { ("0.5 s (fastest channel change)", 500), ("1 s", 1000), ("1.5 s (recommended)", 1500), ("3 s", 3000), ("5 s", 5000), ("10 s (unstable connections)", 10000) },
            s.BufferMs, value => s.BufferMs = value);
        Combo("Xtream stream format (takes effect after reloading the playlist)", new[] { ("MPEG-TS (recommended)", 0), ("HLS (m3u8)", 1) },
            s.LiveFormat == "m3u8" ? 1 : 0, value => s.LiveFormat = value == 1 ? "m3u8" : "ts");
        Check("Reconnect automatically when a channel freezes", s.AutoReconnect, value => s.AutoReconnect = value);
        Check("Use hardware video decoding (takes effect after restarting the app)", s.HardwareDecoding, value => s.HardwareDecoding = value);
        TextRow("User-Agent (optional)", s.UserAgent, value => s.UserAgent = value);
        Note("Playback speed (0.5× to 16×) is on the player's control bar for movies, episodes, catch-up, recordings and downloads. Keys: ] faster, [ slower, \\ normal.");

        Section("Recording");
        Check("Convert recordings to MP4 automatically", s.AutoConvertMp4, value => s.AutoConvertMp4 = value);
        Check("Keep the original .ts file after converting", s.KeepTs, value => s.KeepTs = value);
        Combo("Start early", new[] { ("Off", 0), ("1 min", 1), ("2 min", 2), ("5 min", 5), ("10 min", 10) }, s.PadBeforeMinutes, value => s.PadBeforeMinutes = value);
        Combo("Finish late", new[] { ("Off", 0), ("2 min", 2), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30) }, s.PadAfterMinutes, value => s.PadAfterMinutes = value);
        Place(recordingFolder, Theme.S(720), Theme.S(22));
        Buttons(
            new FlatButton("Change recordings folder…", Theme.Glyph.Folder, (_, _) => ChooseFolder(true)),
            new FlatButton("Use the default folder", "", (_, _) =>
            {
                s.RecordingsFolder = "";
                Save();
            }));
        Note("Recordings are captured as .ts so that they can resume after any interruption, then turned into .mp4. If a stream stops, the recorder reconnects by itself until the end time. Any folder works, including a network drive. Keep the app running while recording (it can be minimised).");

        Section("Downloads");
        Place(downloadFolder, Theme.S(720), Theme.S(22));
        Buttons(
            new FlatButton("Change downloads folder…", Theme.Glyph.Folder, (_, _) => ChooseFolder(false)),
            new FlatButton("Use the default folder", "", (_, _) =>
            {
                s.DownloadsFolder = "";
                Save();
            }));
        Combo("Download at the same time", new[] { ("1 file", 1), ("2 files", 2), ("3 files", 3), ("4 files", 4) }, s.MaxDownloads, value => s.MaxDownloads = value);
        Note("Interrupted downloads resume by themselves from where they stopped, also after the app is closed and opened again.");

        Section("Channel groups");
        Place(groupsStatus, Theme.S(720), Theme.S(22));
        Buttons(new FlatButton("Choose which groups to show…", Theme.Glyph.List, (_, _) =>
        {
            using var dialog = new GroupsDialog(App);
            dialog.ShowDialog(this);
            RefreshLabels();
        }));

        Section("About");
        Note($"{Storage.AppName} {Application.ProductVersion.Split('+')[0]}. This is a media player. It contains no channels or content; you add a playlist supplied by your own TV provider. Video playback uses the VLC engine (LGPL).");
        y += Theme.S(30);
        var spacer = new Label { AutoSize = false, BackColor = Theme.Background };
        spacer.SetBounds(0, y, 10, 10);
        body.Controls.Add(spacer);
    }

    // ---- Small layout helpers: every row is placed at the running "y" position.

    private int Left0 => Theme.S(22);

    private void Place(Control control, int width, int height)
    {
        control.SetBounds(Left0, y, width, height);
        if (control is Label label) label.AutoSize = false;
        body.Controls.Add(control);
        y += height + Theme.S(8);
    }

    private void Section(string title)
    {
        y += Theme.S(14);
        Label label = Theme.Caption(title, true);
        label.Location = new Point(Left0, y);
        body.Controls.Add(label);
        y += Theme.S(34);
    }

    private void Note(string text)
    {
        var label = new Label { Text = text, ForeColor = Theme.Faint, BackColor = Theme.Background, Font = Theme.Ui(9f), AutoSize = false };
        int lines = Math.Max(1, (int)Math.Ceiling(text.Length / 105.0));
        Place(label, Theme.S(720), Theme.S(20) * lines + Theme.S(4));
    }

    private void Buttons(params FlatButton[] buttons)
    {
        int x = Left0;
        foreach (FlatButton button in buttons)
        {
            body.Controls.Add(button);
            int width = button.PreferredWidth();
            button.SetBounds(x, y, width, Theme.S(36));
            x += width + Theme.S(8);
        }
        y += Theme.S(46);
    }

    private void Combo(string caption, (string Text, int Value)[] options, int current, Action<int> apply)
    {
        Label label = Theme.Caption(caption);
        label.Location = new Point(Left0, y + Theme.S(5));
        var combo = Theme.Style(new ComboBox());
        combo.SetBounds(Left0 + Theme.S(430), y, Theme.S(290), Theme.S(28));
        foreach (var (text, _) in options) combo.Items.Add(text);
        int index = Array.FindIndex(options, o => o.Value == current);
        combo.SelectedIndex = index < 0 ? 0 : index;
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            apply(options[combo.SelectedIndex].Value);
            Save();
        };
        body.Controls.Add(label);
        body.Controls.Add(combo);
        y += Theme.S(38);
    }

    private void Check(string caption, bool current, Action<bool> apply)
    {
        var check = Theme.Style(new CheckBox { Text = caption, Checked = current, AutoSize = true });
        check.Location = new Point(Left0, y);
        check.CheckedChanged += (_, _) =>
        {
            apply(check.Checked);
            Save();
        };
        body.Controls.Add(check);
        y += Theme.S(32);
    }

    private void TextRow(string caption, string current, Action<string> apply)
    {
        Label label = Theme.Caption(caption);
        label.Location = new Point(Left0, y + Theme.S(5));
        var box = Theme.Style(new TextBox { Text = current });
        box.SetBounds(Left0 + Theme.S(430), y, Theme.S(290), Theme.S(28));
        box.Leave += (_, _) =>
        {
            apply(box.Text.Trim());
            Save();
        };
        body.Controls.Add(label);
        body.Controls.Add(box);
        y += Theme.S(38);
    }

    private void Save()
    {
        App.Settings.Save();
        RefreshLabels();
    }

    private void ChooseFolder(bool recordings)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = recordings ? "Choose where recordings are saved" : "Choose where downloads are saved",
            UseDescriptionForTitle = true,
            SelectedPath = recordings ? App.Settings.EffectiveRecordingsFolder : App.Settings.EffectiveDownloadsFolder
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // Make sure the folder can actually be written to (network drives may be read-only or offline).
            string probe = Path.Combine(dialog.SelectedPath, ".siptv-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "That folder cannot be written to:\n" + ex.Message, Storage.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (recordings) App.Settings.RecordingsFolder = dialog.SelectedPath;
        else App.Settings.DownloadsFolder = dialog.SelectedPath;
        Save();
    }

    private void EditPlaylist(Playlist playlist)
    {
        using var dialog = new PlaylistDialog(App, playlist);
        dialog.ShowDialog(this);
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        playlists.Count = App.Library.Playlists.Count;
        guideStatus.Text = string.IsNullOrEmpty(App.Library.EpgStatus) ? "No guide loaded yet" : App.Library.EpgStatus;
        recordingFolder.Text = "Recordings folder: " + App.Settings.EffectiveRecordingsFolder;
        downloadFolder.Text = "Downloads folder: " + App.Settings.EffectiveDownloadsFolder;
        groupsStatus.Text = App.Settings.HiddenGroups.Count + " of " + App.Library.Groups.Count + " groups hidden";
    }

    protected override void Arrange() => body.SetBounds(0, 0, Width, Height);

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Theme.Background);

    public override void OnShown() => RefreshLabels();

    public override void OnLibraryChanged() => RefreshLabels();

    public override void OnTick()
    {
        string status = string.IsNullOrEmpty(App.Library.EpgStatus) ? "No guide loaded yet" : App.Library.EpgStatus;
        if (guideStatus.Text != status) guideStatus.Text = status;
    }
}
