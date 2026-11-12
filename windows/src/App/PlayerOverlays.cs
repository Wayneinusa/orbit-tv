using System.Globalization;

namespace SuperiorIptv.Win;

/// <summary>The control bar shown at the bottom of the player.</summary>
public sealed class PlayerBar : Control
{
    private readonly MainForm form;
    private readonly SeekBar seek = new();
    private readonly FlatButton back, channels, previous, next, favorite, record, rewind, playPause, forward, forwardMinute, speed, download, audio, subtitles, aspect, mute, fullScreen;
    private readonly FlatButton[] leftGroup, rightGroup;
    private bool showSeek;

    private AppServices App => form.Services;
    private PlayerEngine Player => form.Services.Player;

    public PlayerBar(MainForm form)
    {
        this.form = form;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        seek.BackColor = Theme.Panel;
        seek.Seek += fraction => form.BarAction(() => Player.SeekTo(fraction));

        back = Make("Back", Theme.Glyph.Back, () => form.ClosePlayer());
        channels = Make("Channels", Theme.Glyph.List, () => form.ShowChannelList());
        next = Make("Next", Theme.Glyph.Up, () => form.Zap(1));
        previous = Make("Previous", Theme.Glyph.Down, () => form.Zap(-1));
        favorite = Make("Favorite", Theme.Glyph.Star, () =>
        {
            Channel channel = Player.Item?.Channel;
            if (channel != null) App.Library.ToggleFavorite(channel);
        });
        record = Make("Record", Theme.Glyph.Record, () =>
        {
            Channel channel = Player.Item?.Channel;
            if (channel == null) return;
            Recording active = App.Recordings.ActiveFor(channel.Id);
            if (active != null) App.Recordings.Stop(active.Id);
            else Sheets.Record(form, App, channel);
        });
        record.GlyphColor = Theme.Live;
        rewind = Make("-10 s", Theme.Glyph.Rewind, () => Player.SeekBy(-10));
        playPause = Make("Pause", Theme.Glyph.Pause, () => Player.TogglePause());
        forward = Make("+10 s", Theme.Glyph.Forward, () => Player.SeekBy(10));
        forwardMinute = Make("+1 min", Theme.Glyph.Forward, () => Player.SeekBy(60));
        speed = Make("Speed 1×", Theme.Glyph.Speed, SpeedMenu, 10);
        download = Make("Download", Theme.Glyph.Download, DownloadCurrent);
        audio = Make("Audio", Theme.Glyph.Audio, () => TrackMenu(true), 10);
        subtitles = Make("Subtitles", Theme.Glyph.Subtitles, () => TrackMenu(false), 10);
        aspect = Make("Original", Theme.Glyph.Aspect, () => Player.CycleAspect());
        mute = Make("Sound", Theme.Glyph.Volume, () => Player.Muted = !Player.Muted);
        fullScreen = Make("Full screen", Theme.Glyph.FullScreen, () => form.ToggleBorderless());

        leftGroup = new[] { back, channels, previous, next, favorite, record, rewind, playPause, forward, forwardMinute, speed, download };
        rightGroup = new[] { audio, subtitles, aspect, mute, fullScreen };
    }

    private FlatButton Make(string text, string glyph, Action action, double keepSeconds = 4)
    {
        var button = new FlatButton(text, glyph)
        {
            Stacked = true,
            Fill = Theme.Raised,
            BackColor = Theme.Panel,
            GlyphSize = 14f,
            TextSize = 8f,
            Size = new Size(Theme.S(82), Theme.S(58))
        };
        button.Click += (_, _) => form.BarAction(action, keepSeconds);
        Controls.Add(button);
        return button;
    }

    public static string SpeedLabel(float rate) => rate.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    public int PreferredHeight => Theme.S(showSeek ? 116 : 78);

    /// <summary>Decides which buttons apply to what is playing, and lays them out.</summary>
    public void Rebuild()
    {
        PlayableItem item = Player.Item;
        bool live = item?.IsLive ?? false;
        showSeek = item != null && !live;
        channels.Shown = previous.Shown = next.Shown = favorite.Shown = record.Shown = live;
        rewind.Shown = playPause.Shown = forward.Shown = forwardMinute.Shown = speed.Shown = item != null && !live;
        download.Shown = item != null && !string.IsNullOrEmpty(item.DownloadUrl);
        audio.Shown = Player.AudioTracks.Count > 1;
        subtitles.Shown = Player.SubtitleTracks.Count > 0;
        if (Controls.Contains(seek) != showSeek)
        {
            if (showSeek) Controls.Add(seek);
            else Controls.Remove(seek);
        }
        LayoutButtons();
        Sync(true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutButtons();
    }

    private void LayoutButtons()
    {
        int top = Height - Theme.S(68);
        int x = Theme.S(10);
        foreach (FlatButton button in leftGroup)
        {
            if (!button.Shown) continue;
            button.Location = new Point(x, top);
            x += button.Width + Theme.S(6);
        }
        int right = Width - Theme.S(10);
        for (int i = rightGroup.Length - 1; i >= 0; i--)
        {
            FlatButton button = rightGroup[i];
            if (!button.Shown) continue;
            right -= button.Width;
            // Never overlap the left group on a narrow window.
            button.Location = new Point(Math.Max(right, x), top);
            right -= Theme.S(6);
        }
        if (showSeek) seek.SetBounds(Theme.S(78), Theme.S(8), Math.Max(50, Width - Theme.S(156)), Theme.S(24));
        Invalidate();
    }

    /// <summary>Refreshes captions and the position bar from the player's current state.</summary>
    public void Sync(bool force)
    {
        PlayableItem item = Player.Item;
        if (item == null) return;
        bool wantAudio = Player.AudioTracks.Count > 1, wantSubtitles = Player.SubtitleTracks.Count > 0;
        if (audio.Shown != wantAudio || subtitles.Shown != wantSubtitles)
        {
            audio.Shown = wantAudio;
            subtitles.Shown = wantSubtitles;
            LayoutButtons();
        }

        if (item.IsLive && item.Channel != null)
        {
            bool isFavorite = App.Library.IsFavorite(item.Channel);
            favorite.Glyph = isFavorite ? Theme.Glyph.StarFilled : Theme.Glyph.Star;
            favorite.Selected = isFavorite;
            bool recording = App.Recordings.ActiveFor(item.Channel.Id) != null;
            record.Caption = recording ? "Stop rec." : "Record";
            record.Glyph = recording ? Theme.Glyph.Stop : Theme.Glyph.Record;
        }
        else
        {
            bool paused = Player.Status == PlayerStatus.Paused;
            playPause.Caption = paused ? "Play" : "Pause";
            playPause.Glyph = paused ? Theme.Glyph.Play : Theme.Glyph.Pause;
            speed.Caption = "Speed " + SpeedLabel(Player.Rate);
            speed.Selected = Math.Abs(Player.Rate - 1f) > 0.01f;
            seek.Enabled = Player.CanSeek;
            if (!seek.IsDragging)
            {
                seek.Value = Player.Progress;
                seek.Invalidate();
            }
        }

        if (download.Shown)
        {
            DownloadItem existing = App.Downloads.FindBySource(item.Id);
            download.Caption = existing == null ? "Download"
                : existing.State == DownloadState.Completed ? "Downloaded"
                : existing.State == DownloadState.Paused ? "Paused"
                : $"{existing.Fraction:P0}";
            download.Selected = existing != null && existing.State != DownloadState.Completed;
        }
        aspect.Caption = Player.AspectLabel;
        mute.Caption = Player.Muted ? "Muted" : "Sound";
        mute.Glyph = Player.Muted ? Theme.Glyph.Mute : Theme.Glyph.Volume;
        if (showSeek) Invalidate(new Rectangle(0, 0, Width, Theme.S(40)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(Theme.Panel);
        if (!showSeek) return;
        Theme.DrawText(g, Theme.Clock(Player.TimeMs), Theme.Ui(9f), Theme.Text, new Rectangle(Theme.S(8), Theme.S(8), Theme.S(68), Theme.S(24)), HorizontalAlignment.Right);
        string total = Player.DurationMs > 0 ? Theme.Clock(Player.DurationMs) : "--:--";
        Theme.DrawText(g, total, Theme.Ui(9f), Theme.Secondary, new Rectangle(Width - Theme.S(76), Theme.S(8), Theme.S(68), Theme.S(24)));
    }

    public void SpeedMenu()
    {
        if (!Player.CanChangeSpeed) return;
        var entries = PlayerEngine.Speeds.Select(rate =>
        {
            string label = SpeedLabel(rate) + (rate == 1f ? "  (normal)" : "") + (Math.Abs(rate - Player.Rate) < 0.01f ? "   ✓" : "");
            return (label, (Action)(() =>
            {
                Player.SetRate(rate);
                form.ShowControls();
            }));
        }).ToArray();
        Painters.ShowMenu(form, speed.PointToScreen(new Point(0, -Theme.S(8) - entries.Length * Theme.S(26))), entries);
    }

    private void TrackMenu(bool forAudio)
    {
        List<TrackOption> tracks = forAudio ? Player.AudioTracks : Player.SubtitleTracks;
        int currentId = forAudio ? Player.CurrentAudio : Player.CurrentSubtitle;
        var entries = tracks.Select(track => (track.Name + (track.Id == currentId ? "   ✓" : ""), (Action)(() =>
        {
            if (forAudio) Player.SelectAudio(track.Id);
            else Player.SelectSubtitle(track.Id);
        }))).ToArray();
        if (entries.Length == 0) return;
        FlatButton anchor = forAudio ? audio : subtitles;
        Painters.ShowMenu(form, anchor.PointToScreen(new Point(0, -Theme.S(8) - entries.Length * Theme.S(26))), entries);
    }

    private void DownloadCurrent()
    {
        PlayableItem item = Player.Item;
        if (item == null) return;
        DownloadItem existing = App.Downloads.FindBySource(item.Id);
        if (existing != null)
        {
            App.Toast(existing.State == DownloadState.Completed ? "Already downloaded: " + existing.FilePath : "Already in Downloads (" + existing.State + ")");
            return;
        }
        if (App.DownloadCurrent()) App.Toast("Added to Downloads. It keeps downloading while you watch.");
    }
}

/// <summary>What is playing: channel and programme for live TV, title and position for everything else.</summary>
public sealed class InfoBanner : Control
{
    private readonly AppServices app;

    public InfoBanner(AppServices app)
    {
        this.app = app;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.FromArgb(16, 20, 28);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Theme.Prepare(g);
        g.Clear(BackColor);
        PlayerEngine player = app.Player;
        PlayableItem item = player.Item;
        if (item == null) return;
        DateTime now = DateTime.UtcNow;
        int pad = Theme.S(14);

        // Clock and state on the right
        int right = Width - pad;
        Theme.DrawText(g, DateTime.Now.ToString("HH:mm"), Theme.Ui(15f), Theme.Text, new Rectangle(right - Theme.S(90), 0, Theme.S(90), Height), HorizontalAlignment.Right);
        right -= Theme.S(100);
        string state = player.Status switch
        {
            PlayerStatus.Loading => player.Reconnects > 0 ? "Reconnecting…" : "Loading…",
            PlayerStatus.Failed => "This stream could not be played",
            PlayerStatus.Paused => "Paused",
            _ => ""
        };
        if (item.Channel != null && app.Recordings.ActiveFor(item.Channel.Id) != null) state = (state.Length > 0 ? state + "   " : "") + "● REC";
        if (!item.IsLive && Math.Abs(player.Rate - 1f) > 0.01f) state = (state.Length > 0 ? state + "   " : "") + PlayerBar.SpeedLabel(player.Rate);
        if (state.Length > 0)
        {
            int stateWidth = Theme.MeasureWidth(g, state, Theme.Ui(10f, FontStyle.Bold)) + Theme.S(8);
            Color color = state.Contains("REC") ? Theme.Live : player.Status == PlayerStatus.Failed ? Theme.Warn : Theme.Accent;
            Theme.DrawText(g, state, Theme.Ui(10f, FontStyle.Bold), color, new Rectangle(right - stateWidth, 0, stateWidth, Height), HorizontalAlignment.Right);
            right -= stateWidth + Theme.S(10);
        }

        int x = pad;
        if (item.IsLive && item.Channel != null)
        {
            Channel channel = item.Channel;
            Theme.DrawText(g, channel.Number.ToString(), Theme.Ui(16f, FontStyle.Bold), Theme.Text, new Rectangle(x, 0, Theme.S(70), Height), HorizontalAlignment.Center);
            x += Theme.S(76);
            var logo = new Rectangle(x, pad, Theme.S(92), Height - pad * 2);
            Painters.Logo(g, this, channel.Logo, channel.Name, logo);
            x = logo.Right + Theme.S(14);
            int width = Math.Max(10, right - x);
            Programme current = app.Library.Epg.Current(channel, now);
            if (current == null)
            {
                Theme.DrawText(g, channel.Name, Theme.Ui(13f, FontStyle.Bold), Theme.Text, new Rectangle(x, 0, width, Height));
                return;
            }
            Theme.DrawText(g, channel.Name, Theme.Ui(9.5f), Theme.Secondary, new Rectangle(x, Theme.S(8), width, Theme.S(20)));
            Theme.DrawText(g, current.Title, Theme.Ui(12.5f, FontStyle.Bold), Theme.Text, new Rectangle(x, Theme.S(27), width, Theme.S(26)));
            int barWidth = Math.Min(width - Theme.S(110), Theme.S(420));
            Theme.DrawText(g, Theme.Time(current.Start), Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x, Theme.S(54), Theme.S(44), Theme.S(18)));
            Theme.DrawProgress(g, new Rectangle(x + Theme.S(48), Theme.S(61), Math.Max(10, barWidth), Theme.S(4)), current.Progress(now), Theme.Accent);
            Theme.DrawText(g, Theme.Time(current.Stop), Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x + Theme.S(56) + Math.Max(10, barWidth), Theme.S(54), Theme.S(50), Theme.S(18)));
            Programme next = app.Library.Epg.Next(channel, now);
            if (next != null)
            {
                Theme.DrawText(g, $"Next: {next.Title} · {Theme.Time(next.Start)}", Theme.Ui(8.5f), Theme.Secondary, new Rectangle(x, Theme.S(73), width, Theme.S(18)));
            }
        }
        else
        {
            int width = Math.Max(10, right - x);
            string kind = item.Kind switch
            {
                PlayKind.Catchup => "Catch-up",
                PlayKind.Recording => "Recording",
                PlayKind.Download => "Downloaded",
                PlayKind.Episode => "Series",
                _ => "Movie"
            };
            Theme.DrawText(g, kind.ToUpperInvariant(), Theme.Ui(8f, FontStyle.Bold), Theme.Accent, new Rectangle(x, Theme.S(10), width, Theme.S(18)));
            Theme.DrawText(g, item.Title, Theme.Ui(13f, FontStyle.Bold), Theme.Text, new Rectangle(x, Theme.S(28), width, Theme.S(28)));
            string detail = item.Subtitle;
            if (player.DurationMs > 0)
            {
                detail = (string.IsNullOrEmpty(detail) ? "" : detail + "   ·   ") + Theme.Clock(player.TimeMs) + " / " + Theme.Clock(player.DurationMs);
            }
            Theme.DrawText(g, detail, Theme.Ui(9.5f), Theme.Secondary, new Rectangle(x, Theme.S(58), width, Theme.S(22)));
        }
    }
}

/// <summary>The channel list that slides over the left side of the player.</summary>
public sealed class ChannelPanel : Control
{
    private readonly MainForm form;
    private readonly VirtualList list = new() { RowHeight = Theme.S(64) };
    private readonly FlatButton earlier, later;
    private List<Channel> channels = new();

    private AppServices App => form.Services;

    public ChannelPanel(MainForm form)
    {
        this.form = form;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        earlier = new FlatButton("", Theme.Glyph.Left, (_, _) => StepGroup(-1));
        later = new FlatButton("", Theme.Glyph.Right, (_, _) => StepGroup(1));
        list.DrawItem = (g, index, bounds, state) =>
            Painters.ChannelRow(g, list, bounds, state, channels[index], App, App.Player.Item?.Id == channels[index].Id, DateTime.UtcNow);
        list.Activated += index =>
        {
            Channel channel = channels[index];
            form.HideChannelList();
            App.Watch(channel);
        };
        Controls.AddRange(new Control[] { earlier, later, list });
    }

    public void Reload()
    {
        channels = App.Library.ChannelsIn(App.SelectedGroup);
        list.Count = channels.Count;
        int playing = channels.FindIndex(c => c.Id == App.Player.Item?.Channel?.Id);
        list.SelectedIndex = playing;
        if (playing >= 0) list.EnsureVisible(playing, true);
        else list.ScrollToTop();
        Invalidate();
    }

    public void FocusList() => list.Focus();

    public void StepGroup(int offset)
    {
        List<string> groups = App.Library.VisibleGroups();
        if (groups.Count == 0) return;
        int index = Math.Max(0, groups.IndexOf(App.SelectedGroup));
        App.SelectedGroup = groups[((index + offset) % groups.Count + groups.Count) % groups.Count];
        Reload();
        form.ShowControls();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int header = Theme.S(56);
        earlier.SetBounds(Theme.S(10), Theme.S(10), Theme.S(40), Theme.S(36));
        later.SetBounds(Width - Theme.S(50), Theme.S(10), Theme.S(40), Theme.S(36));
        list.SetBounds(0, header, Width, Math.Max(10, Height - header));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Prepare(e.Graphics);
        e.Graphics.Clear(Theme.Background);
        Theme.DrawText(e.Graphics, App.SelectedGroup, Theme.Ui(11.5f, FontStyle.Bold), Theme.Text,
            new Rectangle(Theme.S(58), 0, Math.Max(10, Width - Theme.S(116)), Theme.S(56)), HorizontalAlignment.Center);
    }
}
