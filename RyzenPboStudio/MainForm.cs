using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace RyzenPboStudio;

internal sealed class MainForm : Form
{
    // ── 控件 ────────────────────────────────────────────────────────────────
    private readonly ComboBox _modeCombo = new();          // 单项测试下拉
    private PillButton _seqBtn = null!;                    // 顺序测试
    private PillButton _comboBtn = null!;                  // 和项测试
    private PillButton _autoAdjBtn = null!;                // 自动调整负压模式
    private PillButton _manualAdjBtn = null!;              // 手动模式（报错只提醒）
    private bool _autoAdjust = true;                       // true=报错自动回退负压；false=只提醒不改负压
    private readonly TextBox _durationBox = new();
    private readonly TextBox _roundsBox = new();
    private readonly PillButton _startBtn = Primary("▶  开始测试");
    private readonly PillButton _stopBtn = Ghost("■  停止测试");
    private readonly Label _statusLabel = new() { Text = "就绪", AutoSize = true };
    private readonly Label _initialOffsetsLabel = new() { Text = "初始负压: 未读取", AutoSize = true };
    private readonly Label _offsetsLabel = new() { Text = "当前负压: 未读取", AutoSize = true };
    private readonly TextBox _logBox = new();          // AMD PBO 页日志
    private readonly TextBox _logBoxTesting = new();   // TESTING 页日志（与 _logBox 内容镜像）
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _offsetTimer = new() { Interval = 1000 };
    private LinkLabel _updateLink = null!;   // 状态条「检查更新」
    private bool _updateBusy;                // 更新流程进行中，避免重入

    // TESTING 页测试设置卡内各段的统一宽度：下拉框、模式按钮、调整方式按钮、
    // 以及底部开始/停止两键之和都对齐到这个值，改宽度只需改这里一处。
    private const int TestRowWidth = 370;

    // ── 手动 Curve Optimizer 编辑器 ─────────────────────────────────────────
    private const int CoDisplaySlotCount = 16;
    private const int CoSlotsPerColumn = 8;
    private readonly List<NumericUpDown> _coCells = new();
    private int _coSlotCount;
    private readonly NumericUpDown _fmaxBox = new();

    // ── PBO 限制参数条（fmax 复用 _fmaxBox）─────────────────────────────────
    private readonly NumericUpDown _pptBox = new();
    private readonly NumericUpDown _edcBox = new();
    private readonly NumericUpDown _tdcBox = new();

    // ── 手动 Curve Shaper 编辑器（5 频率档 × 3 温度档）──────────────────────
    private readonly NumericUpDown[,] _csCells = new NumericUpDown[5, 3];
    private PillButton _csApplyBtn = null!;
    private PillButton _csRefreshBtn = null!;
    private PillButton _csLoadBtn = null!;
    private PillButton _csSaveBtn = null!;
    private Label _csTitleLabel = null!;
    private CsState _csState = new();

    // ── 运行状态 ────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<string> _logQueue = new();
    private CancellationTokenSource? _cts;
    private volatile bool _stopRequested;
    private Task? _testTask;
    private bool _testCompletedNormally;
    private string _testMode = "VT3";
    private string _selectedMode = "VT3";
    private int _durationSeconds = Config.DefaultDuration;
    // 顺序测试（SEQ）的三个阶段轮数：默认 20 / 10 / 10。
    private Dictionary<string, int> _iterations = new()
    {
        ["VT3"] = Config.DefaultVt3,
        ["BKT"] = Config.DefaultBkt,
        ["SVT"] = Config.DefaultSvt,
    };
    private int _singleRounds = Config.DefaultBkt;   // 单项测试轮数（默认 10）
    private List<int> _initialOffsets = new();

    // 和项测试（COMBO）固定参数：VT3+BKT+SVT 一起跑 10 轮。
    private static readonly string[] ComboAlgos = { "VSTv3", "BKT", "SVT" };
    private const int ComboRounds = 10;

    // 单项测试下拉选项：显示文本 + 传给 y-cruncher 的组件名（全部已验证合法；VSTv3 是 VT3 的别名）。
    private static readonly (string display, string mode, string algo)[] ModeOptions =
    {
        ("BKT — Basecase + Karatsuba (Scalar Integer)", "BKT", "BKT"),
        ("BBP — BBP Digit Extraction (AVX512 Float)", "BBP", "BBP"),
        ("SFTv4 — Small In-Cache FFTv4 (AVX512 Float)", "SFTv4", "SFTv4"),
        ("SNT — Small In-Cache N63 (AVX512 Integer)", "SNT", "SNT"),
        ("SVT — Small In-Cache VT3 (AVX512 Integer)", "SVT", "SVT"),
        ("FFTv4 — Fast Fourier Transform v4 (AVX512 Float)", "FFTv4", "FFTv4"),
        ("N63 — Classic NTT v2 (AVX512 Integer)", "N63", "N63"),
        ("VT3 — Vector Transform v3 (AVX512 Integer)", "VT3", "VSTv3"),
    };

    public MainForm()
    {
        Text = "AMD Ryzen PBO Studio";
        ClientSize = new Size(1370, 926);   // 宽度容纳双 CCD 并排（9950X 等 16 核），不做横向滚动
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 9F);

        TrySetIcon();
        BuildUi();
        SetStatus("就绪", Theme.Success);
        Shown += (_, _) => InitCoEditor();
        // 启动后静默查一次更新：有新版才弹窗，无更新或网络不通都不打扰
        Shown += async (_, _) => await RunUpdateFlowAsync(manual: false);
        Load += (_, _) => FitToScreen();

        Log.OnLine += line => _logQueue.Enqueue(line);
        _logTimer.Tick += OnLogTimer;
        _offsetTimer.Tick += OnOffsetTimer;
        _logTimer.Start();
        _offsetTimer.Start();

        Log.Write("程序已启动，请选择测试模式");
        if (Workspace.WasInterrupted())
        {
            var st = Workspace.LoadState();
            string resumeHint = st != null && st.TestMode == "SEQ" && !string.IsNullOrEmpty(st.SeqPhase)
                ? $" · 将从中断的 {st.SeqPhase} 阶段继续"
                : "";
            SetStatus($"上次测试异常中断 · 开始后将自动回退恢复负压{resumeHint}", Theme.Warn);
            Log.Write($"检测到上次测试异常中断（疑似死机/断电），点击「开始测试」将从崩溃前负压回退一档继续{resumeHint}", "WARN");
        }
    }

    // ── 界面构建 ────────────────────────────────────────────────────────────

    private MonitorView? _monitorView;     // 顶部常驻每核监控
    private Control _pboPanel = null!;      // AMD PBO 页：CO | CS + 参数条 + LOG
    private Control _testingPanel = null!;  // TESTING 页：测试设置 | 运行日志
    private NavTabButton _navPboBtn = null!;
    private NavTabButton _navTestingBtn = null!;

    /// <summary>按屏幕 DPI 与工作区自适应窗口：先随系统缩放放大布局（字体已由 GDI 自动放大），
    /// 放大后若超出工作区再整体等比回缩（含字体），最后居中。100% 缩放且屏幕足够时不做任何改动。</summary>
    private void FitToScreen()
    {
        float dpi = DeviceDpi / 96f;
        if (dpi > 1.01f)
            Scale(new SizeF(dpi, dpi));

        var wa = Screen.FromControl(this).WorkingArea;
        float fit = Math.Min((float)wa.Width / Width, (float)wa.Height / Height);
        if (fit < 1f)
        {
            ScaleAllFonts(this, fit);
            Scale(new SizeF(fit, fit));
        }

        Location = new Point(
            wa.Left + Math.Max(0, (wa.Width - Width) / 2),
            wa.Top + Math.Max(0, (wa.Height - Height) / 2));
    }

    /// <summary>递归缩放控件树的所有字体（Scale 只缩布局不缩字体）。</summary>
    private static void ScaleAllFonts(Control root, float s)
    {
        foreach (Control c in root.Controls) ScaleAllFonts(c, s);
        root.Font = new Font(root.Font.FontFamily, root.Font.Size * s, root.Font.Style);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Bg,
            Padding = new Padding(18),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 378));  // 顶部：常驻监控（身份条 + CCD0/CCD1 + 限制条）
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 中部：AMD PBO / TESTING 内容
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));   // 底部：AMD PBO / TESTING 沉浸式导航

        var monitorCard = BuildMonitorCard();
        monitorCard.Margin = new Padding(0, 0, 0, 10);

        // 中部内容宿主：两页叠放，按底部按钮切换可见
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _pboPanel = BuildPboPanel();
        _pboPanel.Dock = DockStyle.Fill;
        _testingPanel = BuildTestingPanel();
        _testingPanel.Dock = DockStyle.Fill;
        host.Controls.Add(_pboPanel);
        host.Controls.Add(_testingPanel);

        root.Controls.Add(monitorCard, 0, 0);
        root.Controls.Add(host, 0, 1);
        root.Controls.Add(BuildNavBar(), 0, 2);
        Controls.Add(root);

        ShowPage(pbo: true);   // 默认进 AMD PBO 页
    }

    /// <summary>AMD PBO 页：左列 = CO | CS（上）+ 参数条（下），右列 = LOG 拉满高度齐平。</summary>
    private Control BuildPboPanel()
    {
        // 左列：CO | CS 在上，参数条在下
        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // CO | CS
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));      // 开源声明
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));      // 参数条

        var coCs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 8),
        };
        coCs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        coCs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        var coCard = BuildCoCard(); coCard.Margin = new Padding(0, 0, 6, 0);
        var csCard = BuildCsCard(); csCard.Margin = new Padding(6, 0, 0, 0);
        coCs.Controls.Add(coCard, 0, 0);
        coCs.Controls.Add(csCard, 1, 0);

        var credit = new Label
        {
            Text = "Curve Optimizer / Curve Shaper 功能实现源码来自开源项目 ZenStates-Core / SMUDebugTool",
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Bg,
            Font = new Font(Theme.FontFamily, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(2, 0, 0, 2),
        };

        left.Controls.Add(coCs, 0, 0);
        left.Controls.Add(credit, 0, 1);
        left.Controls.Add(BuildParamBar(), 0, 2);

        // 右列：LOG（拉满左列总高，底边与参数条齐平）
        return TwoColRow(60, left, BuildLogCard(_logBox));
    }

    /// <summary>TESTING 页：测试设置 | 运行日志 并排，底部状态条（右端显示版本）。</summary>
    private Control BuildTestingPanel()
    {
        var cfg = BuildConfigCard();
        var logCard = BuildLogCard(_logBoxTesting);

        // 左栏用绝对宽度而非百分比：卡片内容是固定的 TestRowWidth，用百分比会随窗口
        // 尺寸变化把开始/停止按钮裁掉。宽度 = 内容 + 卡片左右内边距 32 + 栏间距 6 +
        // 余量 34（含 body 出现纵向滚动条时占用的约 17px，避免横向再被挤掉）。
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TestRowWidth + 72));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 日志吃掉剩余宽度
        cfg.Margin = new Padding(0, 0, 6, 0);
        logCard.Margin = new Padding(6, 0, 0, 0);
        top.Controls.Add(cfg, 0, 0);
        top.Controls.Add(logCard, 1, 0);

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Font = new Font(Theme.FontFamily, 10.5F, FontStyle.Bold);
        _statusLabel.BackColor = Theme.Bg;
        // 高度 40 以容纳右端版本 + 作者两行文字
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Theme.Bg, Padding = new Padding(4, 4, 0, 0) };
        statusBar.Controls.Add(_statusLabel);    // Dock=Fill 先加
        statusBar.Controls.Add(BuildVersionPanel());   // Dock=Right 后加，占右端

        var holder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        holder.Controls.Add(top);          // Dock=Fill 先加
        holder.Controls.Add(statusBar);    // Dock=Bottom 后加
        return holder;
    }

    /// <summary>底部导航：沉浸式标签 AMD PBO / TESTING（仿 HYDRA 底栏）。</summary>
    private Control BuildNavBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(0, 6, 0, 0),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _navPboBtn = NewNavButton("AMD PBO", () => ShowPage(pbo: true));
        _navPboBtn.Margin = new Padding(0);
        _navTestingBtn = NewNavButton("TESTING", () => ShowPage(pbo: false));
        _navTestingBtn.Margin = new Padding(0);

        bar.Controls.Add(_navPboBtn, 0, 0);
        bar.Controls.Add(_navTestingBtn, 1, 0);
        return bar;
    }

    private static NavTabButton NewNavButton(string text, Action onClick)
    {
        var b = new NavTabButton
        {
            Text = text,
            Size = new Size(170, 42),
            Anchor = AnchorStyles.None,   // 在各自 50% 单元格内居中
            Font = new Font(Theme.FontFamily, 11.5F, FontStyle.Bold),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>切换中部内容页：监控常驻顶部不动，仅 AMD PBO / TESTING 区互换。</summary>
    private void ShowPage(bool pbo)
    {
        _pboPanel.Visible = pbo;
        _testingPanel.Visible = !pbo;
        if (pbo) _pboPanel.BringToFront();
        else _testingPanel.BringToFront();

        StyleNav(_navPboBtn, pbo);
        StyleNav(_navTestingBtn, !pbo);
    }

    private static void StyleNav(NavTabButton? b, bool active)
    {
        if (b == null) return;
        b.Active = active;
        b.Invalidate();
    }

    /// <summary>两栏行：左列占 leftPercent%，上下两行用同一比例保证竖向对齐。</summary>
    private static TableLayoutPanel TwoColRow(int leftPercent, Control left, Control right)
    {
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPercent));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - leftPercent));
        left.Margin = new Padding(0, 0, 6, 0);
        right.Margin = new Padding(6, 0, 0, 0);
        split.Controls.Add(left, 0, 0);
        split.Controls.Add(right, 1, 0);
        return split;
    }

    /// <summary>顶部常驻监控卡片：嵌入 MonitorView（每核 CPPC/AMD CO/EFFREQ/FREQ/VID）。</summary>
    private Control BuildMonitorCard()
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(12, 10, 12, 10);

        try
        {
            _monitorView = new MonitorView { Dock = DockStyle.Fill };
            card.Controls.Add(_monitorView);
        }
        catch (Exception ex)
        {
            card.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.Warn,
                BackColor = Theme.Surface,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "监控初始化失败：" + ex.Message,
            });
        }
        return card;
    }

    private Control BuildConfigCard()
    {
        var card = NewCard();
        card.Margin = new Padding(0);

        // 单列纵向排布：各段标题在上、内容在下，整体宽度统一为 TestRowWidth，多余横向空间让给日志。
        // AutoScroll 是兜底：不同 DPI / 系统字体下各段实际高度会有出入，一旦纵向放不下就出现
        // 滚动条，而不是把底部的开始/停止按钮直接裁掉。正常情况下内容能放下，不会出现滚动条。
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 2, 0, 0),
        };

        // 单项测试：标题 + 其下方的下拉框
        var modeTitle = new Label { Text = "单项测试", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 4), Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold) };

        _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeCombo.FlatStyle = FlatStyle.Flat;
        _modeCombo.Width = TestRowWidth;
        _modeCombo.BackColor = Theme.SurfaceAlt;
        _modeCombo.ForeColor = Theme.TextHi;
        _modeCombo.Font = new Font(Theme.FontFamily, 10F);
        _modeCombo.Margin = new Padding(0, 0, 0, 6);
        foreach (var opt in ModeOptions) _modeCombo.Items.Add(opt.display);
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_modeCombo.SelectedIndex < 0) return;
            _selectedMode = ModeOptions[_modeCombo.SelectedIndex].mode; // 选单项即清除组合测试高亮
            RestyleModeButtons();
        };
        var fields = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        AddFieldPair(fields, "每轮(秒)", _durationBox, 66);
        AddFieldPair(fields, "轮数", _roundsBox, 60);
        _durationBox.Text = _durationSeconds.ToString();
        _roundsBox.Text = _singleRounds.ToString();

        // 多项测试（顺序 / 组合）
        var comboTitle = new Label { Text = "多项测试", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 4), Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold) };
        _seqBtn = MakeModeButton("顺序测试  VT3→BKT→SVT  20/10/10轮", "SEQ");
        _comboBtn = MakeModeButton("组合测试  VT3+BKT+SVT 同跑  10轮", "COMBO");

        // 负压调整方式：自动回退 / 手动（只提醒）
        var adjTitle = new Label { Text = "负压调整方式", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 4, 0, 4), Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold) };
        _autoAdjBtn   = MakeAdjustButton("自动调整负压模式  报错自动回退一档", true);
        _manualAdjBtn = MakeAdjustButton("手动模式  报错只提醒，不改负压", false);

        // 开始 / 停止：横排置于负压调整方式下方，两键等分 TestRowWidth（含中间 10px 间隔）
        const int runGap = 10;
        int runBtnWidth = (TestRowWidth - runGap) / 2;
        var runRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        _startBtn.Size = new Size(runBtnWidth, 44);
        _stopBtn.Size = new Size(runBtnWidth, 44);
        _startBtn.Margin = new Padding(0, 0, runGap, 0);
        _stopBtn.Margin = new Padding(0);
        _stopBtn.Enabled = false;
        _startBtn.Click += StartTest;
        _stopBtn.Click += StopTest;
        runRow.Controls.Add(_startBtn);
        runRow.Controls.Add(_stopBtn);

        body.Controls.Add(modeTitle);
        body.Controls.Add(_modeCombo);
        body.Controls.Add(fields);
        body.Controls.Add(comboTitle);
        body.Controls.Add(_seqBtn);
        body.Controls.Add(_comboBtn);
        body.Controls.Add(adjTitle);
        body.Controls.Add(_autoAdjBtn);
        body.Controls.Add(_manualAdjBtn);
        body.Controls.Add(runRow);

        SelectMode(_selectedMode);
        RestyleAdjustButtons();

        card.Controls.Add(body);
        card.Controls.Add(SectionTitle("测试设置"));
        return card;
    }

    /// <summary>负压调整方式按钮（自动/手动）：点选即切换并高亮。</summary>
    private PillButton MakeAdjustButton(string text, bool auto)
    {
        var b = new PillButton
        {
            Text = text,
            Tag = auto,
            Font = new Font(Theme.FontFamily, 9.5F),
            Height = 34,
            Width = TestRowWidth,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.Border,
            ForeColor = Theme.TextHi,
            Margin = new Padding(0, 0, 0, 4),
        };
        b.Click += (_, _) => { _autoAdjust = auto; RestyleAdjustButtons(); };
        return b;
    }

    private void RestyleAdjustButtons()
    {
        foreach (var b in new[] { _autoAdjBtn, _manualAdjBtn })
        {
            if (b == null) continue;
            bool sel = (bool)b.Tag! == _autoAdjust;
            b.Normal = sel ? Theme.Accent : Theme.SurfaceAlt;
            b.Hover = sel ? Theme.AccentHover : Theme.Border;
            b.ForeColor = sel ? Theme.AccentText : Theme.TextHi;
            b.Invalidate();
        }
    }

    /// <summary>手动模式下检测到报错：只提醒用户，负压保持不变。</summary>
    private void NotifyManualError(List<int> crashedPhysical)
    {
        string cores = crashedPhysical.Count > 0
            ? string.Join(", ", crashedPhysical)
            : "未指明（y-cruncher 崩溃）";
        Ui(() =>
        {
            SetStatus($"手动模式：物理核心 [{cores}] 报错，负压未自动调整", Theme.Accent);
            MessageBox.Show(
                $"检测到报错，已停止测试。\n\n报错物理核心: {cores}\n\n" +
                "当前为「手动模式」，负压未做任何改动。请在 AMD PBO 页手动调整负压后重新开始测试。",
                "手动模式 · 检测到报错", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
    }

    /// <summary>组合测试按钮（顺序/和项）：点选即设为当前模式并高亮。</summary>
    private PillButton MakeModeButton(string text, string mode)
    {
        var b = new PillButton
        {
            Text = text,
            Tag = mode,
            Font = new Font(Theme.FontFamily, 9.5F),
            Height = 34,
            Width = TestRowWidth,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,   // 默认灰色，仅选中时由 RestyleModeButtons 改红
            Hover = Theme.Border,
            ForeColor = Theme.TextHi,
            Margin = new Padding(0, 0, 0, 4),
        };
        b.Click += (_, _) => { _selectedMode = mode; RestyleModeButtons(); };
        return b;
    }

    private void RestyleModeButtons()
    {
        foreach (var b in new[] { _seqBtn, _comboBtn })
        {
            if (b == null) continue;
            bool sel = (string)b.Tag! == _selectedMode;
            b.Normal = sel ? Theme.Accent : Theme.SurfaceAlt;
            b.Hover = sel ? Theme.AccentHover : Theme.Border;
            b.ForeColor = sel ? Theme.AccentText : Theme.TextHi;
            b.Invalidate();
        }
    }

    /// <summary>停靠在 TESTING 页底部状态条右端：第一行版本 + 构建日期 + 检查更新，
    /// 第二行作者署名 + 抖音主页链接。</summary>
    private Control BuildVersionPanel()
    {
        string buildDate = typeof(MainForm).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "?";
        string version = typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "?";

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 430,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 6, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 文本右对齐
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 链接贴最右
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _updateLink = FootLink("检查更新", () => _ = RunUpdateFlowAsync(manual: true));
        grid.Controls.Add(DimText($"版本 v{version} · 构建 {buildDate}"), 0, 0);
        grid.Controls.Add(LinkRow(
            FootLink("项目主页", () => OpenUrl(Updater.HomePage)),
            _updateLink), 1, 0);

        grid.Controls.Add(DimText("制作：DY冷漠_OC调试"), 0, 1);
        grid.Controls.Add(LinkRow(
            FootLink("抖音 冷漠OC", () => OpenUrl(DouyinUrl))), 1, 1);
        return grid;
    }

    /// <summary>把若干链接横向排在状态条同一行右端。</summary>
    private static FlowLayoutPanel LinkRow(params Control[] links)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Right,
        };
        foreach (Control l in links) row.Controls.Add(l);
        return row;
    }

    private const string DouyinUrl = "https://v.douyin.com/6VGnQBTxwIQ/";

    private static Label DimText(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Bg,
        Font = new Font(Theme.FontFamily, 8.5F),
        TextAlign = ContentAlignment.MiddleRight,
    };

    /// <summary>状态条上的小号超链接。</summary>
    private static LinkLabel FootLink(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            BackColor = Theme.Bg,
            Font = new Font(Theme.FontFamily, 8.5F),
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            VisitedLinkColor = Theme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(8, 0, 0, 0),
            Anchor = AnchorStyles.Right,
        };
        link.LinkClicked += (_, _) => onClick();
        return link;
    }

    // ── 在线更新 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查 GitHub Release 并在用户确认后完成更新。manual=false 为启动时的静默检查：
    /// 无更新或网络异常都不打扰用户；manual=true 由「检查更新」触发，各种结果都给回应。
    /// </summary>
    private async Task RunUpdateFlowAsync(bool manual)
    {
        if (_updateBusy) return;

        // 压测期间不允许更新：更新要退出程序，中途退出会留下脏标记，
        // 且 CO 已下发到 CPU，应当先让测试正常收尾。
        if (_testTask is { IsCompleted: false })
        {
            if (manual)
                MessageBox.Show("测试进行中，无法更新。\n\n请先停止测试，待其安全收尾后再试。",
                    "正在测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _updateBusy = true;
        _updateLink.Enabled = false;
        try
        {
            UpdateInfo? info;
            try
            {
                info = await Updater.CheckAsync();
            }
            catch (Exception e)
            {
                if (manual)
                    MessageBox.Show($"检查更新失败：{e.Message}", "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IsDisposed) return;

            if (info == null)
            {
                if (manual)
                    MessageBox.Show($"当前已是最新版本 v{Updater.CurrentVersion}。", "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log.Write($"发现新版本 {info.Tag}（当前 v{Updater.CurrentVersion}）");
            string notes = info.Notes.Length > 600 ? info.Notes[..600] + "\n..." : info.Notes;
            var choice = MessageBox.Show(
                $"发现新版本 {info.Tag}，当前为 v{Updater.CurrentVersion}。\n\n" +
                $"{notes}\n\n" +
                $"下载大小约 {info.Size / 1024.0 / 1024.0:F1} MB。\n" +
                "更新会关闭程序、覆盖安装目录后自动重启；\n" +
                "logs 与 profiles 文件夹（负压历史与恢复数据）不会被改动。\n\n" +
                "现在更新吗？",
                "发现新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (choice != DialogResult.Yes) return;

            await DownloadAndApplyAsync(info);
        }
        finally
        {
            _updateBusy = false;
            if (!IsDisposed) _updateLink.Enabled = true;
        }
    }

    /// <summary>下载并解压新版本；两步都成功才关闭程序交给替换脚本。</summary>
    private async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        string zipPath;
        try
        {
            var progress = new Progress<int>(p =>
            {
                if (!IsDisposed) _updateLink.Text = $"下载中 {p}%";
            });
            SetStatus($"正在下载更新 {info.Tag}...", Theme.Warn);
            zipPath = await Updater.DownloadAsync(info, progress);
        }
        catch (Exception e)
        {
            _updateLink.Text = "检查更新";
            SetStatus("更新下载失败", Theme.Accent);
            Log.Write($"更新下载失败：{e.Message}", "WARN");
            MessageBox.Show($"下载更新失败：{e.Message}", "更新",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string newRoot;
        try
        {
            _updateLink.Text = "解压中...";
            SetStatus("正在解压更新...", Theme.Warn);
            newRoot = await Task.Run(() => Updater.ExtractAndVerify(zipPath));
        }
        catch (Exception e)
        {
            _updateLink.Text = "检查更新";
            SetStatus("更新包校验失败", Theme.Accent);
            Log.Write($"更新包解压/校验失败：{e.Message}", "WARN");
            MessageBox.Show($"更新包无法使用：{e.Message}\n\n当前版本未受影响。", "更新",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 到这一步新文件已就绪，剩下的覆盖必须等本进程退出后由脚本执行
        try
        {
            Updater.LaunchReplacerAndExit(newRoot);
        }
        catch (Exception e)
        {
            _updateLink.Text = "检查更新";
            MessageBox.Show($"无法启动更新程序：{e.Message}\n\n当前版本未受影响。", "更新",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Log.Write("更新就绪，正在退出以完成替换");
        Close();
    }

    /// <summary>用系统默认浏览器打开链接。</summary>
    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            MessageBox.Show($"无法打开链接：{e.Message}\n\n{url}", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>运行日志卡片。AMD PBO 页与 TESTING 页各建一个实例（一个 TextBox 无法同时挂在两页），
    /// 两个日志框由 OnLogTimer 镜像写入，内容始终一致。</summary>
    private Control BuildLogCard(TextBox box)
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(14, 12, 14, 12);

        box.Multiline = true;
        box.ReadOnly = true;
        box.ScrollBars = ScrollBars.Vertical;
        box.WordWrap = true;
        box.MaxLength = 0;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Bg;
        box.ForeColor = Color.FromArgb(0xD4, 0xD4, 0xD4);
        box.Font = new Font(Theme.MonoFamily, 10F);
        box.Dock = DockStyle.Fill;

        // 底部按钮条：左=清除日志，右=保存日志（仅手动导出）
        var btnBar = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Theme.Surface };
        var clearBtn = Ghost("清除日志");
        clearBtn.Size = new Size(118, 36);
        clearBtn.Location = new Point(0, 8);
        clearBtn.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        clearBtn.Click += (_, _) => ClearLog();

        var saveBtn = Primary("保存日志");
        saveBtn.Size = new Size(118, 36);
        saveBtn.Top = 8;
        saveBtn.Click += (_, _) => SaveLog();
        btnBar.Resize += (_, _) => saveBtn.Left = btnBar.ClientSize.Width - saveBtn.Width;

        btnBar.Controls.Add(clearBtn);
        btnBar.Controls.Add(saveBtn);

        card.Controls.Add(box);                     // 填充
        card.Controls.Add(btnBar);                  // 底部按钮
        card.Controls.Add(SectionTitle("运行日志")); // 顶部标题
        return card;
    }

    // ── 手动 Curve Optimizer 编辑器 ─────────────────────────────────────────

    /// <summary>CO 卡片：每核负压网格 + Apply/Refresh/Save/Load 按钮列。</summary>
    private Control BuildCoCard()
    {
        var card = NewCard();
        card.Margin = new Padding(0);

        _coSlotCount = Math.Min(RyzenSmu.SlotCount, CoDisplaySlotCount);

        _coCells.Clear();
        for (int i = 0; i < CoDisplaySlotCount; i++) _coCells.Add(NewCoCell());

        var bodyTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 4, 0, 0),
        };
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // CO 核心网格
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // CO 按钮列
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // 占位，使前两列左聚拢
        bodyTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        bodyTable.Controls.Add(BuildCoGrid(), 0, 0);
        bodyTable.Controls.Add(BuildCoButtonColumn(), 1, 0);

        card.Controls.Add(bodyTable);
        card.Controls.Add(SectionTitle("Curve Optimizer"));
        return card;
    }

    private Control BuildCoGrid()
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 8, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // 顶部 “+”：整列加一档
        grid.Controls.Add(MakeStepButton("+", () => StepRange(0, CoSlotsPerColumn, +1)), 0, 0);
        var ccd1Plus = MakeStepButton("+", () => StepRange(CoSlotsPerColumn, CoDisplaySlotCount, +1));
        ccd1Plus.Enabled = _coSlotCount > CoSlotsPerColumn;
        grid.Controls.Add(ccd1Plus, 1, 0);

        for (int r = 0; r < CoSlotsPerColumn; r++)
        {
            grid.Controls.Add(MakeCoreCell(r), 0, r + 1);
            grid.Controls.Add(MakeCoreCell(CoSlotsPerColumn + r), 1, r + 1);
        }

        // 底部 “-”：整列减一档
        grid.Controls.Add(MakeStepButton("-", () => StepRange(0, CoSlotsPerColumn, -1)), 0, CoSlotsPerColumn + 1);
        var ccd1Minus = MakeStepButton("-", () => StepRange(CoSlotsPerColumn, CoDisplaySlotCount, -1));
        ccd1Minus.Enabled = _coSlotCount > CoSlotsPerColumn;
        grid.Controls.Add(ccd1Minus, 1, CoSlotsPerColumn + 1);
        return grid;
    }

    private Control MakeCoreCell(int core)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0, 0, 14, 0),
        };
        f.Controls.Add(new Label
        {
            Text = $"Core {core}",
            AutoSize = false,
            Width = 54,
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 6, 6, 0),
        });
        if (core >= _coSlotCount || RyzenSmu.IsSlotDisabled(core))
        {
            _coCells[core].Enabled = false;   // 熔丝屏蔽核或不存在的 CCD 槽位：恒 0 灰化不可调
        }
        f.Controls.Add(_coCells[core]);
        return f;
    }

    private static NumericUpDown NewCoCell() => new()
    {
        Minimum = -50,
        Maximum = 30,
        Value = 0,
        Width = 58,
        BackColor = Theme.SurfaceAlt,
        ForeColor = Theme.TextHi,
        BorderStyle = BorderStyle.FixedSingle,
        TextAlign = HorizontalAlignment.Center,
        Font = new Font(Theme.MonoFamily, 10F),
        Margin = new Padding(0, 2, 0, 2),
    };

    private PillButton MakeStepButton(string text, Action onClick)
    {
        var b = Ghost(text);
        b.Size = new Size(58, 24);
        b.Font = new Font(Theme.FontFamily, 11F, FontStyle.Bold);
        // 左边距 60 = "Core N" 标签宽(54)+间距(6)，使按钮对齐到数字框这一列
        b.Margin = new Padding(60, 0, 14, 4);
        b.Click += (_, _) => onClick();
        return b;
    }

    private Control BuildCoButtonColumn()
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Surface,
            // 顶边距 ~32 让按钮整体下移，对齐到第一行 Core 0（避开顶部 “+” 行）
            Margin = new Padding(8, 32, 0, 0),
        };
        PillButton Btn(string text, Action click)
        {
            var b = Ghost(text);   // 四个按钮统一灰色
            b.Size = new Size(96, 32);
            b.Margin = new Padding(0, 0, 0, 8);
            b.Click += (_, _) => click();
            return b;
        }
        flow.Controls.Add(Btn("Apply", OnCoApply));
        flow.Controls.Add(Btn("Refresh", OnCoRefresh));
        flow.Controls.Add(Btn("Save", OnCoSave));
        flow.Controls.Add(Btn("Load", OnCoLoad));
        return flow;
    }

    /// <summary>底部参数条：FMax / PPT / EDC / TDC + 一键 Apply（写 SMU 限制）。</summary>
    private Control BuildParamBar()
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(14, 6, 14, 6);

        ConfigNumBox(_fmaxBox, 0, 7000, 25, 92);
        ConfigNumBox(_pptBox, 30, 400, 5, 72);
        ConfigNumBox(_edcBox, 10, 400, 5, 72);
        ConfigNumBox(_tdcBox, 10, 300, 5, 72);
        _pptBox.Value = 142;   // 启动后由 InitPboBoxes 用 CPU 实际值覆盖
        _edcBox.Value = 180;
        _tdcBox.Value = 160;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = Theme.Surface,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
        };
        AddParamPair(flow, "FMax", _fmaxBox);
        AddParamPair(flow, "PPT", _pptBox);
        AddParamPair(flow, "EDC", _edcBox);
        AddParamPair(flow, "TDC", _tdcBox);

        var apply = Primary("Apply");
        apply.Size = new Size(96, 34);
        apply.Margin = new Padding(16, 7, 0, 0);
        apply.Click += (_, _) => OnPboApply();
        flow.Controls.Add(apply);

        card.Controls.Add(flow);
        return card;
    }

    private static void ConfigNumBox(NumericUpDown box, int min, int max, int inc, int width)
    {
        box.Minimum = min;
        box.Maximum = max;
        box.Increment = inc;
        box.Width = width;
        box.BackColor = Theme.SurfaceAlt;
        box.ForeColor = Theme.TextHi;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.TextAlign = HorizontalAlignment.Center;
        box.Font = new Font(Theme.MonoFamily, 10F);
        box.Margin = new Padding(0, 10, 12, 0);
    }

    private static void AddParamPair(FlowLayoutPanel parent, string label, NumericUpDown box)
    {
        parent.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 15, 6, 0),
        });
        parent.Controls.Add(box);
    }

    // ── 手动 Curve Shaper 编辑器 ────────────────────────────────────────────

    /// <summary>CS 卡片：标题 + 5×3 网格 + Apply/Refresh/Save/Load 按钮列。</summary>
    private Control BuildCsCard()
    {
        var card = NewCard();
        card.Margin = new Padding(0);

        var bodyTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 4, 0, 0),
        };
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // CS 5×3 网格
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // CS 按钮列
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // 占位，使前两列左聚拢
        bodyTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bodyTable.Controls.Add(BuildCsGrid(), 0, 0);
        bodyTable.Controls.Add(BuildCsButtonColumn(), 1, 0);

        card.Controls.Add(bodyTable);
        _csTitleLabel = SectionTitle("Curve Shaper");
        card.Controls.Add(_csTitleLabel);
        return card;
    }

    private Control BuildCsButtonColumn()
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Surface,
            // 顶边距 ~24 避开表头行，使按钮对齐到第一行数字框
            Margin = new Padding(8, 24, 0, 0),
        };
        PillButton Btn(string text, Action click)
        {
            var b = Ghost(text);
            b.Size = new Size(96, 32);
            b.Margin = new Padding(0, 0, 0, 8);
            b.Click += (_, _) => click();
            return b;
        }
        _csApplyBtn = Btn("Apply", OnCsApply);
        _csRefreshBtn = Btn("Refresh", OnCsRefresh);
        _csSaveBtn = Btn("Save", OnCsSave);
        _csLoadBtn = Btn("Load", OnCsLoad);
        flow.Controls.Add(_csApplyBtn);
        flow.Controls.Add(_csRefreshBtn);
        flow.Controls.Add(_csSaveBtn);
        flow.Controls.Add(_csLoadBtn);
        return flow;
    }

    private Control BuildCsGrid()
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            ColumnCount = 4,
            Margin = new Padding(0),
        };
        for (int i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // 表头：温度档（列）
        grid.Controls.Add(new Label { Text = "", AutoSize = false, Width = 40, Height = 18, BackColor = Theme.Surface }, 0, 0);
        string[] cols = { "LOW", "MED", "HIGH" };
        for (int c = 0; c < 3; c++) grid.Controls.Add(CsHeader(cols[c]), c + 1, 0);

        // 5 个频率档（行）
        string[] tiers = { "MIN", "LOW", "MED", "HIGH", "MAX" };
        for (int t = 0; t < 5; t++)
        {
            grid.Controls.Add(CsRowLabel(tiers[t]), 0, t + 1);
            for (int c = 0; c < 3; c++)
            {
                _csCells[t, c] = NewCsCell();
                grid.Controls.Add(_csCells[t, c], c + 1, t + 1);
            }
        }
        return grid;
    }

    private static Label CsHeader(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 54,
        Height = 18,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(Theme.FontFamily, 8.5F),
        Margin = new Padding(0, 0, 0, 2),
    };

    private static Label CsRowLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = 40,
        Height = 26,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 4, 0),
    };

    private static NumericUpDown NewCsCell() => new()
    {
        Minimum = -50,
        Maximum = 30,
        Value = 0,
        Width = 50,
        BackColor = Theme.SurfaceAlt,
        ForeColor = Theme.TextHi,
        BorderStyle = BorderStyle.FixedSingle,
        TextAlign = HorizontalAlignment.Center,
        Font = new Font(Theme.MonoFamily, 10F),
        Margin = new Padding(0, 2, 4, 2),
    };

    // ── CO 编辑器行为 ───────────────────────────────────────────────────────

    private void StepRange(int from, int to, int delta)
    {
        for (int i = from; i < to && i < _coCells.Count; i++)
            SetCell(i, (int)_coCells[i].Value + delta);
    }

    /// <summary>把核心 i 的数值框设为 v，并夹到合法范围内。</summary>
    private void SetCell(int i, int v)
    {
        if (i < 0 || i >= _coSlotCount || !_coCells[i].Enabled) return;
        var cell = _coCells[i];
        cell.Value = Math.Clamp(v, (int)cell.Minimum, (int)cell.Maximum);
    }

    private void OnCoApply()
    {
        var vals = _coCells.Take(_coSlotCount).Select(c => (int)c.Value).ToList();
        bool ok = Tuning.Apply(vals, "MANUAL", null, "手动应用");
        Log.Write($"手动应用 CO: [{string.Join(", ", vals)}]" + (ok ? "" : "（写入异常，详见上方）"));
        SetStatus(ok ? "已手动应用 Curve Optimizer" : "CO 应用失败", ok ? Theme.Success : Theme.Accent);
    }

    private void OnCoRefresh()
    {
        var off = RyzenSmu.ReadOffsets(_coSlotCount);
        for (int i = 0; i < _coSlotCount && i < off.Count; i++) SetCell(i, off[i]);
        uint fmax = RyzenSmu.ReadFMax();
        if (fmax > 0) _fmaxBox.Value = Math.Clamp(fmax, (uint)_fmaxBox.Minimum, (uint)_fmaxBox.Maximum);
        Log.Write("已从 CPU 刷新 Curve Optimizer / FMax");
    }

    private void OnCoSave()
    {
        new CoProfile
        {
            Offsets = _coCells.Take(_coSlotCount).Select(c => (int)c.Value).ToList(),
            FMax = (uint)_fmaxBox.Value,
        }.Save();
        Log.Write("已保存 CO 配置到 co_profile.json");
        SetStatus("已保存 CO 配置", Theme.Success);
    }

    private void OnCoLoad()
    {
        var p = CoProfile.Load();
        for (int i = 0; i < _coSlotCount && i < p.Offsets.Count; i++) SetCell(i, p.Offsets[i]);
        if (p.FMax > 0) _fmaxBox.Value = Math.Clamp(p.FMax, (uint)_fmaxBox.Minimum, (uint)_fmaxBox.Maximum);
        Log.Write("已载入 CO 配置（未应用，确认后点 Apply）");
    }

    /// <summary>参数条一键应用：FMax + PPT + EDC + TDC 一起写 SMU。</summary>
    private void OnPboApply()
    {
        uint fmax = (uint)_fmaxBox.Value;
        uint ppt = (uint)_pptBox.Value;
        uint edc = (uint)_edcBox.Value;
        uint tdc = (uint)_tdcBox.Value;
        bool okF = RyzenSmu.SetFMax(fmax);
        bool okP = RyzenSmu.SetPptLimit(ppt);
        bool okE = RyzenSmu.SetEdcLimit(edc);
        bool okT = RyzenSmu.SetTdcLimit(tdc);
        bool ok = okF && okP && okE && okT;
        Log.Write($"应用 PBO 限制: FMax {fmax} MHz / PPT {ppt} W / EDC {edc} A / TDC {tdc} A" + (ok ? "" : "（部分写入失败，详见上方）"));
        SetStatus(ok ? "已应用 PBO 限制（FMax/PPT/EDC/TDC）" : "PBO 限制应用部分失败", ok ? Theme.Success : Theme.Accent);
    }

    // ── Curve Shaper 行为 ───────────────────────────────────────────────────

    private void OnCsApply()
    {
        var g = CsGridFromCells();
        bool ok = RyzenSmu.SetCurveShaper(g);
        if (ok)
        {
            // 保存最后应用值，供旧 BIOS 备用模式（读接口失效）刷新时回填，避免被读回的 0 重置。
            _csState.FromGrid(g);
            _csState.Save();
        }
        Log.Write("手动应用 Curve Shaper" + (_csState.ReadBroken ? "（备用模式）" : "") + (ok ? "" : "（写入异常，详见上方）"));
        SetStatus(ok ? "已应用 Curve Shaper" : "Curve Shaper 应用失败", ok ? Theme.Success : Theme.Accent);
    }

    private int[,] CsGridFromCells()
    {
        var g = new int[5, 3];
        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
                g[t, c] = (int)_csCells[t, c].Value;
        return g;
    }

    private void OnCsRefresh()
    {
        var g = RyzenSmu.ReadCurveShaper(out bool hasInterface);

        if (!hasInterface)
        {
            // 旧 BIOS：raw args 全 0，CS 电压已生效但无读取接口。切到备用模式，用本地保存值回填。
            if (!_csState.ReadBroken) { _csState.ReadBroken = true; _csState.Save(); }
            _csTitleLabel.Text = "Curve Shaper（备用模式）";
            if (!_csState.HasMargins)
                return; // 无本地保存值：保留当前编辑器内容，不用全 0 覆盖
            g = _csState.ToGrid();
        }
        else
        {
            if (_csState.ReadBroken) { _csState.ReadBroken = false; _csState.Save(); }
            _csTitleLabel.Text = "Curve Shaper";
            Log.Write("已从 CPU 刷新 Curve Shaper");
        }

        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
            {
                var cell = _csCells[t, c];
                cell.Value = Math.Clamp(g[t, c], (int)cell.Minimum, (int)cell.Maximum);
            }
    }

    /// <summary>载入上次保存的 Curve Shaper（cs_state.json），仅填入编辑器，需手动 Apply。</summary>
    private void OnCsLoad()
    {
        if (!_csState.HasMargins)
        {
            SetStatus("没有可载入的 CS 配置", Theme.Accent);
            return;
        }
        var g = _csState.ToGrid();
        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
            {
                var cell = _csCells[t, c];
                cell.Value = Math.Clamp(g[t, c], (int)cell.Minimum, (int)cell.Maximum);
            }
        Log.Write("已载入 CS 配置（未应用，确认后点 Apply）");
        SetStatus("已载入 CS 配置", Theme.Success);
    }

    /// <summary>把编辑器当前 Curve Shaper 值保存到 cs_state.json（仅存盘，不写硬件）。</summary>
    private void OnCsSave()
    {
        _csState.FromGrid(CsGridFromCells());
        _csState.Save();
        Log.Write("已保存 CS 配置到 cs_state.json");
        SetStatus("已保存 CS 配置", Theme.Success);
    }

    /// <summary>启动时按 CPU 是否支持启用/禁用 Curve Shaper，并填充当前值。</summary>
    private void InitCsEditor()
    {
        bool ok = RyzenSmu.IsCurveShaperSupported();
        foreach (var cell in _csCells) cell.Enabled = ok;
        _csApplyBtn.Enabled = ok;
        _csRefreshBtn.Enabled = ok;
        _csSaveBtn.Enabled = ok;
        _csLoadBtn.Enabled = ok;
        _csTitleLabel.Text = ok ? "Curve Shaper" : "Curve Shaper（当前 CPU 不支持）";
        if (ok)
        {
            _csState = CsState.Load();
            OnCsRefresh();
        }
    }

    /// <summary>启动时用 CPU 当前值填充编辑器。</summary>
    private void InitCoEditor()
    {
        OnCoRefresh();
        InitCsEditor();
        InitPboBoxes();
    }

    /// <summary>启动时用 CPU 当前 PBO 上限初始化 PPT / EDC / TDC 参数框。</summary>
    private void InitPboBoxes()
    {
        var (ppt, edc, tdc) = RyzenSmu.ReadPboLimits();
        if (ppt > 0) _pptBox.Value = Math.Clamp(ppt, (int)_pptBox.Minimum, (int)_pptBox.Maximum);
        if (edc > 0) _edcBox.Value = Math.Clamp(edc, (int)_edcBox.Minimum, (int)_edcBox.Maximum);
        if (tdc > 0) _tdcBox.Value = Math.Clamp(tdc, (int)_tdcBox.Minimum, (int)_tdcBox.Maximum);
    }

    // ── 构件工厂 ────────────────────────────────────────────────────────────

    private static Card NewCard() => new()
    {
        Dock = DockStyle.Fill,
        Padding = new Padding(16, 12, 16, 12),
        Margin = new Padding(0, 0, 0, 10),
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 22,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold),
        Padding = new Padding(2, 2, 0, 0),
    };

    private static PillButton Primary(string text) => new()
    {
        Text = text,
        Normal = Theme.Accent,
        Hover = Theme.AccentHover,
        ForeColor = Theme.AccentText,
        Font = new Font(Theme.FontFamily, 10.5F, FontStyle.Bold),
        Size = new Size(140, 40),
        Radius = 10,
        BackColor = Theme.Bg,
        AutoSize = false,
    };

    private static PillButton Ghost(string text) => new()
    {
        Text = text,
        Normal = Theme.SurfaceAlt,
        Hover = Theme.Border,
        ForeColor = Theme.TextHi,
        Font = new Font(Theme.FontFamily, 10F),
        Size = new Size(130, 40),
        Radius = 10,
        BackColor = Theme.Bg,
        AutoSize = false,
    };

    /// <summary>把下拉框选中项同步到给定模式（找不到则回退到第一项）。</summary>
    private void SelectComboFor(string mode)
    {
        int idx = Array.FindIndex(ModeOptions, o => o.mode == mode);
        _modeCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private static void AddFieldPair(FlowLayoutPanel parent, string label, TextBox box, int width)
    {
        var pair = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0, 0, 10, 6),
        };
        pair.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 8, 4, 0),
        });

        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.SurfaceAlt;
        box.ForeColor = Theme.TextHi;
        box.Font = new Font(Theme.FontFamily, 10.5F);
        box.TextAlign = HorizontalAlignment.Center;
        box.Dock = DockStyle.Fill;

        var field = new Card
        {
            Fill = Theme.SurfaceAlt,
            Stroke = Theme.Border,
            Radius = 7,
            Width = width,
            Height = 32,
            BackColor = Theme.Surface,
            Padding = new Padding(8, 5, 8, 5),
            Margin = new Padding(0, 2, 0, 0),
        };
        field.Controls.Add(box);
        pair.Controls.Add(field);
        parent.Controls.Add(pair);
    }

    private void TrySetIcon()
    {
        try
        {
            // 开发/散文件部署：磁盘上的 ICON.ico
            string ico = Path.Combine(Workspace.BaseDir, "ICON.ico");
            if (File.Exists(ico)) { Icon = new Icon(ico); return; }

            // 单文件发布：磁盘无 ICON.ico，从内嵌资源加载
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("RyzenPboStudio.ICON.ico");
            if (stream != null) Icon = new Icon(stream);
        }
        catch (Exception e)
        {
            Log.Write($"加载图标失败: {e.Message}", "WARN");
        }
    }

    // ── 暗色标题栏 ──────────────────────────────────────────────────────────

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+)，旧版回退到 19
            if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        }
        catch { /* 非关键，失败则标题栏保持系统色 */ }
    }

    // ── 定时器 ──────────────────────────────────────────────────────────────

    private void OnLogTimer(object? sender, EventArgs e)
    {
        bool any = false;
        while (_logQueue.TryDequeue(out var line))
        {
            // 两页各有一个日志框，同步追加保证切页后内容一致
            _logBox.AppendText(line + "\r\n");
            _logBoxTesting.AppendText(line + "\r\n");
            any = true;
        }
        if (any)
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBoxTesting.SelectionStart = _logBoxTesting.TextLength;
        }
    }

    private void OnOffsetTimer(object? sender, EventArgs e)
    {
        Task.Run(() =>
        {
            try
            {
                int n = RyzenSmu.SlotCount;
                var off = RyzenSmu.ReadOffsets(n);
                Ui(() => _offsetsLabel.Text = $"当前负压: [{string.Join(", ", off)}]");
            }
            catch
            {
                // 读取失败时保持原显示
            }
        });
    }

    // ── 测试控制 ────────────────────────────────────────────────────────────

    private void StartTest(object? sender, EventArgs e)
    {
        _startBtn.Enabled = false;

        if (!TryReadSettings(out int duration, out int singleRounds))
            return;

        // 压测前预检 y-cruncher：本软件不附带，缺失时给出下载与放置指引而非静默失败。
        try
        {
            YCruncher.FindExe();
        }
        catch (FileNotFoundException ex)
        {
            MessageBox.Show(ex.Message, "缺少 y-cruncher",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _startBtn.Enabled = true;
            return;
        }

        _testMode = SelectedMode();
        _durationSeconds = duration;
        _singleRounds = singleRounds;   // SEQ 仍用固定 20/10/10，COMBO 用固定 10
        _stopRequested = false;
        _cts = new CancellationTokenSource();

        SetControlsEnabled(false);
        _stopBtn.Enabled = true;
        SetStatus($"运行中 · 模式 {_testMode} · 每轮 {duration} 秒", Theme.Warn);

        _testTask = Task.Run(RunTestThread);
    }

    private void StopTest(object? sender, EventArgs e)
    {
        _stopRequested = true;
        _cts?.Cancel();
        SetStatus("正在停止...", Theme.Accent);
        _stopBtn.Enabled = false;
    }

    private void RunTestThread()
    {
        _testCompletedNormally = false;
        try
        {
            RunTest();
            _testCompletedNormally = true;
        }
        catch (Exception e)
        {
            Log.Write($"测试异常: {e.Message}", "ERROR");
        }
        finally
        {
            Ui(TestFinished);
        }
    }

    private void RunTest()
    {
        // 先判断上次是否异常中断（脏标记），再立即为本次运行打标记
        bool interrupted = Workspace.WasInterrupted();
        var prev = Workspace.LoadState();
        var lastApplied = Journal.ReadLastOffsets();
        Workspace.MarkInProgress($"start {DateTime.Now:O}");

        int numCores = RyzenSmu.SlotCount;
        Log.Write($"检测到 {numCores} 个核心槽位");

        string? seqResumePhase = null;
        List<int> offsets;
        int testRound = 0;

        if (interrupted && (lastApplied != null || prev != null))
        {
            bool hardReset = Workspace.RecentKernelPowerEvent();
            Log.Write(hardReset
                ? "检测到上次测试异常中断 + 内核电源事件（疑似死机/断电），进行恢复..."
                : "检测到上次测试未正常结束，进行恢复...", "WARN");

            // 崩溃前负压：优先取落盘日志最后一条，其次状态文件
            offsets = lastApplied is { Count: > 0 }
                ? new List<int>(lastApplied)
                : (prev is { Offsets.Count: > 0 } ? new List<int>(prev.Offsets) : Enumerable.Repeat(0, numCores).ToList());

            if (prev != null)
            {
                testRound = prev.TestRound;
                _testMode = string.IsNullOrEmpty(prev.TestMode) ? SelectedMode() : prev.TestMode;
                seqResumePhase = prev.SeqPhase;
                if (prev.DurationSeconds is int d) _durationSeconds = d;
                if (prev.IterationsMap is { } m)
                {
                    _iterations = new Dictionary<string, int>
                    {
                        ["VT3"] = m.GetValueOrDefault("VT3", _iterations["VT3"]),
                        ["BKT"] = m.GetValueOrDefault("BKT", _iterations["BKT"]),
                        ["SVT"] = m.GetValueOrDefault("SVT", _iterations["SVT"]),
                    };
                }
            }
            else
            {
                _testMode = SelectedMode(); // 状态文件丢失，沿用当前界面选择
            }

            var preRecovery = new List<int>(offsets);
            for (int i = 0; i < offsets.Count; i++)
                if (!RyzenSmu.IsSlotDisabled(i)) offsets[i] += Config.StepOnError;
            Log.Write($"崩溃前负压: [{string.Join(", ", preRecovery)}]", "WARN");
            Log.Write($"恢复：所有物理核心负压 +{Config.StepOnError} → [{string.Join(", ", offsets)}]");

            if (!Tuning.Apply(offsets, _testMode, seqResumePhase, "crash-recovery"))
            {
                Log.Write("死机恢复设置负压失败！", "ERROR");
                return;
            }

            if (_testMode == "SEQ" && !string.IsNullOrEmpty(seqResumePhase))
                Log.Write($"恢复测试模式: 顺序测试，从中断的 {seqResumePhase} 阶段继续");
            else
                Log.Write($"恢复测试模式: {_testMode}，从第 {testRound} 轮继续");
            _initialOffsets = preRecovery;
            Ui(() =>
            {
                SelectMode(_testMode);
                ApplySettingsToUi();
                _initialOffsetsLabel.Text = $"初始负压: [{string.Join(", ", preRecovery)}]";
            });
        }
        else
        {
            _testMode = SelectedMode();
            seqResumePhase = null;
            offsets = RyzenSmu.ReadOffsets(numCores);
            testRound = 0;
            Log.Write($"当前负压: [{string.Join(", ", offsets)}]");

            // 记录基线负压（落盘），作为尚未回调时的崩溃恢复依据
            Journal.Record(offsets, _testMode, null, "baseline");
        }

        _initialOffsets = new List<int>(offsets);
        var initSnapshot = new List<int>(offsets);
        Ui(() =>
        {
            _initialOffsetsLabel.Text = $"初始负压: [{string.Join(", ", initSnapshot)}]";
            _offsetsLabel.Text = $"当前负压: [{string.Join(", ", initSnapshot)}]";
        });

        Log.Write($"测试参数: 每轮 {_durationSeconds} 秒");

        var token = _cts!.Token;

        if (_testMode == "SEQ")
        {
            (string name, string algo, int iters)[] phases =
            {
                ("VT3", "VSTv3", _iterations["VT3"]),
                ("BKT", "BKT", _iterations["BKT"]),
                ("SVT", "SVT", _iterations["SVT"]),
            };

            int startIdx = 0;
            if (!string.IsNullOrEmpty(seqResumePhase))
            {
                for (int i = 0; i < phases.Length; i++)
                    if (phases[i].name == seqResumePhase) { startIdx = i; break; }
            }

            for (int pi = startIdx; pi < phases.Length; pi++)
            {
                if (_stopRequested) break;
                var (name, algo, iters) = phases[pi];

                Log.Write($"\n>>> 开始 {name} 测试 ({iters} 轮)");
                Workspace.SaveState(new TestState
                {
                    Offsets = offsets,
                    TestRound = testRound,
                    TestMode = _testMode,
                    SeqPhase = name,
                    DurationSeconds = _durationSeconds,
                    IterationsMap = new Dictionary<string, int>(_iterations),
                    Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                });

                bool ok;
                (ok, offsets) = YCruncher.RunStressTest(new[] { algo }, iters, offsets, _durationSeconds, _testMode, token, _autoAdjust, NotifyManualError);
                if (!ok)
                {
                    Log.Write($"{name} 测试未完成", "WARN");
                    break;
                }
                testRound = 0;

                if (pi < phases.Length - 1)
                {
                    Log.Write($">>> {name} 测试完成，清理进程...");
                    YCruncher.Kill();
                }
            }
        }
        else if (_testMode == "COMBO")
        {
            Log.Write($"\n>>> 开始组合测试 VT3+BKT+SVT 同跑 ({ComboRounds} 轮)");
            Workspace.SaveState(new TestState
            {
                Offsets = offsets,
                TestRound = testRound,
                TestMode = _testMode,
                DurationSeconds = _durationSeconds,
                IterationsMap = new Dictionary<string, int>(_iterations),
                Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
            });

            bool ok;
            (ok, offsets) = YCruncher.RunStressTest(ComboAlgos, ComboRounds, offsets, _durationSeconds, _testMode, token, _autoAdjust, NotifyManualError);
            if (ok) Log.Write("组合测试全部完成！");
        }
        else
        {
            var (algo, iters) = GetTestConfig(_testMode);
            Workspace.SaveState(new TestState
            {
                Offsets = offsets,
                TestRound = testRound,
                TestMode = _testMode,
                DurationSeconds = _durationSeconds,
                IterationsMap = new Dictionary<string, int>(_iterations),
                Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
            });

            bool ok;
            (ok, offsets) = YCruncher.RunStressTest(new[] { algo }, iters, offsets, _durationSeconds, _testMode, token, _autoAdjust, NotifyManualError);
            if (ok) Log.Write("测试全部完成！");
        }

        Log.Write("\n" + new string('=', 50));
        Log.Write("最终负压设置:");
        for (int i = 0; i < offsets.Count; i++)
            Log.Write($"  物理核心 {i}: {offsets[i]}");

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"测试完成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"测试模式: {_testMode}");
            sb.AppendLine();
            sb.AppendLine($"每轮时间: {_durationSeconds} 秒");
            sb.AppendLine(_testMode switch
            {
                "SEQ" => $"测试轮数: 顺序 VT3={_iterations["VT3"]}, BKT={_iterations["BKT"]}, SVT={_iterations["SVT"]}",
                "COMBO" => $"测试轮数: 组合 {ComboRounds}",
                _ => $"测试轮数: {_singleRounds}",
            });
            sb.AppendLine();
            for (int i = 0; i < offsets.Count; i++)
                sb.AppendLine($"物理核心 {i}: {offsets[i]}");
            File.WriteAllText(Workspace.FinalOffsets, sb.ToString(), Encoding.UTF8);
            Log.Write($"最终结果已保存到 {Workspace.FinalOffsets}");
        }
        catch (Exception e)
        {
            Log.Write($"保存结果失败: {e.Message}", "WARN");
        }

        Workspace.ClearState();
        Workspace.ClearInProgress();   // 正常结束/手动停止，清除脏标记

        var finalSnapshot = new List<int>(offsets);
        Ui(() => _offsetsLabel.Text = $"最终负压: [{string.Join(", ", finalSnapshot)}]");
    }

    private void TestFinished()
    {
        SetStatus("正在清理进程...", Theme.Warn);
        YCruncher.Kill();

        SetControlsEnabled(true);
        _stopBtn.Enabled = false;
        SetStatus("就绪", Theme.Success);

        if (_testCompletedNormally && !_stopRequested)
            MessageBox.Show("测试已完成！", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show("测试已停止，进程已清理完成。", "已停止", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────────

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private (string algo, int iters) GetTestConfig(string mode)
    {
        string algo = Array.Find(ModeOptions, o => o.mode == mode).algo;
        if (string.IsNullOrEmpty(algo)) algo = "VSTv3"; // 兜底（理论上不会命中，SEQ/COMBO 不走这里）
        return (algo, _singleRounds);
    }

    private bool TryReadSettings(out int duration, out int singleRounds)
    {
        duration = 0;
        singleRounds = 0;
        try
        {
            duration = ReadPositiveInt(_durationBox.Text, "每轮时间");
            singleRounds = ReadPositiveInt(_roundsBox.Text, "轮数");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static int ReadPositiveInt(string raw, string label)
    {
        if (!int.TryParse(raw.Trim(), out int v))
            throw new InvalidOperationException($"{label} 必须是正整数");
        if (v <= 0)
            throw new InvalidOperationException($"{label} 必须大于 0");
        return v;
    }

    private void ApplySettingsToUi()
    {
        _durationBox.Text = _durationSeconds.ToString();
        _roundsBox.Text = _singleRounds.ToString();
    }

    private string SelectedMode() => _selectedMode;

    private void SelectMode(string mode)
    {
        _selectedMode = mode;
        // 单项测试同步下拉；顺序/和项不在下拉里，仅高亮对应按钮。
        if (Array.Exists(ModeOptions, o => o.mode == mode)) SelectComboFor(mode);
        RestyleModeButtons();
    }

    private void SetControlsEnabled(bool enabled)
    {
        _startBtn.Enabled = enabled;
        _modeCombo.Enabled = enabled;
        _durationBox.Enabled = enabled;
        _roundsBox.Enabled = enabled;
        if (_seqBtn != null) _seqBtn.Enabled = enabled;
        if (_comboBtn != null) _comboBtn.Enabled = enabled;
        if (_autoAdjBtn != null) _autoAdjBtn.Enabled = enabled;
        if (_manualAdjBtn != null) _manualAdjBtn.Enabled = enabled;
    }

    private void ClearLog()
    {
        while (_logQueue.TryDequeue(out _)) { }
        _logBox.Clear();
        _logBoxTesting.Clear();
    }

    /// <summary>手动导出当前日志到文件（启动不再自动落盘，仅点此按钮时写出）。</summary>
    private void SaveLog()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "保存日志",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"undervolt_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            InitialDirectory = Workspace.LogsDir,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dlg.FileName, _logBox.Text, Encoding.UTF8);
            SetStatus("日志已保存：" + dlg.FileName, Theme.Success);
        }
        catch (Exception ex)
        {
            SetStatus("保存日志失败：" + ex.Message, Theme.Warn);
        }
    }

    /// <summary>安全地把动作切回 UI 线程执行。</summary>
    private void Ui(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(action);
        }
        catch
        {
            // 窗口正在关闭，忽略
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 仅用户主动关闭算「正常退出」；系统重启/关机、任务管理器结束等都视为异常中断。
        bool userClosing = e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall;

        if (_testTask is { IsCompleted: false })
        {
            // 系统重启/关机时不要弹窗阻塞流程；只在用户主动关闭时确认。
            if (userClosing)
            {
                var r = MessageBox.Show("测试正在运行，确定要退出吗？", "确认",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            _stopRequested = true;
            _cts?.Cancel();
        }

        // 关键：只有用户主动关闭才清脏标记。系统重启/关机/被结束进程时保留脏标记，
        // 以便下次启动识别为「上次测试中断」并从中断的 phase 续跑（含负压回退恢复）。
        if (userClosing)
            Workspace.ClearInProgress();
        YCruncher.Kill();
        base.OnFormClosing(e);
    }
}
