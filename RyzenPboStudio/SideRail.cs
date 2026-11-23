using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>Full-height left navigation rail with brand header, page tabs, hardware spec card, and bottom utility dock.</summary>
internal sealed class SideRail : Panel
{
    public const int RailWidth = 232;

    private readonly NavRailButton _pboBtn;
    private readonly NavRailButton _testingBtn;
    private readonly NavRailButton _memoryBtn;
    private readonly NavRailButton _loggingBtn;
    private readonly SysSpecCard _specCard;
    private readonly UtilityStrip _utilStrip;
    private readonly ToolTip _tip = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 120 };

    public AccentToggle BootApplyToggle { get; }

    public event EventHandler? PboClicked;
    public event EventHandler? TestingClicked;
    public event EventHandler? MemoryClicked;
    public event EventHandler? LoggingClicked;
    public event EventHandler? UpdateClicked;
    public event EventHandler? SponsorClicked;
    public event EventHandler? InfoClicked;
    public event EventHandler? SettingsClicked;
    public event EventHandler? VersionShattered;

    public string UpdateTip
    {
        get => _utilStrip.UpdateTip;
        set => _utilStrip.UpdateTip = value;
    }

    public bool UpdateAvailable
    {
        get => _utilStrip.UpdateAvailable;
        set => _utilStrip.UpdateAvailable = value;
    }

    public SideRail()
    {
        Width = RailWidth;
        Dock = DockStyle.Left;
        BackColor = Theme.RailBg;
        Padding = new Padding(0, 0, 1, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        var brand = new BrandHeader { Dock = DockStyle.Top, Height = 72 };
        brand.VersionShattered += (_, e) => VersionShattered?.Invoke(this, e);

        var navPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 198,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.RailBg,
            Padding = new Padding(12, 12, 12, 8),
            Margin = new Padding(0),
        };
        navPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        navPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        navPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        navPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        navPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        // Order matches MainForm.Page: PBO, Testing, Memory, Logging.
        _pboBtn = MakeNavButton(L.T("PBO Tuning"), NavIconKind.Sliders, new Padding(0, 0, 0, 6), () => PboClicked?.Invoke(this, EventArgs.Empty));
        _testingBtn = MakeNavButton(L.T("Stability Test"), NavIconKind.Pulse, new Padding(0, 0, 0, 6), () => TestingClicked?.Invoke(this, EventArgs.Empty));
        _memoryBtn = MakeNavButton(L.T("Memory OC"), NavIconKind.Memory, new Padding(0, 0, 0, 6), () => MemoryClicked?.Invoke(this, EventArgs.Empty));
        _loggingBtn = MakeNavButton(L.T("Run Log"), NavIconKind.Terminal, new Padding(0), () => LoggingClicked?.Invoke(this, EventArgs.Empty));

        navPanel.Controls.Add(_pboBtn, 0, 0);
        navPanel.Controls.Add(_testingBtn, 0, 1);
        navPanel.Controls.Add(_memoryBtn, 0, 2);
        navPanel.Controls.Add(_loggingBtn, 0, 3);

        _specCard = new SysSpecCard { Dock = DockStyle.Fill };
        var specWrap = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.RailBg,
            Padding = new Padding(12, 2, 12, 10),
        };
        specWrap.Controls.Add(_specCard);

        var bottomDock = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 94,
            BackColor = Theme.RailBg,
            Padding = new Padding(12, 10, 12, 8),
        };
        bottomDock.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.BorderSubtle);
            e.Graphics.DrawLine(pen, 12, 0, bottomDock.Width - 13, 0);
        };

        BootApplyToggle = new AccentToggle
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = L.T("Apply PBO at startup"),
            Font = new Font(Theme.FontFamily, 8.75F),
            BackColor = Theme.RailBg,
            ForeColor = Theme.TextMid,
        };

        var spacer = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Theme.RailBg };

        _utilStrip = new UtilityStrip(_tip) { Dock = DockStyle.Top, Height = 32 };
        _utilStrip.UpdateClicked += (_, e) => UpdateClicked?.Invoke(this, e);
        _utilStrip.SponsorClicked += (_, e) => SponsorClicked?.Invoke(this, e);
        _utilStrip.InfoClicked += (_, e) => InfoClicked?.Invoke(this, e);
        _utilStrip.SettingsClicked += (_, e) => SettingsClicked?.Invoke(this, e);

        bottomDock.Controls.Add(_utilStrip);
        bottomDock.Controls.Add(spacer);
        bottomDock.Controls.Add(BootApplyToggle);

        // Dock order: Fill first, then Top/Bottom edges from inner to outer.
        Controls.Add(specWrap);
        Controls.Add(bottomDock);
        Controls.Add(navPanel);
        Controls.Add(brand);
    }

    private static NavRailButton MakeNavButton(string text, NavIconKind kind, Padding margin, Action onClick)
    {
        var b = new NavRailButton
        {
            Text = text,
            IconKind = kind,
            Dock = DockStyle.Fill,
            Margin = margin,
            Font = new Font(Theme.FontFamily, 9.75F, FontStyle.Bold),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public void SetActivePage(int index)
    {
        _pboBtn.Active = index == 0;
        _testingBtn.Active = index == 1;
        _memoryBtn.Active = index == 2;
        _loggingBtn.Active = index == 3;
        _pboBtn.Invalidate();
        _testingBtn.Invalidate();
        _memoryBtn.Invalidate();
        _loggingBtn.Invalidate();
    }

    public void SetSpecs(string cpu, string memCap, string memBus, string board, string firmware, string gpu, string os)
        => _specCard.SetSpecs(cpu, memCap, memBus, board, firmware, gpu, os);

    public void SetMemoryBus(string memCap, string memBus)
        => _specCard.SetMemoryBus(memCap, memBus);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.BorderSubtle);
        e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Draggable brand header: app icon, the full product name on one line, and the version pill.</summary>
    private sealed class BrandHeader : Control
    {
        private const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly Bitmap? _icon;
        private Font? _titleFont;
        private Font? _verFont;
        private Rectangle _pillRect;
        private int _verClicks;
        private bool _shattered;
        private bool _pillBlack = AppSettings.Current.PboBiosUnlocked;
        private System.Windows.Forms.Timer? _animTimer;
        private long _animStartMs;
        private Shard[]? _shards;

        private const float WobbleMs = 340f;
        private const float ShatterMs = 520f;
        private const float TotalAnimMs = WobbleMs + ShatterMs;

        private readonly record struct Shard(float X0, float Y0, float Vx, float Vy, float Rot0, float RotSpeed, PointF[] Poly, Color Color);

        public event EventHandler? VersionShattered;

        public BrandHeader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.RailBg;
            RebuildFonts(9F);
            Font = new Font(Theme.FontFamily, 9F);

            try
            {
                using var stream = typeof(BrandHeader).Assembly.GetManifestResourceStream("RyzenPboStudio.ICON.ico");
                if (stream != null)
                {
                    using var ico = new Icon(stream, 48, 48);
                    _icon = ico.ToBitmap();
                }
            }
            catch { _icon = null; }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            RebuildFonts(Font?.Size ?? 9F);
            Invalidate();
        }

        private void RebuildFonts(float basePt)
        {
            var oldT = _titleFont;
            var oldV = _verFont;
            (_titleFont, _verFont) = CreateFonts(basePt);
            oldT?.Dispose();
            oldV?.Dispose();
        }

        private static (Font title, Font ver) CreateFonts(float basePt)
        {
            float k = Math.Max(0.6f, basePt / 9f);
            return (
                new Font(Theme.FontFamily, 10F * k, FontStyle.Bold),
                new Font(Theme.FontFamily, 7.75F * k, FontStyle.Bold)
            );
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Width <= 4 || Height <= 4 || _titleFont == null || _verFont == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            float s = Math.Max(0.7f, Height / 72f);
            int padX = (int)Math.Round(15 * s);
            int iconSz = Math.Max(22, (int)Math.Round(34 * s));
            int iconY = (Height - iconSz) / 2;
            if (_icon != null)
                g.DrawImage(_icon, padX, iconY, iconSz, iconSz);

            int textX = padX + iconSz + (int)Math.Round(10 * s);
            int textW = Math.Max(0, Width - textX - (int)Math.Round(10 * s));
            var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            const string appTitle = "AMD Ryzen PBO Studio";
            string ver = HeaderBar.DisplayVersion;
            // Measured without EndEllipsis, which shrinks widths measured against an empty size. The full name only just
            // fits at 10 pt; when the rail is narrower the font steps down until it fits, since hinting keeps the width
            // from shrinking in proportion to the size.
            var measure = flags & ~TextFormatFlags.EndEllipsis;
            var titleFont = _titleFont;
            var titleSz = TextRenderer.MeasureText(g, appTitle, titleFont, Size.Empty, measure);
            Font? fitted = null;
            for (float pt = titleFont.Size - 0.25f; titleSz.Width > textW && textW > 0 && pt >= 6f; pt -= 0.25f)
            {
                fitted?.Dispose();
                fitted = new Font(_titleFont.FontFamily, pt, _titleFont.Style);
                titleFont = fitted;
                titleSz = TextRenderer.MeasureText(g, appTitle, titleFont, Size.Empty, measure);
            }
            var verSz = TextRenderer.MeasureText(g, ver, _verFont, Size.Empty, measure);

            int pillPadX = (int)Math.Round(8 * s);
            int pillH = verSz.Height + (int)Math.Round(4 * s);
            int pillW = Math.Min(textW, verSz.Width + pillPadX * 2);
            int gap = (int)Math.Round(5 * s);
            int totalH = titleSz.Height + gap + pillH;
            int topY = (Height - totalH) / 2;

            TextRenderer.DrawText(g, appTitle, titleFont, new Rectangle(textX, topY, Math.Max(textW, titleSz.Width + 1), titleSz.Height), Theme.TextHi, measure);
            fitted?.Dispose();

            if (pillW > 8)
            {
                var pillRect = new Rectangle(textX, topY + titleSz.Height + gap, pillW, pillH);
                _pillRect = pillRect;
                DrawVersionPill(g, pillRect, ver);
            }
            else
            {
                _pillRect = Rectangle.Empty;
            }

            int lineInset = (int)Math.Round(14 * s);
            using var divider = new Pen(Theme.BorderSubtle);
            g.DrawLine(divider, lineInset, Height - 1, Math.Max(lineInset, Width - lineInset), Height - 1);
        }

        private void DrawVersionPill(Graphics g, Rectangle pillRect, string ver)
        {
            float elapsed = _animTimer != null ? Environment.TickCount64 - _animStartMs : -1f;
            if (elapsed >= 0f && elapsed < WobbleMs)
            {
                // Phase 1: wobble/twist the red pill and sketch hairline cracks right before shattering.
                float p = elapsed / WobbleMs;
                float angle = MathF.Sin(p * MathF.PI * 5f) * (6f + 9f * p);
                float dx = MathF.Sin(p * MathF.PI * 7f) * (1.2f + 1.8f * p);
                float dy = MathF.Cos(p * MathF.PI * 6f) * (0.8f + 1.0f * p);
                float cx = pillRect.X + pillRect.Width * 0.5f;
                float cy = pillRect.Y + pillRect.Height * 0.5f;

                var state = g.Save();
                g.TranslateTransform(cx + dx, cy + dy);
                g.RotateTransform(angle);
                g.TranslateTransform(-cx, -cy);

                using var pillPath = Theme.RoundRect(pillRect, pillRect.Height / 2);
                using var pillBg = new SolidBrush(Theme.AccentSoft);
                using var pillPen = new Pen(Color.FromArgb(140, Theme.Accent), 1.15f);
                g.FillPath(pillBg, pillPath);
                g.DrawPath(pillPen, pillPath);

                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
                using var tb = new SolidBrush(Theme.Accent);
                g.DrawString(ver, _verFont!, tb, pillRect, sf);

                if (p > 0.48f)
                {
                    int alpha = Math.Clamp((int)(230 * (p - 0.48f) / 0.52f), 0, 230);
                    using var crack = new Pen(Color.FromArgb(alpha, 255, 200, 195), 1.1f);
                    float yMid = pillRect.Y + pillRect.Height * 0.5f;
                    g.DrawLines(crack, new[]
                    {
                        new PointF(pillRect.X + pillRect.Width * 0.28f, pillRect.Y + 1),
                        new PointF(pillRect.X + pillRect.Width * 0.36f, yMid),
                        new PointF(pillRect.X + pillRect.Width * 0.31f, pillRect.Bottom - 1),
                    });
                    g.DrawLines(crack, new[]
                    {
                        new PointF(pillRect.X + pillRect.Width * 0.68f, pillRect.Y + 1),
                        new PointF(pillRect.X + pillRect.Width * 0.61f, yMid + 1),
                        new PointF(pillRect.X + pillRect.Width * 0.72f, pillRect.Bottom - 1),
                    });
                }

                g.Restore(state);
                return;
            }

            bool black = _pillBlack || elapsed >= WobbleMs;
            using (var pillPath = Theme.RoundRect(pillRect, pillRect.Height / 2))
            {
                Color bg = black ? Color.FromArgb(0x06, 0x07, 0x0A) : Theme.AccentSoft;
                Color border = black ? Color.FromArgb(0x2B, 0x31, 0x3E) : Color.FromArgb(95, Theme.Accent);
                Color fg = black ? Color.FromArgb(0x8A, 0x94, 0xA6) : Theme.Accent;
                using var pillBg = new SolidBrush(bg);
                using var pillPen = new Pen(border);
                g.FillPath(pillBg, pillPath);
                g.DrawPath(pillPen, pillPath);

                TextRenderer.DrawText(g, ver, _verFont!, pillRect, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }

            // Phase 2: flying red fragments bursting off the newly blackened pill.
            if (elapsed >= WobbleMs && _shards != null)
            {
                float q = Math.Clamp((elapsed - WobbleMs) / ShatterMs, 0f, 1f);
                float t = (elapsed - WobbleMs) / 1000f;
                int alpha = Math.Clamp((int)(255 * MathF.Pow(1f - q, 1.35f)), 0, 255);
                if (alpha > 0)
                {
                    foreach (var sh in _shards)
                    {
                        float x = sh.X0 + sh.Vx * t;
                        float y = sh.Y0 + sh.Vy * t + 145f * t * t;
                        var state = g.Save();
                        g.TranslateTransform(x, y);
                        g.RotateTransform(sh.Rot0 + sh.RotSpeed * t);
                        using var sb = new SolidBrush(Color.FromArgb(alpha, sh.Color));
                        g.FillPolygon(sb, sh.Poly);
                        g.Restore(state);
                    }
                }
            }
        }

        private void TriggerVersionShatter()
        {
            _shattered = true;
            _animStartMs = Environment.TickCount64;
            _shards = CreateShards(_pillRect);
            _animTimer?.Stop();
            _animTimer?.Dispose();
            _animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animTimer.Tick += (_, _) =>
            {
                if (Environment.TickCount64 - _animStartMs >= TotalAnimMs)
                {
                    _pillBlack = true;
                    _shards = null;
                    _animTimer?.Stop();
                    _animTimer?.Dispose();
                    _animTimer = null;
                }
                Invalidate();
            };
            _animTimer.Start();
            Invalidate();
            VersionShattered?.Invoke(this, EventArgs.Empty);
        }

        private static Shard[] CreateShards(Rectangle r)
        {
            var rng = new Random(0x52595A4E);
            Color[] palette = { Theme.Accent, Theme.AccentHover, Color.FromArgb(0x9E, 0x1B, 0x18), Theme.AccentSoft };
            const int count = 18;
            var shards = new Shard[count];
            float cx = r.X + r.Width * 0.5f;
            float cy = r.Y + r.Height * 0.5f;
            for (int i = 0; i < count; i++)
            {
                float fx = (float)(r.X + rng.NextDouble() * Math.Max(1, r.Width));
                float fy = (float)(r.Y + rng.NextDouble() * Math.Max(1, r.Height));
                float dx = fx - cx;
                float dy = fy - cy;
                float len = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
                float speed = 42f + (float)rng.NextDouble() * 68f;
                float vx = dx / len * speed + ((float)rng.NextDouble() - 0.5f) * 24f;
                float vy = dy / len * speed - 18f - (float)rng.NextDouble() * 34f;
                float sz = 2.4f + (float)rng.NextDouble() * 3.2f;
                var poly = new[]
                {
                    new PointF(-sz, -sz * 0.55f),
                    new PointF(sz * 0.85f, -sz * 0.35f),
                    new PointF(sz * 0.25f, sz * 0.9f),
                };
                shards[i] = new Shard(fx, fy, vx, vy, (float)(rng.NextDouble() * 360.0), ((float)rng.NextDouble() - 0.5f) * 540f, poly, palette[i % palette.Length]);
            }
            return shards;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (!_pillRect.IsEmpty && Rectangle.Inflate(_pillRect, 4, 4).Contains(e.Location))
            {
                if (!_pillBlack && !_shattered && ++_verClicks >= 5)
                    TriggerVersionShatter();
                return;
            }
            if (FindForm() is not { } form) return;
            ReleaseCapture();
            SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animTimer?.Stop();
                _animTimer?.Dispose();
                _animTimer = null;
                _titleFont?.Dispose();
                _verFont?.Dispose();
                _titleFont = _verFont = null;
                _icon?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Word-wrapped hardware spec card at the bottom of its area, just above the boot-apply toggle.</summary>
    private sealed class SysSpecCard : Control
    {
        private static readonly Color CardFill = Color.FromArgb(0x12, 0x16, 0x1F);
        private static readonly string[] Labels = { L.T("Processor"), L.T("Memory and Bus"), L.T("Motherboard"), L.T("Firmware"), L.T("Graphics and System") };

        private readonly string[] _primary = { "—", "—", "—", "—", "—" };
        private readonly string[] _secondary = { "", "", "", "", "" };

        private Font? _hdrFont;
        private Font? _lblFont;
        private Font? _valFont;
        private Font? _subTextFont;
        private Font? _subMonoFont;

        public SysSpecCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.RailBg;
            RebuildFonts(8.5F);
            Font = new Font(Theme.FontFamily, 8.5F);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            RebuildFonts(Font?.Size ?? 8.5F);
            Invalidate();
        }

        private void RebuildFonts(float basePt)
        {
            var a = _hdrFont; var b = _lblFont; var c = _valFont; var d = _subTextFont; var m = _subMonoFont;
            (_hdrFont, _lblFont, _valFont, _subTextFont, _subMonoFont) = CreateFonts(basePt);
            a?.Dispose(); b?.Dispose(); c?.Dispose(); d?.Dispose(); m?.Dispose();
        }

        private static (Font hdr, Font lbl, Font val, Font subText, Font subMono) CreateFonts(float basePt)
        {
            float k = Math.Max(0.6f, basePt / 8.5f);
            return (
                new Font(Theme.FontFamily, 8.25F * k, FontStyle.Bold),
                new Font(Theme.FontFamily, 7.5F * k),
                new Font(Theme.FontFamily, 8.25F * k),
                new Font(Theme.FontFamily, 7.75F * k),
                new Font(Theme.FontFamily, 8.0F * k)
            );
        }

        public void SetSpecs(string cpu, string memCap, string memBus, string board, string firmware, string gpu, string os)
        {
            bool changed = false;
            changed |= SetField(_primary, 0, cpu);
            changed |= SetField(_primary, 1, memCap);
            if (!string.IsNullOrWhiteSpace(memBus)) changed |= SetField(_secondary, 1, memBus);
            changed |= SetField(_primary, 2, board);
            changed |= SetField(_primary, 3, firmware);
            changed |= SetField(_primary, 4, gpu);
            changed |= SetField(_secondary, 4, os);
            if (changed) Invalidate();
        }

        public void SetMemoryBus(string memCap, string memBus)
        {
            bool changed = false;
            changed |= SetField(_primary, 1, memCap);
            changed |= SetField(_secondary, 1, memBus);
            if (changed) Invalidate();
        }

        private static bool SetField(string[] arr, int idx, string value)
        {
            string v = string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
            if (arr[idx] == v) return false;
            arr[idx] = v;
            return true;
        }

        private static int MeasureWrappedHeight(Graphics g, string text, Font font, int width, TextFormatFlags flags, int minH)
        {
            if (width <= 4 || string.IsNullOrEmpty(text)) return minH;
            int h = TextRenderer.MeasureText(g, text, font, new Size(width, int.MaxValue), flags).Height;
            return Math.Max(minH, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? BackColor);
            if (Width <= 4 || Height <= 20 || _hdrFont == null || _lblFont == null || _valFont == null
                || _subTextFont == null || _subMonoFont == null) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float s = Math.Max(0.7f, Width / 206f);
            int padX = (int)Math.Round(12 * s);
            int padY = (int)Math.Round(10 * s);
            int innerW = Math.Max(1, Width - padX * 2);

            var hdrFlags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            var wrapFlags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak
                          | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

            string hdrText = L.T("System Overview");
            int hdrH = TextRenderer.MeasureText(g, hdrText, _hdrFont, Size.Empty, hdrFlags).Height;
            int divOffset = padY + hdrH + (int)Math.Round(6 * s);
            int bodyOffset = divOffset + (int)Math.Round(8 * s);

            int lblH = TextRenderer.MeasureText(g, "Ag", _lblFont, Size.Empty, hdrFlags).Height;
            int valLineH = TextRenderer.MeasureText(g, "Ag", _valFont, Size.Empty, hdrFlags).Height;
            int subTextLineH = TextRenderer.MeasureText(g, "Ag", _subTextFont, Size.Empty, hdrFlags).Height;
            int subMonoLineH = TextRenderer.MeasureText(g, "Ag", _subMonoFont, Size.Empty, hdrFlags).Height;
            int innerGap = Math.Max(1, (int)Math.Round(2 * s));
            int subGap = Math.Max(2, (int)Math.Round(3 * s));

            int[] valHeights = new int[5];
            int[] subHeights = new int[5];
            int totalNeeded = 0;
            for (int i = 0; i < 5; i++)
            {
                string p = _primary[i].Length > 0 ? _primary[i] : "—";
                valHeights[i] = MeasureWrappedHeight(g, p, _valFont, innerW, wrapFlags, valLineH);
                bool hasSub = _secondary[i].Length > 0;
                if (hasSub)
                {
                    Font subFont = i == 1 ? _subMonoFont : _subTextFont;
                    int minSubH = i == 1 ? subMonoLineH : subTextLineH;
                    subHeights[i] = MeasureWrappedHeight(g, _secondary[i], subFont, innerW, wrapFlags, minSubH);
                }
                totalNeeded += lblH + innerGap + valHeights[i] + (hasSub ? subGap + subHeights[i] : 0);
            }

            // The card is as tall as its content and sits at the bottom of the area.
            int interGap = (int)Math.Round(7 * s);
            int cardH = Math.Min(Height - 1, bodyOffset + totalNeeded + interGap * 4 + padY);
            int top = Height - 1 - cardH;
            int divY = top + divOffset;
            int y = top + bodyOffset;
            int bottomLimit = Height - 1 - padY;
            using (var path = Theme.RoundRect(new Rectangle(0, top, Width - 1, cardH), 10))
            using (var fill = new SolidBrush(CardFill))
            using (var border = new Pen(Theme.BorderSubtle))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }
            TextRenderer.DrawText(g, hdrText, _hdrFont, new Rectangle(padX, top + padY, innerW, hdrH), Theme.TextLo, hdrFlags);
            using (var divPen = new Pen(Theme.BorderSubtle))
                g.DrawLine(divPen, padX, divY, Width - padX, divY);

            for (int i = 0; i < 5; i++)
            {
                if (y + lblH > bottomLimit) break;
                TextRenderer.DrawText(g, Labels[i], _lblFont, new Rectangle(padX, y, innerW, lblH), Theme.TextLo, hdrFlags);
                y += lblH + innerGap;

                string p = _primary[i].Length > 0 ? _primary[i] : "—";
                int drawValH = Math.Min(valHeights[i], Math.Max(0, bottomLimit - y));
                if (drawValH > 0)
                {
                    TextRenderer.DrawText(g, p, _valFont, new Rectangle(padX, y, innerW, drawValH), Theme.TextHi, wrapFlags);
                    y += drawValH;
                }

                if (_secondary[i].Length > 0 && y + subGap < bottomLimit)
                {
                    y += subGap;
                    int drawSubH = Math.Min(subHeights[i], Math.Max(0, bottomLimit - y));
                    if (drawSubH > 0)
                    {
                        Color subColor = i == 1 ? Theme.Telemetry : Theme.TextMid;
                        Font subFont = i == 1 ? _subMonoFont : _subTextFont;
                        TextRenderer.DrawText(g, _secondary[i], subFont, new Rectangle(padX, y, innerW, drawSubH), subColor, wrapFlags);
                        y += drawSubH;
                    }
                }

                y += interGap;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hdrFont?.Dispose();
                _lblFont?.Dispose();
                _valFont?.Dispose();
                _subTextFont?.Dispose();
                _subMonoFont?.Dispose();
                _hdrFont = _lblFont = _valFont = _subTextFont = _subMonoFont = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Icon-only footer strip for Settings, Update, Sponsor and About; each names itself in a tooltip.</summary>
    private sealed class UtilityStrip : Control
    {
        private const int BtnSettings = 0, BtnUpdate = 1, BtnSponsor = 2, BtnInfo = 3, BtnCount = 4;

        private readonly ToolTip _tip;
        private int _hoverBtn = -1, _pressedBtn = -1;
        private string _updateTip = L.T("Check for updates");
        private bool _updateAvailable;

        public event EventHandler? UpdateClicked;
        public event EventHandler? SponsorClicked;
        public event EventHandler? InfoClicked;
        public event EventHandler? SettingsClicked;

        public string UpdateTip
        {
            get => _updateTip;
            set
            {
                if (_updateTip == value) return;
                _updateTip = value;
                if (_hoverBtn == BtnUpdate) _tip.SetToolTip(this, _updateTip);
            }
        }

        public bool UpdateAvailable
        {
            get => _updateAvailable;
            set { if (_updateAvailable != value) { _updateAvailable = value; Invalidate(); } }
        }

        public UtilityStrip(ToolTip tip)
        {
            _tip = tip;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.RailBg;
            Font = new Font(Theme.FontFamily, 8F);
        }

        private Rectangle BtnRect(int i)
        {
            int gap = Math.Max(4, Width / 36);
            int bw = Math.Max(16, (Width - gap * (BtnCount - 1)) / BtnCount);
            int x = i * (bw + gap);
            if (i == BtnCount - 1) bw = Math.Max(16, Width - x);
            return new Rectangle(x, 0, bw, Height);
        }

        private int Hit(Point p)
        {
            for (int i = 0; i < BtnCount; i++)
                if (BtnRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? BackColor);
            if (Width <= 10 || Height <= 6) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float s = Math.Max(0.7f, Height / 32f);

            for (int i = 0; i < BtnCount; i++)
            {
                var r = BtnRect(i);
                bool hot = _hoverBtn == i;
                Color fill = hot ? Theme.SurfaceHover : Theme.SurfaceAlt;
                using (var path = Theme.RoundRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), 7))
                using (var bg = new SolidBrush(fill))
                using (var pen = new Pen(hot ? Theme.Border : Theme.BorderSubtle))
                {
                    g.FillPath(bg, path);
                    g.DrawPath(pen, path);
                }

                Color fg = hot ? Theme.TextHi : Theme.TextMid;
                int iconSz = Math.Max(12, (int)Math.Round(16 * s));
                DrawUtilIcon(g, i, r.Left + r.Width / 2, r.Top + r.Height / 2, iconSz, fg);

                if (i == BtnUpdate && _updateAvailable)
                {
                    int dotD = Math.Max(5, (int)Math.Round(6 * s));
                    using var dotBrush = new SolidBrush(Theme.Accent);
                    g.FillEllipse(dotBrush, r.Right - dotD - 4, r.Top + 4, dotD, dotD);
                }
            }
        }

        private static void DrawUtilIcon(Graphics g, int i, int cx, int cy, int sz, Color color)
        {
            float r = sz * 0.44f;
            using var pen = new Pen(color, Math.Max(1.25f, sz / 9.5f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            if (i == BtnSettings)
            {
                // Gear: eight teeth around a ring with a hub.
                float ring = r * 0.68f, hub = r * 0.3f;
                for (int k = 0; k < 8; k++)
                {
                    double a = k * Math.PI / 4;
                    float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
                    g.DrawLine(pen, cx + dx * ring, cy + dy * ring, cx + dx * r, cy + dy * r);
                }
                g.DrawEllipse(pen, cx - ring, cy - ring, ring * 2, ring * 2);
                g.DrawEllipse(pen, cx - hub, cy - hub, hub * 2, hub * 2);
            }
            else if (i == BtnUpdate)
            {
                g.DrawLine(pen, cx, cy - r, cx, cy + r * 0.45f);
                g.DrawLines(pen, new[]
                {
                    new PointF(cx - r * 0.55f, cy - r * 0.1f),
                    new PointF(cx, cy + r * 0.48f),
                    new PointF(cx + r * 0.55f, cy - r * 0.1f),
                });
                g.DrawLines(pen, new[]
                {
                    new PointF(cx - r * 0.95f, cy + r * 0.45f),
                    new PointF(cx - r * 0.95f, cy + r * 0.95f),
                    new PointF(cx + r * 0.95f, cy + r * 0.95f),
                    new PointF(cx + r * 0.95f, cy + r * 0.45f),
                });
            }
            else if (i == BtnSponsor)
            {
                float x0 = cx - r * 0.85f, x1 = cx + r * 0.55f, top = cy - r * 0.25f, bot = cy + r * 0.95f;
                g.DrawRectangle(pen, x0, top, x1 - x0, bot - top);
                g.DrawArc(pen, x1 - 1, cy - r * 0.05f, r * 0.65f, r * 0.7f, -90, 180);
                g.DrawLine(pen, cx - r * 0.35f, top - 2, cx - r * 0.35f, top - r * 0.55f);
                g.DrawLine(pen, cx + r * 0.1f, top - 2, cx + r * 0.1f, top - r * 0.55f);
            }
            else
            {
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                using var dot = new SolidBrush(color);
                float d = Math.Max(2f, sz * 0.16f);
                g.FillEllipse(dot, cx - d / 2f, cy - r * 0.58f, d, d);
                g.DrawLine(pen, cx, cy - r * 0.1f, cx, cy + r * 0.55f);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = Hit(e.Location);
            if (hit == _hoverBtn) return;
            _hoverBtn = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            string? tip = hit switch
            {
                BtnSettings => L.T("Settings"),
                BtnUpdate => _updateTip,
                BtnSponsor => L.T("Support the author"),
                BtnInfo => L.T("About AMD Ryzen PBO Studio"),
                _ => null,
            };
            _tip.SetToolTip(this, tip);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverBtn != -1)
            {
                _hoverBtn = -1;
                Cursor = Cursors.Default;
                _tip.SetToolTip(this, null);
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) _pressedBtn = Hit(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int p = _pressedBtn;
            _pressedBtn = -1;
            if (e.Button != MouseButtons.Left || p < 0 || Hit(e.Location) != p) return;
            if (p == BtnSettings) SettingsClicked?.Invoke(this, EventArgs.Empty);
            else if (p == BtnUpdate) UpdateClicked?.Invoke(this, EventArgs.Empty);
            else if (p == BtnSponsor) SponsorClicked?.Invoke(this, EventArgs.Empty);
            else InfoClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
