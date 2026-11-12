namespace SuperiorIptv.Win;

/// <summary>TV guide: one row per channel, time running left to right.</summary>
public sealed class GuidePage : PageBase
{
    private readonly GuideGrid grid;
    private readonly FlatButton group, earlier, nowButton, later, refresh;
    private int ticks;

    public GuidePage(AppServices app) : base(app)
    {
        grid = new GuideGrid(app);
        group = new FlatButton(App.SelectedGroup, Theme.Glyph.List, (_, _) => PickGroup());
        earlier = new FlatButton("", Theme.Glyph.Left, (_, _) => grid.Page(-1));
        nowButton = new FlatButton("Now", "", (_, _) => grid.GoToNow());
        later = new FlatButton("", Theme.Glyph.Right, (_, _) => grid.Page(1));
        refresh = new FlatButton("Update guide", Theme.Glyph.Refresh, async (_, _) =>
        {
            refresh.Enabled = false;
            await App.Library.RefreshEpgAsync();
            if (!IsDisposed) refresh.Enabled = true;
        });
        Controls.AddRange(new Control[] { group, earlier, nowButton, later, refresh, grid });
    }

    private void PickGroup()
    {
        var entries = App.Library.VisibleGroups().Select(name => (name + "  (" + App.Library.CountIn(name) + ")", (Action)(() =>
        {
            App.SelectedGroup = name;
            Reload();
        }))).ToArray();
        Painters.ShowMenu(this, group.PointToScreen(new Point(0, group.Height)), entries);
    }

    private void Reload()
    {
        group.Caption = App.SelectedGroup;
        grid.SetChannels(App.Library.ChannelsIn(App.SelectedGroup));
        Arrange();
        Invalidate();
    }

    protected override void Arrange()
    {
        int x = Theme.S(14);
        group.SetBounds(x, Theme.S(11), Math.Min(Theme.S(300), group.PreferredWidth()), Theme.S(36));
        earlier.Width = later.Width = Theme.S(40);
        PlaceHeaderButtons(earlier, nowButton, later, refresh);
        grid.SetBounds(0, HeaderHeight, Width, Height - HeaderHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(Theme.Background);
        int x = group.Right + Theme.S(16);
        int width = Math.Max(10, earlier.Left - x - Theme.S(10));
        Theme.DrawText(g, grid.WindowStart.ToLocalTime().ToString("dddd d MMMM"), Theme.Ui(11f, FontStyle.Bold), Theme.Text, new Rectangle(x, Theme.S(8), width, Theme.S(24)));
        Theme.DrawText(g, App.Library.EpgStatus, Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x, Theme.S(30), width, Theme.S(20)));
    }

    public override void OnShown()
    {
        Reload();
        grid.GoToNow();
    }

    public override void OnLibraryChanged()
    {
        Reload();
    }

    public override void OnTick()
    {
        ticks++;
        if (ticks % 20 == 0) grid.Invalidate();
        if (ticks % 4 == 0) Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
    }
}

/// <summary>The painted guide grid.</summary>
public sealed class GuideGrid : Control
{
    private readonly AppServices app;
    private List<Channel> channels = new();
    private int scroll;
    private Point hover = new(-1, -1);

    public DateTime WindowStart { get; private set; } = Slot(DateTime.UtcNow);

    public GuideGrid(AppServices app)
    {
        this.app = app;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Background;
    }

    private static DateTime Slot(DateTime utc) => new DateTime(utc.Ticks - utc.Ticks % TimeSpan.FromMinutes(30).Ticks, DateTimeKind.Utc);

    private int RowHeight => Theme.S(60);
    private int RulerHeight => Theme.S(30);
    private int ChannelWidth => Math.Min(Theme.S(250), Math.Max(Theme.S(150), Width / 5));
    private int TimelineWidth => Math.Max(50, Width - ChannelWidth - Theme.S(10));

    /// <summary>Visible time span: roughly one hour per 330 pixels, in half-hour steps.</summary>
    private double Hours => Math.Clamp(Math.Round(TimelineWidth / (double)Theme.S(330) * 2) / 2, 1.5, 5);
    private DateTime WindowEnd => WindowStart.AddHours(Hours);
    private int MaxScroll => Math.Max(0, channels.Count * RowHeight - (Height - RulerHeight));

    public void SetChannels(List<Channel> list)
    {
        channels = list;
        scroll = Math.Clamp(scroll, 0, MaxScroll);
        Invalidate();
    }

    public void GoToNow()
    {
        WindowStart = Slot(DateTime.UtcNow);
        Parent?.Invalidate();
        Invalidate();
    }

    public void Page(int direction)
    {
        WindowStart = WindowStart.AddHours(direction * Math.Max(1, Hours - 0.5));
        Parent?.Invalidate();
        Invalidate();
    }

    private int XFor(DateTime utc)
    {
        double fraction = (utc - WindowStart).TotalSeconds / (Hours * 3600);
        return ChannelWidth + (int)(fraction * TimelineWidth);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(Theme.Background);
        DateTime now = DateTime.UtcNow;
        DateTime end = WindowEnd;

        if (channels.Count == 0)
        {
            Theme.DrawText(g, "No channels in this group", Theme.Ui(10.5f), Theme.Faint, new Rectangle(0, 0, Width, Theme.S(120)), HorizontalAlignment.Center);
            return;
        }

        // Rows
        int first = Math.Max(0, scroll / RowHeight);
        int last = Math.Min(channels.Count - 1, (scroll + Height) / RowHeight);
        Region clip = g.Clip;
        g.SetClip(new Rectangle(0, RulerHeight, Width, Height - RulerHeight));
        for (int i = first; i <= last; i++)
        {
            Channel channel = channels[i];
            int y = RulerHeight + i * RowHeight - scroll;
            var cell = new Rectangle(Theme.S(6), y + Theme.S(2), ChannelWidth - Theme.S(10), RowHeight - Theme.S(4));
            bool channelHover = hover.Y >= y && hover.Y < y + RowHeight && hover.X < ChannelWidth;
            Theme.FillRound(g, channelHover ? Theme.Hover : Theme.Panel, cell, Theme.S(8));
            Theme.DrawText(g, channel.Number.ToString(), Theme.Ui(8f), Theme.Secondary, new Rectangle(cell.X + Theme.S(2), cell.Y, Theme.S(32), cell.Height), HorizontalAlignment.Right);
            var logo = new Rectangle(cell.X + Theme.S(40), cell.Y + Theme.S(8), Theme.S(48), cell.Height - Theme.S(16));
            Painters.Logo(g, this, channel.Logo, channel.Name, logo);
            Theme.DrawText(g, channel.Name, Theme.Ui(9f, FontStyle.Bold), Theme.Text, new Rectangle(logo.Right + Theme.S(8), cell.Y, cell.Right - logo.Right - Theme.S(12), cell.Height));

            g.SetClip(new Rectangle(ChannelWidth, Math.Max(y, RulerHeight), TimelineWidth, RowHeight), System.Drawing.Drawing2D.CombineMode.Replace);
            List<Programme> items = app.Library.Epg.Between(channel, WindowStart, end);
            if (items.Count == 0)
            {
                var empty = new Rectangle(ChannelWidth, y + Theme.S(2), TimelineWidth, RowHeight - Theme.S(4));
                Theme.FillRound(g, Color.FromArgb(120, Theme.Panel), empty, Theme.S(8));
                Theme.DrawText(g, "No programme information", Theme.Ui(9f), Theme.Faint, Rectangle.Inflate(empty, -Theme.S(10), 0));
            }
            foreach (Programme programme in items)
            {
                int x1 = Math.Max(XFor(programme.Start), ChannelWidth);
                int x2 = Math.Min(XFor(programme.Stop), ChannelWidth + TimelineWidth);
                var box = new Rectangle(x1 + 1, y + Theme.S(2), x2 - x1 - Theme.S(3), RowHeight - Theme.S(4));
                if (box.Width < Theme.S(5)) continue;
                bool isLive = programme.IsLive(now);
                bool isPast = programme.Stop <= now;
                bool hovered = hover.Y >= y && hover.Y < y + RowHeight && hover.X >= box.X && hover.X < box.Right;
                Color fill = hovered ? Theme.Hover : isLive ? Theme.Raised : isPast ? Color.FromArgb(150, Theme.Panel) : Theme.Panel;
                Theme.FillRound(g, fill, box, Theme.S(8));
                if (isLive) Theme.StrokeRound(g, Theme.Accent, box, Theme.S(8), 1f);
                if (box.Width < Theme.S(34)) continue;

                bool hasCatchup = isPast && Catchup.IsAvailable(channel, programme, now);
                bool booked = app.Recordings.IsScheduled(channel.Id, programme.Start);
                Color textColor = isPast && !hasCatchup ? Theme.Faint : Theme.Text;
                int tx = box.X + Theme.S(8);
                if (booked)
                {
                    using var dot = new SolidBrush(Theme.Live);
                    g.FillEllipse(dot, tx, box.Y + Theme.S(10), Theme.S(9), Theme.S(9));
                    tx += Theme.S(14);
                }
                if (hasCatchup)
                {
                    Theme.DrawGlyph(g, Theme.Glyph.Catchup, 8f, Theme.Secondary, new Rectangle(tx - Theme.S(2), box.Y + Theme.S(4), Theme.S(16), Theme.S(20)));
                    tx += Theme.S(18);
                }
                int textWidth = box.Right - tx - Theme.S(6);
                Theme.DrawText(g, programme.Title, Theme.Ui(9f, FontStyle.Bold), textColor, new Rectangle(tx, box.Y + Theme.S(4), textWidth, Theme.S(22)));
                Theme.DrawText(g, Theme.Time(programme.Start), Theme.Ui(8f), Theme.Secondary, new Rectangle(box.X + Theme.S(8), box.Y + Theme.S(26), box.Width - Theme.S(14), Theme.S(18)));
                if (isLive)
                {
                    Theme.DrawProgress(g, new Rectangle(box.X + Theme.S(8), box.Bottom - Theme.S(8), box.Width - Theme.S(16), Theme.S(3)), programme.Progress(now), Theme.Accent);
                }
            }
            g.SetClip(new Rectangle(0, RulerHeight, Width, Height - RulerHeight), System.Drawing.Drawing2D.CombineMode.Replace);
        }
        g.Clip = clip;

        // Ruler with half-hour marks
        Theme.Fill(g, Theme.Background, new Rectangle(0, 0, Width, RulerHeight));
        for (DateTime mark = WindowStart; mark < end; mark = mark.AddMinutes(30))
        {
            int x = XFor(mark);
            Theme.DrawText(g, Theme.Time(mark), Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x + Theme.S(4), 0, Theme.S(80), RulerHeight));
            using var pen = new Pen(Color.FromArgb(40, 255, 255, 255));
            g.DrawLine(pen, x, RulerHeight - Theme.S(8), x, RulerHeight);
        }

        // "Now" line
        if (now >= WindowStart && now < end)
        {
            int x = XFor(now);
            using var pen = new Pen(Theme.Live, 2f);
            g.DrawLine(pen, x, RulerHeight - Theme.S(4), x, Height);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Shift) != 0)
        {
            Page(e.Delta > 0 ? -1 : 1);
            return;
        }
        scroll = Math.Clamp(scroll - e.Delta * RowHeight / 120 * 2, 0, MaxScroll);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        hover = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (CanFocus) Focus();
        if (e.Y < RulerHeight || channels.Count == 0) return;
        int index = (e.Y - RulerHeight + scroll) / RowHeight;
        if (index < 0 || index >= channels.Count) return;
        Channel channel = channels[index];
        if (e.X < ChannelWidth)
        {
            if (e.Button == MouseButtons.Right) LivePage.ChannelMenu(this, app, channel, PointToScreen(e.Location));
            else app.Watch(channel);
            return;
        }
        double fraction = (e.X - ChannelWidth) / (double)TimelineWidth;
        DateTime time = WindowStart.AddSeconds(fraction * Hours * 3600);
        Programme programme = app.Library.Epg.Between(channel, WindowStart, WindowEnd).FirstOrDefault(p => p.Start <= time && time < p.Stop);
        if (programme != null) Sheets.Programme(this, app, channel, programme);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: Page(-1); break;
            case Keys.Right: Page(1); break;
            case Keys.Down: scroll = Math.Clamp(scroll + RowHeight, 0, MaxScroll); Invalidate(); break;
            case Keys.Up: scroll = Math.Clamp(scroll - RowHeight, 0, MaxScroll); Invalidate(); break;
            case Keys.PageDown: scroll = Math.Clamp(scroll + Height - RulerHeight, 0, MaxScroll); Invalidate(); break;
            case Keys.PageUp: scroll = Math.Clamp(scroll - Height + RulerHeight, 0, MaxScroll); Invalidate(); break;
        }
    }
}
