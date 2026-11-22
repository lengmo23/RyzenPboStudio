using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>Slim draggable workspace title bar with page heading, global status pill, clock, and window buttons.</summary>
internal sealed class HeaderBar : Control
{
    private const int BtnMin = 0, BtnClose = 1, BtnCount = 2;
    private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private int _hoverBtn = -1, _pressedBtn = -1;
    private bool _hoverStatus;
    private bool _hoverNoteLink, _pressedNoteLink;
    private readonly ToolTip _tip = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 120 };
    private string? _tipText;

    private string _pageTitle = "";
    private string _statusText = L.T("Ready");
    private Color _statusColor = Theme.Success;
    private string _clockText = DateTime.Now.ToString("yyyy/MM/dd HH:mm");
    private string? _notePrefix, _noteLink, _noteUrl;

    private Font? _titleFont;
    private Font? _statusFont;
    private Font? _clockFont;
    private Rectangle _statusRect;
    private Rectangle _noteLinkRect;

    public event Action<string>? NoteLinkClicked;

    /// <summary>Version shown to the user, e.g. "V3.0.5" or "V2.2.0Beta". The SDK appends "+commit" build
    /// metadata to the informational version, which is dropped.</summary>
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
        ForeColor = Theme.TextHi;
        RebuildFonts(9F);
        Font = new Font(Theme.FontFamily, 9F);
    }

    public string PageTitle
    {
        get => _pageTitle;
        set { if (_pageTitle != value) { _pageTitle = value; Invalidate(); } }
    }

    public void SetStatus(string text, Color color)
    {
        if (_statusText == text && _statusColor == color) return;
        _statusText = text;
        _statusColor = color;
        if (_hoverStatus) SetTip(_statusText);
        Invalidate();
    }

    public void SetClock(string text)
    {
        if (_clockText == text) return;
        _clockText = text;
        Invalidate();
    }

    /// <summary>Shows a subtle attribution note with a clickable author handle right of the status capsule.</summary>
    public void SetPageNote(string? prefix, string? link, string? url)
    {
        if (_notePrefix == prefix && _noteLink == link && _noteUrl == url) return;
        _notePrefix = prefix;
        _noteLink = link;
        _noteUrl = url;
        _noteLinkRect = Rectangle.Empty;
        _hoverNoteLink = false;
        _pressedNoteLink = false;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RebuildFonts(Font?.Size ?? 9F);
        Invalidate();
    }

    private void RebuildFonts(float basePt)
    {
        var oldTitle = _titleFont;
        var oldStatus = _statusFont;
        var oldClock = _clockFont;
        (_titleFont, _statusFont, _clockFont) = CreateScaledFonts(basePt);
        oldTitle?.Dispose();
        oldStatus?.Dispose();
        oldClock?.Dispose();
    }

    private static (Font title, Font status, Font clock) CreateScaledFonts(float basePt)
    {
        float k = Math.Max(0.6f, basePt / 9f);
        return (
            new Font(Theme.FontFamily, 11.5f * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 8.75f * k),
            new Font(Theme.FontFamily, 8.5f * k)
        );
    }

    private Rectangle BtnRect(int i)
    {
        float s = Math.Max(0.7f, Height / 42f);
        int bw = Math.Max(26, (int)Math.Round(38 * s));
        int bh = Math.Max(20, (int)Math.Round(30 * s));
        int gap = Math.Max(2, (int)Math.Round(4 * s));
        int right = Math.Max(6, (int)Math.Round(10 * s));
        int top = (Height - bh) / 2;
        int idxFromRight = BtnCount - 1 - i;
        return new Rectangle(Width - right - (idxFromRight + 1) * bw - idxFromRight * gap, top, bw, bh);
    }

    private int HitButton(Point p)
    {
        for (int i = 0; i < BtnCount; i++)
            if (BtnRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Width <= 4 || Height <= 4 || _titleFont == null || _statusFont == null || _clockFont == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = Math.Max(0.7f, Height / 42f);
        int padX = (int)Math.Round(18 * s);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        // Right side: window buttons + clock
        for (int i = 0; i < BtnCount; i++) DrawWindowButton(g, i, s);

        var firstBtn = BtnRect(BtnMin);
        var clockSize = TextRenderer.MeasureText(g, _clockText, _clockFont, Size.Empty, flags);
        int clockRight = firstBtn.Left - (int)Math.Round(12 * s);
        var clockRect = new Rectangle(Math.Max(0, clockRight - clockSize.Width), 0, clockSize.Width, Height);
        TextRenderer.DrawText(g, _clockText, _clockFont, clockRect, Theme.TextLo, flags);

        // Left side: current page title + global status capsule
        var titleSize = TextRenderer.MeasureText(g, _pageTitle, _titleFont, Size.Empty, flags);
        var titleRect = new Rectangle(padX, 0, titleSize.Width, Height);
        TextRenderer.DrawText(g, _pageTitle, _titleFont, titleRect, Theme.TextHi, flags);

        int pillLeft = titleRect.Right + (int)Math.Round(14 * s);
        int maxPillRight = clockRect.Left - (int)Math.Round(12 * s);
        int availW = maxPillRight - pillLeft;
        if (availW > (int)Math.Round(48 * s))
        {
            int pillH = Math.Max(18, (int)Math.Round(25 * s));
            int dotD = Math.Max(6, (int)Math.Round(7.5f * s));
            int glowD = dotD + Math.Max(4, (int)Math.Round(5 * s));
            int innerPadL = (int)Math.Round(10 * s);
            int innerGap = (int)Math.Round(7 * s);
            int innerPadR = (int)Math.Round(11 * s);
            var textNeed = TextRenderer.MeasureText(g, _statusText, _statusFont, Size.Empty, flags);
            int pillW = Math.Min(availW, innerPadL + dotD + innerGap + textNeed.Width + innerPadR);
            int pillY = (Height - pillH) / 2;
            _statusRect = new Rectangle(pillLeft, pillY, pillW, pillH);

            using var pillPath = Theme.RoundRect(_statusRect, pillH / 2);
            using var pillFill = new SolidBrush(Theme.Surface);
            using var pillStroke = new Pen(Color.FromArgb(95, _statusColor), 1f);
            g.FillPath(pillFill, pillPath);
            g.DrawPath(pillStroke, pillPath);

            int dotCx = _statusRect.Left + innerPadL + dotD / 2;
            int dotCy = _statusRect.Top + pillH / 2;
            using (var glow = new SolidBrush(Color.FromArgb(55, _statusColor)))
                g.FillEllipse(glow, dotCx - glowD / 2f, dotCy - glowD / 2f, glowD, glowD);
            using (var dot = new SolidBrush(_statusColor))
                g.FillEllipse(dot, dotCx - dotD / 2f, dotCy - dotD / 2f, dotD, dotD);

            int textX = _statusRect.Left + innerPadL + dotD + innerGap;
            int textW = Math.Max(0, _statusRect.Right - innerPadR - textX);
            var textRect = new Rectangle(textX, _statusRect.Top, textW, pillH);
            TextRenderer.DrawText(g, _statusText, _statusFont, textRect, Theme.TextHi,
                flags | TextFormatFlags.EndEllipsis);
        }
        else
        {
            _statusRect = Rectangle.Empty;
        }

        _noteLinkRect = Rectangle.Empty;
        if (!string.IsNullOrEmpty(_noteLink))
        {
            int noteLeft = (_statusRect.IsEmpty ? pillLeft : _statusRect.Right) + (int)Math.Round(14 * s);
            int maxNoteRight = clockRect.Left - (int)Math.Round(16 * s);
            int noteAvail = maxNoteRight - noteLeft;
            var linkSize = TextRenderer.MeasureText(g, _noteLink, _statusFont, Size.Empty, flags);
            string prefixText = string.IsNullOrEmpty(_notePrefix) ? "" : _notePrefix + " ";
            var prefixSize = prefixText.Length > 0
                ? TextRenderer.MeasureText(g, prefixText, _statusFont, Size.Empty, flags)
                : Size.Empty;
            int prefixW = Math.Min(prefixSize.Width, Math.Max(0, noteAvail - linkSize.Width));
            if (noteAvail >= linkSize.Width + (prefixText.Length > 0 ? Math.Min(prefixSize.Width, (int)Math.Round(40 * s)) : 0))
            {
                if (prefixW > 0)
                {
                    var prefixRect = new Rectangle(noteLeft, 0, prefixW, Height);
                    TextRenderer.DrawText(g, prefixText, _statusFont, prefixRect, Theme.TextMid,
                        flags | TextFormatFlags.EndEllipsis);
                }
                int linkX = noteLeft + prefixW;
                var linkRect = new Rectangle(linkX, 0, linkSize.Width, Height);
                Color linkColor = _pressedNoteLink ? Theme.Accent : Theme.AccentHover;
                TextRenderer.DrawText(g, _noteLink, _statusFont, linkRect, linkColor, flags);
                if (_hoverNoteLink)
                {
                    int lineY = (Height + linkSize.Height) / 2 + 1;
                    using var uPen = new Pen(linkColor, 1f);
                    g.DrawLine(uPen, linkX, lineY, linkX + linkSize.Width - 1, lineY);
                }
                _noteLinkRect = new Rectangle(linkX, Math.Max(0, (Height - linkSize.Height) / 2 - 4),
                    linkSize.Width, linkSize.Height + 8);
            }
        }

        using var border = new Pen(Theme.BorderSubtle);
        g.DrawLine(border, 0, Height - 1, Width, Height - 1);
    }

    private void DrawWindowButton(Graphics g, int i, float s)
    {
        var r = BtnRect(i);
        bool hot = _hoverBtn == i;
        if (hot)
        {
            using var bg = new SolidBrush(i == BtnClose ? Theme.Danger : Theme.SurfaceAlt);
            using var path = Theme.RoundRect(r, 6);
            g.FillPath(bg, path);
        }

        int cx = r.Left + r.Width / 2;
        int cy = r.Top + r.Height / 2;
        int half = Math.Max(4, (int)Math.Round(4.5f * s));
        Color fg = hot ? (i == BtnClose ? Color.White : Theme.TextHi) : Theme.TextMid;
        using var pen = new Pen(fg, Math.Max(1.3f, 1.55f * s))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        if (i == BtnMin)
        {
            g.DrawLine(pen, cx - half, cy, cx + half, cy);
        }
        else
        {
            g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
            g.DrawLine(pen, cx - half, cy + half, cx + half, cy - half);
        }
    }

    private bool HitNoteLink(Point p) => !string.IsNullOrEmpty(_noteUrl) && _noteLinkRect.Contains(p);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hit = HitButton(e.Location);
        bool overLink = hit < 0 && HitNoteLink(e.Location);
        _hoverStatus = hit < 0 && !overLink && _statusRect.Contains(e.Location);
        SetTip(overLink ? _noteUrl : (_hoverStatus ? _statusText : null));
        if (hit != _hoverBtn || overLink != _hoverNoteLink)
        {
            _hoverBtn = hit;
            _hoverNoteLink = overLink;
            Cursor = (hit >= 0 || overLink) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverStatus = false;
        SetTip(null);
        if (_hoverBtn != -1 || _hoverNoteLink)
        {
            _hoverBtn = -1;
            _hoverNoteLink = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    private void SetTip(string? text)
    {
        if (_tipText == text) return;
        _tipText = text;
        _tip.SetToolTip(this, text);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressedBtn = HitButton(e.Location);
        if (_pressedBtn >= 0) return;
        if (HitNoteLink(e.Location))
        {
            _pressedNoteLink = true;
            Invalidate();
            return;
        }
        if (FindForm() is not { } form) return;
        ReleaseCapture();
        SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressedNoteLink)
        {
            _pressedNoteLink = false;
            Invalidate();
            if (e.Button == MouseButtons.Left && HitNoteLink(e.Location) && _noteUrl is { } url)
                NoteLinkClicked?.Invoke(url);
            return;
        }
        int pressed = _pressedBtn;
        _pressedBtn = -1;
        if (e.Button != MouseButtons.Left || pressed < 0 || HitButton(e.Location) != pressed || FindForm() is not { } form) return;
        _hoverBtn = -1;
        Invalidate();
        if (pressed == BtnMin) form.WindowState = FormWindowState.Minimized;
        else form.Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose();
            _statusFont?.Dispose();
            _clockFont?.Dispose();
            _titleFont = _statusFont = _clockFont = null;
            _tip.Dispose();
        }
        base.Dispose(disposing);
    }
}
