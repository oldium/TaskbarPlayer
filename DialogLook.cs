using Microsoft.Win32;
using TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>
/// The look of an ordinary dialog when Windows is set to dark for apps. WinForms draws its dialogs light whatever
/// that setting says; its own switch for it changes the whole app and is not final. In light mode a dialog is left
/// as Windows draws it.
/// </summary>
internal static class DialogLook
{
    public static readonly Color Back = Color.FromArgb(32, 32, 32);
    private static readonly Color Text = Color.White;
    private static readonly Color Field = Color.FromArgb(45, 45, 45);
    private static readonly Color Button = Color.FromArgb(51, 51, 51);
    private static readonly Color ButtonEdge = Color.FromArgb(90, 90, 90);
    private static readonly Color Link = Color.FromArgb(96, 205, 255);

    /// <summary>Text that matters less than the rest.</summary>
    public static Color Dim(bool light) => light ? Color.DimGray : Color.FromArgb(170, 170, 170);

    /// <summary>Windows' setting for app windows ("default app mode").</summary>
    public static bool AppsAreLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return AppsAreLightOf(key?.GetValue("AppsUseLightTheme"));
    }

    /// <summary>The meaning of the registry value: 0 is dark; anything else, or no value at all, is light.</summary>
    internal static bool AppsAreLightOf(object? appsUseLightTheme) => appsUseLightTheme is not int value || value != 0;

    /// <summary>Dresses a dialog and the controls it has now.</summary>
    public static void Apply(Form form, bool light)
    {
        if (light) return;

        form.BackColor = Back;
        form.ForeColor = Text;
        foreach (Control control in form.Controls)
        {
            switch (control)
            {
                case TextBox box:
                    box.BackColor = Field;
                    box.ForeColor = Text;
                    box.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case Button button:
                    button.FlatStyle = FlatStyle.Flat;
                    button.UseVisualStyleBackColor = false;
                    button.BackColor = Button;
                    button.ForeColor = Text;
                    button.FlatAppearance.BorderColor = ButtonEdge;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(66, 66, 66);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(40, 40, 40);
                    break;

                case LinkLabel link:
                    link.LinkColor = Link;
                    link.VisitedLinkColor = Link;
                    link.ActiveLinkColor = Text;
                    break;
            }
        }

        // The title bar is Windows' to draw; it does so in dark when told.
        if (form.IsHandleCreated) NativeMethods.DarkTitleBar(form.Handle);
        form.HandleCreated += (_, _) => NativeMethods.DarkTitleBar(form.Handle);
    }
}
