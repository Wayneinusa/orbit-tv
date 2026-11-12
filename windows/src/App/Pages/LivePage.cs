namespace SuperiorIptv.Win;

/// <summary>Live TV: groups on the left, channels in the middle, preview and programme info on the right.</summary>
public sealed class LivePage : PageBase
{
    private readonly VirtualList groups = new() { RowHeight = Theme.S(44), Back = Theme.Panel };
    private readonly VirtualList channels = new() { RowHeight = Theme.S(64), EmptyText = "No channels here. Pick another group, or reload the playlist in Settings." };
    private readonly InfoPanel info;
    private List<string> groupNames = new();
    private List<Channel> channelList = new();
    private int ticks;

    public LivePage(AppServices app) : base(app)
    {
        info = new InfoPanel(app);
        groups.DrawItem = DrawGroup;
        groups.Activated += index =>
        {
            App.SelectedGroup = groupNames[index];
            LoadChannels(true);
        };
        channels.DrawItem = (g, index, bounds, state) =>
            Painters.ChannelRow(g, channels, bounds, state, channelList[index], App, App.Player.Item?.Id == channelList[index].Id, DateTime.UtcNow);
        channels.Activated += index => App.Preview(channelList[index]);
        channels.ContextRequested += (index, point) => ChannelMenu(this, App, channelList[index], point);
        Controls.Add(groups);
        Controls.Add(channels);
        Controls.Add(info);
    }

    /// <summary>Where the live preview picture goes, in screen coordinates (empty when the pane is hidden).</summary>
    public Rectangle PreviewScreenBounds => info.Visible ? info.RectangleToScreen(info.PreviewBounds) : Rectangle.Empty;

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
        Painters.ShowMenu(owner, screen,
            ("Watch full screen", () => app.Watch(channel)),
            (favoriteText, () => app.Library.ToggleFavorite(channel)),
            ("-", null),
            (recordText, recordAction));
    }

    protected override void Arrange()
    {
        bool wide = Width > Theme.S(900);
        int groupWidth = Math.Clamp(Width / 5, Theme.S(190), Theme.S(280));
        int infoWidth = wide ? Math.Clamp(Width * 38 / 100, Theme.S(340), Theme.S(760)) : 0;
        groups.SetBounds(0, 0, groupWidth, Height);
        channels.SetBounds(groupWidth, HeaderHeight, Width - groupWidth - infoWidth, Height - HeaderHeight);
        info.Visible = wide;
        info.SetBounds(Width - infoWidth, 0, infoWidth, Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Prepare(e.Graphics);
        e.Graphics.Clear(Theme.Background);
        DrawHeader(e.Graphics, groups.Right + Theme.S(16), App.SelectedGroup, channelList.Count + " channels");
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
        channels.Count = channelList.Count;
        if (resetScroll) channels.ScrollToTop();
        Invalidate();
    }

    public override void OnShown()
    {
        LoadChannels(false);
        int playing = channelList.FindIndex(c => c.Id == App.Player.Item?.Channel?.Id);
        if (playing >= 0) channels.EnsureVisible(playing, true);
    }

    public override void OnLibraryChanged() => LoadChannels(false);

    public override void OnTick()
    {
        ticks++;
        info.Invalidate();
        if (ticks % 4 == 0) channels.Invalidate();
    }

    /// <summary>Right-hand pane: the preview picture, buttons and details of the channel being previewed.</summary>
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

        public Rectangle PreviewBounds
        {
            get
            {
                int pad = Theme.S(14);
                int width = Math.Max(10, Width - pad * 2);
                int height = Math.Min(width * 9 / 16, Math.Max(10, Height / 2));
                return new Rectangle(pad, pad, width, height);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Rectangle preview = PreviewBounds;
            int y = preview.Bottom + Theme.S(12);
            int width = (preview.Width - Theme.S(16)) / 3;
            full.SetBounds(preview.X, y, width, Theme.S(38));
            favorite.SetBounds(preview.X + width + Theme.S(8), y, width, Theme.S(38));
            record.SetBounds(preview.X + (width + Theme.S(8)) * 2, y, width, Theme.S(38));
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
                Theme.DrawGlyph(g, Theme.Glyph.Tv, 30f, Theme.Faint, new Rectangle(preview.X, preview.Y, preview.Width, preview.Height - Theme.S(30)));
                Theme.DrawText(g, "Select a channel to preview", Theme.Ui(10f), Theme.Faint,
                    new Rectangle(preview.X, preview.Y + preview.Height / 2 + Theme.S(16), preview.Width, Theme.S(30)), HorizontalAlignment.Center);
                return;
            }

            bool isFavorite = app.Library.IsFavorite(channel);
            favorite.Glyph = isFavorite ? Theme.Glyph.StarFilled : Theme.Glyph.Star;
            favorite.Selected = isFavorite;
            bool recording = app.Recordings.ActiveFor(channel.Id) != null;
            record.Caption = recording ? "Stop rec." : "Record";
            record.Glyph = recording ? Theme.Glyph.Stop : Theme.Glyph.Record;

            DateTime now = DateTime.UtcNow;
            int x = preview.X, width = preview.Width;
            int y = full.Bottom + Theme.S(16);
            var logo = new Rectangle(x, y, Theme.S(64), Theme.S(44));
            Painters.Logo(g, this, channel.Logo, channel.Name, logo);
            Theme.DrawText(g, channel.Name, Theme.Ui(13f, FontStyle.Bold), Theme.Text, new Rectangle(logo.Right + Theme.S(12), y, width - logo.Width - Theme.S(12), Theme.S(26)));
            Theme.DrawText(g, channel.Group, Theme.Ui(9f), Theme.Secondary, new Rectangle(logo.Right + Theme.S(12), y + Theme.S(24), width - logo.Width - Theme.S(12), Theme.S(20)));
            y += Theme.S(58);

            Programme current = app.Library.Epg.Current(channel, now);
            if (current == null)
            {
                Theme.DrawText(g, "No programme information", Theme.Ui(9.5f), Theme.Faint, new Rectangle(x, y, width, Theme.S(24)));
                return;
            }
            Theme.DrawText(g, current.Title, Theme.Ui(11.5f, FontStyle.Bold), Theme.Text, new Rectangle(x, y, width, Theme.S(26)));
            y += Theme.S(28);
            Theme.DrawText(g, Theme.Range(current), Theme.Ui(9f), Theme.Secondary, new Rectangle(x, y, width, Theme.S(20)));
            y += Theme.S(24);
            Theme.DrawProgress(g, new Rectangle(x, y, width, Theme.S(4)), current.Progress(now), Theme.Accent);
            y += Theme.S(14);
            if (!string.IsNullOrEmpty(current.Desc))
            {
                int descHeight = Math.Min(Theme.S(96), Theme.MeasureHeight(g, current.Desc, Theme.Ui(9f), width));
                Theme.DrawWrapped(g, current.Desc, Theme.Ui(9f), Theme.Secondary, new Rectangle(x, y, width, descHeight));
                y += descHeight + Theme.S(10);
            }

            List<Programme> upcoming = app.Library.Epg.Upcoming(channel, now, 6);
            if (upcoming.Count == 0 || y > Height - Theme.S(50)) return;
            Theme.DrawText(g, "UP NEXT", Theme.Ui(8f, FontStyle.Bold), Theme.Faint, new Rectangle(x, y, width, Theme.S(20)));
            y += Theme.S(22);
            foreach (Programme programme in upcoming)
            {
                if (y > Height - Theme.S(24)) break;
                Theme.DrawText(g, Theme.Time(programme.Start), Theme.Ui(9f), Theme.Secondary, new Rectangle(x, y, Theme.S(50), Theme.S(22)));
                Theme.DrawText(g, programme.Title, Theme.Ui(9f), Theme.Text, new Rectangle(x + Theme.S(54), y, width - Theme.S(54), Theme.S(22)));
                y += Theme.S(22);
            }
        }
    }
}
