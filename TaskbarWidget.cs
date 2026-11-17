using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using TaskbarPlayer.Native;
using static TaskbarPlayer.Native.NativeMethods;

namespace TaskbarPlayer;

internal enum WidgetPlacement
{
    Auto,
    Left,
    Right,
}

/// <summary>
/// A layered child window hosted inside the Windows taskbar (Shell_TrayWnd), drawn with per-pixel alpha
/// so it blends with the taskbar's own background.
/// </summary>
internal sealed class TaskbarWidget : NativeWindow, IDisposable
{
    private readonly MediaController _media;
    private readonly AppSettings _settings;
    private FlyoutWindow? _flyout;
    private Tip? _tip; // what the flyout shows
    private readonly FrameMemory _frame = new();
    private WidgetColumn? _column; // the layout on a side taskbar
    private WidgetFit _fit;        // what of the widget is shown
    private Bitmap? _still;   // the picture without its progress line
    private Bitmap? _picture; // what the window shows
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 600 };

    private IntPtr _tray;
    private Rectangle _bounds;
    private float _scale = 1f;
    private bool _showInfo;
    private float _textWidth;
    private float _buttonWidth = 34;
    private readonly PlacementState _placement;
    private readonly WidgetAccessible _accessible;
    private readonly MeasureSchedule _schedule = new();
    private readonly TaskbarWatcher _watcher;
    private bool _watching;
    private bool _standingIn; // on the main taskbar because the chosen display has none
    private readonly StandInSearch _standIn = new();
    private readonly SideKeeper _side = new();
    private WidgetPlacement? _placedFor; // the Position setting the side was chosen under
    // The taskbar's own windows for the tray and the app area: Explorer resizes them with their content. Another
    // display's taskbar has only the last one, and it is the only sign of a change of its own app icons.
    private readonly IntPtr[] _taskbarParts = new IntPtr[3];
    private readonly Rectangle[] _taskbarPartRects = new Rectangle[3];
    private Rectangle _taskbarRect;
    private Rectangle _screenBounds;
    private bool _shown;
    private bool? _autoUsesLeft;
    private string? _loggedSide;
    private RectangleF _infoRect;
    private readonly Dictionary<WidgetPart, RectangleF> _buttonRects = new();
    private RectangleF _progressRect;
    private float _mouseX;
    private float? _seekFraction; // set while dragging on the timeline
    private WidgetPart _hover;
    private WidgetPart _pressed;
    private bool _trackingMouse;
    private float _volume;
    private bool _muted;
    private bool _tipFailureLogged;

    public TaskbarWidget(MediaController media, AppSettings settings, LayoutCache cache)
    {
        _media = media;
        _settings = settings;
        _placement = new PlacementState(cache, classic: !TaskbarProbe.HasXamlTaskbar(Environment.OSVersion.Version));
        _hoverTimer.Tick += (_, _) => ShowHoverTip();
        _watcher = new TaskbarWatcher(_schedule.Changed);
        _accessible = new WidgetAccessible(
            () => GetWindowRect(Handle, out var window) ? window.ToRectangle() : Rectangle.Empty,
            DescribeParts,
            // After the tool's call has returned: a press may open a menu.
            part => SynchronizationContext.Current?.Post(_ => PartClicked?.Invoke(part, PartScreenRect(part)), null));
    }

    public event Action<WidgetPart, Rectangle>? PartClicked;
    public event Action<Point>? ContextMenuRequested;
    public event Action<int>? WheelScrolled;

    /// <summary>The user clicked or dragged on the timeline; the argument is the target position (0..1).</summary>
    public event Action<float>? SeekRequested;

    /// <summary>Text shown when no media session is available (e.g. API failure).</summary>
    public string? StatusText { get; set; }

    /// <summary>Decides whether the add-to-playlist button is shown (YouTube Music sources only).</summary>
    public Func<bool>? PlaylistButtonVisible { get; set; }

    private string? _flashText;
    private DateTime _flashUntil;

    /// <summary>Temporarily replaces the second line with a message, e.g. "Added to My playlist".</summary>
    public void Flash(string text, TimeSpan duration)
    {
        _flashText = text;
        _flashUntil = DateTime.UtcNow + duration;
        Render();
    }

    private bool ShowsPlaylistButton => PlaylistButtonVisible?.Invoke() == true;

    private int WantedButtons => ShowsPlaylistButton ? 5 : 4;

    public bool IsAttached => Handle != IntPtr.Zero && IsWindow(Handle) && _tray != IntPtr.Zero && IsWindow(_tray);

    public Rectangle TrayScreenBounds => GetWindowRect(_tray, out var r) ? r.ToRectangle() : Rectangle.Empty;

    /// <summary>Creates (or re-creates after an Explorer restart) the child window inside the taskbar.</summary>
    public bool Attach()
    {
        IntPtr tray = FindTray(out bool wanted);
        StandIn(!wanted);
        _side.Forget();
        // The window the mouse was over is gone or about to go, and with it what the mouse did there. A window that
        // died with its taskbar has already let go of its handle.
        if (Handle == IntPtr.Zero || !IsWindow(Handle) || tray != _tray)
        {
            HideTip();
            _trackingMouse = false;
            _hover = WidgetPart.None;
            _pressed = WidgetPart.None;
            _seekFraction = null;
        }
        if (tray == IntPtr.Zero) return false;

        if (Handle != IntPtr.Zero)
        {
            if (IsWindow(Handle) && tray == _tray) return true;
            if (IsWindow(Handle)) DestroyHandle(); else ReleaseHandle();
        }

        _tray = tray;
        _bounds = Rectangle.Empty;
        _placement.Reset();
        _shown = false;
        _autoUsesLeft = null;
        _loggedSide = null;
        try
        {
            CreateHandle(new CreateParams
            {
                Caption = "TaskbarPlayer",
                Parent = tray,
                // Hidden until it has a place: shown from UpdateLayout.
                Style = WS_CHILD | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                Width = 1,
                Height = 1,
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Attach to taskbar 0x{tray:X} failed: {ex}");
            _tray = IntPtr.Zero;
            return false;
        }
        _watching = _watcher.Watch(tray);
        _taskbarParts[0] = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        _taskbarParts[1] = FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
        _taskbarParts[2] = FindDescendant(tray, "MSTaskListWClass");
        Array.Clear(_taskbarPartRects);
        _taskbarRect = Rectangle.Empty;
        _screenBounds = TaskbarIdentity.ScreenOf(tray).Bounds;
        _frame.Forget();
        Log.Write($"Attached to taskbar 0x{tray:X}, widget 0x{Handle:X}"
            + (_watching ? "" : "; its events cannot be watched, so it is measured on every tick"));
        RefreshIdentity();
        UpdateLayout();
        return true;
    }

    private void RefreshIdentity()
    {
        if (_placement.SetIdentity(TaskbarIdentity.FromTaskbar(_tray))) _autoUsesLeft = null;
    }

    /// <summary>All taskbars: the main one plus one per extra monitor (when shown on all displays).</summary>
    public static IReadOnlyList<(IntPtr Tray, Screen Screen)> FindTaskbars()
    {
        var bars = new List<(IntPtr, Screen)>();
        IntPtr primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) bars.Add((primary, TaskbarIdentity.ScreenOf(primary)));

        IntPtr secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (IsWindowVisible(secondary)) bars.Add((secondary, TaskbarIdentity.ScreenOf(secondary)));
        }
        return bars;
    }

    /// <summary>The taskbar on the chosen monitor, falling back to the main taskbar when that monitor is gone.</summary>
    private IntPtr FindTray() => FindTray(out _);

    /// <param name="wanted">False when the chosen display has no taskbar now and the main one stands in.</param>
    private IntPtr FindTray(out bool wanted)
    {
        wanted = true;
        var bars = FindTaskbars();
        if (bars.Count == 0) return IntPtr.Zero;
        if (_settings.Monitor is string device)
        {
            foreach (var (tray, screen) in bars)
            {
                if (string.Equals(screen.DeviceName, device, StringComparison.OrdinalIgnoreCase)) return tray;
            }
            wanted = false;
        }
        return bars[0].Tray;
    }

    public void SetVolume(float level, bool muted)
    {
        if (Math.Abs(level - _volume) < 0.001f && muted == _muted) return;
        _volume = level;
        _muted = muted;
        Render();
    }

    /// <summary>A setting or the displays changed in a way that may move the widget: look at everything now.</summary>
    public void Recheck()
    {
        _schedule.Changed();
        // The chosen display's taskbar can come after a change like this one.
        if (_standingIn) _standIn.Begin();
        // Whatever happened to the screen: the next picture is drawn whole.
        _frame.Forget();
        Look(onTimer: false);
    }

    /// <summary>
    /// Called periodically: keeps the widget attached, positioned and its progress line current. A tick on which
    /// nothing changed reads the place of three windows and the widget's place among its siblings, nothing else.
    /// </summary>
    public void Tick() => Look(onTimer: true);

    private void Look(bool onTimer)
    {
        // Explorer restarted: our window went with its taskbar.
        if (!IsAttached)
        {
            Attach();
            return;
        }
        // The taskbar of the chosen display gives no sign when it comes back: looked for while standing in.
        if (_standingIn && _standIn.Due() && FindTray() != _tray)
        {
            Attach();
            return;
        }
        if (!GetWindowRect(_tray, out var trayRect)) return;

        // Signs of change that cost next to nothing: where the taskbar and two of its windows are. App icons
        // coming and going announce themselves through the watcher.
        var taskbar = trayRect.ToRectangle();
        bool partsMoved = TaskbarPartsMoved();
        if (taskbar != _taskbarRect || partsMoved) _schedule.Changed();
        _taskbarRect = taskbar;

        bool moved = false;
        // A close look costs us, and measuring costs Explorer: only when a sign asked for it, unless there is no
        // other way to learn of changes.
        if (_schedule.Owed(_placement.Measured && _watching))
        {
            // The chosen monitor's taskbar (re)appeared, or the setting changed.
            if (FindTray(out bool wanted) != _tray)
            {
                Attach();
                return;
            }
            if (_standingIn == wanted) StandIn(!wanted);
            FindParts();
            _screenBounds = TaskbarIdentity.ScreenOf(_tray).Bounds;
            RefreshIdentity();
            ProbeTaskbar();
            moved = UpdateLayout();
        }
        // Explorer occasionally re-orders its own children; stay on top of the XAML content.
        if (GetWindow(Handle, GW_HWNDPREV) != IntPtr.Zero)
        {
            SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        // "Resting" there means unchanged since the tick before: only the timer's ticks are half a second apart.
        if (onTimer && _placement.ObserveTaskbar(taskbar, _screenBounds)) Render();
        bool flashExpired = _flashText is not null && DateTime.UtcNow >= _flashUntil;
        if (flashExpired) _flashText = null;
        if (!moved && (flashExpired || (_media.IsPlaying && _media.Duration > TimeSpan.Zero))) Render();
    }

    private void StandIn(bool standingIn)
    {
        _standingIn = standingIn;
        if (standingIn) _standIn.Begin();
        else _standIn.End();
    }

    // The windows whose rectangles are signs. One that was not there yet when the widget attached, or that
    // Explorer made anew, is found when a look is owed.
    private void FindParts()
    {
        if (!IsWindow(_taskbarParts[0])) _taskbarParts[0] = FindWindowEx(_tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (!IsWindow(_taskbarParts[1])) _taskbarParts[1] = FindWindowEx(_tray, IntPtr.Zero, "ReBarWindow32", null);
        if (!IsWindow(_taskbarParts[2])) _taskbarParts[2] = FindDescendant(_tray, "MSTaskListWClass");
    }

    /// <summary>
    /// Measures the free taskbar space on a background thread, then re-lays out. One measurement at a time: what
    /// is asked for while one runs is measured after it.
    /// </summary>
    private async void ProbeTaskbar()
    {
        if (_placement.Classic || !_schedule.Begin()) return;
        var result = Measurement.Nothing;
        try
        {
            IntPtr tray = _tray;
            var identity = _placement.Identity;
            var layout = await Task.Run(() => TaskbarProbe.Probe(tray));
            // Another taskbar by now: what was read is of no use.
            if (tray != _tray) return;
            var before = _placement.Layout;
            result = _placement.Apply(layout, identity) switch
            {
                PlacementStep.Taken => layout == before ? Measurement.Same : Measurement.Different,
                // Kept back until a second look agrees: that look is asked for.
                PlacementStep.Held => Measurement.Different,
                _ => Measurement.Nothing,
            };
        }
        finally
        {
            _schedule.Finished(result);
        }
        UpdateLayout();
    }

    // A cheap sign that the tray or the app area changed: where two of the taskbar's own windows are.
    private bool TaskbarPartsMoved()
    {
        bool moved = false;
        for (int i = 0; i < _taskbarParts.Length; i++)
        {
            if (_taskbarParts[i] == IntPtr.Zero || !GetWindowRect(_taskbarParts[i], out var rect)) continue;
            var now = rect.ToRectangle();
            if (now == _taskbarPartRects[i]) continue;
            _taskbarPartRects[i] = now;
            moved = true;
        }
        return moved;
    }

    /// <summary>Recomputes size and position from the current taskbar layout. Returns true if it changed.</summary>
    public bool UpdateLayout()
    {
        if (!IsAttached || !GetWindowRect(_tray, out var trayRect)) return false;
        var tray = trayRect.ToRectangle();

        // Nothing measured or remembered for this taskbar: no place is better than a wrong one.
        if (!_placement.HasPlace) return Hide("hidden, nothing measured or remembered");
        if (_placedFor != _settings.Placement)
        {
            _placedFor = _settings.Placement;
            _side.Forget();
        }

        uint dpi = GetDpiForWindow(_tray);
        _scale = dpi == 0 ? 1f : dpi / 96f;
        float s = _scale;
        int margin = (int)(8 * s);

        // Everything below is relative to the taskbar window, as a child window's position is.
        Rectangle band;
        Rectangle gap;
        bool alignLeft;
        string gapName = "";
        bool borrowed = false;
        int lead = margin;
        if (_placement.Layout is TaskbarLayout layout)
        {
            band = layout.Band;
            if (_settings.Placement == WidgetPlacement.Left)
            {
                // As far left as there is room: with the app icons at the left end, that is right after them.
                gap = layout.FirstGapWith(2 * margin + (int)Math.Ceiling(WantedButtons * WidgetColumn.SmallestButton * s));
                alignLeft = true;
            }
            else
            {
                bool useLeft = ChooseLeftGap(layout, s);
                gap = useLeft ? layout.GapBefore : layout.GapAfter;
                alignLeft = useLeft; // left gap: next to Widgets; right gap: next to the system tray
            }
            // Directly after the app icons a little more air than next to the Widgets button or the tray.
            if (alignLeft && gap != layout.GapBefore) lead = (int)(16 * s);
            // A remembered layout is good for where the taskbar's fixed parts are. Where the app icons end
            // changes from run to run: a place against them waits for measurements, as a new taskbar does.
            if (!_placement.Measured && layout.AgainstIcons(gap, alignLeft))
            {
                _placement.Distrust();
                return Hide("hidden, the remembered place depends on the app icons");
            }
        }
        else
        {
            // Classic taskbar (Windows 10): its child windows are the layout.
            Rectangle notify = ChildScreenRect("TrayNotifyWnd") ?? new Rectangle(tray.Right, tray.Top, 0, tray.Height);
            Rectangle tasks = ChildScreenRect("ReBarWindow32") ?? new Rectangle(tray.Left, tray.Top, 0, tray.Height);
            band = Rectangle.FromLTRB(tray.Left, tasks.Top, tray.Right, tasks.Bottom);
            gap = Rectangle.FromLTRB(tasks.Right, band.Top, Math.Max(tasks.Right, notify.Left), band.Bottom);
            band.Offset(-tray.Left, -tray.Top);
            gap.Offset(-tray.Left, -tray.Top);
            alignLeft = false;
            gap = WidgetRow.ClassicGap(gap, lead, margin, WantedButtons, s, out borrowed);
        }

        // What is wanted. Short of room the widget gives things up in steps, down to the play button alone. On a
        // classic taskbar without a gap it is the buttons, as it always was there.
        bool vertical = _placement.Layout is { Vertical: true };
        IReadOnlyList<WidgetFit> steps = borrowed
            ? [new WidgetFit(false, false, PreviousNext: true, ShowsPlaylistButton, Volume: true)]
            : WidgetFit.Steps(text: !vertical && _settings.ShowTitle, cover: _settings.ShowCover, playlist: ShowsPlaylistButton);
        WidgetColumn? column = null;
        WidgetRow? row = null;
        void Fit(Rectangle into, bool fromStart, int air)
        {
            column = vertical ? WidgetColumn.Compute(band, into, fromStart, air, margin, steps, s) : null;
            row = vertical ? null : WidgetRow.Compute(band, into, fromStart, air, margin, steps, s);
        }

        Fit(gap, alignLeft, lead);
        if (column is null && row is null && _placement.Layout is TaskbarLayout both)
        {
            // Nothing fits where the widget belongs; the other gap may still hold part of it. There it goes to
            // the end away from the app icons.
            bool toBefore = gap != both.GapBefore;
            var other = toBefore ? both.GapBefore : both.GapAfter;
            Fit(other, toBefore, margin);
            if (column is not null || row is not null)
            {
                gap = other;
                alignLeft = toBefore;
            }
        }
        // Not even the play button fits anywhere: no widget is better than one lying over its neighbours.
        if (column is null && row is null) return Hide("hidden, no room");

        if (_placement.Layout is TaskbarLayout measured)
        {
            // Icons on the move can make a gap look too small for the length of one measurement: the widget goes
            // to the other side of them only when a second measurement agrees. Until then it stays.
            if (!_side.Accepts(gap == measured.GapBefore, _placement.Settled || !_shown)) return false;
            gapName = (measured.Vertical, gap == measured.GapBefore) switch
            {
                (false, true) => "left gap",
                (false, false) => "right gap",
                (true, true) => "upper gap",
                (true, false) => "lower gap",
            };
        }

        Rectangle bounds = column?.Bounds ?? row!.Bounds;
        WidgetFit fit = column?.Fit ?? row!.Fit;
        bool showInfo = fit.Text || fit.Cover;
        float textWidth = row?.TextWidth ?? 0;
        if (row is not null) _buttonWidth = row.ButtonWidth;

        string side = _placement.Layout is null
            ? "classic taskbar"
            : $"{(_placement.Measured ? "measured" : "remembered")}, {_settings.Placement} placement, {gapName}";
        // What was given up is part of the kind of placement: a change of it is worth a line.
        string given = fit == steps[0] ? "" : $", {fit.ButtonCount} button(s){(fit.Cover ? ", cover" : "")}{(fit.Text ? ", text" : "")}";
        side += given;
        LogSide(side, column is null
            ? $"{side}, x {bounds.X}, width {bounds.Width}"
            : $"{side}, y {bounds.Y}, height {bounds.Height}");

        bool changed = bounds != _bounds || showInfo != _showInfo || Math.Abs(textWidth - _textWidth) > 0.5f
            || fit != _fit || (column is null) != (_column is null);
        if (!changed && _shown) return false;

        if (changed)
        {
            _bounds = bounds;
            _showInfo = showInfo;
            _textWidth = textWidth;
            _column = column;
            _fit = fit;
            ComputeParts();
            SetWindowPos(Handle, HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE);
        }
        bool appearing = !_shown;
        _shown = true;
        try
        {
            Render();
        }
        finally
        {
            // Drawn first, so it does not appear empty. Shown also when drawing failed: the next call draws.
            if (appearing) ShowWindow(Handle, SW_SHOWNOACTIVATE);
        }
        return true;
    }

    private bool Hide(string why)
    {
        if (_shown)
        {
            ShowWindow(Handle, SW_HIDE);
            _shown = false;
            // A window hidden under the pointer may not be told that the mouse left.
            HideTip();
            _trackingMouse = false;
            _hover = WidgetPart.None;
            _pressed = WidgetPart.None;
        }
        // Nothing on screen to keep in its place.
        _side.Forget();
        LogSide(why);
        return false;
    }

    // Runs every tick: log only when the kind of placement changes.
    private void LogSide(string side, string? line = null)
    {
        if (side == _loggedSide) return;
        _loggedSide = side;
        Log.Write($"Layout: {line ?? side}");
    }

    private bool ChooseLeftGap(TaskbarLayout layout, float s)
    {
        switch (_settings.Placement)
        {
            case WidgetPlacement.Left: return true;
            case WidgetPlacement.Right: return false;
        }

        int left = layout.Room(layout.GapBefore);
        int right = layout.Room(layout.GapAfter);
        if (_autoUsesLeft is not bool current)
        {
            current = left > right;
        }
        else
        {
            // Hysteresis: only switch sides when the other side is clearly roomier, to avoid jumping around.
            int mine = current ? left : right;
            int other = current ? right : left;
            float minimum = 4 * 26 * s + 16 * s;
            if (other > mine + 40 * s || (mine < minimum && other >= minimum)) current = !current;
        }
        _autoUsesLeft = current;
        return current;
    }

    private float CoverSize(int height) => WidgetRow.CoverSize(height, _scale);

    private Rectangle? ChildScreenRect(string className)
    {
        IntPtr child = FindWindowEx(_tray, IntPtr.Zero, className, null);
        if (child == IntPtr.Zero || !IsWindowVisible(child) || !GetWindowRect(child, out var r)) return null;
        return r.ToRectangle();
    }

    private void ComputeParts()
    {
        if (_column is WidgetColumn column)
        {
            _infoRect = column.Info;
            _buttonRects.Clear();
            var parts = _fit.Buttons;
            for (int i = 0; i < parts.Count && i < column.Buttons.Count; i++) _buttonRects[parts[i]] = column.Buttons[i];
            return;
        }

        float s = _scale;
        float h = _bounds.Height;
        float x = 0;
        _infoRect = RectangleF.Empty;
        if (_showInfo)
        {
            float infoWidth = WidgetRow.InfoStart(_fit, CoverSize(_bounds.Height), s) + _textWidth;
            _infoRect = new RectangleF(0, 0, infoWidth, h);
            x = infoWidth + 6 * s;
        }

        _buttonRects.Clear();
        foreach (var part in _fit.Buttons)
        {
            _buttonRects[part] = new RectangleF(x, 0, _buttonWidth, h);
            x += _buttonWidth;
        }
    }

    // ---------------------------------------------------------------- drawing

    // What the picture is made of apart from its progress line. Equal means the same picture.
    private sealed record Still(Size Size, float Scale, bool Light, WidgetPart Hover, bool Pressed, bool ShowInfo, WidgetFit Fit,
        float TextWidth, float ButtonWidth, Image? Cover, string Title, string? Subtitle, bool HasProgress, string Buttons,
        Rectangle Strip);

    // The progress line: how many pixels of it are filled, and whether the mouse is on it.
    private sealed record Bar(int Fill, bool Seeking);

    private readonly record struct Info(string Title, string? Subtitle, bool HasProgress, bool Seeking, float Fraction);

    /// <summary>
    /// Brings the window's picture up to date. Called freely: nothing is drawn when the picture would be the same,
    /// and only the strip with the progress line when nothing else differs.
    /// </summary>
    public void Render()
    {
        // What is up follows what it tells about: the song, the volume, the widget's place, light or dark.
        if (_hover != WidgetPart.None && _flyout is { Showing: true }) ShowTip();

        if (!IsAttached || !_shown || _bounds.Width <= 0 || _bounds.Height <= 0) return;

        float s = _scale;
        bool light = Glyphs.IsLightTaskbar();
        bool hasSession = _media.HasSession;
        var info = InfoState();
        var strip = _placement.HiddenStrip;
        strip.Offset(-_bounds.X, -_bounds.Y);
        string buttons = $"{(!hasSession || _media.CanPrevious ? 1 : 0)}{(_media.IsPlaying ? Glyphs.Pause : Glyphs.Play)}"
            + $"{(!hasSession || _media.CanNext ? 1 : 0)}{(ShowsPlaylistButton ? 1 : 0)}{Glyphs.ForVolume(_volume, _muted)}";
        // Texts that are not drawn must not cause drawing: the time under the mouse changes with every move.
        bool texts = _showInfo && _textWidth > 0;
        var still = new Still(_bounds.Size, s, light, _hover, _pressed == _hover, _showInfo, _fit, _textWidth,
            _buttonWidth, _media.Cover, texts ? info.Title : "", texts ? info.Subtitle : null, info.HasProgress, buttons, strip);
        float line = _column is WidgetColumn column ? column.Progress.Width : texts ? _textWidth - 4 * s : 0;
        // Whole pixels: the line then changes, and is drawn, only when it grows by one.
        Bar? bar = info.HasProgress && _showInfo && line > 0
            ? new Bar((int)MathF.Round(line * info.Fraction), info.Seeking)
            : null;

        try
        {
            switch (_frame.Decide(still, bar))
            {
                case Redraw.Whole:
                    DrawStill(info, light, strip);
                    Compose(bar, light, strip, whole: true);
                    UpdateLayered(Handle, _picture!);
                    break;

                case Redraw.BarOnly:
                    var drawn = Compose(bar, light, strip, whole: false);
                    UpdateLayered(Handle, _picture!, drawn);
                    break;
            }
        }
        catch
        {
            // What was decided did not reach the screen: the next call draws the whole.
            _frame.Forget();
            throw;
        }
    }

    private static (Color Ink, Color Dim, Color Faint) Inks(bool light)
    {
        Color ink = light ? Color.FromArgb(235, 0, 0, 0) : Color.White;
        return (ink, Color.FromArgb(light ? 150 : 170, ink), Color.FromArgb(light ? 60 : 70, ink));
    }

    // The texts of the song part and the state of its progress line, as they are right now.
    private Info InfoState()
    {
        string title;
        string? subtitle;
        if (_media.HasSession)
        {
            title = string.IsNullOrWhiteSpace(_media.Title) ? _media.SourceName ?? Strings.Unknown : _media.Title!;
            subtitle = string.IsNullOrWhiteSpace(_media.Artist) ? _media.SourceName : _media.Artist;
            if (_flashText is not null && DateTime.UtcNow < _flashUntil) subtitle = _flashText;
        }
        else
        {
            title = StatusText ?? Strings.NothingPlaying;
            subtitle = StatusText is null ? Strings.ClickToStartApp : null;
        }

        bool hasProgress = _media.HasSession && _media.Duration > TimeSpan.Zero;
        bool seeking = hasProgress && _media.CanSeek && (_seekFraction is not null || _hover == WidgetPart.Progress);
        if (seeking)
        {
            // While pointing at the timeline, the second line shows the time under the cursor.
            float at = _seekFraction ?? FractionAt(_mouseX);
            subtitle = $"{Strings.Time(_media.Duration * at)} / {Strings.Time(_media.Duration)}";
        }
        float fraction = hasProgress
            ? Math.Clamp(_seekFraction ?? (float)(_media.Position.TotalSeconds / _media.Duration.TotalSeconds), 0f, 1f)
            : 0f;
        return new Info(title, subtitle, hasProgress, seeking, fraction);
    }

    // Everything but the progress line, kept so that a moving line does not cost drawing it again.
    private void DrawStill(Info info, bool light, Rectangle strip)
    {
        if (_still is null || _still.Size != _bounds.Size)
        {
            _still?.Dispose();
            _still = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        }
        var (fg, fgDim, fgFaint) = Inks(light);
        using var g = Graphics.FromImage(_still);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Alpha 1 keeps the whole widget clickable while being visually transparent.
        g.Clear(Color.FromArgb(1, 0, 0, 0));

        DrawHover(g, light);
        _progressRect = RectangleF.Empty;
        if (_showInfo) DrawInfo(g, _still, info, fg, fgDim, fgFaint);

        bool hasSession = _media.HasSession;
        DrawGlyph(_still, WidgetPart.Previous, Glyphs.Previous, !hasSession || _media.CanPrevious, fg);
        DrawGlyph(_still, WidgetPart.PlayPause, _media.IsPlaying ? Glyphs.Pause : Glyphs.Play, true, fg);
        DrawGlyph(_still, WidgetPart.Next, Glyphs.Next, !hasSession || _media.CanNext, fg);
        DrawGlyph(_still, WidgetPart.AddToPlaylist, Glyphs.AddToPlaylist, true, fg);
        DrawGlyph(_still, WidgetPart.Volume, Glyphs.ForVolume(_volume, _muted), true, fg);
        ClearStrip(g, strip);
    }

    // Where a hidden taskbar stays on screen the mouse must reach the taskbar, not us: fully transparent pixels
    // let it through.
    private static void ClearStrip(Graphics g, Rectangle strip)
    {
        if (strip.IsEmpty) return;
        g.SetClip(strip, CombineMode.Intersect);
        g.Clear(Color.FromArgb(0, 0, 0, 0));
    }

    /// <summary>
    /// Lays the progress line over the still picture into the picture for the window: all of it, or only the strip
    /// the line lives in. Returns what it wrote.
    /// </summary>
    private Rectangle Compose(Bar? bar, bool light, Rectangle strip, bool whole)
    {
        var all = new Rectangle(Point.Empty, _still!.Size);
        if (_picture is null || _picture.Size != _still.Size)
        {
            _picture?.Dispose();
            _picture = new Bitmap(_still.Width, _still.Height, PixelFormat.Format32bppArgb);
            whole = true;
        }
        float s = _scale;
        // The line, twice as thick under the mouse, and the knob it then gets.
        float reach = 5 * s + 1;
        float middle = _progressRect.Top + 1 * s;
        var area = whole
            ? all
            : Rectangle.Intersect(all, Rectangle.FromLTRB((int)MathF.Floor(_progressRect.Left - reach), (int)MathF.Floor(middle - reach),
                (int)MathF.Ceiling(_progressRect.Right + reach), (int)MathF.Ceiling(middle + reach)));

        using var g = Graphics.FromImage(_picture);
        g.SetClip(area);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(_still, 0, 0);
        g.CompositingMode = CompositingMode.SourceOver;
        if (bar is not null && !_progressRect.IsEmpty)
        {
            var (fg, fgDim, fgFaint) = Inks(light);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float thickness = bar.Seeking ? 4 * s : 2 * s;
            float top = middle - thickness / 2;
            using var track = new SolidBrush(fgFaint);
            using var fill = new SolidBrush(bar.Seeking ? fg : fgDim);
            g.FillRectangle(track, _progressRect.Left, top, _progressRect.Width, thickness);
            g.FillRectangle(fill, _progressRect.Left, top, bar.Fill, thickness);
            if (bar.Seeking)
            {
                float r = 5 * s;
                g.FillEllipse(fill, _progressRect.Left + bar.Fill - r, middle - r, 2 * r, 2 * r);
            }
        }
        ClearStrip(g, strip);
        return area;
    }

    private void DrawHover(Graphics g, bool light)
    {
        if (_hover is WidgetPart.None or WidgetPart.Progress) return;
        RectangleF rect = _hover == WidgetPart.Info ? _infoRect : _buttonRects.GetValueOrDefault(_hover);
        if (rect.IsEmpty) return;

        float s = _scale;
        if (_column is null)
        {
            float inset = Math.Max(4 * s, (rect.Height - 40 * s) / 2);
            rect = RectangleF.FromLTRB(rect.Left + 1 * s, rect.Top + inset, rect.Right - 1 * s, rect.Bottom - inset);
        }
        else
        {
            float inset = Math.Max(4 * s, (rect.Width - 40 * s) / 2);
            rect = RectangleF.FromLTRB(rect.Left + inset, rect.Top + 1 * s, rect.Right - inset, rect.Bottom - 1 * s);
        }
        int alpha = _pressed == _hover ? 14 : 24;
        using var brush = new SolidBrush(light ? Color.FromArgb(alpha, 0, 0, 0) : Color.FromArgb(alpha, 255, 255, 255));
        using var path = Glyphs.RoundedRect(rect, 4 * s);
        g.FillPath(brush, path);
    }

    private void DrawInfo(Graphics g, Bitmap bmp, Info info, Color fg, Color fgDim, Color fgFaint)
    {
        float s = _scale;
        float h = _bounds.Height;
        float x = 4 * s;

        if (_fit.Cover)
        {
            float size = CoverSize(_bounds.Height);
            var rect = _column?.Cover ?? new RectangleF(x, (h - size) / 2, size, size);
            using var path = Glyphs.RoundedRect(rect, 4 * s);
            var cover = _media.Cover;
            if (cover is not null)
            {
                var state = g.Save();
                g.SetClip(path);
                int side = Math.Min(cover.Width, cover.Height);
                var src = new Rectangle((cover.Width - side) / 2, (cover.Height - side) / 2, side, side);
                g.DrawImage(cover, rect, src, GraphicsUnit.Pixel);
                g.Restore(state);
            }
            else
            {
                using var bg = new SolidBrush(fgFaint);
                g.FillPath(bg, path);
                DrawCenteredGlyph(bmp, Glyphs.MusicNote, rect, IconSize, fgDim);
            }
            x += size + 8 * s;
        }

        if (_column is WidgetColumn column)
        {
            if (info.HasProgress) _progressRect = column.Progress;
            return;
        }
        if (_textWidth <= 0) return;

        string title = info.Title;
        string? subtitle = info.Subtitle;
        bool hasProgress = info.HasProgress;

        string face = GdiText.UiFace;
        int textSize = (int)MathF.Round(12 * s);
        int titleH = GdiText.LineHeight(face, textSize, semibold: true);
        int subH = subtitle is null ? 0 : GdiText.LineHeight(face, textSize, semibold: false);
        float progressH = hasProgress ? 6 * s : 0;
        // Whole pixels, as the text is drawn on them.
        int left = (int)MathF.Round(x);
        int width = (int)_textWidth;
        float y = MathF.Round((h - titleH - subH - progressH) / 2);

        GdiText.Draw(bmp, title, face, textSize, semibold: true, new Rectangle(left, (int)y, width, titleH), fg, centered: false);
        y += titleH;
        if (subtitle is not null)
        {
            GdiText.Draw(bmp, subtitle, face, textSize, semibold: false, new Rectangle(left, (int)y, width, subH), fgDim, centered: false);
            y += subH;
        }

        // Where the progress line goes; it is laid over the finished picture.
        if (hasProgress) _progressRect = new RectangleF(x + 1 * s, y + 3 * s, _textWidth - 4 * s, 2 * s);
    }

    private float FractionAt(float x) =>
        _progressRect.Width <= 0 ? 0f : Math.Clamp((x - _progressRect.Left) / _progressRect.Width, 0f, 1f);

    // The icon font is drawn for a 16-pixel grid: at other sizes its lines blur.
    private int IconSize => (int)MathF.Round(16 * _scale);

    private void DrawGlyph(Bitmap bmp, WidgetPart part, string glyph, bool enabled, Color fg)
    {
        if (!_buttonRects.TryGetValue(part, out var rect)) return;
        DrawCenteredGlyph(bmp, glyph, rect, IconSize, enabled ? fg : Color.FromArgb(80, fg));
    }

    private static void DrawCenteredGlyph(Bitmap bmp, string glyph, RectangleF rect, int pixelSize, Color color) =>
        GdiText.Draw(bmp, glyph, Glyphs.FontFamilyName, pixelSize, semibold: false, Rectangle.Round(rect), color, centered: true);

    // ---------------------------------------------------------------- input

    private WidgetPart HitTest(Point p)
    {
        if (!_progressRect.IsEmpty && _media.CanSeek)
        {
            // The bar is only 2px high; give it a comfortable hit area. In a row it reaches to the widget's edge,
            // in a column the buttons follow below.
            float s = _scale;
            float below = _column is null ? Math.Max(_progressRect.Bottom + 8 * s, _bounds.Height - 4 * s) : _progressRect.Bottom + 6 * s;
            var hit = RectangleF.FromLTRB(_progressRect.Left - 3 * s, _progressRect.Top - 6 * s, _progressRect.Right + 3 * s, below);
            if (hit.Contains(p)) return WidgetPart.Progress;
        }
        if (_showInfo && _infoRect.Contains(p)) return WidgetPart.Info;
        foreach (var (part, rect) in _buttonRects)
        {
            if (rect.Contains(p)) return part;
        }
        return WidgetPart.None;
    }

    private static Point PointFromLParam(IntPtr lParam)
    {
        long v = lParam.ToInt64();
        return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }

    private Rectangle PartScreenRect(WidgetPart part) =>
        ScreenRect(part == WidgetPart.Info ? _infoRect : _buttonRects.GetValueOrDefault(part));

    private Rectangle ScreenRect(RectangleF rect)
    {
        if (!GetWindowRect(Handle, out var window)) return Rectangle.Empty;
        return new Rectangle(window.Left + (int)rect.X, window.Top + (int)rect.Y, (int)rect.Width, (int)rect.Height);
    }

    /// <summary>The parts as assistive tools are told about them, in the order they are drawn.</summary>
    private IReadOnlyList<AccessiblePart> DescribeParts()
    {
        var parts = new List<AccessiblePart>();
        if (!_shown) return parts;

        bool session = _media.HasSession;
        if (_showInfo)
        {
            string title = string.IsNullOrWhiteSpace(_media.Title) ? _media.SourceName ?? Strings.Unknown : _media.Title!;
            string name = !session ? StatusText ?? Strings.NothingPlaying
                : string.IsNullOrWhiteSpace(_media.Artist) ? title : $"{title} – {_media.Artist}";
            string? description = session ? Strings.SourceLabel(_media.SourceName)
                : StatusText is null ? Strings.ClickToStartApp : null;
            parts.Add(new(WidgetPart.Info, AccessibleRole.PushButton, name, null, description, PartScreenRect(WidgetPart.Info), true));
            if (!_progressRect.IsEmpty)
            {
                parts.Add(new(WidgetPart.Progress, AccessibleRole.ProgressBar, Strings.Timeline,
                    $"{Strings.Time(_media.Position)} / {Strings.Time(_media.Duration)}", null, ScreenRect(_progressRect), true));
            }
        }
        foreach (var part in _fit.Buttons)
        {
            (string name, string? value, bool enabled) = part switch
            {
                WidgetPart.Previous => (Strings.Previous, null, !session || _media.CanPrevious),
                WidgetPart.Next => (Strings.Next, null, !session || _media.CanNext),
                WidgetPart.PlayPause => (!session ? Strings.StartMusicApp : _media.IsPlaying ? Strings.Pause : Strings.Play, null, true),
                WidgetPart.AddToPlaylist => (Strings.AddToPlaylistTip, null, true),
                _ => (Strings.Volume, _muted ? Strings.Muted : $"{(int)Math.Round(_volume * 100)}%", true),
            };
            parts.Add(new(part, AccessibleRole.PushButton, name, value, null, PartScreenRect(part), enabled));
        }
        return parts;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;

            case WM_GETOBJECT:
                if (_accessible.Answer(m.WParam, m.LParam) is IntPtr answer)
                {
                    m.Result = answer;
                    return;
                }
                break;

            case WM_SETCURSOR:
                // Nobody else sets one here: without this the previous window's pointer stays.
                SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                m.Result = 1;
                return;

            case WM_MOUSEMOVE:
                if (!_trackingMouse)
                {
                    TrackMouseLeave(Handle);
                    _trackingMouse = true;
                }
            {
                var p = PointFromLParam(m.LParam);
                _mouseX = p.X;
                if (_seekFraction is not null)
                {
                    _seekFraction = FractionAt(p.X);
                    Render();
                    return;
                }
                var part = HitTest(p);
                if (part == WidgetPart.Progress && _hover == WidgetPart.Progress)
                {
                    Render(); // time under the cursor follows the mouse
                    return;
                }
                SetHover(part);
                return;
            }

            case WM_MOUSELEAVE:
                _trackingMouse = false;
                if (_seekFraction is not null) return; // dragging with capture
                _pressed = WidgetPart.None;
                SetHover(WidgetPart.None);
                return;

            case WM_LBUTTONDOWN:
            {
                var p = PointFromLParam(m.LParam);
                _pressed = HitTest(p);
                HideTip();
                if (_pressed == WidgetPart.Progress)
                {
                    SetCapture(Handle);
                    _seekFraction = FractionAt(p.X);
                }
                Render();
                return;
            }

            case WM_LBUTTONUP:
            {
                if (_seekFraction is float target)
                {
                    _seekFraction = FractionAt(PointFromLParam(m.LParam).X);
                    target = _seekFraction.Value;
                    ReleaseCapture();
                    _pressed = WidgetPart.None;
                    SeekRequested?.Invoke(target);
                    _seekFraction = null;
                    SetHover(HitTest(PointFromLParam(m.LParam)));
                    Render();
                    return;
                }
                var part = HitTest(PointFromLParam(m.LParam));
                bool click = part != WidgetPart.None && part == _pressed;
                _pressed = WidgetPart.None;
                Render();
                if (click) PartClicked?.Invoke(part, PartScreenRect(part));
                return;
            }

            case WM_RBUTTONUP:
            {
                HideTip();
                var p = PointFromLParam(m.LParam);
                GetWindowRect(Handle, out var window);
                ContextMenuRequested?.Invoke(new Point(window.Left + p.X, window.Top + p.Y));
                return;
            }

            case WM_MOUSEWHEEL:
                WheelScrolled?.Invoke((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    private void SetHover(WidgetPart part)
    {
        if (part == _hover) return;
        _hover = part;
        HideTip();
        if (part != WidgetPart.None) _hoverTimer.Start();
        Render();
    }

    private void ShowHoverTip()
    {
        _hoverTimer.Stop();
        ShowTip();
    }

    // What is up and where, to tell when it has to be shown anew.
    private sealed record Tip(object Content, bool Light, float Scale, Point Anchor, TaskbarEdge Edge, Rectangle Taskbar);

    /// <summary>
    /// Shows the tip or the song card of the part under the mouse, or brings a shown one up to date with what it
    /// tells about, with the widget's place and with light or dark.
    /// </summary>
    private void ShowTip()
    {
        if (!GetWindowRect(Handle, out var window)) return;
        bool light = Glyphs.IsLightTaskbar();
        var taskbar = TrayScreenBounds;
        try
        {
            // The song part tells of the song while there is one and gives a hint while there is none. Either
            // stays while the mouse rests there, so a song that comes (back) is shown.
            bool onSong = _hover == WidgetPart.Info;
            object? content = onSong && _media.HasSession ? CurrentSong() : HintText();
            if (content is null) return;

            // Each tip by its own part: the seek hint by the timeline, not by the cover.
            RectangleF rect = _hover switch
            {
                WidgetPart.Info => _infoRect,
                WidgetPart.Progress => _progressRect,
                _ => _buttonRects.GetValueOrDefault(_hover),
            };
            var anchor = new Point(window.Left + (int)rect.X, window.Top + (int)rect.Y);
            var tip = new Tip(content, light, _scale, anchor, TaskbarEdgeNow(taskbar), taskbar);
            if (tip == _tip && _flyout is { Showing: true }) return;

            Bitmap picture = content is SongDetails song
                ? SongCard.Render(song, _scale, light)
                : HoverTip.Render((string)content, _scale, light);
            Flyout.DisplayScale = _scale;
            // As far from the taskbar as its own tooltips are.
            Flyout.ShowPicture(picture, tip.Anchor, tip.Edge, taskbar, (int)(13 * _scale), light, smallCorners: content is string,
                durationMs: onSong ? 0 : 4000);
            // Only now: what failed to show must not count as shown.
            _tip = tip;
        }
        catch (Exception ex)
        {
            // Tooltips are a nicety.
            if (!_tipFailureLogged) Log.Write($"Tooltip failed: {ex.Message}");
            _tipFailureLogged = true;
        }
    }

    /// <summary>The edge of the screen the taskbar is on: as known for it, else as its window lies.</summary>
    public TaskbarEdge TaskbarEdgeNow(Rectangle taskbar) =>
        _placement.Identity?.Edge ?? TaskbarIdentity.EdgeOf(taskbar, Screen.FromRectangle(taskbar).Bounds);

    private string? HintText() => _hover switch
    {
        WidgetPart.Info => Strings.ClickToStartApp,
        WidgetPart.Progress => Strings.ClickOrDragToSeek,
        WidgetPart.Previous => Strings.Previous,
        WidgetPart.PlayPause => !_media.HasSession ? Strings.StartMusicApp : _media.IsPlaying ? Strings.Pause : Strings.Play,
        WidgetPart.Next => Strings.Next,
        WidgetPart.AddToPlaylist => Strings.AddToPlaylistTip,
        WidgetPart.Volume => _muted ? Strings.Muted : Strings.VolumeTip((int)Math.Round(_volume * 100)),
        _ => null,
    };

    private FlyoutWindow Flyout => _flyout is { IsDisposed: false } ? _flyout : _flyout = new FlyoutWindow();

    private SongDetails CurrentSong() => new(
        string.IsNullOrWhiteSpace(_media.Title) ? _media.SourceName ?? Strings.Unknown : _media.Title!,
        _media.Artist, _media.Album, _media.TrackNumber, _media.TrackCount,
        // Whole seconds, as shown: a change below that is no reason to redraw the card.
        TimeSpan.FromSeconds(Math.Floor(_media.Duration.TotalSeconds)), _media.SourceName, _media.Cover);

    private void HideTip()
    {
        _hoverTimer.Stop();
        _flyout?.HideNow();
        _tip = null;
    }

    public void Dispose()
    {
        _hoverTimer.Dispose();
        _watcher.Dispose();
        _flyout?.Dispose();
        _still?.Dispose();
        _picture?.Dispose();
        if (Handle != IntPtr.Zero)
        {
            if (IsWindow(Handle)) DestroyHandle(); else ReleaseHandle();
        }
    }
}
