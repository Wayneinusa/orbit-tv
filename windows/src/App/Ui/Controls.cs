using System.Collections.Concurrent;

namespace SuperiorIptv.Win;

/// <summary>State handed to a row or tile painter.</summary>
public struct ItemState
{
    public bool Selected;
    public bool Hover;
    public bool Checked;
}

/// <summary>
/// A list (or, with <see cref="TileWidth"/> set, a grid) that only paints what is visible, so it stays
/// fast with tens of thousands of channels. The owner supplies the painting through <see cref="DrawItem"/>.
/// </summary>
public class VirtualList : Control
{
    private int count;
    private int scroll;
    private int hover = -1;
    private int selected = -1;
    private bool draggingThumb;
    private int dragOffset;

    public int RowHeight = 56;
    /// <summary>Greater than zero turns the list into a grid of tiles at least this wide.</summary>
    public int TileWidth;
    public Color Back = Theme.Background;
    /// <summary>When on, clicking an item ticks or unticks it instead of activating it.</summary>
    public bool CheckMode;
    public readonly HashSet<int> Checked = new();
    public string EmptyText = "";

    public Action<Graphics, int, Rectangle, ItemState> DrawItem;
    /// <summary>Left click or Enter on an item.</summary>
    public event Action<int> Activated;
    /// <summary>Right click on an item (index, screen position).</summary>
    public event Action<int, Point> ContextRequested;
    public event Action<int> SelectionChanged;
    public event Action CheckedChanged;

    public VirtualList()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
    }

    public int Count
    {
        get => count;
        set
        {
            count = Math.Max(0, value);
            if (selected >= count) selected = -1;
            hover = -1;
            Checked.RemoveWhere(i => i >= count);
            ClampScroll();
            Invalidate();
        }
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            selected = value >= 0 && value < count ? value : -1;
            Invalidate();
        }
    }

    private int Columns => TileWidth > 0 ? Math.Max(1, (Width - Theme.S(10)) / TileWidth) : 1;
    private int CellWidth => (Width - Theme.S(10)) / Columns;
    private int Rows => (count + Columns - 1) / Columns;
    private int ContentHeight => Rows * RowHeight;
    private int MaxScroll => Math.Max(0, ContentHeight - Height);

    private void ClampScroll() => scroll = Math.Clamp(scroll, 0, MaxScroll);

    public void ScrollToTop()
    {
        scroll = 0;
        Invalidate();
    }

    public void EnsureVisible(int index, bool center = false)
    {
        if (index < 0 || index >= count) return;
        int top = index / Columns * RowHeight;
        if (center) scroll = top - (Height - RowHeight) / 2;
        else if (top < scroll) scroll = top;
        else if (top + RowHeight > scroll + Height) scroll = top + RowHeight - Height;
        ClampScroll();
        Invalidate();
    }

    private Rectangle ItemBounds(int index)
    {
        int columns = Columns;
        return new Rectangle(index % columns * CellWidth, index / columns * RowHeight - scroll, CellWidth, RowHeight);
    }

    private int IndexAt(Point p)
    {
        if (p.X >= Columns * CellWidth || p.X < 0) return -1;
        int row = (p.Y + scroll) / RowHeight;
        int index = row * Columns + p.X / Math.Max(1, CellWidth);
        return index >= 0 && index < count ? index : -1;
    }

    private Rectangle ThumbBounds()
    {
        if (MaxScroll <= 0) return Rectangle.Empty;
        int track = Height;
        int thumb = Math.Max(Theme.S(30), (int)((long)track * Height / ContentHeight));
        int y = (int)((long)(track - thumb) * scroll / MaxScroll);
        return new Rectangle(Width - Theme.S(8), y, Theme.S(6), thumb);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(Back);
        if (count == 0)
        {
            if (!string.IsNullOrEmpty(EmptyText))
            {
                Theme.DrawText(g, EmptyText, Theme.Ui(10.5f), Theme.Faint, new Rectangle(0, 0, Width, Math.Min(Height, Theme.S(120))), HorizontalAlignment.Center);
            }
            return;
        }
        int columns = Columns;
        int first = Math.Max(0, scroll / RowHeight * columns);
        int last = Math.Min(count - 1, ((scroll + Height) / RowHeight + 1) * columns - 1);
        for (int i = first; i <= last; i++)
        {
            Rectangle bounds = ItemBounds(i);
            if (bounds.Bottom < e.ClipRectangle.Top || bounds.Top > e.ClipRectangle.Bottom) continue;
            DrawItem?.Invoke(g, i, bounds, new ItemState { Selected = i == selected, Hover = i == hover, Checked = Checked.Contains(i) });
        }
        Rectangle thumb = ThumbBounds();
        if (!thumb.IsEmpty) Theme.FillRound(g, draggingThumb ? Theme.Secondary : Theme.Faint, thumb, thumb.Width / 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll -= e.Delta * RowHeight / 120 * (TileWidth > 0 ? 1 : 2);
        ClampScroll();
        hover = IndexAt(e.Location);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (draggingThumb)
        {
            Rectangle thumb = ThumbBounds();
            int range = Height - thumb.Height;
            if (range > 0) scroll = (int)((long)(e.Y - dragOffset) * MaxScroll / range);
            ClampScroll();
            Invalidate();
            return;
        }
        int index = IndexAt(e.Location);
        if (index != hover)
        {
            hover = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != -1)
        {
            hover = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (CanFocus) Focus();
        Rectangle thumb = ThumbBounds();
        if (e.Button == MouseButtons.Left && !thumb.IsEmpty && e.X >= Width - Theme.S(12))
        {
            draggingThumb = true;
            dragOffset = thumb.Contains(e.Location) ? e.Y - thumb.Y : thumb.Height / 2;
            Capture = true;
            OnMouseMove(e);
            return;
        }
        int index = IndexAt(e.Location);
        if (index < 0) return;
        if (e.Button == MouseButtons.Left)
        {
            if (CheckMode)
            {
                if (!Checked.Remove(index)) Checked.Add(index);
                Invalidate();
                CheckedChanged?.Invoke();
                return;
            }
            Select(index);
            Activated?.Invoke(index);
        }
        else if (e.Button == MouseButtons.Right)
        {
            Select(index);
            ContextRequested?.Invoke(index, PointToScreen(e.Location));
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (draggingThumb)
        {
            draggingThumb = false;
            Capture = false;
            Invalidate();
        }
    }

    private void Select(int index)
    {
        if (index == selected) return;
        selected = index;
        Invalidate();
        SelectionChanged?.Invoke(index);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (count == 0) return;
        int columns = Columns;
        int page = Math.Max(1, Height / RowHeight) * columns;
        int target = selected;
        switch (e.KeyCode)
        {
            case Keys.Down: target = selected < 0 ? 0 : selected + columns; break;
            case Keys.Up: target = selected < 0 ? 0 : selected - columns; break;
            case Keys.Right when TileWidth > 0: target = selected + 1; break;
            case Keys.Left when TileWidth > 0: target = selected - 1; break;
            case Keys.PageDown: target = selected + page; break;
            case Keys.PageUp: target = selected - page; break;
            case Keys.Home: target = 0; break;
            case Keys.End: target = count - 1; break;
            case Keys.Enter:
                if (selected >= 0)
                {
                    if (CheckMode)
                    {
                        if (!Checked.Remove(selected)) Checked.Add(selected);
                        Invalidate();
                        CheckedChanged?.Invoke();
                    }
                    else
                    {
                        Activated?.Invoke(selected);
                    }
                }
                e.Handled = true;
                return;
            default:
                return;
        }
        target = Math.Clamp(target, 0, count - 1);
        Select(target);
        EnsureVisible(target);
        e.Handled = true;
    }
}

/// <summary>A flat, custom-painted button with an optional icon.</summary>
public class FlatButton : Control
{
    private bool hover;
    private bool pressed;
    private bool isSelected;
    private string glyph = "";
    private bool shown = true;

    public Color Fill = Theme.Raised;
    public Color GlyphColor = Theme.Text;
    /// <summary>Icon above a small caption (player bar and navigation rail) instead of icon beside text.</summary>
    public bool Stacked;
    public float GlyphSize = 13f;
    public float TextSize = 10f;

    public FlatButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Theme.Background;
        Cursor = Cursors.Hand;
        Height = Theme.S(36);
    }

    public FlatButton(string text, string glyph = "", EventHandler onClick = null) : this()
    {
        Text = text;
        this.glyph = glyph ?? "";
        if (onClick != null) Click += onClick;
    }

    public string Glyph
    {
        get => glyph;
        set
        {
            if (glyph == (value ?? "")) return;
            glyph = value ?? "";
            Invalidate();
        }
    }

    public bool Selected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Whether this button is meant to be shown. Use this instead of reading Visible, which also
    /// reports false whenever a parent control happens to be hidden.
    /// </summary>
    public bool Shown
    {
        get => shown;
        set
        {
            shown = value;
            Visible = value;
        }
    }

    public string Caption
    {
        get => Text;
        set
        {
            if (Text == value) return;
            Text = value;
            Invalidate();
        }
    }

    /// <summary>Width needed to show the icon and text on one line.</summary>
    public int PreferredWidth()
    {
        using Graphics g = CreateGraphics();
        int width = Theme.S(24);
        if (glyph.Length > 0) width += Theme.S(22);
        width += Theme.MeasureWidth(g, Text, Theme.Ui(TextSize));
        return width;
    }

    public FlatButton AutoWidth()
    {
        Width = PreferredWidth();
        return this;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = false;
        pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(BackColor);
        Color fill = isSelected ? Theme.Accent : pressed ? Theme.Accent : hover ? Theme.Hover : Fill;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        Theme.FillRound(g, fill, bounds, Theme.S(8));
        Color textColor = Enabled ? Theme.Text : Theme.Faint;
        Color iconColor = Enabled ? (isSelected || pressed ? Theme.Text : GlyphColor) : Theme.Faint;

        if (Stacked)
        {
            int iconHeight = string.IsNullOrEmpty(Text) ? Height : Height * 58 / 100;
            if (glyph.Length > 0) Theme.DrawGlyph(g, glyph, GlyphSize, iconColor, new Rectangle(0, Theme.S(2), Width, iconHeight));
            if (!string.IsNullOrEmpty(Text))
            {
                Theme.DrawText(g, Text, Theme.Ui(TextSize), textColor, new Rectangle(Theme.S(2), iconHeight, Width - Theme.S(4), Height - iconHeight - Theme.S(3)), HorizontalAlignment.Center);
            }
            return;
        }

        int x = Theme.S(10);
        if (glyph.Length > 0)
        {
            bool iconOnly = string.IsNullOrEmpty(Text);
            var iconBounds = iconOnly ? new Rectangle(0, 0, Width, Height) : new Rectangle(x - Theme.S(2), 0, Theme.S(24), Height);
            Theme.DrawGlyph(g, glyph, GlyphSize, iconColor, iconBounds);
            x += Theme.S(24);
        }
        if (!string.IsNullOrEmpty(Text))
        {
            Theme.DrawText(g, Text, Theme.Ui(TextSize), textColor, new Rectangle(x, 0, Width - x - Theme.S(8), Height),
                glyph.Length > 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center);
        }
    }
}

/// <summary>Playback position bar: click or drag to seek.</summary>
public class SeekBar : Control
{
    private bool dragging;
    private double dragValue;
    public double Value;
    public event Action<double> Seek;

    public SeekBar()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Height = Theme.S(22);
        Cursor = Cursors.Hand;
    }

    public bool IsDragging => dragging;

    private double FractionAt(int x)
    {
        int margin = Theme.S(8);
        return Math.Clamp((double)(x - margin) / Math.Max(1, Width - 2 * margin), 0, 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left) return;
        dragging = true;
        dragValue = FractionAt(e.X);
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        dragValue = FractionAt(e.X);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!dragging) return;
        dragging = false;
        Capture = false;
        Value = dragValue;
        Invalidate();
        Seek?.Invoke(dragValue);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(BackColor);
        int margin = Theme.S(8);
        int barHeight = Theme.S(5);
        var track = new Rectangle(margin, (Height - barHeight) / 2, Width - 2 * margin, barHeight);
        double value = dragging ? dragValue : Value;
        Theme.DrawProgress(g, track, value, Enabled ? Theme.Accent : Theme.Faint);
        if (Enabled)
        {
            int knob = Theme.S(13);
            int x = track.X + (int)(track.Width * Math.Clamp(value, 0, 1)) - knob / 2;
            using var brush = new SolidBrush(Color.White);
            g.FillEllipse(brush, x, (Height - knob) / 2, knob, knob);
        }
    }
}

/// <summary>Loads channel logos and posters in the background and keeps small copies in memory.</summary>
public static class ImageCache
{
    private const int Limit = 500;
    private static readonly Dictionary<string, Image> Cache = new();
    private static readonly LinkedList<string> Order = new();
    private static readonly HashSet<string> Loading = new();
    private static readonly ConcurrentDictionary<string, byte> Failed = new();
    private static readonly SemaphoreSlim Slots = new(6);
    private static readonly object Gate = new();

    /// <summary>
    /// Returns the image if it is already loaded. Otherwise starts loading it and repaints
    /// <paramref name="owner"/> when it arrives.
    /// </summary>
    public static Image Get(string url, int maxPixel, Control owner)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        string key = maxPixel + "|" + url;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out Image cached)) return cached;
            if (Failed.ContainsKey(key) || !Loading.Add(key)) return null;
        }
        _ = LoadAsync(url, key, maxPixel, owner);
        return null;
    }

    private static async Task LoadAsync(string url, string key, int maxPixel, Control owner)
    {
        Image result = null;
        await Slots.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] data;
            if (Net.IsLocalPath(url))
            {
                data = await File.ReadAllBytesAsync(Net.LocalPath(url)).ConfigureAwait(false);
            }
            else
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using HttpRequestMessage request = Net.Get(url, "");
                using HttpResponseMessage response = await Net.Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new IOException("image not available");
                data = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            if (data.Length > 12_000_000) throw new IOException("image too large");
            using var stream = new MemoryStream(data);
            using Image full = Image.FromStream(stream, false, true);
            float scale = Math.Min(1f, (float)maxPixel / Math.Max(full.Width, full.Height));
            int w = Math.Max(1, (int)(full.Width * scale)), h = Math.Max(1, (int)(full.Height * scale));
            var small = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new Rectangle(0, 0, w, h));
            }
            result = small;
        }
        catch (Exception)
        {
            Failed[key] = 1;
        }
        finally
        {
            Slots.Release();
        }

        lock (Gate)
        {
            Loading.Remove(key);
            if (result != null)
            {
                Cache[key] = result;
                Order.AddLast(key);
                // Forget the oldest pictures; they are not disposed here because a paint may still be using one.
                while (Order.Count > Limit)
                {
                    Cache.Remove(Order.First.Value);
                    Order.RemoveFirst();
                }
            }
        }
        if (result != null && owner != null && owner.IsHandleCreated && !owner.IsDisposed)
        {
            try
            {
                owner.BeginInvoke(new Action(owner.Invalidate));
            }
            catch (Exception)
            {
                // The window closed in the meantime.
            }
        }
    }
}
