using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using static TaskbarPlayer.Native.NativeMethods;

namespace TaskbarPlayer;

internal enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Where the taskbar puts its app icons (the "Taskbar alignment" setting of Windows 11).</summary>
internal enum IconAlignment
{
    Centre,
    Left,
}

/// <summary>
/// What a measured layout belongs to: the monitor, the edge the taskbar sits on, the taskbar's size and scale, and
/// where it puts its app icons. Where the taskbar is does not count: an auto-hiding one slides off the screen and
/// back. Edge and icon alignment are read where Windows keeps the settings, so they are known, not inferred from
/// what was measured.
/// </summary>
internal sealed record TaskbarIdentity(string Monitor, TaskbarEdge Edge, Size TaskbarSize, uint Dpi, IconAlignment Icons)
{
    public static TaskbarIdentity? FromTaskbar(IntPtr tray)
    {
        if (!GetWindowRect(tray, out var rect)) return null;
        var taskbar = rect.ToRectangle();
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        object? location = key?.GetValue("TaskbarLocation");
        var screen = ScreenOf(tray, taskbar, location);

        // The interface path names the monitor itself; \\.\DISPLAY1 can be handed to another one.
        var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevices(screen.DeviceName, 0, ref device, EDD_GET_DEVICE_INTERFACE_NAME)) return null;
        if (string.IsNullOrEmpty(device.DeviceID)) return null;

        return Of(device.DeviceID, taskbar, screen.Bounds, GetDpiForWindow(tray), IconAlignmentOf(key?.GetValue("TaskbarAl")), location);
    }

    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>The display a taskbar is on.</summary>
    public static Screen ScreenOf(IntPtr tray)
    {
        if (!GetWindowRect(tray, out var rect)) return Screen.FromHandle(tray);
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return ScreenOf(tray, rect.ToRectangle(), key?.GetValue("TaskbarLocation"));
    }

    private static Screen ScreenOf(IntPtr tray, Rectangle taskbar, object? taskbarLocation) =>
        InnerPoint(taskbar, taskbarLocation) is Point inner ? Screen.FromPoint(inner) : Screen.FromHandle(tray);

    /// <summary>
    /// A point of the taskbar that is on its own display wherever it rests. A hidden auto-hiding taskbar lies
    /// mostly beyond its display's edge, where another display may be; the side of it that faces the screen
    /// never does. Which side that is follows from the edge the registry names; none without that.
    /// </summary>
    internal static Point? InnerPoint(Rectangle taskbar, object? taskbarLocation) => taskbarLocation switch
    {
        0 => new Point(taskbar.Right - 1, taskbar.Top + taskbar.Height / 2),
        1 => new Point(taskbar.Left + taskbar.Width / 2, taskbar.Bottom - 1),
        2 => new Point(taskbar.Left, taskbar.Top + taskbar.Height / 2),
        3 => new Point(taskbar.Left + taskbar.Width / 2, taskbar.Top),
        _ => null,
    };

    internal static TaskbarIdentity Of(string monitor, Rectangle taskbar, Rectangle screen, uint dpi, IconAlignment icons,
        object? taskbarLocation = null) =>
        new(monitor, EdgeOf(taskbarLocation, taskbar, screen), taskbar.Size, dpi, icons);

    /// <summary>The meaning of the registry value: 0 is left; 1, or no value at all, is centred.</summary>
    internal static IconAlignment IconAlignmentOf(object? taskbarAl) =>
        taskbarAl is int value && value == 0 ? IconAlignment.Left : IconAlignment.Centre;

    /// <summary>
    /// The edge as the registry value names it (0 left, 1 top, 2 right, 3 bottom), when the window lies that way.
    /// Without a value, or with one that cannot be true of the window on screen, the window decides.
    /// </summary>
    internal static TaskbarEdge EdgeOf(object? taskbarLocation, Rectangle taskbar, Rectangle screen)
    {
        var seen = EdgeOf(taskbar, screen);
        TaskbarEdge? named = taskbarLocation switch
        {
            0 => TaskbarEdge.Left,
            1 => TaskbarEdge.Top,
            2 => TaskbarEdge.Right,
            3 => TaskbarEdge.Bottom,
            _ => null,
        };
        return named is TaskbarEdge edge && IsSide(edge) == IsSide(seen) ? edge : seen;
    }

    private static bool IsSide(TaskbarEdge edge) => edge is TaskbarEdge.Left or TaskbarEdge.Right;

    // By the centres, so that a taskbar sliding off its edge keeps that edge.
    internal static TaskbarEdge EdgeOf(Rectangle taskbar, Rectangle screen)
    {
        if (taskbar.Width >= taskbar.Height)
        {
            return taskbar.Top + taskbar.Height / 2 < screen.Top + screen.Height / 2 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }
        return taskbar.Left + taskbar.Width / 2 < screen.Left + screen.Width / 2 ? TaskbarEdge.Left : TaskbarEdge.Right;
    }
}

/// <summary>
/// Measured layouts of the taskbars the widget has been on, most recently used first, so that the next start can
/// place the widget before the first measurement arrives.
/// </summary>
internal sealed class LayoutCache
{
    public const int Capacity = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly List<(TaskbarIdentity Identity, TaskbarLayout Layout)> _entries = new();
    private bool _failureLogged;

    public LayoutCache(string filePath)
    {
        _filePath = filePath;
        Read();
    }

    public static LayoutCache Load() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarPlayer", "layouts.json"));

    public IReadOnlyList<TaskbarIdentity> Identities => _entries.Select(e => e.Identity).ToList();

    public TaskbarLayout? Find(TaskbarIdentity identity)
    {
        int index = _entries.FindIndex(e => e.Identity == identity);
        return index < 0 ? null : _entries[index].Layout;
    }

    /// <summary>Puts the layout on top of the list and writes the file, unless it is there already.</summary>
    public void Remember(TaskbarIdentity identity, TaskbarLayout layout)
    {
        int index = _entries.FindIndex(e => e.Identity == identity);
        if (index == 0 && _entries[0].Layout == layout) return;

        if (index >= 0) _entries.RemoveAt(index);
        _entries.Insert(0, (identity, layout));
        if (_entries.Count > Capacity) _entries.RemoveRange(Capacity, _entries.Count - Capacity);
        Save();
    }

    private void Read()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var saved = JsonSerializer.Deserialize<List<SavedEntry>>(File.ReadAllText(_filePath), JsonOptions);
            foreach (var entry in saved ?? [])
            {
                if (entry?.Monitor is null || entry.TaskbarSize is null || entry.Icons is null || entry.Band is null
                    || entry.GapBefore is null || entry.GapAfter is null)
                {
                    continue;
                }
                if (_entries.Count == Capacity) break;
                var size = new Size(entry.TaskbarSize.Width, entry.TaskbarSize.Height);
                var identity = new TaskbarIdentity(entry.Monitor, entry.Edge, size, entry.Dpi, entry.Icons.Value);
                var layout = new TaskbarLayout(entry.Band.ToRectangle(), entry.GapBefore.ToRectangle(), entry.GapAfter.ToRectangle());
                if (IsPlausible(size, layout)) _entries.Add((identity, layout));
            }
        }
        catch (Exception ex)
        {
            _entries.Clear();
            LogFailure($"Remembered layouts unreadable, starting empty: {ex.Message}");
        }
    }

    // The widget's size comes from these numbers and it lives inside the taskbar: an edited file must not make
    // anything larger than what contains it.
    private static bool IsPlausible(Size taskbar, TaskbarLayout layout)
    {
        var band = layout.Band;
        return band.Width > 0 && band.Height > 0
            && band.Width <= taskbar.Width && band.Height <= taskbar.Height
            && new Rectangle(Point.Empty, taskbar).IntersectsWith(band)
            && Fits(layout.GapBefore, band) && Fits(layout.GapAfter, band);
    }

    private static bool Fits(Rectangle gap, Rectangle band) =>
        gap.Width >= 0 && gap.Width <= band.Width && gap.Height >= 0 && gap.Height <= band.Height;

    private void Save()
    {
        try
        {
            var saved = _entries.Select(e => new SavedEntry(
                e.Identity.Monitor, e.Identity.Edge,
                new SavedSize(e.Identity.TaskbarSize.Width, e.Identity.TaskbarSize.Height), e.Identity.Dpi, e.Identity.Icons,
                SavedRect.From(e.Layout.Band), SavedRect.From(e.Layout.GapBefore), SavedRect.From(e.Layout.GapAfter)));

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            // Replace in one step: a write cut short at shutdown must not leave half a file.
            string temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved, JsonOptions));
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            LogFailure($"Remembered layouts not saved: {ex.Message}");
        }
    }

    private void LogFailure(string message)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        Log.Write(message);
    }

    private sealed record SavedRect(int Left, int Top, int Right, int Bottom)
    {
        public static SavedRect From(Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);

        public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    private sealed record SavedSize(int Width, int Height);

    private sealed record SavedEntry(
        string? Monitor, TaskbarEdge Edge, SavedSize? TaskbarSize, uint Dpi, IconAlignment? Icons, SavedRect? Band, SavedRect? GapBefore,
        SavedRect? GapAfter);
}
