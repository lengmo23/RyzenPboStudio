using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>Draws the borderless title bar and handles dragging from its empty area.</summary>
internal sealed class HeaderBar : Control
{
    private const int IconSize = 48;
    private const int InfoGap = 48; // Gap between title and hardware details.
    private const int BtnW = 46, BtnH = 34; // Match the system title-button hit area.

    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private int hoverBtn = -1, pressedBtn = -1; // Info, minimize, close.

    public event EventHandler? InfoClicked;

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
        int width = Math.Max(0, ClientSize.Width - 3 * BtnW - 8 - x);
        foreach (string line in lines)
        {
            TextRenderer.DrawText(e.Graphics, line, infoFont, new Rectangle(x, ly, width, lineH),
                MonTheme.Fg, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            ly += lineH;
        }

        for (int i = 0; i < 3; i++) DrawWindowButton(e.Graphics, i);
        using var border = new Pen(Theme.Border);
        e.Graphics.DrawLine(border, 0, h - 1, ClientSize.Width, h - 1);
    }

    private Rectangle BtnRect(int i) => new(ClientSize.Width - (3 - i) * BtnW, 0, BtnW, BtnH);

    private int HitButton(Point p)
    {
        for (int i = 0; i < 3; i++) if (BtnRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>Draws a window button; the close button turns red on hover.</summary>
    private void DrawWindowButton(Graphics g, int i)
    {
        var r = BtnRect(i);
        if (hoverBtn == i)
            using (var bg = new SolidBrush(i == 2 ? Theme.Accent : Theme.Border)) g.FillRectangle(bg, r);

        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(hoverBtn == 2 && i == 2 ? Theme.AccentText : MonTheme.Fg, 1.2f);
        if (i == 0)
        {
            g.DrawEllipse(pen, cx - 8, cy - 8, 16, 16);
            TextRenderer.DrawText(g, "i", infoFont, new Rectangle(cx - 5, cy - 8, 10, 16),
                MonTheme.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        else if (i == 1) g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
        else
        {
            g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
            g.DrawLine(pen, cx - 5, cy + 5, cx + 5, cy - 5);
        }
        g.SmoothingMode = SmoothingMode.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitButton(e.Location);
        if (hit != hoverBtn) { hoverBtn = hit; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverBtn != -1) { hoverBtn = -1; Invalidate(); }
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
        Invalidate();
        if (pressed == 0) InfoClicked?.Invoke(this, EventArgs.Empty);
        else if (pressed == 1) form.WindowState = FormWindowState.Minimized;
        else form.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            subFont.Dispose();
            infoFont.Dispose();
            icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
