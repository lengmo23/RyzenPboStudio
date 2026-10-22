using System.Collections.Concurrent;
using System.Text;

namespace RyzenPboStudio;

internal sealed partial class MainForm : Form
{
    // ── 控件 ────────────────────────────────────────────────────────────────
    private readonly ComboBox _modeCombo = new();          // 单项测试下拉
    private PillButton _seqBtn = null!;                    // 顺序测试
    private PillButton _comboBtn = null!;                  // 和项测试
    private PillButton _autoAdjBtn = null!;                // 自动调整负压模式
    private PillButton _manualAdjBtn = null!;              // 手动模式（报错只提醒）
    private bool _autoAdjust = true;                       // true=报错自动回退负压；false=只提醒不改负压
    private readonly List<PillButton> _scopeBtns = new();  // 测试范围：全部 / 各 CCD / 逐 CCD / 自定义
    // 按钮上只留短标题，具体测哪些项、跑几轮放到悬停提示里，省下纵向空间
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 120 };
    private readonly TextBox _durationBox = new();
    private readonly TextBox _roundsBox = new();
    private readonly PillButton _startBtn = Primary("▶  开始测试");
    private readonly PillButton _stopBtn = Ghost("■  停止测试");
    private readonly Label _statusLabel = new() { Text = "就绪", AutoSize = true };
    private readonly Label _initialOffsetsLabel = new() { Text = "初始负压: 未读取", AutoSize = true };
    private readonly Label _offsetsLabel = new() { Text = "当前负压: 未读取", AutoSize = true };
    private readonly TextBox _logBox = new(); // Log textbox on the LOGGING page.
    private readonly TextBox _logBoxPbo = new(); // Mirrors log content on the AMD PBO page.
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _offsetTimer = new() { Interval = 1000 };
    private LinkLabel _updateLink = null!;
    private Form _infoDialog = null!;
    private bool _updateBusy; // Prevent concurrent update attempts.
    private Version? _knownNewVersion; // Keep a declined update visible in the info dialog.

    // Align test controls and the combined Start/Stop buttons to one width.
    private const int TestRowWidth = 370;

    // Curve Optimizer editor
    private const int CoDisplaySlotCount = 16;
    private const int CoSlotsPerColumn = 8;
    private readonly List<NumericUpDown> _coCells = new();
    private int _coSlotCount;
    private readonly NumericUpDown _fmaxBox = new();
    private bool _fmaxWritable = true;

    // ── PBO 限制参数条（fmax 复用 _fmaxBox）─────────────────────────────────
    private readonly NumericUpDown _pptBox = new();
    private readonly NumericUpDown _edcBox = new();
    private readonly NumericUpDown _tdcBox = new();
    private readonly DomainUpDown _scalarBox = new();
    private bool _scalarWritable = true;
    private bool _scalarDirty;   // 用户改过 Scalar 选项；没改过时 Apply 不写它，避免 5000 系顺带打开 PBO
    private bool _scalarLoading; // 程序回填 Scalar 时不算用户改动

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

    // 测试范围：ALL=全部核心（走命令行），CCD=单个 CCD，EACH=逐 CCD 依次跑完整轮次，
    // CUSTOM=自定义物理核。后三者走 y-cruncher 配置文件限定逻辑核。
    private string _testScope = "ALL";
    private int _scopeCcd;
    private List<int> _customCores = new();

    // 和项测试（COMBO）固定参数：VT3+BKT+SVT 一起跑 10 轮。
    private static readonly string[] ComboAlgos = { "VSTv3", "BKT", "SVT" };
    private const int ComboRounds = 10;

    // 单项测试下拉选项：显示文本 + 传给 y-cruncher 的组件名（VSTv3 是 VT3 的别名）。
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
        ("YC018 — Pi Benchmark 5b / 10b（内存密集，按内存容量自动选择）", YCruncher.BenchMode, "bench"),
    };

    public MainForm(bool autostartLaunch = false)
    {
        _autostartLaunch = autostartLaunch;
        Text = "AMD Ryzen PBO Studio";
        // AMD PBO 页 = CO | CS + 下方参数条，右侧 LOG；高度让 CO 卡片底边刚好落在 Core 7 下方的「-」之下。
        // 屏幕放不下时由 FitToScreen 等比缩小
        ClientSize = new Size(1280, 854);
        // 无系统标题栏：HeaderBar 充当标题栏（拖动 / 最小化 / 关闭），四周留 1px 画描边
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        MaximizeBox = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 9F);

        TrySetIcon();
        BuildUi();
        FormClosed += (_, _) => _infoDialog.Dispose();
        SetStatus("就绪", Theme.Success);
        Shown += (_, _) => InitCoEditor();
        // After editor init, before the silent update check (which skips itself once a resumed test runs)
        Shown += (_, _) => OnAutostartShown();
        // Check silently at startup; prompt only when an update is available.
        Shown += async (_, _) => await RunUpdateFlowAsync(manual: false);
        Load += (_, _) => FitToScreen();

        Log.OnLine += line => _logQueue.Enqueue(line);
        _logTimer.Tick += OnLogTimer;
        _offsetTimer.Tick += OnOffsetTimer;
        _logTimer.Start();
        _offsetTimer.Start();

        Log.Write("程序已启动");
        if (Workspace.WasInterrupted())
        {
            var st = Workspace.LoadState();
            string resumeHint = st != null && st.TestMode == "SEQ" && !string.IsNullOrEmpty(st.SeqPhase)
                ? $" · 将从中断的 {st.SeqPhase} 阶段继续"
                : "";
            // 中断后已手动改过负压：那组值算用户接管，开测时原样沿用，只续跑测试阶段
            if (Journal.ReadLastEntry()?.Reason == Journal.ManualTakeover)
            {
                SetStatus($"上次测试异常中断 · 负压已手动接管，开始后按此值续跑{resumeHint}", Theme.Warn);
                Log.Write($"上次测试异常中断，手动接管负压后不再自动调整{resumeHint}", "WARN");
            }
            else
            {
                SetStatus($"上次测试异常中断 · 开始后将自动回退恢复负压{resumeHint}", Theme.Warn);
                Log.Write($"上次测试异常中断（疑似死机/断电）：开始测试时崩溃前负压 +{Config.StepOnError}{resumeHint}", "WARN");
            }
        }
    }

    // ── 界面构建 ────────────────────────────────────────────────────────────

    private MonitorView? _monitorView;     // 顶部常驻每核监控
    private readonly HeaderBar _headerBar = new() { Dock = DockStyle.Top, Height = 64 };   // 窗口标题栏 + 硬件信息
    private Control _pboPanel = null!;      // AMD PBO 页：CO | CS + 参数条
    private Control _testingPanel = null!;  // TESTING 页：测试设置
    private Control _loggingPanel = null!;  // LOGGING 页：运行日志
    private NavTabButton _navPboBtn = null!;
    private NavTabButton _navTestingBtn = null!;
    private NavTabButton _navLoggingBtn = null!;

    private enum Page { Pbo, Testing, Logging }

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
            Padding = new Padding(18, 8, 18, 18),
        };
        // 292 时 CCD 表每行约 26px（RowH 下限），再矮就会裁掉 VID 行
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 292));  // 顶部：常驻监控（CCD0/CCD1 + 限制条）
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 中部：AMD PBO / TESTING / LOGGING 内容
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // 底部：AMD PBO / TESTING / LOGGING 沉浸式导航（按钮 42 + 上边距 6）

        var monitorCard = BuildMonitorCard();
        monitorCard.Margin = new Padding(0, 0, 0, 10);

        // 中部内容宿主：三页叠放，按底部按钮切换可见
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        _pboPanel = BuildPboPanel();
        _pboPanel.Dock = DockStyle.Fill;
        _testingPanel = BuildTestingPanel();
        _testingPanel.Dock = DockStyle.Fill;
        _loggingPanel = BuildLogCard(_logBox);
        _loggingPanel.Dock = DockStyle.Fill;
        host.Controls.Add(_pboPanel);
        host.Controls.Add(_testingPanel);
        host.Controls.Add(_loggingPanel);

        root.Controls.Add(monitorCard, 0, 0);
        root.Controls.Add(host, 0, 1);
        root.Controls.Add(BuildNavBar(), 0, 2);
        Controls.Add(root);
        Controls.Add(_headerBar); // Later Dock=Top controls claim the top edge first.
        _infoDialog = BuildInfoDialog();
        _headerBar.InfoClicked += (_, _) => _infoDialog.ShowDialog(this);

        ShowPage(Page.Pbo);
    }

    /// <summary>Builds the AMD PBO page with CO, CS, PBO limits, and source credit.</summary>
    private const int FooterH = 28;

    private Control BuildPboPanel()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 800));   // 参数条带单位后需约 770px，CS 吃掉 CO 以外的宽度
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 6, 0),
        };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // CO | CS
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));      // PBO 参数条
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, FooterH)); // 开源声明

        // CO、CS 按各自网格 + 按钮列的自然宽度定宽，不留空白；上下内边距收小，卡片底边贴近 Core 7 下方的「-」
        var coCs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 8),
        };
        coCs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 414));
        coCs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var coCard = BuildCoCard(); coCard.Margin = new Padding(0, 0, 6, 0); coCard.Padding = new Padding(16, 8, 16, 8);
        var csCard = BuildCsCard(); csCard.Margin = new Padding(6, 0, 0, 0); csCard.Padding = new Padding(16, 8, 16, 8);
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
            Padding = new Padding(2, 0, 0, 0),
        };
        left.Controls.Add(coCs, 0, 0);
        left.Controls.Add(BuildParamBar(), 0, 1);
        left.Controls.Add(credit, 0, 2);

        // 下边距等于开源声明行高，LOG 底边与参数条底边齐平
        var logCard = BuildLogCard(_logBoxPbo);
        logCard.Margin = new Padding(6, 0, 0, FooterH);

        page.Controls.Add(left, 0, 0);
        page.Controls.Add(logCard, 1, 0);
        return page;
    }

    /// <summary>TESTING 页：测试设置 | Single Core Boost / 电压对齐结果，底部状态条（右端显示版本）。</summary>
    private Control BuildTestingPanel()
    {
        var cfg = BuildConfigCard();
        // 结果卡片底部放 Single Core Boost / 电压对齐，与左侧开始 / 停止底边对齐
        var results = NewCard();
        results.Padding = new Padding(16, 8, 16, 8);   // 与左侧测试设置卡片一致，底部按钮才对齐
        results.Controls.Add(_resultsPanel);
        results.Controls.Add(BuildMeasureRow((TestRowWidth - 10) / 2, 10));

        // 左栏固定宽度，窗口变窄时不裁掉开始/停止按钮。
        // +60 = 卡片内边距 32 + 栏间距 6 + 纵向滚动条余量 22。
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TestRowWidth + 60));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 结果表吃掉剩余宽度
        cfg.Margin = new Padding(0, 0, 6, 0);
        results.Margin = new Padding(6, 0, 0, 0);
        top.Controls.Add(cfg, 0, 0);
        top.Controls.Add(results, 1, 0);

        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Font = new Font(Theme.FontFamily, 10.5F, FontStyle.Bold);
        _statusLabel.BackColor = Theme.Bg;
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Theme.Bg, Padding = new Padding(4, 4, 0, 0) };
        statusBar.Controls.Add(_statusLabel);

        var holder = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        holder.Controls.Add(top);
        holder.Controls.Add(statusBar);
        return holder;
    }

    /// <summary>底部导航：沉浸式标签 AMD PBO / TESTING / LOGGING（仿 HYDRA 底栏）。</summary>
    private Control BuildNavBar()
    {
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(0, 6, 0, 0),
        };
        for (int i = 0; i < 3; i++)
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));

        _navPboBtn = NewNavButton("AMD PBO", () => ShowPage(Page.Pbo));
        _navPboBtn.Margin = new Padding(0);
        _navTestingBtn = NewNavButton("TESTING", () => ShowPage(Page.Testing));
        _navTestingBtn.Margin = new Padding(0);
        _navLoggingBtn = NewNavButton("LOGGING", () => ShowPage(Page.Logging));
        _navLoggingBtn.Margin = new Padding(0);

        bar.Controls.Add(_navPboBtn, 0, 0);
        bar.Controls.Add(_navTestingBtn, 1, 0);
        bar.Controls.Add(_navLoggingBtn, 2, 0);
        return bar;
    }

    private static NavTabButton NewNavButton(string text, Action onClick)
    {
        var b = new NavTabButton
        {
            Text = text,
            Size = new Size(170, 42),
            Anchor = AnchorStyles.None,   // 在各自单元格内居中
            Font = new Font(Theme.FontFamily, 11.5F, FontStyle.Bold),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>切换中部内容页：监控常驻顶部不动，仅 AMD PBO / TESTING / LOGGING 区互换。</summary>
    private void ShowPage(Page page)
    {
        var panel = page switch { Page.Testing => _testingPanel, Page.Logging => _loggingPanel, _ => _pboPanel };
        _pboPanel.Visible = page == Page.Pbo;
        _testingPanel.Visible = page == Page.Testing;
        _loggingPanel.Visible = page == Page.Logging;
        panel.BringToFront();

        StyleNav(_navPboBtn, page == Page.Pbo);
        StyleNav(_navTestingBtn, page == Page.Testing);
        StyleNav(_navLoggingBtn, page == Page.Logging);
    }

    private static void StyleNav(NavTabButton? b, bool active)
    {
        if (b == null) return;
        b.Active = active;
        b.Invalidate();
    }

    /// <summary>顶部常驻监控卡片：嵌入 MonitorView（每核 CPPC/AMD CO/EFFREQ/FREQ/VID）。</summary>
    private Control BuildMonitorCard()
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(12, 10, 12, 10);

        try
        {
            _monitorView = new MonitorView(_headerBar) { Dock = DockStyle.Fill };
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
        // 上下内边距比普通卡片小，紧凑排版下各段在 340px 高内放得下
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(16, 8, 16, 8);

        // 单列纵向排布，各段统一 TestRowWidth；高 DPI 下放不下时出滚动条，不裁掉开始/停止按钮。
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0),
        };

        // 单项测试：标题与每轮(秒) / 轮数同一行，下拉框在其下方
        var modeTitle = new Label { Text = "单项测试", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 6, 12, 0), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };

        _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeCombo.FlatStyle = FlatStyle.Flat;
        _modeCombo.Width = TestRowWidth;
        _modeCombo.BackColor = Theme.SurfaceAlt;
        _modeCombo.ForeColor = Theme.TextHi;
        _modeCombo.Font = new Font(Theme.FontFamily, 10F);
        _modeCombo.Margin = new Padding(0, 0, 0, 2);
        foreach (var opt in ModeOptions) _modeCombo.Items.Add(opt.display);
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_modeCombo.SelectedIndex < 0) return;
            _selectedMode = ModeOptions[_modeCombo.SelectedIndex].mode; // 选单项即清除组合测试高亮
            RestyleModeButtons();
        };
        var head = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        head.Controls.Add(modeTitle);
        AddFieldPair(head, "每轮(秒)", _durationBox, 66);
        AddFieldPair(head, "轮数", _roundsBox, 60);
        _durationBox.Text = _durationSeconds.ToString();
        _roundsBox.Text = _singleRounds.ToString();

        // 多项测试（顺序 / 组合）：两键横排，测试项与轮数放在悬停提示里
        var comboTitle = new Label { Text = "多项测试", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 0), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };
        const int modeGap = 10;
        int modeBtnWidth = (TestRowWidth - modeGap) / 2;
        _seqBtn = MakeModeButton("顺序测试", "SEQ", modeBtnWidth,
            $"VT3 → BKT → SVT 依次各测一遍\n轮数：VT3 {_iterations["VT3"]} 轮 / BKT {_iterations["BKT"]} 轮 / SVT {_iterations["SVT"]} 轮\n每项跑完清理进程再进入下一项");
        _comboBtn = MakeModeButton("组合测试", "COMBO", modeBtnWidth,
            $"VT3 + BKT + SVT 三项同时跑，共 {ComboRounds} 轮\n同时施加多种负载，比单项更容易暴露不稳定");
        _seqBtn.Margin = new Padding(0, 0, modeGap, 0);
        _comboBtn.Margin = new Padding(0);
        var modeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0) };
        modeRow.Controls.Add(_seqBtn);
        modeRow.Controls.Add(_comboBtn);

        // 负压调整方式：自动回退 / 手动（只提醒），两键横排等分一行
        var adjTitle = new Label { Text = "负压调整方式", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 0), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };
        const int adjGap = 10;
        int adjBtnWidth = (TestRowWidth - adjGap) / 2;
        _autoAdjBtn   = MakeAdjustButton("自动模式", true,
            $"压测报错时，把出错核心的负压自动回退一档（+{Config.StepOnError}）后重跑整轮，\n直到该轮通过或你手动停止。y-cruncher 整体崩溃时，回退本次测试范围内仍在负压的核心。");
        _manualAdjBtn = MakeAdjustButton("手动模式", false,
            "压测报错时只弹提示并停止，不改动任何负压。\n由你自己判断怎么调，调完重新开始测试。");
        _autoAdjBtn.Width = adjBtnWidth;
        _manualAdjBtn.Width = adjBtnWidth;
        _autoAdjBtn.Margin = new Padding(0, 0, adjGap, 0);
        _manualAdjBtn.Margin = new Padding(0);
        var adjRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 0, 0, 0) };
        adjRow.Controls.Add(_autoAdjBtn);
        adjRow.Controls.Add(_manualAdjBtn);

        // 测试范围：第一行「全部核心 | 自定义」所有机型都有；第二行「各 CCD | 逐 CCD」只有多 CCD 机型才出。
        var scopeTitle = new Label { Text = "测试范围", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 0), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };
        var scopeRow1 = BuildScopeRow(new (string, string, string)[]
        {
            ("全部核心", "ALL", "所有核心一起压测，与旧版本行为一致"),
            ("自定义…", "CUSTOM", "自行勾选参与压测的物理核心"),
        });
        var scopeRow2 = BuildCcdScopeRow();
        if (scopeRow2 != null) scopeRow1.Margin = new Padding(0, 0, 0, 4);   // 与 CCD 行拉开间距

        // 开始 / 停止：停靠卡片底部，两键等分 TestRowWidth（含中间 10px 间隔），上内边距与范围按钮隔开
        const int runGap = 10;
        int runBtnWidth = (TestRowWidth - runGap) / 2;
        var runRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
        // 空心描边，与实心红的已选模式/范围按钮区分
        _startBtn.Normal = Theme.SurfaceAlt;
        _startBtn.Hover = Theme.Border;
        _startBtn.Outline = Theme.Accent;
        _startBtn.ForeColor = Color.FromArgb(0xF2, 0x73, 0x6C);
        _startBtn.Size = new Size(runBtnWidth, 32);
        _stopBtn.Size = new Size(runBtnWidth, 32);
        _startBtn.Margin = new Padding(0, 0, runGap, 0);
        _stopBtn.Margin = new Padding(0);
        _stopBtn.Enabled = false;
        _startBtn.Click += StartTest;
        _stopBtn.Click += StopTest;
        runRow.Controls.Add(_startBtn);
        runRow.Controls.Add(_stopBtn);

        body.Controls.Add(head);
        body.Controls.Add(_modeCombo);
        body.Controls.Add(comboTitle);
        body.Controls.Add(modeRow);
        body.Controls.Add(adjTitle);
        body.Controls.Add(adjRow);
        body.Controls.Add(scopeTitle);
        body.Controls.Add(scopeRow1);
        if (scopeRow2 != null) body.Controls.Add(scopeRow2);

        SelectMode(_selectedMode);
        RestyleAdjustButtons();
        RestyleScopeButtons();

        // 添加顺序决定停靠优先级：标题占顶、按钮占底、body 填中间
        card.Controls.Add(body);
        card.Controls.Add(runRow);
        card.Controls.Add(SectionTitle("测试设置"));
        return card;
    }

    /// <summary>一行测试范围按钮，等分 TestRowWidth。</summary>
    private FlowLayoutPanel BuildScopeRow(IReadOnlyList<(string text, string tag, string tip)> specs)
    {
        const int gap = 6;
        int w = (TestRowWidth - gap * (specs.Count - 1)) / specs.Count;
        var row = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
        for (int i = 0; i < specs.Count; i++)
        {
            var b = MakeScopeButton(specs[i].text, specs[i].tag, w, specs[i].tip);
            b.Margin = new Padding(0, 0, i < specs.Count - 1 ? gap : 0, 0);
            row.Controls.Add(b);
        }
        return row;
    }

    /// <summary>测试范围第二行：每个 CCD 一个键 + 逐 CCD 依次。单 CCD 机型返回 null，整行不出现。</summary>
    private FlowLayoutPanel? BuildCcdScopeRow()
    {
        int ccds = CoreTopology.CcdCount;
        if (ccds <= 1) return null;

        var specs = new List<(string, string, string)>();
        for (int c = 0; c < ccds; c++)
        {
            // 提示里的核心编号要跟 CO 编辑器的槽位一致，不能用 OS 物理核序号
            var cores = CoreTopology.PhysicalCoresOfCcd(c)
                .Select(RyzenSmu.OsCoreToSlot).OrderBy(x => x).ToList();
            specs.Add(($"CCD{c}", $"CCD{c}",
                $"只压 CCD{c}（物理核 {string.Join(", ", cores)}）\n另一个 CCD 空闲时功耗预算全给被测 CCD，\n频率比全核负载时更高，结论不能直接套用到全核"));
        }
        specs.Add(("逐 CCD", "EACH",
            "对每个 CCD 各跑一遍完整轮次（CCD0 → CCD1 …）\n中途某个 CCD 没跑完就不再往下走"));
        return BuildScopeRow(specs);
    }

    /// <summary>测试范围按钮：点选即切换并高亮；「自定义…」先弹核心选择框，取消则不改范围。</summary>
    private PillButton MakeScopeButton(string text, string tag, int width, string tip)
    {
        var b = new PillButton
        {
            Text = text,
            Tag = tag,
            Font = new Font(Theme.FontFamily, 9.5F),
            Height = 30,
            Width = width,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.Border,
            ForeColor = Theme.TextHi,
            Margin = new Padding(0, 0, 0, 2),
        };
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) =>
        {
            if (tag == "CUSTOM")
            {
                if (!PromptCustomCores()) return;
                _testScope = "CUSTOM";
            }
            else if (tag.StartsWith("CCD"))
            {
                _testScope = "CCD";
                _scopeCcd = int.Parse(tag[3..]);
            }
            else
            {
                _testScope = tag;   // ALL / EACH
            }
            RestyleScopeButtons();
        };
        _scopeBtns.Add(b);
        return b;
    }

    private void RestyleScopeButtons()
    {
        string active = _testScope switch
        {
            "CCD" => $"CCD{_scopeCcd}",
            _ => _testScope,
        };
        foreach (var b in _scopeBtns)
        {
            bool sel = (string)b.Tag! == active;
            b.Normal = sel ? Theme.Accent : Theme.SurfaceAlt;
            b.Hover = sel ? Theme.AccentHover : Theme.Border;
            b.ForeColor = sel ? Theme.AccentText : Theme.TextHi;
            b.Invalidate();
        }
    }

    /// <summary>Selects physical cores, excluding fused-off slots; requires at least one selection.</summary>
    private bool PromptCustomCores()
    {
        int slots = _coSlotCount > 0 ? _coSlotCount : RyzenSmu.SlotCount;
        if (slots <= 0) { ThemedDialog.Show(this, "未能读取核心拓扑，无法自定义测试范围。", "提示", warning: true); return false; }

        using var dlg = new Form
        {
            Text = "选择测试核心",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9F),
        };
        dlg.HandleCreated += (_, _) => DarkTitleBar.Apply(dlg.Handle);

        int rows = (slots + 1) / 2;
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = rows,
            AutoSize = true,
            BackColor = Theme.Surface,
            Padding = new Padding(14, 12, 14, 4),
        };
        var boxes = new CheckBox[slots];
        for (int i = 0; i < slots; i++)
        {
            bool disabled = RyzenSmu.IsSlotDisabled(i);
            var cb = new CheckBox
            {
                Text = $"核心 {i}" + (disabled ? "（屏蔽）" : ""),
                AutoSize = true,
                Enabled = !disabled,
                Checked = !disabled && (_customCores.Count == 0 || _customCores.Contains(i)),
                ForeColor = disabled ? Theme.TextLo : Theme.TextHi,
                BackColor = Theme.Surface,
                Margin = new Padding(6, 4, 18, 4),
            };
            boxes[i] = cb;
            grid.Controls.Add(cb, i / rows, i % rows);
        }

        var okBtn = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 88, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Theme.SurfaceAlt, ForeColor = Theme.TextHi };
        okBtn.FlatAppearance.BorderColor = Theme.Border;
        var cancelBtn = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 88, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Theme.SurfaceAlt, ForeColor = Theme.TextHi };
        cancelBtn.FlatAppearance.BorderColor = Theme.Border;
        var btnRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = new Padding(14, 4, 14, 12) };
        btnRow.Controls.Add(okBtn);
        btnRow.Controls.Add(cancelBtn);

        // 复选框与按钮放在同一个 TableLayoutPanel 里，避免复选框盖住确定/取消
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(grid, 0, 0);
        layout.Controls.Add(btnRow, 0, 1);
        dlg.Controls.Add(layout);
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;
        dlg.AutoSize = true;
        dlg.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        if (dlg.ShowDialog(this) != DialogResult.OK) return false;

        var picked = new List<int>();
        for (int i = 0; i < slots; i++)
            if (boxes[i].Enabled && boxes[i].Checked) picked.Add(i);

        if (picked.Count == 0)
        {
            ThemedDialog.Show(this, "至少要选择一个核心。", "提示", warning: true);
            return false;
        }
        _customCores = picked;
        return true;
    }

    /// <summary>负压调整方式按钮（自动/手动）：点选即切换并高亮。</summary>
    private PillButton MakeAdjustButton(string text, bool auto, string tip)
    {
        var b = new PillButton
        {
            Text = text,
            Tag = auto,
            Font = new Font(Theme.FontFamily, 9.5F),
            Height = 30,
            Width = TestRowWidth,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.Border,
            ForeColor = Theme.TextHi,
            Margin = new Padding(0, 0, 0, 2),
        };
        _tips.SetToolTip(b, tip);
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
            ThemedDialog.Show(this,
                $"检测到报错，已停止测试。\n\n报错物理核心: {cores}\n\n" +
                "当前为「手动模式」，负压未做任何改动。请在 AMD PBO 页手动调整负压后重新开始测试。",
                "手动模式 · 检测到报错", warning: true);
        });
    }

    /// <summary>Selects sequential or combined test mode.</summary>
    private PillButton MakeModeButton(string text, string mode, int width, string tip)
    {
        var b = new PillButton
        {
            Text = text,
            Tag = mode,
            Font = new Font(Theme.FontFamily, 9.5F),
            Height = 30,
            Width = width,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,   // 默认灰色，仅选中时由 RestyleModeButtons 改红
            Hover = Theme.Border,
            ForeColor = Theme.TextHi,
            Margin = new Padding(0, 0, 0, 2),
        };
        _tips.SetToolTip(b, tip);
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

    /// <summary>Builds the version and author dialog opened from the title bar.</summary>
    private Form BuildInfoDialog()
    {
        string buildDate = typeof(MainForm).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "?";
        string version = HeaderBar.DisplayVersion;

        var dialog = new Form
        {
            Text = "关于 AMD Ryzen PBO Studio",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(480, 160),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 10F),
        };
        dialog.HandleCreated += (_, _) => DarkTitleBar.Apply(dialog.Handle);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Surface,
            Padding = new Padding(22, 18, 22, 14),
        };
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        Label InfoLine(string text, bool title = false) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = title ? Theme.TextHi : Theme.TextLo,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, title ? 13F : 9.5F,
                title ? FontStyle.Bold : FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        content.Controls.Add(InfoLine("AMD Ryzen PBO Studio", title: true), 0, 0);
        content.Controls.Add(InfoLine($"版本 {version} · 构建 {buildDate}"), 0, 1);
        content.Controls.Add(InfoLine("制作：DY冷漠_OC调试"), 0, 2);

        var links = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0),
        };
        links.Controls.Add(FootLink("项目主页", () => OpenUrl(Updater.HomePage)));
        links.Controls.Add(FootLink("抖音 冷漠OC", () => OpenUrl(DouyinUrl)));
        _updateLink = FootLink("检查更新", () => _ = RunUpdateFlowAsync(manual: true));
        links.Controls.Add(_updateLink);
        content.Controls.Add(links, 0, 3);

        dialog.Controls.Add(content);
        return dialog;
    }

    private const string DouyinUrl = "https://v.douyin.com/6VGnQBTxwIQ/";

    /// <summary>Creates a small link for the version dialog.</summary>
    private static LinkLabel FootLink(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 9.5F),
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentHover,
            VisitedLinkColor = Theme.Accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, 18, 0),
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

        // Do not exit for an update while a stress test is running.
        if (_testTask is { IsCompleted: false })
        {
            if (manual)
                ThemedDialog.Show(Form.ActiveForm ?? this, "测试进行中，无法更新。\n\n请先停止测试，待其安全收尾后再试。", "正在测试");
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
                    ThemedDialog.Show(Form.ActiveForm ?? this, $"检查更新失败：{e.Message}", "检查更新", warning: true);
                return;
            }
            if (IsDisposed) return;

            if (info == null)
            {
                _knownNewVersion = null;
                Updater.ClearSkip();
                RefreshUpdateLinkText();
                if (manual)
                    ThemedDialog.Show(Form.ActiveForm ?? this, $"当前已是最新版本 v{Updater.CurrentVersion}。", "检查更新");
                return;
            }

            Log.Write($"发现新版本 {info.Tag}（当前 v{Updater.CurrentVersion}）");
            _knownNewVersion = info.Version;
            RefreshUpdateLinkText();

            // Keep a skipped version visible in the info dialog without prompting at startup.
            if (!manual && Updater.IsSkipped(info.Version)) return;

            string notes = info.Notes.Length > 600 ? info.Notes[..600] + "\n..." : info.Notes;
            bool update = ThemedDialog.Ask(Form.ActiveForm ?? this,
                $"发现新版本 {info.Tag}，当前为 v{Updater.CurrentVersion}。\n\n" +
                $"{notes}\n\n" +
                $"下载大小约 {info.Size / 1024.0 / 1024.0:F1} MB。\n" +
                "更新会关闭程序、覆盖安装目录后自动重启；\n" +
                "logs 与 profiles 文件夹（负压历史与恢复数据）不会被改动。\n\n" +
                "现在更新吗？",
                "发现新版本", "现在更新", "暂不更新");
            if (!update)
            {
                Updater.SkipVersion(info.Version);
                return;
            }

            await DownloadAndApplyAsync(info);
        }
        finally
        {
            _updateBusy = false;
            if (!IsDisposed) _updateLink.Enabled = true;
        }
    }

    /// <summary>状态条链接文字：已检测到新版本时常驻显示版本号，否则显示「检查更新」。</summary>
    private void RefreshUpdateLinkText()
    {
        if (IsDisposed) return;
        _updateLink.Text = _knownNewVersion != null ? $"有新版本 v{_knownNewVersion}" : "检查更新";
    }

    /// <summary>下载并解压新版本；两步都成功才关闭程序交给替换脚本。</summary>
    private async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        string zipPath;
        try
        {
            // 百分比变化时才刷新界面
            int lastPercent = -1;
            string source = "";
            var progress = new Progress<int>(p =>
            {
                if (IsDisposed || p == lastPercent) return;
                lastPercent = p;
                _updateLink.Text = $"下载中 {p}%";
                SetStatus($"正在下载更新 {info.Tag}（{source}） {p}%", Theme.Warn);
            });
            SetStatus($"正在下载更新 {info.Tag}...", Theme.Warn);
            zipPath = await Updater.DownloadAsync(info, progress,
                s => { source = s; lastPercent = -1; },   // 换源后让下一次回调必定刷新
                // 连接与换源期间没有进度可报，把阶段文字直接写到状态条，等待过程才可见
                stage => Ui(() =>
                {
                    if (IsDisposed) return;
                    _updateLink.Text = "下载中...";
                    SetStatus($"更新 {info.Tag} · {stage}", Theme.Warn);
                }));
        }
        catch (Exception e)
        {
            RefreshUpdateLinkText();
            SetStatus("更新下载失败", Theme.Accent);
            Log.Write($"更新下载失败：{e.Message}", "WARN");
            ShowPanFallbackDialog(info.Tag);
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
            RefreshUpdateLinkText();
            SetStatus("更新包校验失败", Theme.Accent);
            Log.Write($"更新包解压/校验失败：{e.Message}", "WARN");
            ThemedDialog.Show(Form.ActiveForm ?? this, $"更新包无法使用：{e.Message}\n\n当前版本未受影响。", "更新", warning: true);
            return;
        }

        // Replacement must wait until this process exits.
        try
        {
            Updater.LaunchReplacerAndExit(newRoot);
        }
        catch (Exception e)
        {
            RefreshUpdateLinkText();
            ThemedDialog.Show(Form.ActiveForm ?? this, $"无法启动更新程序：{e.Message}\n\n当前版本未受影响。", "更新", warning: true);
            return;
        }

        Log.Write("更新就绪，正在退出以完成替换");
        Close();
    }

    /// <summary>下载源全部失败后的兜底提示：给出网盘地址与提取码，用只读 TextBox 显示以便复制。</summary>
    private void ShowPanFallbackDialog(string tag)
    {
        using var dlg = new Form
        {
            Text = "下载更新失败",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9F),
        };
        dlg.HandleCreated += (_, _) => DarkTitleBar.Apply(dlg.Handle);

        var tip = new Label
        {
            Text = $"GitHub 与各镜像源均无法下载 {tag}。\n可从网盘手动下载，解压后覆盖到本程序目录（logs 与 profiles 请保留）。",
            AutoSize = true,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Margin = new Padding(2, 0, 2, 10),
        };

        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        grid.Controls.Add(NewFieldLabel("网盘链接"), 0, 0);
        var urlBox = NewCopyableBox(Updater.PanUrl, 300);
        grid.Controls.Add(urlBox, 1, 0);
        grid.Controls.Add(NewFieldLabel("提取密码"), 0, 1);
        var codeBox = NewCopyableBox(Updater.PanCode, 300);
        grid.Controls.Add(codeBox, 1, 1);

        var copyBtn = NewDialogButton("复制链接和密码", 130);
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText($"{Updater.PanUrl}\n提取密码：{Updater.PanCode}");
                copyBtn.Text = "已复制";
            }
            catch (Exception ex)
            {
                // 剪贴板偶尔被别的进程占用，复制不了就让用户自己选中文本框
                Log.Write($"复制网盘地址失败：{ex.Message}", "WARN");
                copyBtn.Text = "复制失败";
            }
        };
        var openBtn = NewDialogButton("打开网盘", 96);
        openBtn.Click += (_, _) => OpenUrl(Updater.PanUrl);
        var closeBtn = NewDialogButton("关闭", 80);
        closeBtn.DialogResult = DialogResult.Cancel;

        var btnRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 12, 0, 0),
        };
        btnRow.Controls.Add(copyBtn);
        btnRow.Controls.Add(openBtn);
        btnRow.Controls.Add(closeBtn);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 14, 16, 12),
        };
        for (int i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(tip, 0, 0);
        layout.Controls.Add(grid, 0, 1);
        layout.Controls.Add(btnRow, 0, 2);

        dlg.Controls.Add(layout);
        dlg.CancelButton = closeBtn;
        dlg.AutoSize = true;
        dlg.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        dlg.ShowDialog(Form.ActiveForm ?? this);
    }

    private static Label NewFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        Margin = new Padding(0, 7, 10, 4),
    };

    /// <summary>只读但可选中复制的文本框。用 ReadOnly 而不是 Enabled=false：
    /// 禁用的 TextBox 会被系统改成灰底，且文字选不中。</summary>
    private static TextBox NewCopyableBox(string text, int width) => new()
    {
        Text = text,
        ReadOnly = true,
        Width = width,
        BackColor = Theme.SurfaceAlt,
        ForeColor = Theme.TextHi,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font(Theme.MonoFamily, 9.5F),
        Margin = new Padding(0, 4, 0, 4),
    };

    private static Button NewDialogButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextHi,
            Margin = new Padding(8, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        return b;
    }

    /// <summary>Opens a link in the default browser.</summary>
    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ThemedDialog.Show(Form.ActiveForm ?? this, $"无法打开链接：{e.Message}\n\n{url}", "提示", warning: true);
        }
    }

    /// <summary>Builds a log card for either page; separate textboxes mirror the same log
    /// because one control cannot belong to two parents.</summary>
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

        btnBar.Controls.Add(clearBtn);

        // Dock order keeps the title and buttons outside the filling log box.
        card.Controls.Add(box);
        card.Controls.Add(btnBar);
        card.Controls.Add(SectionTitle("运行日志"));
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
            var b = Ghost(text);
            b.Size = new Size(96, 32);
            b.Margin = new Padding(0, 0, 0, 8);
            b.Click += (_, _) => click();
            return b;
        }
        flow.Controls.Add(Btn("Apply", OnCoApply));
        flow.Controls.Add(Btn("Refresh", OnCoRefresh));
        flow.Controls.Add(Btn("Save", OnCoSave));
        flow.Controls.Add(Btn("Load", OnCoLoad));
        var boot = BuildBootApplyButton();
        boot.Size = new Size(96, 32);
        boot.Margin = new Padding(0, 16, 0, 0);   // 与 Apply~Load 拉开，表明是独立开关
        flow.Controls.Add(boot);
        return flow;
    }

    /// <summary>PBO 参数卡片（CS 右侧）：FMax / PPT / EDC / TDC 竖排 + 一键 Apply（写 SMU 限制），底部为开机自动应用 CO/CS。</summary>
    /// <summary>CO | CS 下方的参数条：FMax / PPT / EDC / TDC / Scalar + 一键 Apply（写 SMU 限制）。</summary>
    private Control BuildParamBar()
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(14, 6, 14, 6);

        ConfigNumBox(_fmaxBox, 0, 7000, 25, 74);
        ConfigNumBox(_pptBox, 30, 400, 5, 62);
        ConfigNumBox(_edcBox, 10, 400, 5, 62);
        ConfigNumBox(_tdcBox, 10, 300, 5, 62);
        _pptBox.Value = 142;   // 启动后由 InitPboBoxes 用 CPU 实际值覆盖
        _edcBox.Value = 180;
        _tdcBox.Value = 160;

        // PBO Scalar：与数字框同款上下箭头框。列表倒序排，箭头向上即倍数变大；Auto 即 1X
        StyleUpDown(_scalarBox, 84);
        _scalarBox.ReadOnly = true;
        _scalarBox.Wrap = false;
        for (int x = 10; x >= 2; x--) _scalarBox.Items.Add($"{x}X");
        _scalarBox.Items.Add("Auto(1X)");
        SelectScalar(1);
        _scalarBox.SelectedItemChanged += (_, _) => { if (!_scalarLoading) _scalarDirty = true; };
        _tips.SetToolTip(_scalarBox,
            "PBO Scalar：Auto 即 1X。倍数越大，SMU 在 boost 时给的电压越高。\n" +
            "只在选项改动后点 Apply 才写入；5000 系写入时会同时打开 PBO。");

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
        };
        AddParamPair(flow, "FMax (MHz)", _fmaxBox);
        AddParamPair(flow, "PPT (W)", _pptBox);
        AddParamPair(flow, "EDC (A)", _edcBox);
        AddParamPair(flow, "TDC (A)", _tdcBox);
        AddParamPair(flow, "Scalar", _scalarBox);

        var apply = Primary("Apply");
        apply.Size = new Size(84, 30);
        apply.Margin = new Padding(0, 4, 0, 0);
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
        StyleUpDown(box, width);
    }

    /// <summary>参数条里数字框与 Scalar 框共用的外观，保证同高同基线。</summary>
    private static void StyleUpDown(UpDownBase box, int width)
    {
        box.Width = width;
        box.BackColor = Theme.SurfaceAlt;
        box.ForeColor = Theme.TextHi;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.TextAlign = HorizontalAlignment.Center;
        box.Font = new Font(Theme.MonoFamily, 10F);
        box.Margin = new Padding(0, 7, 10, 0);
    }

    /// <summary>按倍数（1~10）选中 Scalar 项；列表倒序，10X 在索引 0、Auto(1X) 在末尾。</summary>
    private void SelectScalar(int scalar)
    {
        _scalarLoading = true;
        _scalarBox.SelectedIndex = 10 - Math.Clamp(scalar, 1, 10);
        _scalarLoading = false;
    }

    private uint SelectedScalar() => (uint)(10 - _scalarBox.SelectedIndex);

    private static void AddParamPair(FlowLayoutPanel parent, string label, Control box)
    {
        parent.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 11, 6, 0),
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
            // 顶边距与 CO 按钮列一致（32），两张卡片的 Apply/Refresh/Save/Load 水平对齐；
            // 左边距 18 让按钮落在网格右侧剩余空间的中间，不贴着网格也不贴卡片右边
            Margin = new Padding(18, 32, 0, 0),
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
        grid.Controls.Add(new Label { Text = "", AutoSize = false, Width = CsRowLabelWidth, Height = 18, BackColor = Theme.Surface }, 0, 0);
        string[] cols = { "LOW", "MED", "HIGH" };
        for (int c = 0; c < 3; c++) grid.Controls.Add(CsHeader(cols[c], last: c == 2), c + 1, 0);

        // 5 个频率档（行）
        string[] tiers = { "MIN", "LOW", "MED", "HIGH", "MAX" };
        for (int t = 0; t < 5; t++)
        {
            grid.Controls.Add(CsRowLabel(tiers[t]), 0, t + 1);
            for (int c = 0; c < 3; c++)
            {
                _csCells[t, c] = NewCsCell(last: c == 2);
                grid.Controls.Add(_csCells[t, c], c + 1, t + 1);
            }
        }
        return grid;
    }

    /// <summary>温度档列（LOW/MED/HIGH）之间的横向间距，表头与数字框共用以保持同宽居中。</summary>
    private const int CsColumnGap = 10;

    /// <summary>温度档列宽。卡片宽度要同时容下 5×3 网格与右侧按钮列，CS 数字框比 CO 的窄。</summary>
    private const int CsCellWidth = 46;

    /// <summary>last=true 时不留右间距：最后一列的间距会把按钮列挤出卡片。</summary>
    private static Label CsHeader(string text, bool last) => new()
    {
        Text = text,
        AutoSize = false,
        Width = CsCellWidth,
        Height = 18,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font(Theme.FontFamily, 8.5F),
        Margin = new Padding(0, 0, last ? 0 : CsColumnGap, 2),
    };

    /// <summary>频率档行标（MIN/LOW/MED/HIGH/MAX）的列宽，表头占位格与之同宽。</summary>
    private const int CsRowLabelWidth = 40;

    private static Label CsRowLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = CsRowLabelWidth,
        Height = 26,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        // 右对齐，让文字紧贴第一列数字框
        TextAlign = ContentAlignment.MiddleRight,
        // 上下 5：与数字框同步拉开五个频率档之间的行距
        Margin = new Padding(0, 5, 6, 5),
    };

    private static NumericUpDown NewCsCell(bool last) => new()
    {
        Minimum = -50,
        Maximum = 30,
        Value = 0,
        Width = CsCellWidth,
        BackColor = Theme.SurfaceAlt,
        ForeColor = Theme.TextHi,
        BorderStyle = BorderStyle.FixedSingle,
        TextAlign = HorizontalAlignment.Center,
        Font = new Font(Theme.MonoFamily, 10F),
        Margin = new Padding(0, 5, last ? 0 : CsColumnGap, 5),
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
        // Manual changes during a test do not take ownership; crash recovery still backs off one step.
        bool testRunning = _testTask is { IsCompleted: false };
        string reason = testRunning ? Journal.ManualInTest : Journal.ManualTakeover;
        bool ok = Tuning.Apply(vals, "MANUAL", null, reason);
        Log.WriteCo(ok ? "手动应用 CO：" : "手动应用 CO 写入失败：", vals, level: ok ? "INFO" : "ERROR");
        if (ok && Workspace.WasInterrupted())
        {
            if (testRunning)
                Log.Write("检测到手动应用 CO：后续回退以此值为基准", "WARN");
            else
            {
                // 保留脏标记以续跑中断的阶段，但沿用用户设定的负压，不再叠加 +StepOnError
                Log.Write("检测到手动应用 CO，测试沿用手动设定续跑", "WARN");
                SetStatus("已接管中断负压 · 开始后按此值续跑", Theme.Warn);
                return;
            }
        }
        SetStatus(ok ? "已手动应用 Curve Optimizer" : "CO 应用失败", ok ? Theme.Success : Theme.Accent);
    }

    private void OnCoRefresh()
    {
        var off = RyzenSmu.ReadOffsets(_coSlotCount);
        for (int i = 0; i < _coSlotCount && i < off.Count; i++) SetCell(i, off[i]);
        uint fmax = RyzenSmu.ReadFMax();
        if (fmax > 0) _fmaxBox.Value = Math.Clamp(fmax, (uint)_fmaxBox.Minimum, (uint)_fmaxBox.Maximum);
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
        // 不支持 FMax 写入时跳过：SetBoostLimitAllCore 不校验消息 ID，会向 SMU 发送消息 0
        bool okF = !_fmaxWritable || RyzenSmu.SetFMax(fmax);
        bool okP = RyzenSmu.SetPptLimit(ppt);
        bool okE = RyzenSmu.SetEdcLimit(edc);
        bool okT = RyzenSmu.SetTdcLimit(tdc);
        uint scalar = SelectedScalar();   // Auto = 1X
        bool writeScalar = _scalarWritable && _scalarDirty;
        bool okS = !writeScalar || RyzenSmu.SetPboScalar(scalar);
        if (writeScalar && okS) _scalarDirty = false;
        bool ok = okF && okP && okE && okT && okS;
        string fmaxPart = _fmaxWritable ? $"FMax {fmax} MHz / " : "FMax 跳过（不支持）/ ";
        string scalarPart = !_scalarWritable ? " / Scalar 跳过（不支持）"
                          : writeScalar ? $" / Scalar {_scalarBox.Text}" : " / Scalar 未改动";
        Log.Write($"应用 PBO 限制: {fmaxPart}PPT {ppt} W / EDC {edc} A / TDC {tdc} A{scalarPart}" + (ok ? "" : "（部分写入失败，详见上方）"));
        SetStatus(ok ? "已应用 PBO 限制（FMax/PPT/EDC/TDC/Scalar）" : "PBO 限制应用部分失败", ok ? Theme.Success : Theme.Accent);
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

        // FMax 在 Zen3 上没有写入命令，置灰输入框挡住修改；PPT / TDC / EDC 仍可写，Apply 照常可用。
        _fmaxWritable = RyzenSmu.IsFMaxWriteSupported();
        if (!_fmaxWritable)
        {
            _fmaxBox.Enabled = false;
            Log.Write("当前 CPU 的 SMU 无 FMax 写入命令，已禁用该项；PPT / TDC / EDC 不受影响");
        }

        // Scalar 读数 0 是 OC 模式或读不到，按 Auto 显示；1~10 取整后回填
        float s = RyzenSmu.ReadPboScalar();
        SelectScalar(s >= 1 ? (int)Math.Round(s) : 1);
        _scalarWritable = RyzenSmu.IsPboScalarWriteSupported();
        if (!_scalarWritable)
        {
            _scalarBox.Enabled = false;
            Log.Write("当前 CPU 的 SMU 无 PBO Scalar 写入命令，已禁用该项");
        }
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
        Height = 26,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        Font = new Font(Theme.FontFamily, 10.5F, FontStyle.Bold),
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
            Margin = new Padding(0, 0, 10, 0),
        };
        pair.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 6, 4, 0),
        });

        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.SurfaceAlt;
        box.ForeColor = Theme.TextHi;
        box.Font = new Font(Theme.FontFamily, 10F);
        box.TextAlign = HorizontalAlignment.Center;
        box.Dock = DockStyle.Fill;

        var field = new Card
        {
            Fill = Theme.SurfaceAlt,
            Stroke = Theme.Border,
            Radius = 7,
            Width = width,
            Height = 28,
            BackColor = Theme.Surface,
            Padding = new Padding(8, 3, 8, 2),
            Margin = new Padding(0),
        };
        field.Controls.Add(box);
        pair.Controls.Add(field);
        parent.Controls.Add(pair);
    }

    private void TrySetIcon()
    {
        try
        {
            // 磁盘上的 ICON.ico
            string ico = Path.Combine(Workspace.BaseDir, "ICON.ico");
            if (File.Exists(ico)) { Icon = new Icon(ico); return; }

            // 磁盘上没有时回退到内嵌资源
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("RyzenPboStudio.ICON.ico");
            if (stream != null) Icon = new Icon(stream);
        }
        catch (Exception e)
        {
            Log.Write($"加载图标失败: {e.Message}", "WARN");
        }
    }

    // ── 无边框窗口 ──────────────────────────────────────────────────────────

    /// <summary>无边框窗口补回系统行为：WS_MINIMIZEBOX | WS_SYSMENU 让任务栏单击可最小化 / 还原、Alt+Space 可用；
    /// CS_DROPSHADOW 给窗口加阴影。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000 | 0x00080000;   // WS_MINIMIZEBOX | WS_SYSMENU
            cp.ClassStyle |= 0x00020000;           // CS_DROPSHADOW
            return cp;
        }
    }

    /// <summary>四周 1px 描边（Padding 留出的那一圈）。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    // ── 定时器 ──────────────────────────────────────────────────────────────

    private void OnLogTimer(object? sender, EventArgs e)
    {
        bool any = false;
        while (_logQueue.TryDequeue(out var line))
        {
            // AMD PBO 页与 LOGGING 页各有一个日志框，同步追加保证切页后内容一致
            _logBox.AppendText(line + "\r\n");
            _logBoxPbo.AppendText(line + "\r\n");
            any = true;
        }
        if (any)
        {
            _logBox.SelectionStart = _logBox.TextLength;
            _logBoxPbo.SelectionStart = _logBoxPbo.TextLength;
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
            ThemedDialog.Show(this, ex.Message, "缺少 y-cruncher", warning: true);
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
        SetStatus($"运行中 · 模式 {_testMode} · 范围 {ScopeLabel(_testScope, _scopeCcd)} · 每轮 {duration} 秒", Theme.Warn);

        if (sender == _startBtn) AutoStart.ResetResumeCount();   // manual start resets the auto-resume streak
        _testTask = Task.Run(RunTestThread);
    }

    private void StopTest(object? sender, EventArgs e)
    {
        _stopRequested = true;
        _cts?.Cancel();
        _measureCts?.Cancel();
        SetStatus("正在停止...", Theme.Accent);
        _stopBtn.Enabled = false;
    }

    private void RunTestThread()
    {
        _testCompletedNormally = false;
        try
        {
            _testCompletedNormally = RunTest();
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

    /// <summary>返回 true 表示所有测试都已跑完；未跑完（含 y-cruncher 中途退出、下发负压失败）返回 false。</summary>
    private bool RunTest()
    {
        // 先判断上次是否异常中断（脏标记），再立即为本次运行打标记
        bool interrupted = Workspace.WasInterrupted();
        var prev = Workspace.LoadState();
        var lastEntry = Journal.ReadLastEntry();
        var lastApplied = lastEntry?.Offsets;
        // 中断后用户手动改过负压：以这组值起跑，不再叠加 +StepOnError
        bool manualTakeover = lastEntry?.Reason == Journal.ManualTakeover;
        Workspace.MarkInProgress($"start {DateTime.Now:O}");
        OnTestStartedForAutostart();

        int numCores = RyzenSmu.SlotCount;

        string? seqResumePhase = null;
        List<int> offsets;
        int testRound = 0;

        if (interrupted && (lastApplied != null || prev != null))
        {
            bool hardReset = Workspace.RecentKernelPowerEvent();
            Log.Write(hardReset
                ? "上次测试异常中断 + 内核电源事件（疑似死机/断电），执行恢复"
                : "上次测试未正常结束，执行恢复", "WARN");

            // 崩溃前负压：优先取落盘日志最后一条，其次状态文件
            offsets = lastApplied is { Count: > 0 }
                ? new List<int>(lastApplied)
                : (prev is { Offsets.Count: > 0 } ? new List<int>(prev.Offsets) : Enumerable.Repeat(0, numCores).ToList());

            if (prev != null)
            {
                testRound = prev.TestRound;
                _testMode = string.IsNullOrEmpty(prev.TestMode) ? SelectedMode() : prev.TestMode;
                seqResumePhase = prev.SeqPhase;
                if (!string.IsNullOrEmpty(prev.TestScope)) _testScope = prev.TestScope;
                if (prev.ScopeCcd is int pc) _scopeCcd = pc;
                if (prev.ScopeCores is { Count: > 0 } sc) _customCores = new List<int>(sc);
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
            if (manualTakeover)
            {
                Log.WriteCo("沿用手动应用的负压：", offsets, level: "WARN");
            }
            else
            {
                for (int i = 0; i < offsets.Count; i++)
                    if (!RyzenSmu.IsSlotDisabled(i)) offsets[i] += Config.StepOnError;
                Log.WriteCo($"死机恢复，所有核心负压 +{Config.StepOnError}：", offsets, preRecovery, "WARN");
            }

            if (!Tuning.Apply(offsets, _testMode, seqResumePhase, "crash-recovery"))
            {
                Log.Write("死机恢复设置负压失败", "ERROR");
                return false;
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
                RestyleScopeButtons();
                _initialOffsetsLabel.Text = $"初始负压: [{string.Join(", ", preRecovery)}]";
            });
        }
        else
        {
            _testMode = SelectedMode();
            seqResumePhase = null;
            // Start EACH mode at CCD0 unless resuming an interrupted CCD.
            if (_testScope == "EACH") _scopeCcd = 0;
            offsets = RyzenSmu.ReadOffsets(numCores);
            testRound = 0;
            Log.WriteCo("当前 CO 值：", offsets);

            // Persist baseline offsets for recovery before the first test callback.
            Journal.Record(offsets, _testMode, null, "baseline");
        }

        // The Pi benchmark always runs on all cores
        if (_testMode == YCruncher.BenchMode && _testScope != "ALL")
        {
            Log.Write($"YC018 基准测试固定使用全部核心，已忽略所选测试范围（{ScopeLabel(_testScope, _scopeCcd)}）", "WARN");
            _testScope = "ALL";
            Ui(RestyleScopeButtons);
        }

        _initialOffsets = new List<int>(offsets);
        var initSnapshot = new List<int>(offsets);
        Ui(() =>
        {
            _initialOffsetsLabel.Text = $"初始负压: [{string.Join(", ", initSnapshot)}]";
            _offsetsLabel.Text = $"当前负压: [{string.Join(", ", initSnapshot)}]";
        });

        var token = _cts!.Token;

        // 逐 CCD 模式下把整套测试对每个 CCD 各跑一遍；其余模式只跑一遍。
        // 断电恢复时 _scopeCcd 记着上次跑到哪个 CCD，从那里接着来。
        bool eachCcd = _testScope == "EACH" && CoreTopology.CcdCount > 1;
        int scopeStart = eachCcd ? Math.Clamp(_scopeCcd, 0, CoreTopology.CcdCount - 1) : 0;
        int scopeEnd = eachCcd ? CoreTopology.CcdCount - 1 : 0;

        bool allPassed = true;
        for (int pass = scopeStart; pass <= scopeEnd; pass++)
        {
            if (_stopRequested) break;
            if (eachCcd) _scopeCcd = pass;
            // 逐 CCD 时 CCD 号来自循环，单选某个 CCD 时来自用户的选择（此时循环只跑一趟，pass 恒为 0）
            int ccd = eachCcd ? pass : _scopeCcd;
            var (scopeLogical, scopeCores) = ResolveScope(eachCcd ? "CCD" : _testScope, ccd);
            string scopeLabel = ScopeLabel(eachCcd ? "CCD" : _testScope, ccd);

            bool passOk = RunOnePass(scopeLogical, scopeCores, scopeLabel);
            seqResumePhase = null;   // 下一个 CCD 从第一个阶段重新开始
            if (!passOk)             // 本轮没跑完（取消／无法继续），不再往下一个 CCD 走
            {
                allPassed = false;
                break;
            }
            if (pass < scopeEnd)
            {
                Log.Write($">>> CCD{pass} 测试完成，清理进程...");
                YCruncher.Kill();
            }
        }

        bool RunOnePass(List<int>? scopeLogical, List<int>? scopeCores, string scopeLabel)
        {
            bool passOk = true;
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
                        TestScope = _testScope,
                        ScopeCcd = _scopeCcd,
                        ScopeCores = _testScope == "CUSTOM" ? new List<int>(_customCores) : null,
                        DurationSeconds = _durationSeconds,
                        IterationsMap = new Dictionary<string, int>(_iterations),
                        Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    });

                    bool ok;
                    (ok, offsets) = YCruncher.RunStressTest(new[] { algo }, iters, offsets, _durationSeconds, _testMode, token, _autoAdjust, NotifyManualError, scopeLogical, scopeCores, scopeLabel);
                    if (!ok)
                    {
                        Log.Write($"{name} 测试未完成", "WARN");
                        passOk = false;
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
                    TestScope = _testScope,
                    ScopeCcd = _scopeCcd,
                    ScopeCores = _testScope == "CUSTOM" ? new List<int>(_customCores) : null,
                    DurationSeconds = _durationSeconds,
                    IterationsMap = new Dictionary<string, int>(_iterations),
                    Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                });

                bool ok;
                (ok, offsets) = YCruncher.RunStressTest(ComboAlgos, ComboRounds, offsets, _durationSeconds, _testMode, token, _autoAdjust, NotifyManualError, scopeLogical, scopeCores, scopeLabel);
                if (ok) Log.Write("组合测试全部完成！");
                else passOk = false;
            }
            else
            {
                var (algo, iters) = GetTestConfig(_testMode);
                Workspace.SaveState(new TestState
                {
                    Offsets = offsets,
                    TestRound = testRound,
                    TestMode = _testMode,
                    TestScope = _testScope,
                    ScopeCcd = _scopeCcd,
                    ScopeCores = _testScope == "CUSTOM" ? new List<int>(_customCores) : null,
                    DurationSeconds = _durationSeconds,
                    IterationsMap = new Dictionary<string, int>(_iterations),
                    Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                });

                bool ok;
                (ok, offsets) = _testMode == YCruncher.BenchMode
                    ? YCruncher.RunPiBench(iters, offsets, _testMode, token, _autoAdjust, NotifyManualError)
                    : YCruncher.RunStressTest(new[] { algo }, iters, offsets, _durationSeconds, _testMode, token, _autoAdjust, NotifyManualError, scopeLogical, scopeCores, scopeLabel);
                if (ok) Log.Write("测试全部完成！");
                else passOk = false;
            }
            return passOk;
        }

        // An incomplete run must not produce final results unless the user stopped it.
        if (!allPassed && !_stopRequested)
        {
            Log.WriteCo("测试未完成，未生成最终结果。当前负压：", offsets, level: "WARN");
            Workspace.ClearState();
            Workspace.ClearInProgress(); // Clear the marker to avoid a false backoff next run.
            var partialSnapshot = new List<int>(offsets);
            Ui(() => _offsetsLabel.Text = $"当前负压: [{string.Join(", ", partialSnapshot)}]");
            return false;
        }

        Log.Write("\n" + new string('=', 50));
        Log.WriteCo("过测 PBO 参数：", offsets);
        if (RyzenSmu.IsCurveShaperSupported())
        {
            int[,]? cs = RyzenSmu.ReadCurveShaper(out bool csReadable);
            // Old BIOS cannot read CS back; the last applied values stand in
            if (!csReadable) cs = _csState.HasMargins ? _csState.ToGrid() : null;
            if (cs != null)
            {
                Log.Write($"{"Curve Shaper",-12}{"LOW",5}{"MED",5}{"HIGH",5}");
                string[] tiers = { "MIN", "LOW", "MED", "HIGH", "MAX" };
                for (int t = 0; t < 5; t++)
                    Log.Write($"{tiers[t],-12}{cs[t, 0],5}{cs[t, 1],5}{cs[t, 2],5}");
            }
        }

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"测试完成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"测试模式: {_testMode}");
            sb.AppendLine($"测试范围: {ScopeLabel(_testScope, _scopeCcd)}");
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
        return true;
    }

    private void TestFinished()
    {
        SetStatus("正在清理进程...", Theme.Warn);
        YCruncher.Kill();

        SetControlsEnabled(true);
        _stopBtn.Enabled = false;
        SetStatus("就绪", Theme.Success);
        OnTestFinishedForAutostart();

        if (_testCompletedNormally && !_stopRequested)
            ThemedDialog.Show(this, "测试已完成！", "完成");
        else if (!_stopRequested)
            ThemedDialog.Show(this, "测试未跑完，未生成最终结果，原因见运行日志。", "测试未完成", warning: true);
        else
            ThemedDialog.Show(this, "测试已停止，进程已清理完成。", "已停止");
    }

    // Single Core Boost and voltage alignment

    private PillButton _scbBtn = null!;
    private PillButton _alignBtn = null!;
    private CancellationTokenSource? _measureCts;
    private Task? _measureTask;
    private readonly ResultsPanel _resultsPanel = new() { Dock = DockStyle.Fill };   // TESTING 页右栏

    private FlowLayoutPanel BuildMeasureRow(int btnWidth, int gap)
    {
        // 上内边距把按钮和结果摘要隔开；按钮与开始 / 停止同高
        var row = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
        _scbBtn = Ghost("Single Core Boost");
        _alignBtn = Ghost("电压对齐");
        _scbBtn.Size = _alignBtn.Size = new Size(btnWidth, 32);
        _scbBtn.Margin = new Padding(0, 0, gap, 0);
        _alignBtn.Margin = new Padding(0);
        _tips.SetToolTip(_scbBtn, "逐核运行单核负载，测量当前 CO 下各核能跑到的频率 / 有效频率 / 电压\n只是测量，不是压力测试，不修改任何参数");
        _tips.SetToolTip(_alignBtn, "CO 全部置 0 后逐核测量单核电压，各 CCD 以 CPPC 最高核心中同频电压最低者为基准对齐\n再分 CCD 粗找、细找单核频率平台，最终全核复核");
        _scbBtn.Click += (_, _) => StartMeasure("Single Core Boost", (win, ct) => SingleCoreBoost.Run(_monitorView, win, ct));
        _alignBtn.Click += (_, _) => OnAlignClick();
        row.Controls.Add(_scbBtn);
        row.Controls.Add(_alignBtn);
        return row;
    }

    private void OnAlignClick()
    {
        if (!VoltageAlign.Confirm(this)) return;
        int editorMin = (int)_coCells[0].Minimum;
        StartMeasure("电压对齐", (win, ct) =>
        {
            var final = VoltageAlign.Run(_monitorView, editorMin, win, ct);
            if (final == null) return false;
            Ui(() => { for (int i = 0; i < _coSlotCount && i < final.Count; i++) SetCell(i, final[i]); });
            return true;
        });
    }

    /// <summary>Runs a single-core measurement in the background, results shown on the TESTING page. Mutually exclusive
    /// with stress tests; the stop button cancels it. The offset poll timer is paused so it does not wake cores.</summary>
    private void StartMeasure(string title, Func<ResultsPanel, CancellationToken, bool> work)
    {
        if (_testTask is { IsCompleted: false } || _measureTask is { IsCompleted: false }) return;
        if (!SingleCoreBoost.IsSupported)
        {
            ThemedDialog.Show(this, "CPU 不支持 AVX2 / FMA，无法运行单核负载。", title, warning: true);
            return;
        }

        var cts = new CancellationTokenSource();
        _measureCts = cts;
        var win = _resultsPanel;
        win.Begin(title);

        SetControlsEnabled(false);
        _stopBtn.Enabled = true;
        _offsetTimer.Stop();
        SetStatus($"{title} 运行中", Theme.Warn);

        _measureTask = Task.Run(() =>
        {
            bool ok = false;
            try { ok = work(win, cts.Token); }
            catch (Exception e) { Log.Write($"{title} 异常: {e.Message}", "ERROR"); }
            finally
            {
                bool cancelled = cts.IsCancellationRequested;
                if (cancelled) Log.Write($"{title} 已取消", "WARN");
                win.Finish(cancelled ? "已取消" : ok ? "完成" : "失败，原因见运行日志", ok && !cancelled);
                Ui(() =>
                {
                    SetControlsEnabled(true);
                    _stopBtn.Enabled = false;
                    _offsetTimer.Start();
                    SetStatus("就绪", Theme.Success);
                });
            }
        });
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
        if (string.IsNullOrEmpty(algo)) algo = "VSTv3"; // 兜底
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
            ThemedDialog.Show(this, ex.Message, "参数错误", warning: true);
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

    /// <summary>把测试范围解析成 (要压的逻辑核, 对应物理核)。全部核心返回 (null, null)，
    /// 表示不限定核心、走 y-cruncher 命令行路径。</summary>
    private (List<int>? logical, List<int>? physical) ResolveScope(string scope, int ccd)
    {
        switch (scope)
        {
            case "CCD":
            {
                var logical = CoreTopology.LogicalCoresOfCcd(ccd);
                if (logical.Count == 0) return (null, null);   // 拓扑读不到就退回全核，不因此中断测试
                var slots = CoreTopology.PhysicalCoresOfCcd(ccd)
                    .Select(RyzenSmu.OsCoreToSlot).OrderBy(x => x).ToList();
                return (logical, slots);
            }
            case "CUSTOM":
            {
                if (_customCores.Count == 0) return (null, null);
                // 复选框给的是槽位，问逻辑核之前要换回 OS 物理核序号
                var osCores = _customCores.Select(RyzenSmu.SlotToOsCore).Where(c => c >= 0).ToList();
                var logical = CoreTopology.LogicalCoresOfPhysical(osCores);
                if (logical.Count == 0) return (null, null);
                return (logical, new List<int>(_customCores));
            }
            default:
                return (null, null);
        }
    }

    private string ScopeLabel(string scope, int ccd) => scope switch
    {
        "CCD" => $"CCD{ccd}",
        "CUSTOM" => $"自定义核心 [{string.Join(", ", _customCores)}]",
        "EACH" => "逐 CCD 依次",
        _ => "全部核心",
    };

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
        foreach (var b in _scopeBtns) b.Enabled = enabled;
        if (_scbBtn != null) _scbBtn.Enabled = enabled;
        if (_alignBtn != null) _alignBtn.Enabled = enabled;
    }

    private void ClearLog()
    {
        while (_logQueue.TryDequeue(out _)) { }
        _logBox.Clear();
        _logBoxPbo.Clear();
    }

    /// <summary>Dispatches an action to the UI thread when the window is available.</summary>
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
        // Only user-initiated close is a normal exit; preserve recovery state on shutdown or termination.
        bool userClosing = e.CloseReason is CloseReason.UserClosing or CloseReason.ApplicationExitCall;

        if (_testTask is { IsCompleted: false })
        {
            // Do not block system shutdown with a confirmation dialog.
            if (userClosing)
            {
                if (!ThemedDialog.Ask(this, "测试正在运行，确定要退出吗？", "确认", "退出", "取消"))
                {
                    e.Cancel = true;
                    return;
                }
            }
            _stopRequested = true;
            _cts?.Cancel();
        }

        _measureCts?.Cancel();

        // 只有用户主动关闭才清脏标记；其余情况保留，下次启动按「上次测试中断」续跑并回退负压。
        if (userClosing)
            Workspace.ClearInProgress();
        OnClosingForAutostart(userClosing);
        YCruncher.Kill();
        base.OnFormClosing(e);
    }
}
