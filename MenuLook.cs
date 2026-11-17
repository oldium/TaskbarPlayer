using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// Gives the app's menus the taskbar's light or dark mode, rounded corners and a darker thin edge. Left alone,
/// WinForms draws them light whatever the mode.
/// </summary>
internal static class MenuLook
{
    private static readonly Painter Dark = new(light: false);
    private static readonly Painter Light = new(light: true);
    private static readonly Font TextFont = new(GdiText.UiFace, 9f);
    private static readonly Font IconFont = new(Glyphs.FontFamilyName, 9f);
    private static readonly string Tick = ((char)0xE73E).ToString();

    private static readonly ConditionalWeakTable<ToolStripDropDown, object> Prepared = new();
    private static bool s_light;

    /// <summary>
    /// Where a menu that belongs to a part of the widget opens: away from the taskbar, starting at the part.
    /// </summary>
    public static (Point At, ToolStripDropDownDirection Direction) Beside(Rectangle part, TaskbarEdge edge, Rectangle taskbar) =>
        edge switch
        {
            TaskbarEdge.Top => (new Point(part.Left, taskbar.Bottom), ToolStripDropDownDirection.BelowRight),
            TaskbarEdge.Left => (new Point(taskbar.Right, part.Top), ToolStripDropDownDirection.BelowRight),
            TaskbarEdge.Right => (new Point(taskbar.Left, part.Top), ToolStripDropDownDirection.BelowLeft),
            _ => (part.Location, ToolStripDropDownDirection.AboveRight),
        };

    /// <summary>Which way a menu opens at the mouse pointer: into the screen.</summary>
    public static ToolStripDropDownDirection AtPointer(TaskbarEdge edge) => edge switch
    {
        TaskbarEdge.Bottom => ToolStripDropDownDirection.AboveRight,
        TaskbarEdge.Right => ToolStripDropDownDirection.BelowLeft,
        _ => ToolStripDropDownDirection.BelowRight,
    };

    /// <summary>To be called before a menu is shown; its submenus follow by themselves.</summary>
    public static void Apply(ContextMenuStrip menu, bool light)
    {
        s_light = light;
        menu.Renderer = light ? Light : Dark;
        menu.Font = TextFont;
        Prepare(menu);
    }

    private static void Prepare(ToolStripDropDown dropDown)
    {
        if (!Prepared.TryGetValue(dropDown, out _))
        {
            Prepared.Add(dropDown, dropDown);
            // The shadow of old: Windows gives a rounded window its own.
            if (FlyoutWindow.RoundedByWindows && dropDown is ToolStripDropDownMenu menu) menu.DropShadowEnabled = false;
            // Clear of the rounded corners.
            dropDown.Padding = new Padding(1, 4, 1, 4);
            dropDown.HandleCreated += (_, _) => Shape(dropDown);
            dropDown.Opening += (_, _) =>
            {
                Shape(dropDown);
                foreach (ToolStripItem item in dropDown.Items)
                {
                    // Roomier rows, nearer to the taskbar's own menus.
                    if (item is not ToolStripSeparator) item.Padding = new Padding(4, 3, 4, 3);
                    if (item is ToolStripDropDownItem { HasDropDownItems: true } parent) Prepare(parent.DropDown);
                }
            };
        }
        Shape(dropDown);
    }

    private static void Shape(ToolStripDropDown dropDown)
    {
        if (dropDown.IsHandleCreated) NativeMethods.StyleFlyout(dropDown.Handle, dark: !s_light, FlyoutColors.SolidEdge(s_light));
    }

    private sealed class Colors(bool light) : ProfessionalColorTable
    {
        private readonly Color _back = FlyoutColors.Solid(light);

        public override Color ToolStripDropDownBackground => _back;
        public override Color ImageMarginGradientBegin => _back;
        public override Color ImageMarginGradientMiddle => _back;
        public override Color ImageMarginGradientEnd => _back;
        // Windows draws the edge of a window it rounds; a square one gets its edge here.
        public override Color MenuBorder => FlyoutWindow.RoundedByWindows ? _back : FlyoutColors.SolidEdge(light);
        public override Color SeparatorDark => FlyoutColors.Line(light);
        public override Color SeparatorLight => _back;
    }

    private sealed class Painter(bool light) : ToolStripProfessionalRenderer(new Colors(light))
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(FlyoutColors.Solid(light));

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            // No strip of another colour down the left side.
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            bool open = e.Item is ToolStripMenuItem { DropDown.Visible: true };
            if (!e.Item.Enabled || !(e.Item.Selected || open)) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Glyphs.RoundedRect(new RectangleF(4, 0, e.Item.Width - 8, e.Item.Height - 1), 4);
            using var brush = new SolidBrush(FlyoutColors.Highlight(light));
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? FlyoutColors.Text(light) : FlyoutColors.Dim(light);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Enabled: false } ? FlyoutColors.Dim(light) : FlyoutColors.Text(light);
            base.OnRenderArrow(e);
        }

        // A plain tick in the text's colour, not a tick in a tinted box.
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) =>
            TextRenderer.DrawText(e.Graphics, Tick, IconFont, e.ImageRectangle,
                e.Item.Enabled ? FlyoutColors.Text(light) : FlyoutColors.Dim(light),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(FlyoutColors.Line(light));
            e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }
    }
}
