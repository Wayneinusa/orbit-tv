namespace SuperiorIptv.Win;

/// <summary>Row painters used by more than one screen.</summary>
public static class Painters
{
    /// <summary>Background of a list row: accent when selected or playing, lighter on hover.</summary>
    public static Rectangle RowBackground(Graphics g, Rectangle bounds, bool selected, bool hover, Color? idle = null)
    {
        var r = new Rectangle(bounds.X + Theme.S(4), bounds.Y + Theme.S(2), bounds.Width - Theme.S(14), bounds.Height - Theme.S(4));
        Color fill = selected ? Theme.AccentSoft : hover ? Theme.Hover : idle ?? Color.Empty;
        if (!fill.IsEmpty) Theme.FillRound(g, fill, r, Theme.S(8));
        if (selected) Theme.StrokeRound(g, Theme.Accent, r, Theme.S(8));
        return r;
    }

    public static void Logo(Graphics g, Control owner, string url, string fallbackName, Rectangle r)
    {
        Theme.FillRound(g, Color.FromArgb(18, 255, 255, 255), r, Theme.S(6));
        Image image = ImageCache.Get(url, 160, owner);
        if (image != null)
        {
            Theme.DrawImage(g, image, Rectangle.Inflate(r, -Theme.S(3), -Theme.S(3)), false);
        }
        else
        {
            string initials = fallbackName.Length >= 2 ? fallbackName[..2].ToUpperInvariant() : fallbackName.ToUpperInvariant();
            Theme.DrawText(g, initials, Theme.Ui(9f, FontStyle.Bold), Theme.Secondary, r, HorizontalAlignment.Center);
        }
    }

    public static void ChannelRow(Graphics g, Control owner, Rectangle bounds, ItemState state, Channel channel, AppServices app, bool playing, DateTime utcNow)
    {
        Rectangle r = RowBackground(g, bounds, state.Selected || playing, state.Hover);
        int x = r.X + Theme.S(6);
        Theme.DrawText(g, channel.Number.ToString(), Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x, r.Y, Theme.S(40), r.Height), HorizontalAlignment.Right);
        x += Theme.S(48);
        var logo = new Rectangle(x, r.Y + Theme.S(6), Theme.S(56), r.Height - Theme.S(12));
        Logo(g, owner, channel.Logo, channel.Name, logo);
        x = logo.Right + Theme.S(10);

        int right = r.Right - Theme.S(8);
        if (playing)
        {
            Theme.DrawGlyph(g, Theme.Glyph.Play, 9f, Theme.Text, new Rectangle(right - Theme.S(18), r.Y, Theme.S(18), r.Height));
            right -= Theme.S(22);
        }
        if (app.Recordings.ActiveFor(channel.Id) != null)
        {
            using var brush = new SolidBrush(Theme.Live);
            g.FillEllipse(brush, right - Theme.S(12), r.Y + r.Height / 2 - Theme.S(5), Theme.S(10), Theme.S(10));
            right -= Theme.S(18);
        }
        if (app.Library.IsFavorite(channel))
        {
            Theme.DrawGlyph(g, Theme.Glyph.StarFilled, 8.5f, Theme.Warn, new Rectangle(right - Theme.S(18), r.Y, Theme.S(18), r.Height));
            right -= Theme.S(20);
        }
        if (channel.Catchup != null)
        {
            Theme.DrawGlyph(g, Theme.Glyph.Catchup, 8.5f, Theme.Secondary, new Rectangle(right - Theme.S(18), r.Y, Theme.S(18), r.Height));
            right -= Theme.S(20);
        }

        int width = Math.Max(10, right - x);
        Programme programme = app.Library.Epg.Current(channel, utcNow);
        if (programme == null)
        {
            Theme.DrawText(g, channel.Name, Theme.Ui(10f, FontStyle.Bold), Theme.Text, new Rectangle(x, r.Y, width, r.Height));
            return;
        }
        int third = r.Height / 3;
        Theme.DrawText(g, channel.Name, Theme.Ui(10f, FontStyle.Bold), Theme.Text, new Rectangle(x, r.Y + Theme.S(3), width, third + Theme.S(4)));
        Theme.DrawText(g, programme.Title, Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x, r.Y + third + Theme.S(3), width, third));
        Theme.DrawProgress(g, new Rectangle(x, r.Bottom - Theme.S(9), width, Theme.S(3)), programme.Progress(utcNow), Theme.Accent);
    }

    /// <summary>A poster tile with a caption (movies and series).</summary>
    public static void PosterTile(Graphics g, Control owner, Rectangle bounds, ItemState state, string name, string poster, bool checkMode, string badge)
    {
        var r = Rectangle.Inflate(bounds, -Theme.S(6), -Theme.S(6));
        Color fill = state.Checked ? Theme.AccentSoft : state.Selected ? Theme.AccentSoft : state.Hover ? Theme.Hover : Color.Empty;
        if (!fill.IsEmpty) Theme.FillRound(g, fill, r, Theme.S(10));

        int captionHeight = Theme.S(40);
        var art = new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(6), r.Width - Theme.S(12), r.Height - captionHeight - Theme.S(10));
        Theme.FillRound(g, Color.FromArgb(18, 255, 255, 255), art, Theme.S(8));
        Image image = ImageCache.Get(poster, 360, owner);
        if (image != null)
        {
            using System.Drawing.Drawing2D.GraphicsPath clip = Theme.RoundPath(art, Theme.S(8));
            System.Drawing.Drawing2D.GraphicsState saved = g.Save();
            g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
            Theme.DrawImage(g, image, art, true);
            g.Restore(saved);
        }
        else
        {
            Theme.DrawGlyph(g, Theme.Glyph.Movie, 22f, Theme.Faint, art);
        }
        Theme.DrawWrapped(g, name, Theme.Ui(8.5f), Theme.Text, new Rectangle(art.X, art.Bottom + Theme.S(4), art.Width, captionHeight));

        if (!string.IsNullOrEmpty(badge))
        {
            int badgeWidth = Theme.MeasureWidth(g, badge, Theme.Ui(7.5f, FontStyle.Bold)) + Theme.S(12);
            var tag = new Rectangle(art.X + Theme.S(6), art.Bottom - Theme.S(26), badgeWidth, Theme.S(20));
            Theme.FillRound(g, Color.FromArgb(220, 20, 24, 32), tag, Theme.S(6));
            Theme.DrawText(g, badge, Theme.Ui(7.5f, FontStyle.Bold), Theme.Good, tag, HorizontalAlignment.Center);
        }
        if (checkMode)
        {
            var box = new Rectangle(art.Right - Theme.S(30), art.Y + Theme.S(6), Theme.S(24), Theme.S(24));
            Theme.FillRound(g, state.Checked ? Theme.Accent : Color.FromArgb(200, 20, 24, 32), box, Theme.S(6));
            Theme.StrokeRound(g, Color.White, box, Theme.S(6));
            if (state.Checked) Theme.DrawGlyph(g, Theme.Glyph.Check, 9f, Color.White, box);
        }
        if (state.Selected) Theme.StrokeRound(g, Theme.Accent, r, Theme.S(10));
    }

    /// <summary>A plain two-line row with an icon (search results, episodes, settings lists).</summary>
    public static void TwoLineRow(Graphics g, Rectangle bounds, ItemState state, string glyph, Color glyphColor, string title, string detail, bool checkMode = false)
    {
        Rectangle r = RowBackground(g, bounds, state.Selected || state.Checked, state.Hover, Theme.Panel);
        int x = r.X + Theme.S(8);
        if (checkMode)
        {
            var box = new Rectangle(x, r.Y + r.Height / 2 - Theme.S(11), Theme.S(22), Theme.S(22));
            Theme.FillRound(g, state.Checked ? Theme.Accent : Theme.Raised, box, Theme.S(5));
            Theme.StrokeRound(g, Theme.Secondary, box, Theme.S(5), 1f);
            if (state.Checked) Theme.DrawGlyph(g, Theme.Glyph.Check, 8.5f, Color.White, box);
            x += Theme.S(32);
        }
        if (!string.IsNullOrEmpty(glyph))
        {
            Theme.DrawGlyph(g, glyph, 12f, glyphColor, new Rectangle(x, r.Y, Theme.S(30), r.Height));
            x += Theme.S(38);
        }
        int width = Math.Max(10, r.Right - x - Theme.S(8));
        if (string.IsNullOrEmpty(detail))
        {
            Theme.DrawText(g, title, Theme.Ui(10f, FontStyle.Bold), Theme.Text, new Rectangle(x, r.Y, width, r.Height));
        }
        else
        {
            Theme.DrawText(g, title, Theme.Ui(10f, FontStyle.Bold), Theme.Text, new Rectangle(x, r.Y + Theme.S(4), width, r.Height / 2));
            Theme.DrawText(g, detail, Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x, r.Y + r.Height / 2 - Theme.S(2), width, r.Height / 2 - Theme.S(2)));
        }
    }

    public static void SectionRow(Graphics g, Rectangle bounds, string title)
    {
        Theme.DrawText(g, title.ToUpperInvariant(), Theme.Ui(8.5f, FontStyle.Bold), Theme.Faint,
            new Rectangle(bounds.X + Theme.S(14), bounds.Y + Theme.S(8), bounds.Width - Theme.S(28), bounds.Height - Theme.S(8)));
    }

    /// <summary>Shows a dark context menu at a screen position.</summary>
    public static void ShowMenu(Control owner, Point screen, params (string Text, Action Run)[] entries)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Raised,
            ForeColor = Theme.Text,
            ShowImageMargin = false,
            Font = Theme.Ui(10f),
            Renderer = new ToolStripProfessionalRenderer(new DarkColors())
        };
        foreach (var (text, run) in entries)
        {
            if (text == "-")
            {
                menu.Items.Add(new ToolStripSeparator());
                continue;
            }
            var entry = new ToolStripMenuItem(text) { ForeColor = Theme.Text };
            if (run == null) entry.Enabled = false;
            else entry.Click += (_, _) => run();
            menu.Items.Add(entry);
        }
        menu.Closed += (_, _) => owner.BeginInvoke(new Action(menu.Dispose));
        menu.Show(screen);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.Accent;
        public override Color MenuItemBorder => Theme.Accent;
        public override Color MenuBorder => Theme.Hover;
        public override Color ToolStripDropDownBackground => Theme.Raised;
        public override Color ImageMarginGradientBegin => Theme.Raised;
        public override Color ImageMarginGradientMiddle => Theme.Raised;
        public override Color ImageMarginGradientEnd => Theme.Raised;
        public override Color SeparatorDark => Theme.Hover;
        public override Color SeparatorLight => Theme.Hover;
    }
}
