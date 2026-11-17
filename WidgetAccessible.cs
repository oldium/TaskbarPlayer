using System.Runtime.InteropServices;

namespace TaskbarPlayer;

/// <summary>One part of the widget as assistive tools are told about it. A push button can be pressed.</summary>
internal sealed record AccessiblePart(WidgetPart Part, AccessibleRole Role, string Name, string? Value, string? Description,
    Rectangle ScreenBounds, bool Enabled);

/// <summary>
/// Tells assistive tools (screen readers) what the widget holds: its parts with names, values and places, and
/// lets them press one. The widget draws everything itself, so without this they find an empty pane.
/// </summary>
internal sealed class WidgetAccessible : AccessibleObject
{
    private const int OBJID_CLIENT = -4;
    private static Guid s_accessibleInterface = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [DllImport("oleacc.dll")]
    private static extern IntPtr LresultFromObject(ref Guid iid, IntPtr wParam, [MarshalAs(UnmanagedType.IUnknown)] object accessible);

    private readonly Func<Rectangle> _bounds;
    private readonly Func<IReadOnlyList<AccessiblePart>> _parts;
    private readonly Action<WidgetPart> _press;

    /// <param name="bounds">The widget on the screen.</param>
    /// <param name="parts">Its parts as they are right now, in the order they are drawn.</param>
    /// <param name="press">Does what a click on the part does.</param>
    public WidgetAccessible(Func<Rectangle> bounds, Func<IReadOnlyList<AccessiblePart>> parts, Action<WidgetPart> press)
    {
        _bounds = bounds;
        _parts = parts;
        _press = press;
    }

    /// <summary>
    /// The answer to a window's WM_GETOBJECT when it asks for what is inside the window; null for the other
    /// questions, which are Windows' to answer.
    /// </summary>
    public IntPtr? Answer(IntPtr wParam, IntPtr lParam) =>
        unchecked((int)lParam.ToInt64()) == OBJID_CLIENT ? LresultFromObject(ref s_accessibleInterface, wParam, this) : null;

    public override string? Name
    {
        get => Strings.AppName;
        set { }
    }

    public override AccessibleRole Role => AccessibleRole.ToolBar;

    public override Rectangle Bounds => _bounds();

    public override int GetChildCount() => _parts().Count;

    public override AccessibleObject? GetChild(int index)
    {
        var parts = _parts();
        return index >= 0 && index < parts.Count ? new PartAccessible(this, parts[index].Part) : null;
    }

    public override AccessibleObject? HitTest(int x, int y)
    {
        // Parts can lie inside one another (the timeline in the song part): the smallest is the most exact.
        AccessiblePart? hit = null;
        foreach (var part in _parts())
        {
            if (!part.ScreenBounds.Contains(x, y)) continue;
            if (hit is null || Area(part) < Area(hit)) hit = part;
        }
        if (hit is not null) return new PartAccessible(this, hit.Part);
        return _bounds().Contains(x, y) ? this : null;

        static long Area(AccessiblePart part) => (long)part.ScreenBounds.Width * part.ScreenBounds.Height;
    }

    public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
    {
        AccessibleNavigation.FirstChild => GetChild(0),
        AccessibleNavigation.LastChild => GetChild(GetChildCount() - 1),
        _ => null,
    };

    private AccessiblePart? Find(WidgetPart part) => _parts().FirstOrDefault(p => p.Part == part);

    private AccessibleObject? Neighbour(WidgetPart part, int step)
    {
        var parts = _parts();
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Part == part) return GetChild(i + step);
        }
        return null;
    }

    // Looks its part up at every question, so it never tells of a state that has passed.
    private sealed class PartAccessible(WidgetAccessible owner, WidgetPart part) : AccessibleObject
    {
        private AccessiblePart? Info => owner.Find(part);

        private bool Pressable => Info is { Enabled: true, Role: AccessibleRole.PushButton };

        public override string? Name
        {
            get => Info?.Name;
            set { }
        }

        public override string? Value
        {
            get => Info?.Value;
            set { }
        }

        public override string? Description => Info?.Description;

        public override AccessibleRole Role => Info?.Role ?? AccessibleRole.None;

        public override AccessibleStates State => Info switch
        {
            null => AccessibleStates.Invisible | AccessibleStates.Unavailable,
            { Enabled: false } => AccessibleStates.Unavailable,
            _ => AccessibleStates.None,
        };

        public override Rectangle Bounds => Info?.ScreenBounds ?? Rectangle.Empty;

        public override AccessibleObject? Parent => owner;

        public override string? DefaultAction => Pressable ? Strings.Press : null;

        public override void DoDefaultAction()
        {
            if (Pressable) owner._press(part);
        }

        public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
        {
            AccessibleNavigation.Next or AccessibleNavigation.Right => owner.Neighbour(part, 1),
            AccessibleNavigation.Previous or AccessibleNavigation.Left => owner.Neighbour(part, -1),
            _ => null,
        };
    }
}
