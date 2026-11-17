using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarPlayer;

/// <summary>What tells whose a top-level window is.</summary>
/// <param name="Process">The name of its program file, without ".exe".</param>
/// <param name="WindowAppId">The app id the window names itself: browsers and the frames of Store apps do.</param>
/// <param name="PackageAppId">The app id of the package its process runs from, for packaged apps.</param>
/// <param name="Cloaked">Not shown although visible: on another desktop, or hosted inside a frame window.</param>
internal readonly record struct WindowFacts(IntPtr Handle, string Process, string? WindowAppId, string? PackageAppId, bool Cloaked);

/// <summary>
/// Brings the app that plays to the front. Windows names the source of a media session by an app id; which window
/// belongs to it has to be worked out.
/// </summary>
internal static class SourceWindow
{
    private const string AppsFolder = "shell:AppsFolder";

    /// <summary>
    /// The window of the app with this id among <paramref name="windows"/> (topmost first), or zero. The most
    /// certain sign wins: the window names the app itself, then its package does, then the id is the name of the
    /// window's program file, then the Start menu entry with that id starts that program file
    /// (<paramref name="startMenuTarget"/>). Of equals, one that shows before a cloaked one, then the topmost.
    /// </summary>
    internal static IntPtr Pick(string appId, IReadOnlyList<WindowFacts> windows, string? startMenuTarget)
    {
        string? named = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(appId) : null;
        string? started = string.IsNullOrEmpty(startMenuTarget) ? null : Path.GetFileNameWithoutExtension(startMenuTarget);

        IntPtr best = IntPtr.Zero;
        int bestRank = int.MaxValue;
        foreach (var window in windows)
        {
            int rank = Same(window.WindowAppId, appId) ? 0
                : Same(window.PackageAppId, appId) ? 2
                : Same(window.Process, named) ? 4
                : Same(window.Process, started) ? 6
                : -1;
            if (rank < 0) continue;
            if (window.Cloaked) rank++;
            if (rank < bestRank)
            {
                best = window.Handle;
                bestRank = rank;
            }
        }
        return best;
    }

    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Brings the app with this id to the front: its window when it has one, else by starting its Start menu
    /// entry (an app that runs already shows its window then). False when neither is possible.
    /// </summary>
    public static bool Show(string appId)
    {
        try
        {
            var windows = TopLevelWindows();
            IntPtr window = Pick(appId, windows, null);
            bool hasEntry = false;
            if (window == IntPtr.Zero)
            {
                // Asked only now: the shell takes a moment.
                (hasEntry, string? target) = StartMenuEntry(appId);
                window = Pick(appId, windows, target);
            }
            if (window != IntPtr.Zero)
            {
                // Asynchronous: an app that hangs must not hang us.
                if (IsIconic(window)) ShowWindowAsync(window, SW_RESTORE);
                SetForegroundWindow(window);
                return true;
            }
            if (!hasEntry) return false;

            // Quoted: an app id may contain spaces.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppsFolder}\\{appId}\"") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Showing the playing app failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether the Start menu has an entry with this app id, and the program file it starts (entries of packaged
    /// apps name none). Needs the single-threaded apartment of the UI thread.
    /// </summary>
    private static (bool Found, string? Target) StartMenuEntry(string appId)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return (false, null);

        object? shell = null, folder = null, item = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            folder = ((dynamic?)shell)?.NameSpace(AppsFolder);
            item = ((dynamic?)folder)?.ParseName(appId);
            if (item is null) return (false, null);
            return (true, ((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath") as string);
        }
        finally
        {
            foreach (object? held in new[] { item, folder, shell })
            {
                if (held is not null && Marshal.IsComObject(held)) Marshal.ReleaseComObject(held);
            }
        }
    }

    /// <summary>The top-level windows that can have a taskbar button, topmost first.</summary>
    internal static List<WindowFacts> TopLevelWindows()
    {
        var windows = new List<WindowFacts>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            // Hosted inside a frame window, which is the one that shows.
            if (className.ToString() == "Windows.UI.Core.CoreWindow") return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            var (process, package) = ProcessFacts(pid);
            if (process is not null) windows.Add(new WindowFacts(hwnd, process, WindowAppId(hwnd), package, cloaked != 0));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static (string? Name, string? PackageAppId) ProcessFacts(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return (null, null);
        try
        {
            var path = new StringBuilder(1024);
            uint pathLength = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref pathLength)) return (null, null);

            var id = new StringBuilder(256);
            uint idLength = (uint)id.Capacity;
            bool packaged = GetApplicationUserModelId(process, ref idLength, id) == 0;
            return (Path.GetFileNameWithoutExtension(path.ToString()), packaged ? id.ToString() : null);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string? WindowAppId(IntPtr hwnd)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
        try
        {
            var key = new PROPERTYKEY { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            if (store.GetValue(ref key, out var value) != 0) return null;
            try
            {
                return value.Type == VT_LPWSTR ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int SW_RESTORE = 9;
    private const ushort VT_LPWSTR = 31;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint relation);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(int access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);
}
