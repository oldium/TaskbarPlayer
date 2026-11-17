using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace TaskbarPlayer;

/// <summary>Minimal flat slider (0..1) used for volume and track position.</summary>
internal sealed class SliderBar : Control
{
    private float _value;
    private float _keyStep;
    private bool _hover;

    public SliderBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised while the user changes the value (dragging, wheel, keys).</summary>
    public event EventHandler? ValueChanging;

    /// <summary>Raised when the user finishes changing the value.</summary>
    public event EventHandler? ValueCommitted;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Value
    {
        get => _value;
        set
        {
            float v = Math.Clamp(value, 0f, 1f);
            if (v != _value)
            {
                _value = v;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsDragging { get; private set; }

    /// <summary>Change per mouse-wheel notch; 0 disables the wheel.</summary>
    [DefaultValue(0f)]
    public float WheelStep { get; set; }

    /// <summary>
    /// Change per arrow key (Page Up/Down five times as much, Home/End the ends); 0 keeps the slider out of the
    /// keyboard's reach, above 0 it can take the focus.
    /// </summary>
    [DefaultValue(0f)]
    public float KeyStep
    {
        get => _keyStep;
        set
        {
            _keyStep = value;
            SetStyle(ControlStyles.Selectable, value > 0);
            TabStop = value > 0;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor { get; set; } = Color.FromArgb(80, 80, 80);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.FromArgb(225, 225, 225);
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverFillColor { get; set; } = Color.FromArgb(76, 194, 255);

    private float DpiScale => DeviceDpi / 96f;
    private float KnobRadius => 6f * DpiScale;
    private float Pad => KnobRadius + 1;

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        IsDragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsDragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsDragging) return;
        SetFromX(e.X);
        IsDragging = false;
        Capture = false;
        Invalidate();
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (WheelStep <= 0 || !Enabled) return;
        Value += Math.Sign(e.Delta) * WheelStep;
        ValueChanging?.Invoke(this, EventArgs.Empty);
        ValueCommitted?.Invoke(this, EventArgs.Empty);
        if (e is HandledMouseEventArgs h) h.Handled = true;
    }

    // Without this the arrow keys would move the focus instead of reaching the slider.
    protected override bool IsInputKey(Keys keyData) => Target(keyData) is not null || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleKey(e.KeyData)) e.Handled = true;
    }

    /// <summary>Changes the value as the key asks. False for keys that mean nothing to a slider.</summary>
    public bool HandleKey(Keys keyData)
    {
        if (Target(keyData) is not float target) return false;
        Value = target;
        ValueChanging?.Invoke(this, EventArgs.Empty);
        ValueCommitted?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private float? Target(Keys keyData)
    {
        if (_keyStep <= 0 || !Enabled) return null;
        return keyData switch
        {
            Keys.Left or Keys.Down => _value - _keyStep,
            Keys.Right or Keys.Up => _value + _keyStep,
            Keys.PageDown => _value - 5 * _keyStep,
            Keys.PageUp => _value + 5 * _keyStep,
            Keys.Home => 0f,
            Keys.End => 1f,
            _ => null,
        };
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessibleObject(this);

    private sealed class SliderAccessibleObject(SliderBar owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.Slider;

        public override string? Value
        {
            get => $"{(int)Math.Round(owner.Value * 100)}%";
            set { }
        }
    }

    private void SetFromX(int x)
    {
        float width = Width - 2 * Pad;
        if (width <= 0) return;
        Value = (x - Pad) / width;
        ValueChanging?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float thickness = 4f * DpiScale;
        float width = Width - 2 * Pad;
        float cy = Height / 2f;
        // With the keyboard focus it looks as under the mouse: the knob shows where the arrow keys act.
        bool active = Enabled && (_hover || IsDragging || Focused);

        using (var track = new SolidBrush(TrackColor))
        {
            FillPill(g, track, new RectangleF(Pad, cy - thickness / 2, width, thickness));
        }

        if (!Enabled) return;

        float filled = width * _value;
        if (filled > 0)
        {
            using var fill = new SolidBrush(active ? HoverFillColor : FillColor);
            FillPill(g, fill, new RectangleF(Pad, cy - thickness / 2, filled, thickness));
        }

        if (active)
        {
            float r = KnobRadius;
            g.FillEllipse(Brushes.White, Pad + filled - r, cy - r, 2 * r, 2 * r);
        }
    }

    private static void FillPill(Graphics g, Brush brush, RectangleF r)
    {
        float d = r.Height;
        if (r.Width <= d)
        {
            g.FillEllipse(brush, r.X, r.Y, Math.Max(r.Width, 1), d);
            return;
        }
        using var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
