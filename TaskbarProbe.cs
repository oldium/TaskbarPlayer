using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// Free space in the taskbar, in physical pixels: the gaps before and after the app icons, counted from Start. On
/// a taskbar along the top or bottom edge they are left and right of the icons, on one along a side above and
/// below them. What <see cref="TaskbarProbe.Probe"/> returns is relative to the taskbar window's top-left corner,
/// as the widget's own position is.
/// </summary>
internal sealed record TaskbarLayout(Rectangle Band, Rectangle GapBefore, Rectangle GapAfter)
{
    /// <summary>A taskbar along a side of the screen: its parts follow each other from top to bottom.</summary>
    public bool Vertical => Band.Height > Band.Width;

    /// <summary>How much room a gap has along the taskbar.</summary>
    public int Room(Rectangle gap) => Vertical ? gap.Height : gap.Width;

    /// <summary>
    /// Where something that wants the Start end goes: the free space before the app icons, or, when that cannot
    /// hold <paramref name="needed"/> pixels (icons at that end), the free space after them.
    /// </summary>
    public Rectangle FirstGapWith(int needed) => Room(GapBefore) >= needed ? GapBefore : GapAfter;

    /// <summary>
    /// Whether something laid into <paramref name="gap"/> at its Start end (or at its other end) lies against the
    /// app icons. Where the icons end changes from run to run; where the taskbar's fixed parts are does not.
    /// </summary>
    public bool AgainstIcons(Rectangle gap, bool fromStart) => fromStart ? gap == GapAfter : gap == GapBefore;
}

internal readonly record struct TaskbarElement(string ClassName, Rectangle Bounds, string AutomationId = "");

/// <summary>
/// Reads the real button positions of the Windows 11 taskbar through UI Automation. The XAML taskbar draws its
/// icons itself, so the classic child windows (ReBarWindow32 etc.) no longer report where the icons are.
/// Call from a background thread: a query waits for Explorer, up to the two limits. No layout means that the
/// taskbar could not be read this time.
/// </summary>
internal static class TaskbarProbe
{
    public const uint ConnectTimeoutMs = 2000;
    public const uint QueryTimeoutMs = 5000;

    private const string SurfaceClass = "Windows.UI.Composition.DesktopWindowContentBridge";
    private const string FrameClass = "Taskbar.TaskbarFrameAutomationPeer";
    private const string TaskButtonPrefix = "Taskbar.TaskListButton";
    private const string StartButtonId = "StartButton";

    private const int BoundingRectangleProperty = 30001;
    private const int ControlTypeProperty = 30003;
    private const int ClassNameProperty = 30012;
    private const int AutomationIdProperty = 30011;
    private const int ButtonControlType = 50000;
    private const int TreeScopeDescendants = 4;
    private const int ElementModeNone = 0;
    private const int TimeoutResult = unchecked((int)0x80131505);

    private static IUIAutomation2? s_automation;
    private static IUIAutomationCacheRequest? s_cache;
    private static IUIAutomationCondition? s_condition;

    private static (IntPtr Window, string Outcome)? s_logged;

    // Runs every tick: log only a changed outcome.
    private static TaskbarLayout? Report(IntPtr window, Stopwatch watch, TaskbarLayout? layout, string outcome, string detail)
    {
        if (s_logged != (window, outcome))
        {
            s_logged = (window, outcome);
            Log.Write($"Taskbar probe {outcome} after {watch.ElapsedMilliseconds} ms: {detail}");
        }
        return layout;
    }

    /// <summary>
    /// The taskbar that draws its own icons came with Windows 11 (build 22000). Older ones are classic: their
    /// child windows are the layout and there is nothing to probe.
    /// </summary>
    internal static bool HasXamlTaskbar(Version os) => os.Build >= 22000;

    public static TaskbarLayout? Probe(IntPtr tray)
    {
        // Querying the surface alone keeps our own widget window out of the walk.
        IntPtr surface = NativeMethods.FindDescendant(tray, SurfaceClass);
        if (surface == IntPtr.Zero)
        {
            return Report(tray, Stopwatch.StartNew(), null, "found no icon surface", "not created yet");
        }

        if (!NativeMethods.GetWindowRect(tray, out var before)) return null;
        var measured = Read(surface);
        if (measured is null || !NativeMethods.GetWindowRect(tray, out var after)) return null;
        return ToTaskbar(measured, before.ToRectangle(), after.ToRectangle());
    }

    /// <summary>
    /// Screen coordinates to coordinates inside the taskbar window. An auto-hiding taskbar slides: a measurement
    /// taken while it moved fits neither position, so there is none.
    /// </summary>
    internal static TaskbarLayout? ToTaskbar(TaskbarLayout measured, Rectangle taskbarBefore, Rectangle taskbarAfter)
    {
        if (taskbarBefore != taskbarAfter) return null;

        Rectangle Local(Rectangle r)
        {
            r.Offset(-taskbarBefore.Left, -taskbarBefore.Top);
            return r;
        }
        return new TaskbarLayout(Local(measured.Band), Local(measured.GapBefore), Local(measured.GapAfter));
    }

    /// <summary>The query itself. Its layout is in screen coordinates.</summary>
    internal static TaskbarLayout? Read(IntPtr surface)
    {
        var watch = Stopwatch.StartNew();
        IUIAutomationElement? root = null;
        IUIAutomationElementArray? found = null;
        try
        {
            var automation = Automation();
            int hr = automation.ElementFromHandle(surface, out root);
            if (hr != 0 || root is null) return Report(surface, watch, null, Describe(hr), "ElementFromHandle");

            hr = root.FindAllBuildCache(TreeScopeDescendants, s_condition!, s_cache!, out found);
            if (hr != 0) return Report(surface, watch, null, Describe(hr), "FindAllBuildCache");

            var elements = new List<TaskbarElement>();
            int count = 0;
            found?.get_Length(out count);
            for (int i = 0; i < count; i++)
            {
                if (found!.GetElement(i, out var element) != 0 || element is null) continue;
                element.GetCachedPropertyValue(BoundingRectangleProperty, out object? bounds);
                element.GetCachedPropertyValue(ClassNameProperty, out object? className);
                element.GetCachedPropertyValue(AutomationIdProperty, out object? automationId);
                Marshal.ReleaseComObject(element);
                // Left, top, width, height.
                if (bounds is not double[] { Length: 4 } r || r[2] <= 0 || r[3] <= 0) continue;
                elements.Add(new TaskbarElement(
                    className as string ?? "",
                    Rectangle.FromLTRB((int)r[0], (int)r[1], (int)(r[0] + r[2]), (int)(r[1] + r[3])),
                    automationId as string ?? ""));
            }

            var layout = Compute(elements);
            if (layout is null)
            {
                bool hasFrame = elements.Any(e => e.ClassName == FrameClass);
                return Report(surface, watch, null, "found no usable layout",
                    $"{elements.Count} elements, frame {(hasFrame ? "present" : "missing")}");
            }
            // Counted from the taskbar's own corner, as the widget's place is: a taskbar on another display
            // does not start at the screen's zero.
            int x = layout.Band.Left, y = layout.Band.Top;
            return Report(surface, watch, layout, "succeeded", layout.Vertical
                ? $"upper gap {layout.GapBefore.Top - y}..{layout.GapBefore.Bottom - y}, lower gap {layout.GapAfter.Top - y}..{layout.GapAfter.Bottom - y}"
                : $"left gap {layout.GapBefore.Left - x}..{layout.GapBefore.Right - x}, right gap {layout.GapAfter.Left - x}..{layout.GapAfter.Right - x}");
        }
        catch (Exception ex)
        {
            return Report(surface, watch, null, $"failed with {ex.GetType().Name}", ex.Message);
        }
        finally
        {
            if (found is not null) Marshal.ReleaseComObject(found);
            if (root is not null) Marshal.ReleaseComObject(root);
        }
    }

    private static string Describe(int hr) => hr == TimeoutResult ? "timed out" : $"failed with 0x{hr:X8}";

    private static IUIAutomation2 Automation()
    {
        if (s_automation is not null) return s_automation;

        var automation = (IUIAutomation2)new CUIAutomation8();
        Marshal.ThrowExceptionForHR(automation.put_ConnectionTimeout(ConnectTimeoutMs));
        Marshal.ThrowExceptionForHR(automation.put_TransactionTimeout(QueryTimeoutMs));

        Marshal.ThrowExceptionForHR(automation.CreateCacheRequest(out var cache));
        Marshal.ThrowExceptionForHR(cache.AddProperty(BoundingRectangleProperty));
        Marshal.ThrowExceptionForHR(cache.AddProperty(ClassNameProperty));
        Marshal.ThrowExceptionForHR(cache.AddProperty(AutomationIdProperty));
        // Cached values are all we read; no live element is needed.
        Marshal.ThrowExceptionForHR(cache.put_AutomationElementMode(ElementModeNone));

        Marshal.ThrowExceptionForHR(automation.CreatePropertyCondition(ControlTypeProperty, ButtonControlType, out var isButton));
        Marshal.ThrowExceptionForHR(automation.CreatePropertyCondition(ClassNameProperty, FrameClass, out var isFrame));
        Marshal.ThrowExceptionForHR(automation.CreateOrCondition(isButton, isFrame, out var condition));

        s_cache = cache;
        s_condition = condition;
        s_automation = automation;
        return automation;
    }

    /// <summary>
    /// The free gaps before and after the app icons, in the coordinates of the elements, or null when the
    /// elements do not describe them.
    /// </summary>
    internal static TaskbarLayout? Compute(IReadOnlyList<TaskbarElement> elements)
    {
        var frame = Rectangle.Empty;
        var buttons = new List<(Rectangle Rect, bool IsTask, bool IsStart)>();
        foreach (var element in elements)
        {
            if (element.ClassName == FrameClass)
            {
                frame = element.Bounds;
            }
            else
            {
                buttons.Add((element.Bounds, element.ClassName.StartsWith(TaskButtonPrefix, StringComparison.Ordinal),
                    element.AutomationId == StartButtonId));
            }
        }
        if (frame.IsEmpty) return null;

        // A side taskbar is computed lying down, and the result stood up again.
        if (frame.Height <= frame.Width) return ComputeLying(frame, buttons);
        var lying = ComputeLying(Turn(frame), buttons.Select(b => (Turn(b.Rect), b.IsTask, b.IsStart)).ToList());
        return lying is null ? null : new TaskbarLayout(Turn(lying.Band), Turn(lying.GapBefore), Turn(lying.GapAfter));
    }

    // Mirrors a rectangle over the diagonal: what runs down runs across, and back.
    private static Rectangle Turn(Rectangle r) => new(r.Y, r.X, r.Height, r.Width);

    private static TaskbarLayout? ComputeLying(Rectangle frame, List<(Rectangle Rect, bool IsTask, bool IsStart)> buttons)
    {
        buttons = buttons
            .Where(b => b.Rect.Bottom > frame.Top && b.Rect.Top < frame.Bottom)
            .OrderBy(b => b.Rect.Left)
            .ToList();
        int first = buttons.FindIndex(b => b.IsTask);
        int last = buttons.FindLastIndex(b => b.IsTask);
        // A taskbar without app icons (another display's, showing only its own windows): Start stands for them.
        if (first < 0) first = last = buttons.FindIndex(b => b.IsStart);
        if (first < 0) return null;

        // Grow the app group with adjacent buttons (Start, Search, Task view).
        const int touching = 4;
        while (first > 0 && buttons[first - 1].Rect.Right >= buttons[first].Rect.Left - touching) first--;
        while (last < buttons.Count - 1 && buttons[last + 1].Rect.Left <= buttons[last].Rect.Right + touching) last++;

        int groupLeft = buttons[first].Rect.Left;
        int groupRight = buttons.Skip(first).Take(last - first + 1).Max(b => b.Rect.Right);
        int leftBound = first > 0 ? buttons.Take(first).Max(b => b.Rect.Right) : frame.Left;
        int rightBound = last < buttons.Count - 1 ? buttons.Skip(last + 1).Min(b => b.Rect.Left) : frame.Right;

        return new TaskbarLayout(
            frame,
            Rectangle.FromLTRB(leftBound, frame.Top, Math.Max(leftBound, groupLeft), frame.Bottom),
            Rectangle.FromLTRB(groupRight, frame.Top, Math.Max(groupRight, rightBound), frame.Bottom));
    }
}
