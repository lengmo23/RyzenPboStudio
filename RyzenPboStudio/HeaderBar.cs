namespace RyzenPboStudio;

/// <summary>监控区顶部标题栏（仿 HYDRA）：左侧图标 + 程序名 / 版本，右侧三行硬件信息。
/// 直接绘制，不嵌套 Label，避免在紧凑 TableLayout 里被压成零高度。</summary>
internal sealed class HeaderBar : Control
{
    private const int IconSize = 48;
    private const int InfoGap = 48;   // 标题块与硬件信息之间的间距

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
        BackColor = MonTheme.Bg;
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

        // 三行硬件信息竖向居中，超宽时末尾省略
        int lineH = TextRenderer.MeasureText(e.Graphics, "Ag", infoFont, Size.Empty, flags).Height + 3;
        int ly = (h - lineH * lines.Length) / 2;
        int width = Math.Max(0, ClientSize.Width - x - 4);
        foreach (string line in lines)
        {
            TextRenderer.DrawText(e.Graphics, line, infoFont, new Rectangle(x, ly, width, lineH),
                MonTheme.Fg, flags | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
            ly += lineH;
        }
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
