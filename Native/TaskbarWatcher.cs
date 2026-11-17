using System.Runtime.InteropServices;

namespace TaskbarPlayer.Native;

/// <summary>
/// Tells when Explorer rearranges one of a taskbar's windows, as it does when an app icon comes or goes. Explorer
/// raises the event by itself; listening costs nothing while nothing happens.
/// <para>
/// With several displays Explorer raises it on the main taskbar's window only, also for an icon on another
/// display's taskbar: so the main taskbar's signs count for every taskbar.
/// </para>
/// </summary>
internal sealed class TaskbarWatcher : IDisposable
{
    private const uint EVENT_OBJECT_REORDER = 0x8004;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x2;

    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    private readonly Action _changed;
    // Held here: Windows keeps calling it for as long as the hook is set.
    private readonly WinEventProc _callback;
    private IntPtr _hook;
    private IntPtr _tray;
    private IntPtr _mainTray;

    public TaskbarWatcher(Action changed)
    {
        _changed = changed;
        _callback = OnEvent;
    }

    /// <summary>
    /// Watches this taskbar from now on, in place of any other. To be called on a thread with a message loop: the
    /// events arrive there. False when Windows refused.
    /// </summary>
    public bool Watch(IntPtr tray)
    {
        Stop();
        if (tray == IntPtr.Zero || GetWindowThreadProcessId(tray, out uint process) == 0) return false;

        _tray = tray;
        _mainTray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        _hook = SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER, IntPtr.Zero, _callback, process, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        return _hook != IntPtr.Zero;
    }

    private void OnEvent(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Explorer has other windows too: only the taskbars' own count.
        if (Belongs(hwnd, _tray) || Belongs(hwnd, _mainTray)) _changed();
    }

    private static bool Belongs(IntPtr hwnd, IntPtr tray) => tray != IntPtr.Zero && (hwnd == tray || IsChild(tray, hwnd));

    private void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _tray = IntPtr.Zero;
        _mainTray = IntPtr.Zero;
    }

    public void Dispose() => Stop();
}
