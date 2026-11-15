namespace SuperiorIptv.Win;

/// <summary>
/// Live TV: groups on the left; on the right the preview with programme details on top and, below it,
/// the channels of the group with their programme timeline.
/// </summary>
public sealed class LivePage : PageBase
{
    private readonly VirtualList groups = new() { RowHeight = Theme.S(44), Back = Theme.Panel };
    private readonly GuideGrid grid;
    private readonly InfoPanel info;
    private readonly FlatButton earlier, nowButton, later;
    private List<string> groupNames = new();
    private List<Channel> channelList = new();
    private int ticks;
    // Kept in a field of its own: Control.Visible also reads false while a parent is hidden.
    private bool showInfo;

    public LivePage(AppServices app) : base(app)
    {
        info = new InfoPanel(app);
        grid = new GuideGrid(app)
        {
            ShowClock = true,
            FollowNow = true,
            EmptyText = "No channels here. Pick another group, or reload the playlist in Settings."
        };
        grid.ChannelClicked = channel =>
        {
            grid.MarkedId = "";
            App.Preview(channel);
        };
        earlier = new FlatButton("", Theme.Glyph.Left, (_, _) => grid.Page(-1));
        nowButton = new FlatButton("Now", "", (_, _) => grid.GoToNow());
        later = new FlatButton("", Theme.Glyph.Right, (_, _) => grid.Page(1));
        groups.DrawItem = DrawGroup;
        groups.Activated += index =>
        {
            App.SelectedGroup = groupNames[index];
            grid.MarkedId = "";
            LoadChannels(true);
        };
        Controls.AddRange(new Control[] { groups, info, earlier, nowButton, later, grid });
    }

    /// <summary>Where the live preview picture goes, in screen coordinates (empty when the pane is hidden).</summary>
    public Rectangle PreviewScreenBounds => showInfo ? info.RectangleToScreen(info.PreviewBounds) : Rectangle.Empty;

    /// <summary>Right-click menu of a channel, used by every list that shows channels.</summary>
    public static void ChannelMenu(Control owner, AppServices app, Channel channel, Point screen)
    {
        Recording active = app.Recordings.ActiveFor(channel.Id);
        string favoriteText = app.Library.IsFavorite(channel) ? "Remove from Favorites" : "Add to Favorites";
        string recordText = active != null ? "Stop recording" : "Record…";
        Action recordAction = () =>
        {
            if (active != null) app.Recordings.Stop(active.Id);
            else Sheets.Record(owner, app, channel);
        };
        var entries = new List<(string, Action)>
        {
            ("Watch full screen", () => app.Watch(channel)),
            (favoriteText, () => app.Library.ToggleFavorite(channel)),
            ("-", null),
            (recordText, recordAction),
            ("-", null),
            ("Guide source…", () => Sheets.GuideSource(owner, app, channel))
        };
        if (!string.IsNullOrEmpty(channel.Group)) entries.Add(("Open category: " + channel.Group, () => app.OpenCategory(channel)));
        Painters.ShowMenu(owner, screen, entries.ToArray());
    }

    /// <summary>Shows the category a channel belongs to, scrolled to that channel.</summary>
    public void Reveal(Channel channel)
    {
        bool hidden = App.Settings.HiddenGroups.Contains(channel.Group);
        App.SelectedGroup = hidden || string.IsNullOrEmpty(channel.Group) ? ChannelGroups.All : channel.Group;
        grid.MarkedId = channel.Id;
        LoadChannels(true);
        int index = channelList.FindIndex(c => c.Id == channel.Id);
        if (index >= 0) grid.EnsureVisible(index, true);
        int groupIndex = groupNames.IndexOf(App.SelectedGroup);
        if (groupIndex >= 0) groups.EnsureVisible(groupIndex, true);
    }

    protected override void Arrange()
    {
        bool wide = Width > Theme.S(900);
        int groupWidth = Math.Clamp(Width / 6, Theme.S(180), Theme.S(250));
        int rightWidth = Width - groupWidth;
        groups.SetBounds(0, 0, groupWidth, Height);

        // Preview and details on top, about a third of the height; hidden when the window is too small.
        int topHeight = 0;
        showInfo = wide && Height > Theme.S(520);
        info.Visible = showInfo;
        if (showInfo)
        {
            int pad = Theme.S(14);
            int previewWidth = Math.Min(rightWidth * 44 / 100, Theme.S(620));
            int previewHeight = Math.Min(previewWidth * 9 / 16, Height * 40 / 100);
            topHeight = previewHeight + pad * 2;
            info.SetBounds(groupWidth, 0, rightWidth, topHeight);
        }

        // One line with the group's name and the buttons that move the timeline.
        int barTop = topHeight;
        earlier.Width = later.Width = Theme.S(40);
        int x = Width - Theme.S(14);
        foreach (FlatButton button in new[] { later, nowButton, earlier })
        {
            int width = button == nowButton ? Theme.S(64) : Theme.S(40);
            x -= width;
            button.SetBounds(x, barTop + Theme.S(6), width, Theme.S(32));
            x -= Theme.S(6);
        }
        int gridTop = barTop + Theme.S(44);
        // The channel column is kept narrow so that most of the width goes to the programme timeline.
        grid.ChannelColumn = Math.Clamp(rightWidth * 27 / 100, Theme.S(210), Theme.S(300));
        grid.SetBounds(groupWidth, gridTop, rightWidth, Math.Max(10, Height - gridTop));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(Theme.Background);
        int x = groups.Right + Theme.S(14);
        int top = showInfo ? info.Bottom : 0;
        var line = new Rectangle(x, top, Math.Max(10, earlier.Left - x - Theme.S(10)), Theme.S(44));
        string title = App.SelectedGroup;
        int titleWidth = Math.Min(line.Width, Theme.MeasureWidth(g, title, Theme.Ui(12.5f, FontStyle.Bold)) + Theme.S(4));
        Theme.DrawText(g, title, Theme.Ui(12.5f, FontStyle.Bold), Theme.Text, new Rectangle(line.X, line.Y, titleWidth, line.Height));
        Theme.DrawText(g, channelList.Count + " channels", Theme.Ui(9f), Theme.Secondary,
            new Rectangle(line.X + titleWidth + Theme.S(12), line.Y + Theme.S(2), line.Width - titleWidth - Theme.S(12), line.Height));
    }

    private void DrawGroup(Graphics g, int index, Rectangle bounds, ItemState state)
    {
        string name = groupNames[index];
        Rectangle r = Painters.RowBackground(g, bounds, name == App.SelectedGroup, state.Hover);
        int x = r.X + Theme.S(10);
        string glyph = name switch
        {
            ChannelGroups.Favorites => Theme.Glyph.StarFilled,
            ChannelGroups.Recent => Theme.Glyph.Clock,
            ChannelGroups.All => Theme.Glyph.Tv,
            _ => ""
        };
        if (glyph.Length > 0)
        {
            Theme.DrawGlyph(g, glyph, 9.5f, Theme.Secondary, new Rectangle(x, r.Y, Theme.S(20), r.Height));
            x += Theme.S(26);
        }
        string count = App.Library.CountIn(name).ToString();
        int countWidth = Theme.MeasureWidth(g, count, Theme.Ui(8.5f));
        Theme.DrawText(g, count, Theme.Ui(8.5f), Theme.Secondary, new Rectangle(r.Right - countWidth - Theme.S(8), r.Y, countWidth + Theme.S(4), r.Height), HorizontalAlignment.Right);
        Theme.DrawText(g, name, Theme.Ui(10f), Theme.Text, new Rectangle(x, r.Y, r.Right - countWidth - Theme.S(14) - x, r.Height));
    }

    private void LoadChannels(bool resetScroll)
    {
        groupNames = App.Library.VisibleGroups();
        if (!groupNames.Contains(App.SelectedGroup)) App.SelectedGroup = ChannelGroups.All;
        groups.Count = groupNames.Count;
        channelList = App.Library.ChannelsIn(App.SelectedGroup);
        grid.SetChannels(channelList);
        if (resetScroll) grid.ScrollToTop();
        Invalidate();
    }

    public override void OnShown()
    {
        LoadChannels(false);
        int playing = channelList.FindIndex(c => c.Id == App.Player.Item?.Channel?.Id);
        if (playing >= 0 && grid.MarkedId.Length == 0) grid.EnsureVisible(playing, true);
    }

    public override void OnLibraryChanged() => LoadChannels(false);

    public override void OnTick()
    {
        ticks++;
        info.Invalidate();
        grid.KeepUp();
        if (ticks % 4 == 0) grid.Invalidate();
    }

    /// <summary>Top pane: the preview picture on the left, details of the channel being previewed on the right.</summary>
    private sealed class InfoPanel : Control
    {
        private readonly AppServices app;
        private readonly FlatButton full, favorite, record;

        public InfoPanel(AppServices app)
        {
            this.app = app;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            full = new FlatButton("Full screen", Theme.Glyph.FullScreen, (_, _) =>
            {
                Channel channel = app.Player.Item?.Channel;
                if (channel != null) app.Watch(channel);
            });
            favorite = new FlatButton("Favorite", Theme.Glyph.Star, (_, _) =>
            {
                Channel channel = app.Player.Item?.Channel;
                if (channel != null) app.Library.ToggleFavorite(channel);
            });
            record = new FlatButton("Record", Theme.Glyph.Record, (_, _) =>
            {
                Channel channel = app.Player.Item?.Channel;
                if (channel == null) return;
                Recording active = app.Recordings.ActiveFor(channel.Id);
                if (active != null) app.Recordings.Stop(active.Id);
                else Sheets.Record(this, app, channel);
            }) { GlyphColor = Theme.Live };
            Controls.AddRange(new Control[] { full, favorite, record });
        }

        private int Pad => Theme.S(14);

        public Rectangle PreviewBounds
        {
            get
            {
                int height = Math.Max(10, Height - Pad * 2);
                int width = Math.Max(10, Math.Min(height * 16 / 9, Width / 2));
                return new Rectangle(Pad, Pad, width, height);
            }
        }

        /// <summary>The area to the right of the preview.</summary>
        private Rectangle Details
        {
            get
            {
                Rectangle preview = PreviewBounds;
                int x = preview.Right + Theme.S(18);
                return new Rectangle(x, Pad, Math.Max(10, Width - x - Pad), Math.Max(10, Height - Pad * 2));
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Rectangle details = Details;
            int width = Math.Min(Theme.S(132), (details.Width - Theme.S(16)) / 3);
            int y = details.Bottom - Theme.S(38);
            full.SetBounds(details.X, y, width, Theme.S(38));
            favorite.SetBounds(details.X + width + Theme.S(8), y, width, Theme.S(38));
            record.SetBounds(details.X + (width + Theme.S(8)) * 2, y, width, Theme.S(38));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Prepare(g);
            g.Clear(Theme.Background);
            Rectangle preview = PreviewBounds;
            Theme.FillRound(g, Color.Black, preview, Theme.S(8));

            Channel channel = app.Player.Item?.Channel;
            bool live = channel != null && app.Player.Item.IsLive;
            full.Enabled = favorite.Enabled = record.Enabled = live;
            if (!live)
            {
                Theme.DrawGlyph(g, Theme.Glyph.Tv, 26f, Theme.Faint, new Rectangle(preview.X, preview.Y, preview.Width, preview.Height - Theme.S(30)));
                Theme.DrawText(g, "Select a channel to preview", Theme.Ui(10f), Theme.Faint,
                    new Rectangle(preview.X, preview.Y + preview.Height / 2 + Theme.S(14), preview.Width, Theme.S(30)), HorizontalAlignment.Center);
                return;
            }

            bool isFavorite = app.Library.IsFavorite(channel);
            favorite.Glyph = isFavorite ? Theme.Glyph.StarFilled : Theme.Glyph.Star;
            favorite.Selected = isFavorite;
            bool recording = app.Recordings.ActiveFor(channel.Id) != null;
            record.Caption = recording ? "Stop rec." : "Record";
            record.Glyph = recording ? Theme.Glyph.Stop : Theme.Glyph.Record;

            DateTime now = DateTime.UtcNow;
            Rectangle details = Details;
            int x = details.X, width = details.Width, y = details.Y;
            int bottom = full.Top - Theme.S(8);

            var logo = new Rectangle(x, y, Theme.S(60), Theme.S(42));
            Painters.Logo(g, this, channel.Logo, channel.Name, logo);
            int textX = logo.Right + Theme.S(12);
            Theme.DrawText(g, channel.Name, Theme.Ui(12.5f, FontStyle.Bold), Theme.Text, new Rectangle(textX, y, details.Right - textX, Theme.S(24)));
            Theme.DrawText(g, channel.Group, Theme.Ui(9f), Theme.Secondary, new Rectangle(textX, y + Theme.S(23), details.Right - textX, Theme.S(20)));
            y += Theme.S(52);

            Programme current = app.Library.Epg.Current(channel, now);
            if (current == null)
            {
                if (y + Theme.S(24) <= bottom) Theme.DrawText(g, "No programme information", Theme.Ui(9.5f), Theme.Faint, new Rectangle(x, y, width, Theme.S(24)));
                return;
            }
            if (y + Theme.S(26) > bottom) return;
            Theme.DrawText(g, current.Title, Theme.Ui(11.5f, FontStyle.Bold), Theme.Text, new Rectangle(x, y, width, Theme.S(26)));
            y += Theme.S(27);
            if (y + Theme.S(30) > bottom) return;
            string range = TimeText.ShortDate(current.Start.ToLocalTime()) + ", " + Theme.Range(current);
            int rangeWidth = Math.Min(width, Theme.MeasureWidth(g, range, Theme.Ui(9f)) + Theme.S(6));
            Theme.DrawText(g, range, Theme.Ui(9f), Theme.Secondary, new Rectangle(x, y, rangeWidth, Theme.S(20)));
            int progressX = x + rangeWidth + Theme.S(10);
            if (details.Right - progressX > Theme.S(60))
            {
                Theme.DrawProgress(g, new Rectangle(progressX, y + Theme.S(8), Math.Min(details.Right - progressX, Theme.S(260)), Theme.S(4)), current.Progress(now), Theme.Accent);
            }
            y += Theme.S(26);

            Programme next = app.Library.Epg.Next(channel, now);
            int nextHeight = next != null ? Theme.S(22) : 0;
            int room = bottom - y - nextHeight;
            if (!string.IsNullOrEmpty(current.Desc) && room > Theme.S(18))
            {
                int descHeight = Math.Min(room, Theme.MeasureHeight(g, current.Desc, Theme.Ui(9f), width));
                Theme.DrawWrapped(g, current.Desc, Theme.Ui(9f), Theme.Secondary, new Rectangle(x, y, width, descHeight));
                y += descHeight + Theme.S(4);
            }
            if (next != null && y + Theme.S(20) <= bottom)
            {
                Theme.DrawText(g, "Next: " + next.Title + " · " + Theme.Time(next.Start), Theme.Ui(9f), Theme.Text, new Rectangle(x, y, width, Theme.S(20)));
            }
        }
    }
}
