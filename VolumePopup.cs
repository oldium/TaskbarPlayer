using System.Drawing.Drawing2D;
using TaskbarPlayer.Native;

namespace TaskbarPlayer;

/// <summary>Small flyout with a volume slider, shown above the taskbar widget.</summary>
internal sealed class VolumePopup : Form
{
    private readonly Button _mute;
    private readonly SliderBar _slider;
    private readonly Label _percent;
    private readonly bool _light;
    private bool _keyboardUsed;

    public VolumePopup(bool light)
    {
        _light = light;
        var back = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44);
        var fore = light ? Color.FromArgb(26, 26, 26) : Color.White;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(270, 52);
        BackColor = back;
        ForeColor = fore;
        KeyPreview = true;

        _mute = new QuietButton
        {
            Font = new Font(Glyphs.FontFamilyName, 13F),
            Bounds = new Rectangle(8, 8, 38, 36),
            FlatStyle = FlatStyle.Flat,
            ForeColor = fore,
            BackColor = back,
            TabIndex = 1,
            UseMnemonic = false,
            AccessibleName = Strings.Mute,
        };
        _mute.FlatAppearance.BorderSize = 0;
        _mute.FlatAppearance.MouseOverBackColor = light ? Color.FromArgb(230, 230, 230) : Color.FromArgb(60, 60, 60);
        _mute.FlatAppearance.MouseDownBackColor = light ? Color.FromArgb(215, 215, 215) : Color.FromArgb(75, 75, 75);
        _mute.Click += (_, _) => MuteToggled?.Invoke();
        _mute.Paint += (_, e) =>
        {
            // Where the keyboard focus is, once the keyboard is in use.
            if (!_mute.Focused || !_keyboardUsed) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(fore, 2f);
            using var ring = Glyphs.RoundedRect(new RectangleF(1, 1, _mute.Width - 2, _mute.Height - 2), 4);
            e.Graphics.DrawPath(pen, ring);
        };

        _slider = new SliderBar
        {
            Bounds = new Rectangle(50, 9, 170, 34),
            WheelStep = 0.02f,
            KeyStep = 0.02f,
            TabIndex = 0,
            AccessibleName = Strings.Volume,
            BackColor = back,
            TrackColor = light ? Color.FromArgb(200, 200, 200) : Color.FromArgb(90, 90, 90),
            FillColor = light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(76, 194, 255),
            HoverFillColor = light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(76, 194, 255),
        };
        _slider.ValueChanging += (_, _) => VolumeChanged?.Invoke(_slider.Value);

        _percent = new Label
        {
            Bounds = new Rectangle(222, 0, 42, 52),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10F),
        };

        Controls.AddRange([_mute, _slider, _percent]);
        ResumeLayout(performLayout: true);
    }

    public event Action<float>? VolumeChanged;
    public event Action? MuteToggled;

    /// <summary>When the popup was last hidden; lets a click on the widget's volume button toggle it closed.</summary>
    public DateTime HiddenAt { get; private set; }

    public bool IsLight => _light;

    public void SetState(float level, bool muted)
    {
        if (!_slider.IsDragging) _slider.Value = level;
        _mute.Text = Glyphs.ForVolume(level, muted);
        _percent.Text = muted ? "–" : $"{(int)Math.Round(level * 100)}";
    }

    /// <summary>
    /// Where the popup goes: a gap away from the taskbar on the screen's side, centred on its button, and a gap
    /// inside the screen.
    /// </summary>
    internal static Point PlaceBy(Size popup, Rectangle anchor, TaskbarEdge edge, Rectangle taskbar, Rectangle screen, int gap)
    {
        int x = Math.Clamp(anchor.Left + anchor.Width / 2 - popup.Width / 2, screen.Left + gap, screen.Right - popup.Width - gap);
        int y = Math.Clamp(anchor.Top + anchor.Height / 2 - popup.Height / 2, screen.Top + gap, screen.Bottom - popup.Height - gap);
        return edge switch
        {
            TaskbarEdge.Top => new Point(x, taskbar.Bottom + gap),
            TaskbarEdge.Left => new Point(taskbar.Right + gap, y),
            TaskbarEdge.Right => new Point(taskbar.Left - popup.Width - gap, y),
            _ => new Point(x, taskbar.Top - popup.Height - gap),
        };
    }

    public void ShowBy(Rectangle anchor, TaskbarEdge edge, Rectangle taskbar)
    {
        var area = Screen.FromRectangle(taskbar).Bounds;
        Location = PlaceBy(Size, anchor, edge, taskbar, area, (int)(12 * DeviceDpi / 96f));
        _keyboardUsed = false;
        Show();
        Activate();
        _slider.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.StyleFlyout(Handle, dark: !_light, FlyoutColors.SolidEdge(_light));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HideNow();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        _keyboardUsed = true;
        _mute.Invalidate();
        if (keyData == Keys.Escape)
        {
            HideNow();
            return true;
        }
        if (keyData == Keys.M)
        {
            MuteToggled?.Invoke();
            return true;
        }
        // The arrow keys change the volume wherever the focus is; Tab moves between the slider and mute.
        if (_slider.HandleKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void HideNow()
    {
        if (!Visible) return;
        Hide();
        HiddenAt = DateTime.UtcNow;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    // A flat button without WinForms' own focus marks, which do not suit a dark background.
    private sealed class QuietButton : Button
    {
        protected override bool ShowFocusCues => false;

        public override void NotifyDefault(bool value) => base.NotifyDefault(false);
    }
}
