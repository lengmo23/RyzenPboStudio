using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>窗口标题栏（仿 HYDRA，窗口本身无边框）：左侧图标 + 程序名 / 版本，中间三行硬件信息，
/// 右上角最小化 / 关闭；按住空白处拖动窗口。直接绘制，不嵌套 Label。</summary>
internal sealed class HeaderBar : Control
{
    private const int IconSize = 48;
    private const int InfoGap = 48;   // 标题块与硬件信息之间的间距
    private const int BtnW = 46, BtnH = 34;   // 窗口按钮热区，与系统标题栏按钮同尺寸

    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private int hoverBtn = -1, pressedBtn = -1;   // 0 = 最小化，1 = 关闭

    private readonly Font titleFont = new(Theme.FontFamily, 17F, FontStyle.Bold);
    private readonly Font subFont = new(Theme.FontFamily, 9F);
    private readonly Font infoFont = new(Theme.FontFamily, 9F);
    private readonly Bitmap? icon;
    private readonly string title = "AMD Ryzen PBO Studio";
    private readonly string subtitle;
    private readonly string[] lines = { "", "", "" };

    public HeaderBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        ForeColor = MonTheme.Fg;

        string version = typeof(HeaderBar).Assembly.GetName().Version?.ToString(3) ?? "?";
        subtitle = $"v{version} · PBO / Curve Optimizer";
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

    /// <summary>设置第 index 行（0~2）硬件信息；内容不变时不重绘。可在 UI 线程任意时刻调用。</summary>
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

        // 三行硬件信息竖向居中，避开右上角窗口按钮，超宽时末尾省略
        int lineH = TextRenderer.MeasureText(e.Graphics, "Ag", infoFont, Size.Empty, flags).Height + 3;
        int ly = (h - lineH * lines.Length) / 2;
        int width = Math.Max(0, ClientSize.Width - 2 * BtnW - 8 - x);
        foreach (string line in lines)
        {
            TextRenderer.DrawText(e.Graphics, line, infoFont, new Rectangle(x, ly, width, lineH),
                MonTheme.Fg, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            ly += lineH;
        }

        for (int i = 0; i < 2; i++) DrawWindowButton(e.Graphics, i);
        using var border = new Pen(Theme.Border);
        e.Graphics.DrawLine(border, 0, h - 1, ClientSize.Width, h - 1);
    }

    private Rectangle BtnRect(int i) => new(ClientSize.Width - (2 - i) * BtnW, 0, BtnW, BtnH);

    private int HitButton(Point p)
    {
        for (int i = 0; i < 2; i++) if (BtnRect(i).Contains(p)) return i;
        return -1;
    }

    /// <summary>最小化为一横、关闭为一叉；悬停时最小化底色变浅，关闭变 AMD 红（同 Windows 习惯）。</summary>
    private void DrawWindowButton(Graphics g, int i)
    {
        var r = BtnRect(i);
        if (hoverBtn == i)
            using (var bg = new SolidBrush(i == 1 ? Theme.Accent : Theme.Border)) g.FillRectangle(bg, r);

        int cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(hoverBtn == 1 && i == 1 ? Theme.AccentText : MonTheme.Fg, 1.2f);
        if (i == 0) g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
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
        // 空白处按下：交给系统当作标题栏拖动
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
        if (pressed == 0) form.WindowState = FormWindowState.Minimized;
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
