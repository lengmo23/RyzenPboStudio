using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>Obsidian Slate + AMD Red + Precision Cyan theme tokens and drawing helpers.</summary>
internal static class Theme
{
    public static readonly Color RailBg = Color.FromArgb(0x0B, 0x0E, 0x14);
    public static readonly Color Bg = Color.FromArgb(0x0F, 0x13, 0x1A);
    public static readonly Color Surface = Color.FromArgb(0x16, 0x1B, 0x24);
    public static readonly Color SurfaceAlt = Color.FromArgb(0x1E, 0x25, 0x32);
    public static readonly Color SurfaceHover = Color.FromArgb(0x27, 0x30, 0x41);
    public static readonly Color WellBg = Color.FromArgb(0x11, 0x15, 0x1D);
    public static readonly Color Border = Color.FromArgb(0x25, 0x2D, 0x3D);
    public static readonly Color BorderSubtle = Color.FromArgb(0x1C, 0x23, 0x30);
    public static readonly Color SpinButton = Color.FromArgb(0x27, 0x30, 0x41);
    public static readonly Color SpinBorder = Color.FromArgb(0x35, 0x41, 0x57);
    public static readonly Color TextHi = Color.FromArgb(0xED, 0xF1, 0xF7);
    public static readonly Color TextMid = Color.FromArgb(0xA8, 0xB3, 0xC4);
    public static readonly Color TextLo = Color.FromArgb(0x6B, 0x76, 0x89);
    public static readonly Color Accent = Color.FromArgb(0xE2, 0x23, 0x1A);
    public static readonly Color AccentHover = Color.FromArgb(0xF2, 0x47, 0x3F);
    public static readonly Color AccentSoft = Color.FromArgb(0x33, 0x17, 0x18);
    public static readonly Color AccentSoftHover = Color.FromArgb(0x45, 0x1C, 0x1D);
    public static readonly Color AccentText = Color.White;
    public static readonly Color Telemetry = Color.FromArgb(0x38, 0xBD, 0xF8);
    public static readonly Color TelemetrySoft = Color.FromArgb(0x13, 0x26, 0x38);
    public static readonly Color Success = Color.FromArgb(0x22, 0xC5, 0x5E);
    public static readonly Color Warn = Color.FromArgb(0xEA, 0xB3, 0x08);
    public static readonly Color Danger = Color.FromArgb(0xEF, 0x44, 0x44);
    public static readonly Color Gold = Color.FromArgb(0xFB, 0xBF, 0x24);
    public static readonly Color Silver = Color.FromArgb(0xCB, 0xD5, 0xE1);

    public const string FontFamily = "Microsoft YaHei UI";

    /// <summary>Small arrow centered in r for the drop-down and spin buttons; dimmer when disabled. Drawn as whole pixel
    /// rows (7, 5, 3, 1 wide) so up and down arrows match exactly, which polygon filling does not guarantee.</summary>
    public static void FillArrow(Graphics g, Rectangle r, bool up, bool enabled)
    {
        const int rows = 4;
        int left = r.X + (r.Width - (2 * rows - 1)) / 2, top = r.Y + (r.Height - rows) / 2;
        using var brush = new SolidBrush(enabled ? TextHi : TextLo);
        for (int i = 0; i < rows; i++)
            g.FillRectangle(brush, left + i, up ? top + rows - 1 - i : top + i, 2 * (rows - i) - 1, 1);
    }

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0)
        {
            path.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)));
            path.CloseFigure();
            return path;
        }
        int d = radius * 2;
        if (d <= 0 || d > r.Width || d > r.Height)
        {
            path.AddRectangle(r);
            path.CloseFigure();
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Rounded card panel.</summary>
internal sealed class Card : Panel
{
    public int Radius { get; set; } = 10;
    public Color Fill { get; set; } = Theme.Surface;
    public Color Stroke { get; set; } = Theme.Border;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // UserPaint skips background fill, so clear the corners outside the rounded path
        // with the parent color to avoid stale buffer pixels.
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        if (Width <= 1 || Height <= 1) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Radius);
        using var b = new SolidBrush(Fill);
        using var pen = new Pen(Stroke);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>Flat rounded button with hover, pressed, and disabled states.</summary>
internal sealed class PillButton : Button
{
    public int Radius { get; set; } = 8;
    public Color Normal { get; set; } = Theme.Accent;
    public Color Hover { get; set; } = Theme.AccentHover;
    public Color Disabled { get; set; } = Theme.SurfaceAlt;
    /// <summary>Optional outline for secondary/selected buttons; disabled outlines become border gray.</summary>
    public Color Outline { get; set; } = Color.Empty;

    private bool _hover;
    private bool _pressed;
    private bool _softDisabled;

    /// <summary>Renders and behaves like a disabled button while keeping the Win32 HWND enabled
    /// so ToolTip hover text stays visible without flickering.</summary>
    public bool SoftDisabled
    {
        get => _softDisabled;
        set
        {
            if (_softDisabled == value) return;
            _softDisabled = value;
            if (value) { _hover = false; _pressed = false; }
            Cursor = IsInteractive ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    private bool IsInteractive => Enabled && !_softDisabled;

    public PillButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        ForeColor = Theme.AccentText;
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { if (IsInteractive) { _hover = true; Invalidate(); } base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (IsInteractive && e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnClick(EventArgs e) { if (_softDisabled) return; base.OnClick(e); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { _hover = false; _pressed = false; }
        Cursor = IsInteractive ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Clear outside corners with the parent surface color before drawing.
        e.Graphics.Clear((Parent as Card)?.Fill ?? Parent?.BackColor ?? BackColor);
        if (Width <= 1 || Height <= 1) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Radius);
        bool active = IsInteractive;
        Color fill = !active ? Disabled : (_pressed ? BlendDown(_hover ? Hover : Normal) : (_hover ? Hover : Normal));
        using var b = new SolidBrush(fill);
        e.Graphics.FillPath(b, path);
        if (Outline != Color.Empty)
        {
            // Inset the outline so its stroke stays within the control bounds.
            using var op = Theme.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), Math.Max(1, Radius - 1));
            using var pen = new Pen(active ? Outline : Theme.Border, 1.35f);
            e.Graphics.DrawPath(pen, op);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, r,
            active ? ForeColor : Theme.TextLo,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private static Color BlendDown(Color c) =>
        Color.FromArgb(c.A, Math.Max(0, c.R - 14), Math.Max(0, c.G - 14), Math.Max(0, c.B - 14));
}

internal enum NavIconKind { Sliders, Pulse, Memory, Terminal }

/// <summary>Left navigation rail item with vector icon, hover pill, and red active indicator bar.</summary>
internal sealed class NavRailButton : Button
{
    public bool Active { get; set; }
    public NavIconKind IconKind { get; set; }

    private static readonly Color ActiveFill = Color.FromArgb(0x1B, 0x22, 0x30);
    private static readonly Color HoverFill = Color.FromArgb(0x15, 0x1B, 0x26);

    private bool _hover;

    public NavRailButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        TabStop = false;
        BackColor = Theme.RailBg;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (Width <= 2 || Height <= 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (Active || _hover)
        {
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = Theme.RoundRect(r, 8);
            using var bg = new SolidBrush(Active ? ActiveFill : HoverFill);
            g.FillPath(bg, path);
        }

        if (Active)
        {
            int barH = Math.Max(10, Height - 16);
            var bar = new Rectangle(3, (Height - barH) / 2, 3, barH);
            using var barPath = Theme.RoundRect(bar, 1);
            using var accent = new SolidBrush(Theme.Accent);
            g.FillPath(accent, barPath);
        }

        float scale = Height / 40f;
        int iconSize = Math.Max(12, (int)Math.Round(16 * scale));
        int iconX = (int)Math.Round(16 * scale);
        int iconY = (Height - iconSize) / 2;
        Color iconColor = Active ? Theme.Accent : (_hover ? Theme.TextHi : Theme.TextLo);
        DrawNavIcon(g, new Rectangle(iconX, iconY, iconSize, iconSize), IconKind, iconColor);

        int textLeft = iconX + iconSize + (int)Math.Round(11 * scale);
        var textRect = new Rectangle(textLeft, 0, Math.Max(0, Width - textLeft - 8), Height);
        Color fore = Active || _hover ? Theme.TextHi : Theme.TextMid;
        TextRenderer.DrawText(g, Text, Font, textRect, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private static void DrawNavIcon(Graphics g, Rectangle r, NavIconKind kind, Color color)
    {
        float stroke = Math.Max(1.3f, r.Width / 10.5f);
        using var pen = new Pen(color, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var fill = new SolidBrush(color);

        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        switch (kind)
        {
            case NavIconKind.Sliders:
            {
                float[] ys = { y + h * 0.22f, y + h * 0.50f, y + h * 0.78f };
                float[] kx = { x + w * 0.34f, x + w * 0.68f, x + w * 0.44f };
                float kr = Math.Max(1.8f, w * 0.13f);
                for (int i = 0; i < 3; i++)
                {
                    g.DrawLine(pen, x + 1, ys[i], x + w - 1, ys[i]);
                    g.FillEllipse(fill, kx[i] - kr, ys[i] - kr, kr * 2, kr * 2);
                }
                break;
            }
            case NavIconKind.Pulse:
            {
                var pts = new[]
                {
                    new PointF(x + w * 0.05f, y + h * 0.54f),
                    new PointF(x + w * 0.26f, y + h * 0.54f),
                    new PointF(x + w * 0.39f, y + h * 0.18f),
                    new PointF(x + w * 0.56f, y + h * 0.84f),
                    new PointF(x + w * 0.70f, y + h * 0.42f),
                    new PointF(x + w * 0.80f, y + h * 0.54f),
                    new PointF(x + w * 0.95f, y + h * 0.54f),
                };
                g.DrawLines(pen, pts);
                break;
            }
            case NavIconKind.Memory:
            {
                // DIMM: module body with three chips and four contact pins.
                var body = new Rectangle(r.X + 1, r.Y + (int)Math.Round(h * 0.2f), Math.Max(4, r.Width - 2), Math.Max(4, (int)Math.Round(h * 0.48f)));
                using var bodyPath = Theme.RoundRect(body, 2);
                g.DrawPath(pen, bodyPath);
                for (int i = 0; i < 3; i++)
                {
                    float cx = x + w * (0.28f + 0.22f * i);
                    g.DrawLine(pen, cx, body.Top + h * 0.13f, cx, body.Bottom - h * 0.13f);
                }
                for (int i = 0; i < 4; i++)
                {
                    float px = x + w * (0.2f + 0.2f * i);
                    g.DrawLine(pen, px, body.Bottom + h * 0.1f, px, body.Bottom + h * 0.24f);
                }
                break;
            }
            case NavIconKind.Terminal:
            {
                using var box = Theme.RoundRect(new Rectangle(r.X + 1, r.Y + 2, Math.Max(4, r.Width - 2), Math.Max(4, r.Height - 4)), 3);
                g.DrawPath(pen, box);
                g.DrawLine(pen, x + w * 0.26f, y + h * 0.38f, x + w * 0.44f, y + h * 0.52f);
                g.DrawLine(pen, x + w * 0.26f, y + h * 0.66f, x + w * 0.44f, y + h * 0.52f);
                g.DrawLine(pen, x + w * 0.52f, y + h * 0.66f, x + w * 0.74f, y + h * 0.66f);
                break;
            }
        }
    }
}

/// <summary>Modern pill toggle switch with left-aligned label text.</summary>
internal sealed class AccentToggle : Control
{
    private bool _checked;
    private bool _hover;

    public bool Checked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; Invalidate(); } }
    }

    public AccentToggle()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        BackColor = Theme.RailBg;
        ForeColor = Theme.TextMid;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (Width <= 4 || Height <= 4) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = Math.Max(0.75f, Height / 30f);
        int trackW = Math.Max(28, (int)Math.Round(34 * s));
        int trackH = Math.Max(15, (int)Math.Round(18 * s));
        int trackX = Width - trackW - (int)Math.Round(2 * s);
        int trackY = (Height - trackH) / 2;

        if (_hover)
        {
            using var hoverPath = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 6);
            using var hoverBg = new SolidBrush(Color.FromArgb(0x15, 0x1B, 0x26));
            g.FillPath(hoverBg, hoverPath);
        }

        var textRect = new Rectangle((int)Math.Round(4 * s), 0, Math.Max(0, trackX - (int)Math.Round(10 * s)), Height);
        Color fore = _checked || _hover ? Theme.TextHi : Theme.TextMid;
        TextRenderer.DrawText(g, Text, Font, textRect, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var trackRect = new Rectangle(trackX, trackY, trackW, trackH);
        using var trackPath = Theme.RoundRect(trackRect, trackH / 2);
        using var trackBrush = new SolidBrush(_checked ? Theme.Accent : Theme.SurfaceAlt);
        g.FillPath(trackBrush, trackPath);
        if (!_checked)
        {
            using var borderPen = new Pen(_hover ? Theme.SpinBorder : Theme.Border, 1f);
            g.DrawPath(borderPen, trackPath);
        }

        int knobPad = Math.Max(2, (int)Math.Round(2.5f * s));
        int knobD = Math.Max(8, trackH - knobPad * 2);
        int knobX = _checked ? trackRect.Right - knobPad - knobD : trackRect.Left + knobPad;
        int knobY = trackRect.Top + (trackH - knobD) / 2;
        using var knobBrush = new SolidBrush(_checked ? Theme.AccentText : Theme.TextMid);
        g.FillEllipse(knobBrush, knobX, knobY, knobD, knobD);
    }
}

/// <summary>Dark look for flat drop-down ComboBoxes: WinForms paints their button, border and list highlight in
/// system colors, so items are owner-drawn and the button and border are drawn over each WM_PAINT.</summary>
internal sealed class DarkCombo : NativeWindow
{
    private const int FadeMs = 200;
    private const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, LWA_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMBOBOXINFO
    {
        public int cbSize;
        public RECT rcItem, rcButton;
        public int stateButton;
        public IntPtr hwndCombo, hwndItem, hwndList;
    }

    [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr hwnd, ref COMBOBOXINFO info);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    private readonly ComboBox _combo;
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private readonly Stopwatch _fadeClock = new();
    private IntPtr _list;

    private DarkCombo(ComboBox combo)
    {
        _combo = combo;
        // The handlers keep this instance alive for as long as the combo.
        combo.HandleCreated += (_, _) => AssignHandle(combo.Handle);
        combo.HandleDestroyed += (_, _) => ReleaseHandle();
        if (combo.IsHandleCreated) AssignHandle(combo.Handle);
        combo.DropDown += (_, _) => StartFade();
        combo.DropDownClosed += (_, _) => EndFade();
        combo.Disposed += (_, _) => _fade.Dispose();
        _fade.Tick += (_, _) => StepFade();
    }

    /// <summary>The drop-down list fades in: CBN_DROPDOWN arrives before the list is shown, so it starts transparent
    /// and its alpha rises over FadeMs with an ease-out curve.</summary>
    private void StartFade()
    {
        var info = new COMBOBOXINFO { cbSize = Marshal.SizeOf<COMBOBOXINFO>() };
        if (!GetComboBoxInfo(_combo.Handle, ref info) || info.hwndList == IntPtr.Zero) return;
        _list = info.hwndList;
        SetWindowLong(_list, GWL_EXSTYLE, GetWindowLong(_list, GWL_EXSTYLE) | WS_EX_LAYERED);
        SetLayeredWindowAttributes(_list, 0, 0, LWA_ALPHA);
        _fadeClock.Restart();
        _fade.Start();
    }

    private void StepFade()
    {
        double t = Math.Min(1.0, _fadeClock.ElapsedMilliseconds / (double)FadeMs);
        double eased = 1 - (1 - t) * (1 - t);
        if (_list != IntPtr.Zero) SetLayeredWindowAttributes(_list, 0, (byte)Math.Round(255 * eased), LWA_ALPHA);
        if (t >= 1) _fade.Stop();
    }

    private void EndFade()
    {
        _fade.Stop();
        if (_list != IntPtr.Zero) SetLayeredWindowAttributes(_list, 0, 255, LWA_ALPHA);
    }

    /// <param name="center">Center item text, for short values; otherwise left-aligned.</param>
    public static void Apply(ComboBox combo, bool center = false)
    {
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem += (_, e) => DrawItem(combo, e, center);
        combo.EnabledChanged += (_, _) => combo.Invalidate();
        _ = new DarkCombo(combo);
    }

    /// <summary>The list highlight uses the hover color instead of the system blue.</summary>
    private static void DrawItem(ComboBox cb, DrawItemEventArgs e, bool center)
    {
        if (e.Index < 0) return;
        bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
        bool hot = inList && (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(hot ? Theme.SurfaceHover : Theme.SurfaceAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                  | (center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);
        // The closed box uses the combo's ForeColor, so callers can mark a pending value.
        var bounds = center ? e.Bounds : Rectangle.FromLTRB(e.Bounds.Left + 4, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, bounds,
                              inList ? Theme.TextHi : cb.Enabled ? cb.ForeColor : Theme.TextLo, flags);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || !_combo.IsHandleCreated || _combo.Width <= 2 || _combo.Height <= 2) return;   // WM_PAINT
        using var g = Graphics.FromHwnd(_combo.Handle);
        int bw = SystemInformation.VerticalScrollBarWidth;
        var btn = new Rectangle(_combo.Width - bw - 2, 1, bw + 1, _combo.Height - 2);
        using (var bg = new SolidBrush(Theme.SpinButton)) g.FillRectangle(bg, btn);
        using (var pen = new Pen(Theme.SpinBorder))
        {
            g.DrawLine(pen, btn.Left, 1, btn.Left, _combo.Height - 2);
            g.DrawRectangle(pen, 0, 0, _combo.Width - 1, _combo.Height - 1);
        }
        Theme.FillArrow(g, btn, up: false, _combo.Enabled);
    }
}

/// <summary>Dark look for NumericUpDown: draws the border and the up / down buttons, which WinForms paints in system
/// colors, over each WM_PAINT of the box and of its button child.</summary>
internal sealed class DarkSpin : NativeWindow
{
    private readonly Control _target;
    private readonly NumericUpDown _box;
    private readonly bool _buttons;

    private DarkSpin(Control target, NumericUpDown box, bool buttons)
    {
        _target = target;
        _box = box;
        _buttons = buttons;
        // The handlers keep this instance alive for as long as the control.
        target.HandleCreated += (_, _) => AssignHandle(target.Handle);
        target.HandleDestroyed += (_, _) => ReleaseHandle();
        if (target.IsHandleCreated) AssignHandle(target.Handle);
    }

    public static void Apply(NumericUpDown box)
    {
        _ = new DarkSpin(box, box, buttons: false);
        if (box.Controls.OfType<Control>().FirstOrDefault(c => c is not TextBox) is { } spin)
        {
            _ = new DarkSpin(spin, box, buttons: true);
            box.EnabledChanged += (_, _) => spin.Invalidate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F || !_target.IsHandleCreated || _target.Width <= 1 || _target.Height <= 1) return;   // WM_PAINT
        using var g = Graphics.FromHwnd(_target.Handle);
        using var pen = new Pen(Theme.SpinBorder);
        if (!_buttons)
        {
            g.DrawRectangle(pen, 0, 0, _target.Width - 1, _target.Height - 1);
            return;
        }
        var r = _target.ClientRectangle;
        int half = r.Height / 2;
        using (var bg = new SolidBrush(Theme.SpinButton)) g.FillRectangle(bg, r);
        g.DrawLine(pen, 0, 0, 0, r.Height);
        g.DrawLine(pen, 0, half, r.Width, half);
        Theme.FillArrow(g, new Rectangle(0, 0, r.Width, half), up: true, _box.Enabled);
        Theme.FillArrow(g, new Rectangle(0, half, r.Width, r.Height - half), up: false, _box.Enabled);
    }
}
