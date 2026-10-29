using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>Draws the borderless title bar and handles dragging from its empty area.</summary>
internal sealed class HeaderBar : Control
{
    private const int IconSize = 48;
    private const int InfoGap = 48; // Gap between title and hardware details.
    private const int BtnW = 42, BtnH = 42, BtnTop = 4, BtnRight = 6; // Large outlined icons in the HYDRA style.
    private const int BtnUpdate = 0, BtnSponsor = 1, BtnInfo = 2, BtnMin = 3, BtnClose = 4, BtnCount = 5;

    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private int hoverBtn = -1, pressedBtn = -1;
    private readonly ToolTip tip = new();
    private string updateTip = "检查更新";
    private bool updateAvailable;

    public event EventHandler? UpdateClicked;
    public event EventHandler? SponsorClicked;
    public event EventHandler? InfoClicked;

    /// <summary>Hover text of the update button, e.g. the available version or download progress.</summary>
    public string UpdateTip
    {
        get => updateTip;
        set
        {
            if (updateTip == value) return;
            updateTip = value;
            if (hoverBtn == BtnUpdate) ShowTip(BtnUpdate);
        }
    }

    /// <summary>Marks the update button with a dot while a newer release is known.</summary>
    public bool UpdateAvailable
    {
        get => updateAvailable;
        set { if (updateAvailable != value) { updateAvailable = value; Invalidate(); } }
    }

    private readonly Font titleFont = new(Theme.FontFamily, 17F, FontStyle.Bold);
    private readonly Font subFont = new(Theme.FontFamily, 9F);
    private readonly Font infoFont = new(Theme.FontFamily, 9F);
    private readonly Bitmap? icon;
    private readonly string title = "AMD Ryzen PBO Studio";
    private readonly string subtitle;
    private readonly string[] lines = { "", "", "" };

    /// <summary>Version shown to the user, e.g. "V2.2.0Beta" from csproj Version "2.2.0-beta". The SDK appends
    /// "+commit" build metadata to the informational version, which is dropped. The updater keeps comparing the
    /// numeric assembly version, which has no prerelease tag.</summary>
    public static string DisplayVersion { get; } = MakeDisplayVersion();

    private static string MakeDisplayVersion()
    {
        var asm = typeof(HeaderBar).Assembly;
        string info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "";
        if (info.Length == 0) return "V" + (asm.GetName().Version?.ToString(3) ?? "?");
        int dash = info.IndexOf('-');
        if (dash < 0 || dash == info.Length - 1) return "V" + info.TrimEnd('-');
        string pre = info[(dash + 1)..];
        return "V" + info[..dash] + char.ToUpperInvariant(pre[0]) + pre[1..];
    }

    public HeaderBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        ForeColor = MonTheme.Fg;

        subtitle = $"{DisplayVersion} · AUTOMATIC OC TOOL";
        try
        {
            using var stream = typeof(HeaderBar).Assembly.GetManifestResourceStream("RyzenPboStudio.ICON.ico");
            if (stream != null)
            {
                using var ico = new Icon(stream, IconSize, IconSize);
                icon = ico.ToBitmap();
            }
        }
        catch { icon = null; }
    }

    /// <summary>Updates one hardware-info row without repainting unchanged text.</summary>
    public void SetLine(int index, string text)
    {
        if (lines[index] == text) return;
        lines[index] = text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        int h = ClientSize.Height;
        int x = 4;

        if (icon != null)
        {
            e.Graphics.DrawImage(icon, x, (h - IconSize) / 2, IconSize, IconSize);
            x += IconSize + 12;
        }

        var flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var titleSize = TextRenderer.MeasureText(e.Graphics, title, titleFont, Size.Empty, flags);
        var subSize = TextRenderer.MeasureText(e.Graphics, subtitle, subFont, Size.Empty, flags);
        int blockH = titleSize.Height + 2 + subSize.Height;
        int y = (h - blockH) / 2;
        TextRenderer.DrawText(e.Graphics, title, titleFont, new Point(x, y), MonTheme.Fg, flags);
        TextRenderer.DrawText(e.Graphics, subtitle, subFont, new Point(x + 2, y + titleSize.Height + 2), MonTheme.Dim, flags);
        x += Math.Max(titleSize.Width, subSize.Width) + InfoGap;

        // Center hardware details clear of window buttons and trim overlong text.
        int lineH = TextRenderer.MeasureText(e.Graphics, "Ag", infoFont, Size.Empty, flags).Height + 3;
        int ly = (h - lineH * lines.Length) / 2;
        int width = Math.Max(0, ClientSize.Width - BtnRight - BtnCount * BtnW - 8 - x);
        foreach (string line in lines)
        {
            TextRenderer.DrawText(e.Graphics, line, infoFont, new Rectangle(x, ly, width, lineH),
                MonTheme.Fg, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            ly += lineH;
        }

        for (int i = 0; i < BtnCount; i++) DrawWindowButton(e.Graphics, i);
        using var border = new Pen(Theme.Border);
        e.Graphics.DrawLine(border, 0, h - 1, ClientSize.Width, h - 1);
    }

    private Rectangle BtnRect(int i) => new(ClientSize.Width - BtnRight - (BtnCount - i) * BtnW, BtnTop, BtnW, BtnH);

    private int HitButton(Point p)
    {
        for (int i = 0; i < BtnCount; i++) if (BtnRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>Draws a window button as a large outlined icon; it brightens on hover and the close button turns red.</summary>
    private void DrawWindowButton(Graphics g, int i)
    {
        var r = BtnRect(i);
        bool hot = hoverBtn == i;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (hot)
        {
            using var bg = new SolidBrush(i == BtnClose ? Theme.Accent : Theme.Border);
            using var hl = Theme.RoundRect(Rectangle.Inflate(r, -2, -2), 6);
            g.FillPath(bg, hl);
        }

        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        using var pen = new Pen(hot ? (i == BtnClose ? Theme.AccentText : MonTheme.Fg) : MonTheme.Dim, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        if (i == BtnUpdate)
        {
            // Download arrow over a tray.
            g.DrawLine(pen, cx, cy - 11, cx, cy + 4);
            g.DrawLines(pen, new[] { new Point(cx - 6, cy - 2), new Point(cx, cy + 4), new Point(cx + 6, cy - 2) });
            g.DrawLines(pen, new[] { new Point(cx - 11, cy + 4), new Point(cx - 11, cy + 11), new Point(cx + 11, cy + 11), new Point(cx + 11, cy + 4) });
            if (updateAvailable)
                using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, cx + 7, cy - 14, 7, 7);
        }
        else if (i == BtnSponsor)
        {
            // Coffee cup: rounded body, handle on the right, two wisps of steam.
            const int x0 = -11, x1 = 6, top = -2, bottom = 11, rad = 4;
            using var cup = new GraphicsPath();
            cup.AddLine(cx + x0, cy + top, cx + x1, cy + top);
            cup.AddLine(cx + x1, cy + top, cx + x1, cy + bottom - rad);
            cup.AddArc(cx + x1 - 2 * rad, cy + bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90);
            cup.AddLine(cx + x1 - rad, cy + bottom, cx + x0 + rad, cy + bottom);
            cup.AddArc(cx + x0, cy + bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90);
            cup.CloseFigure();
            g.DrawPath(pen, cup);
            g.DrawArc(pen, cx + x1 - 3, cy + 1, 8, 7, -90, 180);
            g.DrawBezier(pen, cx - 6, cy - 5, cx - 8, cy - 8, cx - 4, cy - 10, cx - 6, cy - 13);
            g.DrawBezier(pen, cx, cy - 5, cx - 2, cy - 8, cx + 2, cy - 10, cx, cy - 13);
        }
        else if (i == BtnInfo)
        {
            g.DrawEllipse(pen, cx - 12, cy - 12, 24, 24);
            using (var dot = new SolidBrush(pen.Color)) g.FillEllipse(dot, cx - 1.6f, cy - 7.6f, 3.2f, 3.2f);
            g.DrawLine(pen, cx, cy - 2, cx, cy + 6);
        }
        else if (i == BtnMin)
        {
            using (var box = Theme.RoundRect(new Rectangle(cx - 11, cy - 11, 22, 22), 4)) g.DrawPath(pen, box);
            g.DrawLine(pen, cx - 6, cy, cx + 6, cy);
        }
        else
        {
            g.DrawLine(pen, cx - 10, cy - 10, cx + 10, cy + 10);
            g.DrawLine(pen, cx - 10, cy + 10, cx + 10, cy - 10);
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitButton(e.Location);
        if (hit == hoverBtn) return;
        hoverBtn = hit;
        if (hit is BtnUpdate or BtnSponsor) ShowTip(hit);
        else tip.Hide(this);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        tip.Hide(this);
        if (hoverBtn != -1) { hoverBtn = -1; Invalidate(); }
    }

    private void ShowTip(int btn)
    {
        var r = BtnRect(btn);
        tip.Show(btn == BtnUpdate ? updateTip : "赞助作者", this, r.Left, r.Bottom + 2);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pressedBtn = HitButton(e.Location);
        if (pressedBtn >= 0 || FindForm() is not { } form) return;
        // Treat dragging empty space as dragging the system title bar.
        ReleaseCapture();
        SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int pressed = pressedBtn;
        pressedBtn = -1;
        if (e.Button != MouseButtons.Left || pressed < 0 || HitButton(e.Location) != pressed || FindForm() is not { } form) return;
        hoverBtn = -1;
        tip.Hide(this);
        Invalidate();
        if (pressed == BtnUpdate) UpdateClicked?.Invoke(this, EventArgs.Empty);
        else if (pressed == BtnSponsor) SponsorClicked?.Invoke(this, EventArgs.Empty);
        else if (pressed == BtnInfo) InfoClicked?.Invoke(this, EventArgs.Empty);
        else if (pressed == BtnMin) form.WindowState = FormWindowState.Minimized;
        else form.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            subFont.Dispose();
            infoFont.Dispose();
            tip.Dispose();
            icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
