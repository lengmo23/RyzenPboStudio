using System.Drawing.Drawing2D;

namespace RyzenPboStudio;

/// <summary>暗色主题调色板与绘图工具。</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x16, 0x18, 0x1D);          // 窗口背景
    public static readonly Color Surface = Color.FromArgb(0x21, 0x24, 0x2B);     // 卡片
    public static readonly Color SurfaceAlt = Color.FromArgb(0x2A, 0x2E, 0x37);  // 输入框/次级面
    public static readonly Color Border = Color.FromArgb(0x36, 0x3B, 0x46);      // 描边
    public static readonly Color TextHi = Color.FromArgb(0xE6, 0xE8, 0xEC);      // 主文字
    public static readonly Color TextLo = Color.FromArgb(0x8B, 0x91, 0x9E);      // 次文字
    public static readonly Color Accent = Color.FromArgb(0xE2, 0x23, 0x1A);      // AMD 红
    public static readonly Color AccentHover = Color.FromArgb(0xF2, 0x47, 0x3F);
    public static readonly Color AccentText = Color.White;
    public static readonly Color Success = Color.FromArgb(0x3F, 0xB9, 0x50);
    public static readonly Color Warn = Color.FromArgb(0xD2, 0x99, 0x22);

    public const string FontFamily = "Microsoft YaHei UI";
    public const string MonoFamily = "Consolas";

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

/// <summary>圆角卡片面板。</summary>
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
        // UserPaint 下系统不再自动填背景，必须先把圆角外的四角刷成父级底色，
        // 否则四角会残留缓冲区里的黑色/杂色。
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

/// <summary>扁平圆角按钮，带悬停/禁用态。</summary>
internal sealed class PillButton : Button
{
    public int Radius { get; set; } = 10;
    public Color Normal { get; set; } = Theme.Accent;
    public Color Hover { get; set; } = Theme.AccentHover;
    public Color Disabled { get; set; } = Theme.SurfaceAlt;

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
        // 先用父级底色清掉圆角外的四角，避免黑色/红色残留边
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.RoundRect(r, Radius);
        Color fill = !Enabled ? Disabled : (_hover ? Hover : Normal);
        using var b = new SolidBrush(fill);
        e.Graphics.FillPath(b, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, r,
            Enabled ? ForeColor : Theme.TextLo,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>沉浸式导航标签（仿 HYDRA 底栏）：透明融入背景，激活时亮白文字 + 底部红色短下划线。</summary>
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
