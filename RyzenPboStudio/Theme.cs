using System.Drawing.Drawing2D;

namespace RyzenPboStudio;

/// <summary>Dark theme colors and drawing helpers.</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x16, 0x18, 0x1D);
    public static readonly Color Surface = Color.FromArgb(0x21, 0x24, 0x2B);
    public static readonly Color SurfaceAlt = Color.FromArgb(0x2A, 0x2E, 0x37);
    public static readonly Color SpinButton = Color.FromArgb(0x3A, 0x40, 0x4C);   // drop-down and up / down buttons
    public static readonly Color SpinBorder = Color.FromArgb(0x4A, 0x51, 0x60);   // their border and dividers
    public static readonly Color Border = Color.FromArgb(0x36, 0x3B, 0x46);
    public static readonly Color TextHi = Color.FromArgb(0xE6, 0xE8, 0xEC);
    public static readonly Color TextLo = Color.FromArgb(0x8B, 0x91, 0x9E);
    public static readonly Color Accent = Color.FromArgb(0xE2, 0x23, 0x1A);
    public static readonly Color AccentHover = Color.FromArgb(0xF2, 0x47, 0x3F);
    public static readonly Color AccentText = Color.White;
    public static readonly Color Success = Color.FromArgb(0x3F, 0xB9, 0x50);
    public static readonly Color Warn = Color.FromArgb(0xD2, 0x99, 0x22);

    public const string FontFamily = "Microsoft YaHei UI";
    public const string MonoFamily = "Consolas";

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
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Radius);
        using var b = new SolidBrush(Fill);
        using var pen = new Pen(Stroke);
        e.Graphics.FillPath(b, path);
        e.Graphics.DrawPath(pen, path);
    }
}

/// <summary>Flat rounded button with hover and disabled states.</summary>
internal sealed class PillButton : Button
{
    public int Radius { get; set; } = 10;
    public Color Normal { get; set; } = Theme.Accent;
    public Color Hover { get; set; } = Theme.AccentHover;
    public Color Disabled { get; set; } = Theme.SurfaceAlt;
    /// <summary>Optional outline for hollow buttons; disabled outlines become gray.</summary>
    public Color Outline { get; set; } = Color.Empty;

    private bool _hover;

    public PillButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Theme.AccentText;
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Clear outside corners with the parent color before drawing.
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Radius);
        Color fill = !Enabled ? Disabled : (_hover ? Hover : Normal);
        using var b = new SolidBrush(fill);
        e.Graphics.FillPath(b, path);
        if (Outline != Color.Empty)
        {
            // Inset the outline so its stroke stays within the control bounds.
            using var op = Theme.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), Math.Max(1, Radius - 1));
            using var pen = new Pen(Enabled ? Outline : Theme.Border, 1.5f);
            e.Graphics.DrawPath(pen, op);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, r,
            Enabled ? ForeColor : Theme.TextLo,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Navigation tab with an underline for the active page.</summary>
internal sealed class NavTabButton : Button
{
    public bool Active { get; set; }

    private bool _hover;

    public NavTabButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Theme.Bg;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fore = Active || _hover ? Theme.TextHi : Theme.TextLo;
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (Active)
        {
            const int w = 48, h = 3;
            using var b = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(b, (Width - w) / 2, Height - h - 3, w, h);
        }
    }
}

/// <summary>Dark look for flat drop-down ComboBoxes: WinForms paints their button, border and list highlight in
/// system colors, so items are owner-drawn and the button and border are drawn over each WM_PAINT.</summary>
internal sealed class DarkCombo : NativeWindow
{
    private readonly ComboBox _combo;

    private DarkCombo(ComboBox combo)
    {
        _combo = combo;
        // The handlers keep this instance alive for as long as the combo.
        combo.HandleCreated += (_, _) => AssignHandle(combo.Handle);
        combo.HandleDestroyed += (_, _) => ReleaseHandle();
        if (combo.IsHandleCreated) AssignHandle(combo.Handle);
    }

    /// <param name="center">Center item text, for short values; otherwise left-aligned.</param>
    public static void Apply(ComboBox combo, bool center = false)
    {
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem += (_, e) => DrawItem(combo, e, center);
        combo.EnabledChanged += (_, _) => combo.Invalidate();
        _ = new DarkCombo(combo);
    }

    /// <summary>The list highlight uses the border color instead of the system blue.</summary>
    private static void DrawItem(ComboBox cb, DrawItemEventArgs e, bool center)
    {
        if (e.Index < 0) return;
        bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
        bool hot = inList && (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(hot ? Theme.Border : Theme.SurfaceAlt)) e.Graphics.FillRectangle(bg, e.Bounds);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                  | (center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);
        var bounds = center ? e.Bounds : Rectangle.FromLTRB(e.Bounds.Left + 4, e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, cb.GetItemText(cb.Items[e.Index]), cb.Font, bounds,
                              inList || cb.Enabled ? Theme.TextHi : Theme.TextLo, flags);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x000F) return;   // WM_PAINT
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
        if (m.Msg != 0x000F) return;   // WM_PAINT
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
