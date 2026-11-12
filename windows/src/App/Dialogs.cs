namespace SuperiorIptv.Win;

/// <summary>Base for the app's dark dialogs.</summary>
public class DarkForm : Form
{
    public DarkForm()
    {
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui(10f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}

/// <summary>
/// A details sheet: optional poster, title, subtitle, description and a column of actions.
/// Used for programme details, movie details, record options and confirmations.
/// </summary>
public sealed class ActionDialog : DarkForm
{
    private readonly string heading, subtitle, body, poster;
    private readonly List<Control> rows = new();
    private readonly Control header;
    private int bodyHeight;

    public ActionDialog(string title, string subtitle = "", string body = "", string poster = "")
    {
        Text = Storage.AppName;
        heading = title ?? "";
        this.subtitle = subtitle ?? "";
        this.body = body ?? "";
        this.poster = poster ?? "";
        header = new HeaderPanel(this);
        Controls.Add(header);
    }

    private int ContentWidth => Theme.S(string.IsNullOrEmpty(poster) ? 520 : 600);

    public ActionDialog AddAction(string text, string glyph, Action run, Color? glyphColor = null)
    {
        var button = new FlatButton(text, glyph) { Fill = Theme.Panel, Height = Theme.S(42) };
        if (glyphColor != null) button.GlyphColor = glyphColor.Value;
        button.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
            run?.Invoke();
        };
        rows.Add(button);
        return this;
    }

    public ActionDialog AddNote(string text, Color? color = null)
    {
        rows.Add(new Label
        {
            Text = text,
            ForeColor = color ?? Theme.Secondary,
            BackColor = Theme.Background,
            Font = Theme.Ui(9f),
            AutoSize = false,
            Height = Theme.S(text.Length > 90 ? 58 : text.Length > 45 ? 40 : 24)
        });
        return this;
    }

    public ActionDialog AddClose(string text = "Close")
    {
        return AddAction(text, Theme.Glyph.Close, null);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int pad = Theme.S(20);
        int width = ContentWidth;
        int posterWidth = string.IsNullOrEmpty(poster) ? 0 : Theme.S(150);
        int textWidth = width - posterWidth - (posterWidth > 0 ? pad : 0);
        using (Graphics g = CreateGraphics())
        {
            int needed = Theme.MeasureHeight(g, heading, Theme.Ui(15f, FontStyle.Bold), textWidth) + Theme.S(6);
            if (subtitle.Length > 0) needed += Theme.S(26);
            bodyHeight = Math.Min(Theme.S(230), Theme.MeasureHeight(g, body, Theme.Ui(9.5f), textWidth));
            needed += bodyHeight + Theme.S(8);
            header.SetBounds(pad, pad, width, Math.Max(needed, posterWidth > 0 ? posterWidth * 3 / 2 : 0));
        }
        int y = header.Bottom + Theme.S(12);
        foreach (Control row in rows)
        {
            row.SetBounds(pad, y, width, row.Height);
            Controls.Add(row);
            y += row.Height + Theme.S(6);
        }
        ClientSize = new Size(width + pad * 2, y + pad - Theme.S(6));
    }

    private sealed class HeaderPanel : Control
    {
        private readonly ActionDialog owner;

        public HeaderPanel(ActionDialog owner)
        {
            this.owner = owner;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            g.Clear(Theme.Background);
            int x = 0;
            if (!string.IsNullOrEmpty(owner.poster))
            {
                int posterWidth = Theme.S(150);
                var art = new Rectangle(0, 0, posterWidth, posterWidth * 3 / 2);
                Theme.FillRound(g, Theme.Panel, art, Theme.S(8));
                Image image = ImageCache.Get(owner.poster, 360, this);
                if (image != null) Theme.DrawImage(g, image, art, true);
                else Theme.DrawGlyph(g, Theme.Glyph.Movie, 22f, Theme.Faint, art);
                x = posterWidth + Theme.S(20);
            }
            int width = Width - x;
            int y = 0;
            int headingHeight = Theme.MeasureHeight(g, owner.heading, Theme.Ui(15f, FontStyle.Bold), width);
            Theme.DrawWrapped(g, owner.heading, Theme.Ui(15f, FontStyle.Bold), Theme.Text, new Rectangle(x, y, width, headingHeight));
            y += headingHeight + Theme.S(6);
            if (owner.subtitle.Length > 0)
            {
                Theme.DrawText(g, owner.subtitle, Theme.Ui(10f), Theme.Secondary, new Rectangle(x, y, width, Theme.S(22)));
                y += Theme.S(26);
            }
            Theme.DrawWrapped(g, owner.body, Theme.Ui(9.5f), Color.FromArgb(215, 220, 228), new Rectangle(x, y, width, owner.bodyHeight));
        }
    }
}

/// <summary>Add or edit a playlist.</summary>
public sealed class PlaylistDialog : DarkForm
{
    private readonly AppServices app;
    private readonly Playlist existing;
    private readonly ComboBox kind = Theme.Style(new ComboBox());
    private readonly TextBox name = Theme.Style(new TextBox());
    private readonly TextBox server = Theme.Style(new TextBox());
    private readonly TextBox username = Theme.Style(new TextBox());
    private readonly TextBox password = Theme.Style(new TextBox { UseSystemPasswordChar = true });
    private readonly TextBox address = Theme.Style(new TextBox());
    private readonly TextBox epg = Theme.Style(new TextBox());
    private readonly TextBox userAgent = Theme.Style(new TextBox());
    private readonly Label status = Theme.Caption("");
    private readonly FlatButton save, browse;
    private readonly List<(Label Label, Control Field, int Group)> fields = new();

    public bool Removed { get; private set; }

    public PlaylistDialog(AppServices app, Playlist existing)
    {
        this.app = app;
        this.existing = existing;
        Text = existing == null ? "Add playlist" : "Edit playlist";
        kind.Items.AddRange(new object[] { "Xtream Codes login", "M3U address or file" });
        kind.SelectedIndex = 0;
        kind.SelectedIndexChanged += (_, _) => Arrange();

        AddField("Playlist type", kind, 0);
        AddField("Name", name, 0);
        AddField("Server (for example http://example.com:8080)", server, 1);
        AddField("Username", username, 1);
        AddField("Password", password, 1);
        AddField("M3U web address, or the path of an .m3u file on this PC", address, 2);
        AddField("Guide (XMLTV) address, optional. Leave empty to use the playlist's own", epg, 0);
        AddField("User-Agent, optional. Leave empty unless your provider requires one", userAgent, 0);

        browse = new FlatButton("Browse…", Theme.Glyph.Folder, (_, _) => BrowseFile());
        save = new FlatButton(existing == null ? "Add playlist" : "Save and reload", Theme.Glyph.Check, async (_, _) => await SaveAsync()) { Fill = Theme.Accent };
        var cancel = new FlatButton("Cancel", "", (_, _) => Close());
        var remove = new FlatButton("Remove playlist", Theme.Glyph.Close, (_, _) => Remove()) { GlyphColor = Theme.Live, Visible = existing != null };
        status.AutoSize = false;
        status.ForeColor = Theme.Warn;
        Controls.AddRange(new Control[] { browse, save, cancel, remove, status });
        Tag = new Control[] { cancel, remove };

        if (existing != null)
        {
            name.Text = existing.Name;
            epg.Text = existing.EpgUrl;
            userAgent.Text = existing.UserAgent;
            kind.SelectedIndex = existing.Kind == PlaylistKind.Xtream ? 0 : 1;
            server.Text = existing.Server;
            username.Text = existing.Username;
            password.Text = existing.Password;
            address.Text = existing.Url;
        }
        Arrange();
    }

    private void AddField(string caption, Control field, int group)
    {
        Label label = Theme.Caption(caption);
        fields.Add((label, field, group));
        Controls.Add(label);
        Controls.Add(field);
    }

    private void Arrange()
    {
        int pad = Theme.S(20), width = Theme.S(520), y = pad;
        int active = kind.SelectedIndex == 0 ? 1 : 2;
        foreach (var (label, field, group) in fields)
        {
            bool visible = group == 0 || group == active;
            label.Visible = field.Visible = visible;
            if (!visible) continue;
            label.Location = new Point(pad, y);
            y += Theme.S(22);
            int fieldWidth = field == address ? width - Theme.S(120) : width;
            field.SetBounds(pad, y, fieldWidth, Theme.S(28));
            if (field == address) browse.SetBounds(pad + fieldWidth + Theme.S(8), y - Theme.S(2), Theme.S(112), Theme.S(32));
            y += Theme.S(40);
        }
        browse.Visible = active == 2;
        status.SetBounds(pad, y, width, Theme.S(44));
        y += Theme.S(50);
        var others = (Control[])Tag;
        save.SetBounds(pad, y, Theme.S(190), Theme.S(40));
        others[0].SetBounds(pad + Theme.S(198), y, Theme.S(110), Theme.S(40));
        others[1].SetBounds(pad + width - Theme.S(180), y, Theme.S(180), Theme.S(40));
        ClientSize = new Size(width + pad * 2, y + Theme.S(40) + pad);
    }

    private void BrowseFile()
    {
        using var dialog = new OpenFileDialog { Filter = "Playlists (*.m3u;*.m3u8;*.txt)|*.m3u;*.m3u8;*.txt|All files (*.*)|*.*", Title = "Choose an M3U playlist file" };
        if (dialog.ShowDialog(this) == DialogResult.OK) address.Text = dialog.FileName;
    }

    private void Remove()
    {
        if (MessageBox.Show(this, $"Remove “{existing.Name}” and its channels from the app?", Storage.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        app.Library.Remove(existing.Id);
        Removed = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task SaveAsync()
    {
        bool xtream = kind.SelectedIndex == 0;
        if (xtream && (server.Text.Trim().Length == 0 || username.Text.Trim().Length == 0 || password.Text.Trim().Length == 0))
        {
            status.Text = "Enter the server, username and password.";
            return;
        }
        if (!xtream && address.Text.Trim().Length == 0)
        {
            status.Text = "Enter the playlist address or choose a file.";
            return;
        }

        var playlist = existing != null
            ? new Playlist { Id = existing.Id, ServerTimeZone = existing.ServerTimeZone, LastRefreshed = existing.LastRefreshed }
            : new Playlist();
        playlist.Kind = xtream ? PlaylistKind.Xtream : PlaylistKind.M3U;
        playlist.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : (xtream ? "Xtream playlist" : "M3U playlist");
        playlist.Server = server.Text.Trim();
        playlist.Username = username.Text.Trim();
        playlist.Password = password.Text.Trim();
        playlist.Url = address.Text.Trim();
        playlist.EpgUrl = epg.Text.Trim();
        playlist.UserAgent = userAgent.Text.Trim();

        save.Enabled = false;
        status.ForeColor = Theme.Secondary;
        status.Text = "Loading the playlist…";
        bool loaded = existing == null ? await app.Library.AddAsync(playlist) : await app.Library.UpdateAsync(playlist);
        if (IsDisposed) return;
        save.Enabled = true;
        if (loaded)
        {
            DialogResult = DialogResult.OK;
            Close();
            _ = app.Library.RefreshEpgIfNeededAsync();
        }
        else
        {
            status.ForeColor = Theme.Warn;
            status.Text = app.Library.ErrorText;
        }
    }
}

/// <summary>Pick a channel from a searchable list (used by multi-view).</summary>
public sealed class ChannelPickerDialog : DarkForm
{
    private readonly AppServices app;
    private readonly TextBox search = Theme.Style(new TextBox());
    private readonly VirtualList list = new() { RowHeight = Theme.S(58) };
    private List<Channel> results = new();

    public Channel Picked { get; private set; }

    public ChannelPickerDialog(AppServices app)
    {
        this.app = app;
        Text = "Choose a channel";
        ClientSize = new Size(Theme.S(560), Theme.S(640));
        search.PlaceholderText = "Search all channels";
        search.SetBounds(Theme.S(16), Theme.S(16), ClientSize.Width - Theme.S(32), Theme.S(30));
        list.SetBounds(Theme.S(8), Theme.S(58), ClientSize.Width - Theme.S(16), ClientSize.Height - Theme.S(66));
        list.DrawItem = (g, index, bounds, state) => Painters.ChannelRow(g, list, bounds, state, results[index], app, false, DateTime.UtcNow);
        list.Activated += index =>
        {
            Picked = results[index];
            DialogResult = DialogResult.OK;
            Close();
        };
        search.TextChanged += (_, _) => Fill();
        Controls.Add(search);
        Controls.Add(list);
        Fill();
    }

    private void Fill()
    {
        string text = search.Text.Trim();
        results = text.Length == 0
            ? app.Library.ChannelsIn(app.SelectedGroup).Take(2000).ToList()
            : app.Library.Channels.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
        list.Count = results.Count;
        list.ScrollToTop();
    }
}

/// <summary>Tick which channel groups appear in the lists.</summary>
public sealed class GroupsDialog : DarkForm
{
    public GroupsDialog(AppServices app)
    {
        Text = "Channel groups to show";
        ClientSize = new Size(Theme.S(460), Theme.S(600));
        var box = new CheckedListBox
        {
            CheckOnClick = true,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            Font = Theme.Ui(10.5f),
            IntegralHeight = false
        };
        box.SetBounds(Theme.S(16), Theme.S(16), ClientSize.Width - Theme.S(32), ClientSize.Height - Theme.S(84));
        var hidden = new HashSet<string>(app.Settings.HiddenGroups);
        foreach (string group in app.Library.Groups) box.Items.Add(group, !hidden.Contains(group));

        var done = new FlatButton("Done", Theme.Glyph.Check, (_, _) =>
        {
            var newHidden = new List<string>();
            for (int i = 0; i < box.Items.Count; i++)
            {
                if (!box.GetItemChecked(i)) newHidden.Add((string)box.Items[i]);
            }
            app.Settings.HiddenGroups = newHidden;
            app.Settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }) { Fill = Theme.Accent };
        done.SetBounds(Theme.S(16), ClientSize.Height - Theme.S(56), Theme.S(140), Theme.S(40));
        Controls.Add(box);
        Controls.Add(done);
    }
}

/// <summary>Seasons and episodes of a series, with single and multiple download.</summary>
public sealed class SeriesDialog : DarkForm
{
    private readonly AppServices app;
    private readonly Series show;
    private readonly ComboBox seasons = Theme.Style(new ComboBox());
    private readonly VirtualList list = new() { RowHeight = Theme.S(54), EmptyText = "Loading episodes…" };
    private readonly FlatButton select, downloadSelected, downloadSeason;
    private List<Episode> episodes = new();
    private List<Episode> visible = new();

    public SeriesDialog(AppServices app, Series show)
    {
        this.app = app;
        this.show = show;
        Text = show.Name;
        ClientSize = new Size(Theme.S(720), Theme.S(680));
        int pad = Theme.S(16);

        var title = new Label { Text = show.Name, Font = Theme.Ui(15f, FontStyle.Bold), ForeColor = Theme.Text, AutoSize = false, AutoEllipsis = true };
        title.SetBounds(pad, pad, ClientSize.Width - pad * 2, Theme.S(34));
        string facts = string.Join(" · ", new[] { show.Year, string.IsNullOrEmpty(show.Rating) ? "" : "★ " + show.Rating, show.Group }.Where(s => !string.IsNullOrEmpty(s)));
        var plot = new Label { Text = (facts.Length > 0 ? facts + "\n" : "") + show.Plot, Font = Theme.Ui(9f), ForeColor = Theme.Secondary, AutoSize = false, AutoEllipsis = true };
        plot.SetBounds(pad, pad + Theme.S(38), ClientSize.Width - pad * 2, Theme.S(64));

        int y = pad + Theme.S(110);
        seasons.SetBounds(pad, y, Theme.S(160), Theme.S(30));
        seasons.SelectedIndexChanged += (_, _) => ShowSeason();
        select = new FlatButton("Select episodes", Theme.Glyph.Select, (_, _) => ToggleSelect());
        select.SetBounds(pad + Theme.S(170), y - Theme.S(3), Theme.S(160), Theme.S(36));
        downloadSelected = new FlatButton("Download selected", Theme.Glyph.Download, (_, _) => DownloadChecked()) { Visible = false, Fill = Theme.Accent };
        downloadSelected.SetBounds(pad + Theme.S(338), y - Theme.S(3), Theme.S(190), Theme.S(36));
        downloadSeason = new FlatButton("Download season", Theme.Glyph.Download, (_, _) => DownloadAll());
        downloadSeason.SetBounds(ClientSize.Width - pad - Theme.S(170), y - Theme.S(3), Theme.S(170), Theme.S(36));

        list.SetBounds(Theme.S(8), y + Theme.S(44), ClientSize.Width - Theme.S(16), ClientSize.Height - y - Theme.S(52));
        list.DrawItem = DrawEpisode;
        list.Activated += index =>
        {
            Episode episode = visible[index];
            DialogResult = DialogResult.OK;
            Close();
            app.PlayEpisode(episode, show);
        };
        list.ContextRequested += (index, point) =>
        {
            Episode episode = visible[index];
            Painters.ShowMenu(this, point,
                ("Play", () => { Close(); app.PlayEpisode(episode, show); }),
                ("Download", () => Queue(new[] { episode })));
        };
        list.CheckedChanged += UpdateButtons;
        Controls.AddRange(new Control[] { title, plot, seasons, select, downloadSelected, downloadSeason, list });
        Shown += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (show.Episodes.Count > 0)
        {
            episodes = show.Episodes;
        }
        else if (show.XtreamId != null)
        {
            XtreamClient client = app.Library.XtreamFor(show.PlaylistId);
            try
            {
                if (client != null) episodes = await client.EpisodesAsync(show.XtreamId.Value, show.Id.Length > 8 ? show.Id[..8] : show.Id);
            }
            catch (Exception)
            {
                episodes = new List<Episode>();
            }
        }
        if (IsDisposed) return;
        list.EmptyText = "No episodes were found for this series.";
        seasons.Items.Clear();
        foreach (int season in episodes.Select(e => e.Season).Distinct().OrderBy(s => s)) seasons.Items.Add("Season " + season);
        if (seasons.Items.Count > 0) seasons.SelectedIndex = 0;
        else ShowSeason();
    }

    private void ShowSeason()
    {
        int season = 1;
        if (seasons.SelectedItem is string label) int.TryParse(label.Replace("Season ", ""), out season);
        visible = episodes.Where(e => e.Season == season).ToList();
        list.Checked.Clear();
        list.Count = visible.Count;
        list.ScrollToTop();
        UpdateButtons();
    }

    private void DrawEpisode(Graphics g, int index, Rectangle bounds, ItemState state)
    {
        Episode episode = visible[index];
        DownloadItem download = app.Downloads.FindBySource(episode.Id);
        string detail = episode.Plot;
        if (download != null)
        {
            detail = download.State == DownloadState.Completed ? "Downloaded" : $"Download: {download.State} {download.Fraction:P0}";
        }
        else if (app.Resume.Get(episode.Id) > 0)
        {
            detail = "Watched to " + Theme.Clock(app.Resume.Get(episode.Id));
        }
        Painters.TwoLineRow(g, bounds, state, download?.State == DownloadState.Completed ? Theme.Glyph.Check : Theme.Glyph.Play,
            download?.State == DownloadState.Completed ? Theme.Good : Theme.Secondary,
            $"{episode.Number}. {episode.Title}", detail, list.CheckMode);
    }

    private void ToggleSelect()
    {
        list.CheckMode = !list.CheckMode;
        list.Checked.Clear();
        select.Selected = list.CheckMode;
        list.Invalidate();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        downloadSelected.Visible = list.CheckMode;
        downloadSelected.Enabled = list.Checked.Count > 0;
        downloadSelected.Caption = list.Checked.Count > 0 ? $"Download selected ({list.Checked.Count})" : "Download selected";
        downloadSeason.Enabled = visible.Count > 0;
    }

    private void DownloadChecked()
    {
        Queue(list.Checked.OrderBy(i => i).Select(i => visible[i]).ToList());
        ToggleSelect();
    }

    private void DownloadAll() => Queue(visible);

    private void Queue(IReadOnlyCollection<Episode> items)
    {
        // Lowest episode number first, so they finish in watching order.
        foreach (Episode episode in items.OrderBy(e => e.Number)) app.Download(episode, show);
        app.Toast(items.Count == 1 ? "Added 1 episode to Downloads" : $"Added {items.Count} episodes to Downloads");
        list.Invalidate();
    }
}
