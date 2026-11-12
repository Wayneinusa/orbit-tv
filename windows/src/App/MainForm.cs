using System.Runtime.InteropServices;

namespace SuperiorIptv.Win;

/// <summary>
/// The main window: navigation rail, the current screen, and the player.
///
/// The video is drawn by VLC into one black panel that belongs to this window. In normal mode the panel
/// sits over the Live TV screen's preview area; in player mode it fills the window and the control bar,
/// info banner and channel list are placed on top of it.
///
/// Keyboard in player mode: Up/Down change channel, Left opens the channel list (or skips back),
/// Right skips forward, Enter shows the controls, Space pauses, ] and [ change speed, M mutes,
/// F or F11 toggles full screen, Esc goes back.
/// </summary>
public sealed class MainForm : Form
{
    public static readonly (string Key, string Title, string Glyph)[] Sections =
    {
        ("live", "Live TV", Theme.Glyph.Tv),
        ("guide", "Guide", Theme.Glyph.Guide),
        ("movies", "Movies", Theme.Glyph.Movie),
        ("series", "Series", Theme.Glyph.Series),
        ("recordings", "Recordings", Theme.Glyph.Record),
        ("downloads", "Downloads", Theme.Glyph.Download),
        ("multiview", "Multi-view", Theme.Glyph.Multi),
        ("search", "Search", Theme.Glyph.Search),
        ("settings", "Settings", Theme.Glyph.Settings)
    };

    private readonly AppServices app;
    private readonly Panel rail = new() { BackColor = Theme.Panel };
    private readonly Panel pageHost = new() { BackColor = Theme.Background };
    private readonly Panel video = new() { BackColor = Color.Black, Visible = false };
    private readonly Dictionary<string, PageBase> pages = new();
    private readonly Dictionary<string, FlatButton> railButtons = new();
    private readonly Label toast = new() { Visible = false, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label status = new() { Visible = false, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
    private readonly PlayerBar bar;
    private readonly InfoBanner banner;
    private readonly ChannelPanel channelPanel;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 400 };

    private string currentKey = "";
    private PageBase current;
    private bool playerMode;
    private bool borderless;
    private FormBorderStyle savedBorder;
    private FormWindowState savedState;
    private Rectangle savedBounds;
    private DateTime controlsUntil = DateTime.MinValue;
    private DateTime bannerUntil = DateTime.MinValue;
    private DateTime toastUntil = DateTime.MinValue;
    private Point lastCursor;
    private volatile bool libraryDirty = true;
    private string lastItemId = "";
    private int ticks;

    public AppServices Services => app;
    public string CurrentKey => currentKey;
    public bool InPlayer => playerMode;

    public MainForm(AppServices app)
    {
        this.app = app;
        Text = Storage.AppName;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Ui(10f);
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        MinimumSize = new Size(Theme.S(900), Theme.S(560));
        StartPosition = FormStartPosition.CenterScreen;
        Rectangle area = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(area.Width - Theme.S(60), Theme.S(1500)), Math.Min(area.Height - Theme.S(60), Theme.S(900)));
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception)
        {
        }

        // Navigation rail
        int y = Theme.S(10);
        foreach (var (key, title, glyph) in Sections)
        {
            string target = key;
            var button = new FlatButton(title, glyph, (_, _) => GoTo(target))
            {
                Stacked = true,
                Fill = Theme.Panel,
                BackColor = Theme.Panel,
                GlyphSize = 15f,
                TextSize = 8f
            };
            button.SetBounds(Theme.S(6), y, Theme.S(80), Theme.S(60));
            y += Theme.S(64);
            rail.Controls.Add(button);
            railButtons[key] = button;
        }
        rail.Paint += (_, e) =>
        {
            Theme.Prepare(e.Graphics);
            Theme.DrawText(e.Graphics, DateTime.Now.ToString("HH:mm"), Theme.Ui(9.5f), Theme.Secondary,
                new Rectangle(0, rail.Height - Theme.S(34), rail.Width, Theme.S(24)), HorizontalAlignment.Center);
        };

        toast.BackColor = Theme.Raised;
        toast.ForeColor = Theme.Text;
        toast.Font = Theme.Ui(10f);
        status.BackColor = Theme.Panel;
        status.ForeColor = Theme.Secondary;
        status.Font = Theme.Ui(9f);
        status.Click += (_, _) =>
        {
            app.Library.ErrorText = "";
            UpdateStatus();
        };

        bar = new PlayerBar(this) { Visible = false };
        banner = new InfoBanner(app) { Visible = false };
        channelPanel = new ChannelPanel(this) { Visible = false };

        video.MouseMove += (_, e) =>
        {
            if (!playerMode) return;
            Point p = Cursor.Position;
            if (p == lastCursor) return;
            lastCursor = p;
            ShowControls();
        };
        video.MouseDown += (_, e) =>
        {
            if (!playerMode)
            {
                if (app.Player.Item != null) EnterPlayer();
                return;
            }
            if (channelPanel.Visible) channelPanel.Visible = false;
            else if (e.Button == MouseButtons.Right) BarMenu();
            else ShowControls();
        };
        video.DoubleClick += (_, _) =>
        {
            if (playerMode) ToggleBorderless();
        };

        Controls.AddRange(new Control[] { toast, channelPanel, banner, bar, status, rail, pageHost, video });

        app.Preview = PreviewChannel;
        app.Watch = WatchChannel;
        app.PlayItem = PlayItem;
        app.Toast = ShowToast;
        app.GoTo = GoTo;
        app.Library.Changed += () => libraryDirty = true;
        app.Player.AttachTo(video);

        timer.Tick += (_, _) => Tick();
        timer.Start();
        Arrange();
        GoTo("live");
    }

    // ---- Layout

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Arrange();
    }

    private void Arrange()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || bar == null) return;
        int width = ClientSize.Width, height = ClientSize.Height;
        if (playerMode)
        {
            rail.Visible = pageHost.Visible = status.Visible = false;
            video.SetBounds(0, 0, width, height);
            video.Visible = true;
            int barHeight = bar.PreferredHeight;
            bar.SetBounds(0, height - barHeight, width, barHeight);
            int bannerHeight = Theme.S(96);
            banner.SetBounds(Theme.S(16), height - barHeight - bannerHeight - Theme.S(10), width - Theme.S(32), bannerHeight);
            if (!bar.Visible) banner.Top = height - bannerHeight - Theme.S(16);
            channelPanel.SetBounds(0, 0, Math.Min(Theme.S(430), width * 2 / 3), bar.Visible ? height - barHeight : height);
            channelPanel.BringToFront();
            banner.BringToFront();
            bar.BringToFront();
        }
        else
        {
            bar.Visible = banner.Visible = channelPanel.Visible = false;
            int railWidth = Theme.S(92);
            int statusHeight = status.Visible ? Theme.S(28) : 0;
            rail.SetBounds(0, 0, railWidth, height);
            pageHost.SetBounds(railWidth, 0, width - railWidth, height - statusHeight);
            status.SetBounds(railWidth, height - statusHeight, width - railWidth, statusHeight);
            rail.Visible = pageHost.Visible = true;
            if (current != null) current.SetBounds(0, 0, pageHost.Width, pageHost.Height);
            PlacePreview();
        }
        toast.SetBounds((width - Theme.S(520)) / 2, Theme.S(18), Theme.S(520), Theme.S(40));
        toast.BringToFront();
    }

    /// <summary>Puts the video panel over the Live TV screen's preview area (or hides it).</summary>
    private void PlacePreview()
    {
        if (playerMode) return;
        Rectangle target = Rectangle.Empty;
        if (currentKey == "live" && app.Player.Item != null && pages.TryGetValue("live", out PageBase page))
        {
            Rectangle screen = ((LivePage)page).PreviewScreenBounds;
            if (!screen.IsEmpty) target = RectangleToClient(screen);
        }
        if (target.IsEmpty)
        {
            if (video.Visible) video.Visible = false;
            return;
        }
        if (video.Visible && video.Bounds == target) return;
        video.Bounds = target;
        video.Visible = true;
        video.BringToFront();
        toast.BringToFront();
    }

    // ---- Navigation

    public void GoTo(string key)
    {
        if (playerMode) ClosePlayer();
        if (key == currentKey && current != null) return;
        if (!pages.TryGetValue(key, out PageBase page))
        {
            page = key switch
            {
                "live" => new LivePage(app),
                "guide" => new GuidePage(app),
                "movies" => new MediaPage(app, true),
                "series" => new MediaPage(app, false),
                "recordings" => new RecordingsPage(app),
                "downloads" => new DownloadsPage(app),
                "multiview" => new MultiViewPage(app),
                "search" => new SearchPage(app),
                _ => new SettingsPage(app)
            };
            page.Visible = false;
            pages[key] = page;
            pageHost.Controls.Add(page);
        }

        // The live preview only exists on the Live TV screen; do not leave sound playing without a picture.
        if (currentKey == "live" && key != "live" && app.Player.Item != null) app.Player.Stop();

        if (current != null)
        {
            current.OnHidden();
            current.Visible = false;
        }
        current = page;
        currentKey = key;
        foreach (var pair in railButtons) pair.Value.Selected = pair.Key == key;
        page.SetBounds(0, 0, pageHost.Width, pageHost.Height);
        page.Visible = true;
        page.BringToFront();
        page.Relayout();
        page.OnShown();
        Arrange();
    }

    // ---- Player

    private void StartChannel(Channel channel)
    {
        app.Player.Play(app.Library.Playable(channel));
        app.Library.NoteWatched(channel);
    }

    private bool IsOnAir(Channel channel)
    {
        PlayableItem item = app.Player.Item;
        return item != null && item.IsLive && item.Id == channel.Id;
    }

    /// <summary>First click plays the channel in the preview pane; clicking it again goes to the player.</summary>
    private void PreviewChannel(Channel channel)
    {
        bool hasPreviewPane = currentKey == "live" && !((LivePage)pages["live"]).PreviewScreenBounds.IsEmpty;
        if (IsOnAir(channel) || !hasPreviewPane)
        {
            if (!IsOnAir(channel)) StartChannel(channel);
            EnterPlayer();
            return;
        }
        StartChannel(channel);
        PlacePreview();
    }

    private void WatchChannel(Channel channel)
    {
        if (!IsOnAir(channel)) StartChannel(channel);
        EnterPlayer();
    }

    private void PlayItem(PlayableItem item)
    {
        app.Player.Play(item);
        EnterPlayer();
    }

    private void EnterPlayer()
    {
        playerMode = true;
        channelPanel.Visible = false;
        bar.Rebuild();
        Arrange();
        ShowControls();
        FlashBanner();
        Focus();
    }

    public void ClosePlayer()
    {
        if (!playerMode) return;
        if (borderless) ToggleBorderless();
        playerMode = false;
        PlayableItem item = app.Player.Item;
        bool keepPreview = currentKey == "live" && item != null && item.IsLive;
        if (!keepPreview) app.Player.Stop();
        Arrange();
        current?.OnShown();
    }

    /// <summary>Channel up / down while watching live TV.</summary>
    public void Zap(int offset)
    {
        PlayableItem item = app.Player.Item;
        if (item == null || !item.IsLive || item.Channel == null) return;
        Channel next = app.Library.Neighbour(item.Channel, app.SelectedGroup, offset);
        if (next == null) return;
        StartChannel(next);
        FlashBanner();
    }

    public void ShowControls(double seconds = 4)
    {
        if (!playerMode) return;
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        if (until > controlsUntil) controlsUntil = until;
        if (!bar.Visible)
        {
            bar.Sync(true);
            bar.Visible = banner.Visible = true;
            Arrange();
        }
    }

    private void FlashBanner()
    {
        bannerUntil = DateTime.UtcNow.AddSeconds(5);
        if (playerMode && !banner.Visible)
        {
            banner.Visible = true;
            Arrange();
        }
    }

    public void ShowChannelList()
    {
        if (!playerMode || !(app.Player.Item?.IsLive ?? false)) return;
        channelPanel.Reload();
        channelPanel.Visible = true;
        Arrange();
        channelPanel.FocusList();
    }

    public void HideChannelList()
    {
        channelPanel.Visible = false;
        Focus();
    }

    public void ToggleBorderless()
    {
        if (!borderless)
        {
            savedBorder = FormBorderStyle;
            savedState = WindowState;
            savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            borderless = true;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            borderless = false;
            FormBorderStyle = savedBorder;
            Bounds = savedBounds;
            WindowState = savedState;
        }
        Arrange();
    }

    public void ShowToast(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowToast(text)));
            return;
        }
        toast.Text = text;
        toast.Visible = true;
        toast.BringToFront();
        toastUntil = DateTime.UtcNow.AddSeconds(3.5);
    }

    /// <summary>Right-click on the video: the less common actions.</summary>
    private void BarMenu()
    {
        ShowControls(8);
        var entries = new List<(string, Action)>
        {
            (borderless ? "Leave full screen" : "Full screen", ToggleBorderless),
            ("Picture shape: " + app.Player.AspectLabel, () => app.Player.CycleAspect())
        };
        if (app.Player.CanChangeSpeed) entries.Add(("Speed…", () => bar.SpeedMenu()));
        entries.Add(("-", null));
        entries.Add(("Back", ClosePlayer));
        Painters.ShowMenu(this, Cursor.Position, entries.ToArray());
    }

    // ---- Keyboard

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!playerMode) return base.ProcessCmdKey(ref msg, keyData);
        PlayerEngine player = app.Player;
        bool live = player.Item?.IsLive ?? false;

        if (channelPanel.Visible)
        {
            switch (keyData)
            {
                case Keys.Escape:
                case Keys.Back:
                    HideChannelList();
                    return true;
                case Keys.Left:
                    channelPanel.StepGroup(-1);
                    return true;
                case Keys.Right:
                    channelPanel.StepGroup(1);
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        switch (keyData)
        {
            case Keys.Escape:
            case Keys.Back:
                if (borderless) ToggleBorderless();
                else ClosePlayer();
                return true;
            case Keys.Up:
            case Keys.PageUp:
                if (live) Zap(1);
                else ShowControls();
                return true;
            case Keys.Down:
            case Keys.PageDown:
                if (live) Zap(-1);
                else ShowControls();
                return true;
            case Keys.Left:
                if (live) ShowChannelList();
                else
                {
                    player.SeekBy(-10);
                    ShowControls();
                }
                return true;
            case Keys.Right:
                if (live) FlashBanner();
                else
                {
                    player.SeekBy(10);
                    ShowControls();
                }
                return true;
            case Keys.Enter:
                if (bar.Visible) controlsUntil = DateTime.MinValue;
                else ShowControls(6);
                return true;
            case Keys.Space:
                if (!live) player.TogglePause();
                ShowControls();
                return true;
            case Keys.F:
            case Keys.F11:
                ToggleBorderless();
                return true;
            case Keys.M:
                player.Muted = !player.Muted;
                ShowToast(player.Muted ? "Muted" : "Sound on");
                return true;
            case Keys.OemCloseBrackets:
            case Keys.Oemplus:
            case Keys.Add:
                ChangeSpeed(1);
                return true;
            case Keys.OemOpenBrackets:
            case Keys.OemMinus:
            case Keys.Subtract:
                ChangeSpeed(-1);
                return true;
            case Keys.OemPipe:
            case Keys.D1:
                if (player.CanChangeSpeed)
                {
                    player.SetRate(1f);
                    ShowToast("Speed 1×");
                }
                return true;
            case Keys.L:
                ShowChannelList();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ChangeSpeed(int direction)
    {
        PlayerEngine player = app.Player;
        if (!player.CanChangeSpeed)
        {
            ShowToast("Speed can be changed for movies, catch-up, recordings and downloads, not for live channels");
            return;
        }
        player.StepRate(direction);
        ShowToast("Speed " + PlayerBar.SpeedLabel(player.Rate));
        ShowControls();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!playerMode || channelPanel.Visible) return;
        app.Player.Volume += e.Delta > 0 ? 5 : -5;
        app.Settings.Volume = app.Player.Volume;
        if (app.Player.Muted && e.Delta > 0) app.Player.Muted = false;
        ShowToast($"Volume {app.Player.Volume}%");
    }

    // ---- Housekeeping

    private void UpdateStatus()
    {
        string text = "";
        Color color = Theme.Secondary;
        if (!string.IsNullOrEmpty(app.Library.BusyText)) text = "   " + app.Library.BusyText;
        else if (!string.IsNullOrEmpty(app.Library.ErrorText))
        {
            text = "   ⚠ " + app.Library.ErrorText + "   (click to dismiss)";
            color = Theme.Warn;
        }
        else if (app.Library.EpgRefreshing) text = "   " + app.Library.EpgStatus;
        bool show = text.Length > 0 && !playerMode;
        status.ForeColor = color;
        if (status.Text != text) status.Text = text;
        if (status.Visible != show)
        {
            status.Visible = show;
            Arrange();
        }
    }

    private void Tick()
    {
        ticks++;
        PlayerEngine player = app.Player;
        player.Poll();

        if (libraryDirty)
        {
            libraryDirty = false;
            current?.OnLibraryChanged();
        }
        UpdateStatus();
        if (!playerMode)
        {
            current?.OnTick();
            PlacePreview();
            if (ticks % 30 == 0) rail.Invalidate();
        }

        // Player overlays
        if (playerMode)
        {
            Point cursor = Cursor.Position;
            if (cursor != lastCursor)
            {
                lastCursor = cursor;
                if (RectangleToScreen(ClientRectangle).Contains(cursor)) ShowControls();
            }
            if (bar.Visible && bar.RectangleToScreen(bar.ClientRectangle).Contains(cursor)) ShowControls(2);

            string itemId = player.Item?.Id ?? "";
            if (itemId != lastItemId)
            {
                lastItemId = itemId;
                bar.Rebuild();
                Arrange();
            }
            DateTime now = DateTime.UtcNow;
            bool wantBar = now < controlsUntil;
            bool wantBanner = wantBar || now < bannerUntil || player.Status == PlayerStatus.Loading || player.Status == PlayerStatus.Failed || player.Status == PlayerStatus.Paused;
            if (bar.Visible != wantBar || banner.Visible != wantBanner)
            {
                bar.Visible = wantBar;
                banner.Visible = wantBanner;
                Arrange();
            }
            if (bar.Visible) bar.Sync(false);
            if (banner.Visible) banner.Invalidate();

            // A film or recording that reached its end returns to where the viewer came from.
            if (player.Item != null && !player.Item.IsLive && player.Status == PlayerStatus.Ended) ClosePlayer();
            if (player.Item == null) ClosePlayer();
        }
        else
        {
            lastItemId = "";
        }

        if (toast.Visible && DateTime.UtcNow > toastUntil) toast.Visible = false;

        // Rail badges
        if (ticks % 3 == 0)
        {
            bool recording = app.Recordings.Snapshot().Any(r => r.Status == RecordingStatus.Recording);
            railButtons["recordings"].GlyphColor = recording ? Theme.Live : Theme.Text;
            railButtons["recordings"].Invalidate();
            int active = app.Downloads.ActiveCount;
            railButtons["downloads"].Caption = active > 0 ? $"Downloads ({active})" : "Downloads";
        }

        // Keep the PC awake while something is playing, recording or downloading.
        bool playing = player.Item != null && player.Status != PlayerStatus.Paused;
        bool working = app.Recordings.AnyActive || app.Downloads.ActiveCount > 0;
        uint flags = EsContinuous;
        if (playing || working) flags |= EsSystemRequired;
        if (playing) flags |= EsDisplayRequired;
        SetThreadExecutionState(flags);
    }

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>When true the window closes without asking (used by the self-test).</summary>
    public bool CloseWithoutAsking;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!CloseWithoutAsking && e.CloseReason == CloseReason.UserClosing && (app.Recordings.AnyActive || app.Downloads.ActiveCount > 0))
        {
            DialogResult answer = MessageBox.Show(this,
                "A recording or download is still running.\n\n" +
                "Yes: close anyway. Downloads continue the next time you open the app, and recordings resume if their end time has not passed.\n" +
                "No: keep it running and minimise the window instead.",
                Storage.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.No) WindowState = FormWindowState.Minimized;
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        timer.Stop();
        SetThreadExecutionState(EsContinuous);
        base.OnFormClosed(e);
    }

    /// <summary>Saves a picture of the window's own drawing (the video area stays black). Used by the self-test.</summary>
    public void CaptureTo(string path)
    {
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    // ---- Used by the overlays

    internal void BarAction(Action action, double keepSeconds = 4)
    {
        action();
        ShowControls(keepSeconds);
    }
}
