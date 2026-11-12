using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace SuperiorIptv.Win;

/// <summary>Colours, fonts, icon glyphs and drawing helpers shared by every screen.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb(12, 15, 21);
    public static readonly Color Panel = Color.FromArgb(21, 26, 35);
    public static readonly Color Raised = Color.FromArgb(32, 39, 51);
    public static readonly Color Hover = Color.FromArgb(42, 51, 66);
    public static readonly Color Accent = Color.FromArgb(56, 140, 250);
    public static readonly Color AccentSoft = Color.FromArgb(28, 58, 104);
    public static readonly Color Live = Color.FromArgb(240, 69, 69);
    public static readonly Color Good = Color.FromArgb(64, 199, 120);
    public static readonly Color Warn = Color.FromArgb(240, 180, 60);
    public static readonly Color Text = Color.White;
    public static readonly Color Secondary = Color.FromArgb(160, 166, 178);
    public static readonly Color Faint = Color.FromArgb(100, 106, 118);

    /// <summary>Screen scaling (1.0 at 100 %, 1.5 at 150 %…). Set once at start-up.</summary>
    public static float Scale = 1f;

    public static int S(int pixels) => (int)Math.Round(pixels * Scale);

    private static readonly Dictionary<string, Font> Fonts = new();

    public static Font Ui(float size, FontStyle style = FontStyle.Regular)
    {
        string key = "u" + size + (int)style;
        if (!Fonts.TryGetValue(key, out Font font))
        {
            font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
            Fonts[key] = font;
        }
        return font;
    }

    /// <summary>Windows' built-in icon font (present on every Windows 10 and 11 PC).</summary>
    public static Font Icons(float size)
    {
        string key = "i" + size;
        if (!Fonts.TryGetValue(key, out Font font))
        {
            font = new Font("Segoe MDL2 Assets", size, FontStyle.Regular, GraphicsUnit.Point);
            Fonts[key] = font;
        }
        return font;
    }

    public static class Glyph
    {
        public const string Tv = "\uE7F4";
        public const string Guide = "\uE787";
        public const string Movie = "\uE8B2";
        public const string Series = "\uE8FD";
        public const string Record = "\uE7C8";
        public const string Download = "\uE896";
        public const string Multi = "\uE8A9";
        public const string Search = "\uE721";
        public const string Settings = "\uE713";
        public const string Play = "\uE768";
        public const string Pause = "\uE769";
        public const string Stop = "\uE71A";
        public const string Back = "\uE72B";
        public const string Up = "\uE70E";
        public const string Down = "\uE70D";
        public const string Left = "\uE76B";
        public const string Right = "\uE76C";
        public const string List = "\uE8FD";
        public const string Star = "\uE734";
        public const string StarFilled = "\uE735";
        public const string Rewind = "\uEB9E";
        public const string Forward = "\uEB9D";
        public const string Speed = "\uEC4A";
        public const string Audio = "\uE8D6";
        public const string Subtitles = "\uE7F0";
        public const string Aspect = "\uE799";
        public const string FullScreen = "\uE740";
        public const string Volume = "\uE767";
        public const string Mute = "\uE74F";
        public const string Refresh = "\uE72C";
        public const string Folder = "\uE8B7";
        public const string Check = "\uE73E";
        public const string Clock = "\uE823";
        public const string Close = "\uE711";
        public const string Add = "\uE710";
        public const string Warning = "\uE7BA";
        public const string Catchup = "\uE81C";
        public const string More = "\uE712";
        public const string Select = "\uE762";
    }

    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
    }

    public static GraphicsPath RoundPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        if (r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)));
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color color, Rectangle r, int radius)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using GraphicsPath path = RoundPath(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void StrokeRound(Graphics g, Color color, Rectangle r, int radius, float width = 1.5f)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using GraphicsPath path = RoundPath(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static void Fill(Graphics g, Color color, Rectangle r)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, r);
    }

    private static readonly StringFormat Near = MakeFormat(StringAlignment.Near, false);
    private static readonly StringFormat Center = MakeFormat(StringAlignment.Center, false);
    private static readonly StringFormat Far = MakeFormat(StringAlignment.Far, false);
    private static readonly StringFormat Wrap = MakeFormat(StringAlignment.Near, true);

    private static StringFormat MakeFormat(StringAlignment alignment, bool wrap)
    {
        var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = alignment,
            LineAlignment = wrap ? StringAlignment.Near : StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        format.FormatFlags = wrap ? StringFormatFlags.LineLimit : StringFormatFlags.NoWrap | StringFormatFlags.LineLimit;
        return format;
    }

    /// <summary>One line of text, vertically centred in <paramref name="r"/>, cut off with "…" when too long.</summary>
    public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        if (string.IsNullOrEmpty(text) || r.Width <= 2 || r.Height <= 2) return;
        using var brush = new SolidBrush(color);
        StringFormat format = align == HorizontalAlignment.Center ? Center : align == HorizontalAlignment.Right ? Far : Near;
        g.DrawString(text, font, brush, r, format);
    }

    /// <summary>Word-wrapped text that fills <paramref name="r"/> from the top.</summary>
    public static void DrawWrapped(Graphics g, string text, Font font, Color color, Rectangle r)
    {
        if (string.IsNullOrEmpty(text) || r.Width <= 2 || r.Height <= 2) return;
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, r, Wrap);
    }

    public static int MeasureWidth(Graphics g, string text, Font font)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(g.MeasureString(text, font, 10000, StringFormat.GenericTypographic).Width) + 2;
    }

    public static int MeasureHeight(Graphics g, string text, Font font, int width)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)Math.Ceiling(g.MeasureString(text, font, Math.Max(10, width), Wrap).Height) + 2;
    }

    public static void DrawGlyph(Graphics g, string glyph, float size, Color color, Rectangle r)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(glyph, Icons(size), brush, r, Center);
    }

    public static void DrawProgress(Graphics g, Rectangle r, double value, Color color)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        FillRound(g, Color.FromArgb(60, 255, 255, 255), r, r.Height / 2);
        int width = (int)(r.Width * Math.Clamp(value, 0, 1));
        if (width > 0) FillRound(g, color, new Rectangle(r.X, r.Y, Math.Max(width, r.Height), r.Height), r.Height / 2);
    }

    /// <summary>Draws an image scaled to fit (or fill) a rectangle, keeping its proportions.</summary>
    public static void DrawImage(Graphics g, Image image, Rectangle r, bool fill)
    {
        if (image == null || r.Width <= 0 || r.Height <= 0) return;
        float scale = fill
            ? Math.Max((float)r.Width / image.Width, (float)r.Height / image.Height)
            : Math.Min((float)r.Width / image.Width, (float)r.Height / image.Height);
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        var target = new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        if (fill)
        {
            GraphicsState state = g.Save();
            g.SetClip(r, CombineMode.Intersect);
            g.DrawImage(image, target);
            g.Restore(state);
        }
        else
        {
            g.DrawImage(image, target);
        }
    }

    public static string Bytes(long count)
    {
        if (count < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = count;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{count} B" : $"{value:0.#} {units[unit]}";
    }

    public static string Clock(long milliseconds)
    {
        long total = Math.Max(0, milliseconds / 1000);
        long hours = total / 3600, minutes = total % 3600 / 60, seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }

    public static string Time(DateTime utc) => utc.ToLocalTime().ToString("HH:mm");

    public static string Range(Programme programme) => Time(programme.Start) + " – " + Time(programme.Stop);

    /// <summary>Styles a standard text box, combo box or check box for the dark theme.</summary>
    public static T Style<T>(T control) where T : Control
    {
        control.Font = Ui(10f);
        control.ForeColor = Text;
        control.BackColor = control is TextBox || control is ComboBox || control is ListBox ? Raised : Background;
        if (control is TextBox box) box.BorderStyle = BorderStyle.FixedSingle;
        if (control is ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
        }
        if (control is CheckBox check) check.FlatStyle = FlatStyle.Standard;
        return control;
    }

    public static Label Caption(string text, bool strong = false)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = strong ? Text : Secondary,
            BackColor = Color.Transparent,
            Font = strong ? Ui(12f, FontStyle.Bold) : Ui(9.5f)
        };
    }
}
