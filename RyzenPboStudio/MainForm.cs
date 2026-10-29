using System.Collections.Concurrent;
using System.Text;

namespace RyzenPboStudio;

internal sealed partial class MainForm : Form
{
    // Controls
    private readonly ComboBox _modeCombo = new();
    private PillButton _seqBtn = null!;
    private PillButton _comboBtn = null!;
    private PillButton _autoAdjBtn = null!;
    private PillButton _manualAdjBtn = null!;
    private bool _autoAdjust = true; // Automatic mode backs off CO; manual mode only reports errors.
    private readonly List<PillButton> _scopeBtns = new();
    // Keep detailed test selection and round counts in tooltips to save vertical space.
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 120 };
    private readonly TextBox _durationBox = new();
    private readonly TextBox _roundsBox = new();
    private readonly PillButton _startBtn = Primary("▶  开始测试");
    private readonly PillButton _stopBtn = Ghost("■  停止测试");
    private readonly Label _statusLabel = new() { Text = "就绪", AutoSize = true };
    private readonly Label _initialOffsetsLabel = new() { Text = "初始 CO: 未读取", AutoSize = true };
    private readonly Label _offsetsLabel = new() { Text = "当前 CO: 未读取", AutoSize = true };
    private readonly TextBox _logBox = new(); // Log textbox on the LOGGING page.
    private readonly TextBox _logBoxPbo = new(); // Mirrors log content on the AMD PBO page.
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _offsetTimer = new() { Interval = 1000 };
    private Form _infoDialog = null!;
    private bool _updateBusy; // Prevent concurrent update attempts.
    private Version? _knownNewVersion; // Keep a declined update visible on the update button.

    // Align test controls and the combined Start/Stop buttons to one width.
    private const int TestRowWidth = 370;

    // Curve Optimizer editor
    private const int CoDisplaySlotCount = 16;
    private const int CoSlotsPerColumn = 8;
    private readonly List<NumericUpDown> _coCells = new();
    private int _coSlotCount;
    private readonly NumericUpDown _fmaxBox = new();
    private bool _fmaxWritable = true;

    // PBO limit controls
    private readonly NumericUpDown _pptBox = new();
    private readonly NumericUpDown _edcBox = new();
    private readonly NumericUpDown _tdcBox = new();
    private readonly ComboBox _scalarBox = new();
    private bool _scalarWritable = true;
    private bool _scalarDirty; // Avoid writing unchanged Scalar, which would enable PBO on Zen3.
    private bool _scalarLoading; // Programmatic Scalar refresh is not a user change.

    // Curve Shaper editor
    private readonly NumericUpDown[,] _csCells = new NumericUpDown[5, 3];
    private PillButton _csApplyBtn = null!;
    private PillButton _csRefreshBtn = null!;
    private PillButton _csLoadBtn = null!;
    private PillButton _csSaveBtn = null!;
    private Label _csTitleLabel = null!;
    private CsState _csState = new();

    // Run state
    private readonly ConcurrentQueue<string> _logQueue = new();
    private CancellationTokenSource? _cts;
    private volatile bool _stopRequested;
    private Task? _testTask;
    private bool _testCompletedNormally;
    private string _testMode = "VT3";
    private string _selectedMode = "VT3";
    private int _durationSeconds = Config.DefaultDuration;
    private int _singleRounds = Config.DefaultBkt;
    private List<int> _initialOffsets = new();

    // CCD, EACH, and CUSTOM scopes use a y-cruncher config to restrict logical cores.
    private string _testScope = "ALL";
    private int _scopeCcd;
    private List<int> _customCores = new();

    // VSTv3 is y-cruncher’s alias for VT3.
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
        Config.StepOnError = _multi.BackoffStep; // Before crash recovery reads it.
        Text = "AMD Ryzen PBO Studio";
        ClientSize = new Size(1280, 854);
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
            // A manual CO change after interruption takes ownership of the offsets; resume the stage without backoff.
            if (Journal.ReadLastEntry()?.Reason == Journal.ManualTakeover)
            {
                SetStatus($"上次测试异常中断 · CO 已手动接管，开始后按此值续跑{resumeHint}", Theme.Warn);
                Log.Write($"上次测试异常中断，手动接管 CO 后不再自动调整{resumeHint}", "WARN");
            }
            else
            {
                SetStatus($"上次测试异常中断 · 开始后将自动回退恢复 CO{resumeHint}", Theme.Warn);
                Log.Write($"上次测试异常中断（疑似死机/断电）：开始测试时崩溃前 CO +{Config.StepOnError}{resumeHint}", "WARN");
            }
        }
    }

    // UI construction

    private MonitorView? _monitorView;
    private readonly HeaderBar _headerBar = new() { Dock = DockStyle.Top, Height = 64 };
    private Control _pboPanel = null!;
    private Control _testingPanel = null!;
    private Control _loggingPanel = null!;
    private NavTabButton _navPboBtn = null!;
    private NavTabButton _navTestingBtn = null!;
    private NavTabButton _navLoggingBtn = null!;

    private enum Page { Pbo, Testing, Logging }

    /// <summary>Scales the layout for DPI, then shrinks it to fit the work area and centers it.
    /// Scale does not resize fonts, so font scaling is handled separately.</summary>
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

    /// <summary>Scales control fonts recursively because Scale only resizes the layout.</summary>
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
        // Keep CCD rows tall enough to show the VID row.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 292));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var monitorCard = BuildMonitorCard();
        monitorCard.Margin = new Padding(0, 0, 0, 10);

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
        _headerBar.UpdateClicked += (_, _) => _ = RunUpdateFlowAsync(manual: true);
        _headerBar.SponsorClicked += (_, _) =>
        {
            using var dlg = BuildSponsorDialog();
            dlg.ShowDialog(this);
        };

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
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 800)); // Leave room for PBO values with units and the CS grid.
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
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, FooterH));

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

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
        var boot = BuildBootApplyCheckBox();
        boot.Margin = new Padding(6, 2, 0, 2);
        footer.Controls.Add(credit, 0, 0);
        footer.Controls.Add(boot, 1, 0);
        left.Controls.Add(coCs, 0, 0);
        left.Controls.Add(BuildParamBar(), 0, 1);
        left.Controls.Add(footer, 0, 2);

        var logCard = BuildLogCard(_logBoxPbo);
        logCard.Margin = new Padding(6, 0, 0, FooterH);
        logCard.Padding = new Padding(16, 8, 14, 12);

        page.Controls.Add(left, 0, 0);
        page.Controls.Add(logCard, 1, 0);
        return page;
    }

    /// <summary>Builds the TESTING page with settings, measurement results, and status.</summary>
    private Control BuildTestingPanel()
    {
        var cfg = BuildConfigCard();
        var results = NewCard();
        results.Padding = new Padding(16, 8, 16, 8); // Match bottom padding so action buttons align.
        results.Controls.Add(_resultsPanel);
        results.Controls.Add(BuildMeasureRow((TestRowWidth - 10) / 2, 10));

        // Keep Start/Stop visible when the window narrows.
        // The extra width covers padding, the column gap, and the scrollbar.
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, TestRowWidth + 60));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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

    /// <summary>Builds the bottom page navigation.</summary>
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
            Anchor = AnchorStyles.None,
            Font = new Font(Theme.FontFamily, 11.5F, FontStyle.Bold),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Switches the content page while keeping the monitor visible.</summary>
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

    /// <summary>Builds the persistent per-core monitor card.</summary>
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
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(16, 8, 16, 8);

        // Scroll on high-DPI screens instead of clipping Start/Stop.
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0),
        };

        var modeTitle = new Label { Text = "单项测试", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 4), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };

        _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeCombo.FlatStyle = FlatStyle.Flat;
        _modeCombo.Width = TestRowWidth;
        _modeCombo.BackColor = Theme.SurfaceAlt;
        _modeCombo.ForeColor = Theme.TextHi;
        _modeCombo.Font = new Font(Theme.FontFamily, 10F);
        _modeCombo.Margin = new Padding(0);
        DarkCombo.Apply(_modeCombo);
        foreach (var opt in ModeOptions) _modeCombo.Items.Add(opt.display);
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_modeCombo.SelectedIndex < 0) return;
            _selectedMode = ModeOptions[_modeCombo.SelectedIndex].mode;
            RestyleModeButtons();
        };
        var fields = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 6, 0, 6) };
        AddFieldPair(fields, "每轮(秒)", _durationBox, 66);
        AddFieldPair(fields, "轮数", _roundsBox, 60);
        _durationBox.Text = _durationSeconds.ToString();
        _roundsBox.Text = _singleRounds.ToString();

        var comboTitle = new Label { Text = "多项测试", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 2), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };
        const int modeGap = 10;
        _multiSetBtn = BuildMenuButton("设置顺序测试与组合测试的项目、轮数和每轮时间", ShowMultiTestDialog);
        int modeBtnWidth = (TestRowWidth - _multiSetBtn.Width - 2 * modeGap) / 2;
        _seqBtn = MakeModeButton("顺序测试", "SEQ", modeBtnWidth, SeqTip());
        _comboBtn = MakeModeButton("组合测试", "COMBO", modeBtnWidth, ComboTip());
        _seqBtn.Margin = new Padding(0, 0, modeGap, 0);
        _comboBtn.Margin = new Padding(0, 0, modeGap, 0);
        var modeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        modeRow.Controls.Add(_seqBtn);
        modeRow.Controls.Add(_comboBtn);
        modeRow.Controls.Add(_multiSetBtn);

        var adjTitle = new Label { Text = "CO 调整方式", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 2), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };
        const int adjGap = 10;
        _stepSetBtn = BuildMenuButton("设置自动模式每次回退的 CO 步长", ShowStepDialog);
        int adjBtnWidth = (TestRowWidth - _stepSetBtn.Width - 2 * adjGap) / 2;
        _autoAdjBtn   = MakeAdjustButton("自动模式", true, AutoAdjustTip());
        _manualAdjBtn = MakeAdjustButton("手动模式", false,
            "报错仅提示");
        _autoAdjBtn.Width = adjBtnWidth;
        _manualAdjBtn.Width = adjBtnWidth;
        _autoAdjBtn.Margin = new Padding(0, 0, adjGap, 0);
        _stepSetBtn.Margin = new Padding(0, 0, adjGap, 0);
        _manualAdjBtn.Margin = new Padding(0);
        var adjRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        adjRow.Controls.Add(_autoAdjBtn);
        adjRow.Controls.Add(_stepSetBtn);
        adjRow.Controls.Add(_manualAdjBtn);

        var scopeTitle = new Label { Text = "测试范围", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 2), Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold) };
        var scopeRow1 = BuildScopeRow(new (string, string, string)[]
        {
            ("全部核心", "ALL", "所有核心参与压测"),
            ("自定义…", "CUSTOM", "自行勾选参与压测的物理核心"),
        });
        var scopeRow2 = BuildCcdScopeRow();
        if (scopeRow2 != null) scopeRow1.Margin = new Padding(0, 0, 0, 4);

        const int runGap = 10;
        int runBtnWidth = (TestRowWidth - runGap) / 2;
        var runRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
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

        body.Controls.Add(modeTitle);
        body.Controls.Add(_modeCombo);
        body.Controls.Add(fields);
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

        // Dock order keeps the title at the top, buttons at the bottom, and body between them.
        card.Controls.Add(body);
        card.Controls.Add(runRow);
        card.Controls.Add(SectionTitle("测试设置"));
        return card;
    }

    /// <summary>Builds one row of equally sized scope buttons.</summary>
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

    /// <summary>Builds CCD scope buttons; returns null for a single-CCD CPU.</summary>
    private FlowLayoutPanel? BuildCcdScopeRow()
    {
        int ccds = CoreTopology.CcdCount;
        if (ccds <= 1) return null;

        var specs = new List<(string, string, string)>();
        for (int c = 0; c < ccds; c++)
        {
            // Use SMU slot IDs in tooltips so they match the CO editor; OS core IDs can differ.
            var cores = CoreTopology.PhysicalCoresOfCcd(c)
                .Select(RyzenSmu.OsCoreToSlot).OrderBy(x => x).ToList();
            specs.Add(($"CCD{c}", $"CCD{c}",
                $"只压 CCD{c}（物理核 {string.Join(", ", cores)}）\n另一个 CCD 空闲时功耗预算全给被测 CCD，\n频率比全核负载时更高，结论不能直接套用到全核"));
        }
        specs.Add(("逐 CCD", "EACH",
            "对每个 CCD 各跑一遍完整轮次（CCD0 → CCD1 …）"));
        return BuildScopeRow(specs);
    }

    /// <summary>Changes the test scope; cancelling custom selection preserves the current scope.</summary>
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
        if (slots <= 0) { ThemedDialog.Show(this, "未能读取核心拓扑，无法自定义测试范围。", "提示"); return false; }

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

        // Keep the checkboxes and dialog buttons in one layout so they cannot overlap.
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
            ThemedDialog.Show(this, "至少要选择一个核心。", "提示");
            return false;
        }
        _customCores = picked;
        return true;
    }

    /// <summary>Switches between automatic backoff and manual adjustment.</summary>
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

    /// <summary>Reports a manual-mode error without changing CO offsets.</summary>
    private void NotifyManualError(List<int> crashedPhysical)
    {
        string cores = crashedPhysical.Count > 0
            ? string.Join(", ", crashedPhysical)
            : "未指明（y-cruncher 崩溃）";
        Ui(() =>
        {
            SetStatus($"手动模式：物理核心 [{cores}] 报错，CO 未自动调整", Theme.Accent);
            ThemedDialog.Show(this,
                $"检测到报错，已停止测试。\n\n报错物理核心: {cores}\n\n" +
                "当前为「手动模式」，CO 未做任何改动。请在 AMD PBO 页手动调整 CO 后重新开始测试。",
                "手动模式 · 检测到报错");
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
            Normal = Theme.SurfaceAlt,
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

    /// <summary>Builds the about dialog opened from the title bar.</summary>
    private Form BuildInfoDialog()
    {
        var asm = typeof(MainForm).Assembly;
        string buildDate = asm
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "?";
        string copyright = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyCopyrightAttribute), false)
            .Cast<System.Reflection.AssemblyCopyrightAttribute>().FirstOrDefault()?.Copyright ?? "";

        var dialog = new Form
        {
            Text = "关于 AMD Ryzen PBO Studio",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 10F),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        dialog.HandleCreated += (_, _) => DarkTitleBar.Apply(dialog.Handle);

        const int contentWidth = 480;
        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(28, 20, 28, 16),
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, contentWidth));

        // Anchor None centers each row in the fixed-width column.
        void Line(string text, float size, Color color, bool bold = false, int top = 0) => content.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = color,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, top, 0, 2),
        });

        PillButton LinkButton(string text, string url)
        {
            var b = new PillButton
            {
                Text = text,
                Size = new Size(170, 36),
                Radius = 4,
                Font = new Font(Theme.FontFamily, 10F),
                BackColor = Theme.Surface,
                Normal = Theme.SurfaceAlt,
                Hover = Theme.Border,
                Outline = Theme.TextLo,
                ForeColor = Theme.TextHi,
                Margin = new Padding(4, 0, 4, 0),
            };
            b.Click += (_, _) => OpenUrl(url);
            return b;
        }

        Line("AMD Ryzen PBO Studio", 20F, Theme.Accent, bold: true);
        Line($"{HeaderBar.DisplayVersion} · 构建 {buildDate}", 9.5F, Theme.TextLo);

        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.None,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 14, 0, 0),
        };
        links.Controls.Add(LinkButton("项目主页", Updater.HomePage));
        content.Controls.Add(links);

        content.Controls.Add(new Panel
        {
            Size = new Size(contentWidth - 40, 1),
            Anchor = AnchorStyles.None,
            BackColor = Theme.Border,
            Margin = new Padding(0, 20, 0, 4),
        });

        Line("开发者", 16F, Theme.Accent, bold: true, top: 10);
        const string devName = "lengmo23 (", devHandle = "@冷漠OC";
        var dev = new LinkLabel
        {
            Text = devName + devHandle + ")",
            LinkArea = new LinkArea(devName.Length, devHandle.Length),
            AutoSize = true,
            Anchor = AnchorStyles.None,
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 10.5F),
            LinkColor = Theme.AccentHover,
            ActiveLinkColor = Theme.Accent,
            VisitedLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = new Padding(0, 0, 0, 2),
        };
        dev.LinkClicked += (_, _) => OpenUrl(DouyinUrl);
        content.Controls.Add(dev);
        Line("特别致谢", 16F, Theme.Accent, bold: true, top: 14);
        Line("@古腾古腾咕咚咚（思路指导与建议）", 10.5F, Theme.TextHi);
        Line("@阿肝（新功能建议与测试）", 10.5F, Theme.TextHi);
        Line("@Yee（核心测试）", 10.5F, Theme.TextHi);
        Line(copyright, 8.5F, Theme.TextLo, top: 26);

        dialog.Controls.Add(content);
        return dialog;
    }

    private const string DouyinUrl = "https://v.douyin.com/6VGnQBTxwIQ/";

    private const int QrHeight = 420;

    /// <summary>Sponsor dialog: a short thank-you note above the Alipay and WeChat payment codes.</summary>
    private static Form BuildSponsorDialog()
    {
        const int width = 640;   // fits both codes side by side at QrHeight
        var dlg = NewDialog("赞助作者");
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(24, 20, 24, 18),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));

        // Headings are centered; body paragraphs wrap across the full width so the text block fills the dialog.
        Label Para(string text, float size, Color color, bool bold = false, int top = 0, bool body = false) => new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Anchor = body ? AnchorStyles.Left : AnchorStyles.None,
            TextAlign = body ? ContentAlignment.TopLeft : ContentAlignment.MiddleCenter,
            ForeColor = color,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, top, 0, 0),
        };

        root.Controls.Add(Para("请作者喝杯咖啡", 18F, Theme.Accent, bold: true));
        root.Controls.Add(Para("Buy Me a Coffee", 9.5F, Theme.TextLo, top: 2));
        root.Controls.Add(Para(
            "AMD Ryzen PBO Studio 是我利用业余时间独立开发的开源工具。每一个功能都是经过日夜不断打磨开发迭代的结果。" +
            "如果它帮你省下了反复调参的时间，获得了更好的电脑性能，欢迎请我喝杯咖啡！\n" +
            "你的每一份支持，都是我继续维护和更新它的动力，也让更多玩家能用上更好用的调参工具。",
            10F, Theme.TextHi, top: 18, body: true));
        root.Controls.Add(Para("赞助完全自愿，不影响任何功能的使用。感谢每一位使用者！", 9F, Theme.TextLo, top: 14));

        var codes = Flow(FlowDirection.LeftToRight);
        codes.Anchor = AnchorStyles.None;
        codes.Margin = new Padding(0, 24, 0, 4);
        foreach (string name in new[] { "Alipay", "Wechat" })
        {
            if (LoadQr(name) is not { } img) continue;
            dlg.Disposed += (_, _) => img.Dispose();
            codes.Controls.Add(new PictureBox
            {
                Image = img,
                Size = img.Size,
                Margin = new Padding(10, 0, 10, 0),
            });
        }
        root.Controls.Add(codes);

        dlg.Controls.Add(root);
        // No close button: the title bar closes it, and Esc does too.
        dlg.KeyPreview = true;
        dlg.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) dlg.Close(); };
        return dlg;
    }

    /// <summary>Loads an embedded payment code scaled to QrHeight with smooth resampling, or null if missing.</summary>
    private static Bitmap? LoadQr(string name)
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream($"RyzenPboStudio.{name}.png");
            if (stream == null) return null;
            using var src = Image.FromStream(stream);
            var bmp = new Bitmap(src.Width * QrHeight / src.Height, QrHeight);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(Point.Empty, bmp.Size));
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // Online updates

    /// <summary>
    /// Checks GitHub Releases and applies an update after user confirmation. Startup checks stay silent
    /// on no update or network failure; manual checks report every outcome.
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
        _headerBar.UpdateTip = "正在检查更新...";
        try
        {
            UpdateInfo? info;
            IReadOnlyList<ReleaseNote>? currentNotes;
            try
            {
                (info, currentNotes) = await Updater.CheckAsync();
            }
            catch (Exception e)
            {
                if (manual)
                    ThemedDialog.Show(Form.ActiveForm ?? this, $"检查更新失败：{e.Message}", "检查更新");
                return;
            }
            if (IsDisposed) return;

            if (info == null)
            {
                _knownNewVersion = null;
                Updater.ClearSkip();
                RefreshUpdateTip();
                if (manual)
                {
                    using var latest = BuildUpToDateDialog(currentNotes);
                    latest.ShowDialog(Form.ActiveForm ?? this);
                }
                return;
            }

            Log.Write($"发现新版本 {info.Tag}（当前 v{Updater.CurrentVersion}）");
            _knownNewVersion = info.Version;
            RefreshUpdateTip();

            // Keep a skipped version visible on the update button without prompting at startup.
            if (!manual && Updater.IsSkipped(info.Version)) return;

            bool update;
            using (var prompt = BuildUpdatePrompt(info))
                update = prompt.ShowDialog(Form.ActiveForm ?? this) == DialogResult.Yes;
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
            RefreshUpdateTip();
        }
    }

    private const int MaxNotesHeight = 260; // Longer release notes scroll.
    private const int NotesDialogWidth = 480;

    private static TableLayoutPanel NewNotesRoot()
    {
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(22, 18, 22, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NotesDialogWidth));
        return root;
    }

    private static Label NotesPara(string text, float size, Color color, int maxWidth, bool bold = false, Padding? margin = null)
    {
        var m = margin ?? new Padding(0);
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(maxWidth - m.Left, 0),   // indented lines wrap inside the column
            ForeColor = color,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = m,
        };
    }

    /// <summary>Release notes, one bold line per change with a dim explanation, scrolling inside a fixed-height area.</summary>
    private static Control BuildNotesScroller(IReadOnlyList<ReleaseNote> releaseNotes)
    {
        // The notes' width leaves room for the scrollbar.
        int noteWidth = NotesDialogWidth - SystemInformation.VerticalScrollBarWidth - 4;
        var notes = Flow(FlowDirection.TopDown);
        foreach (var note in releaseNotes)
        {
            if (note.Title.Length > 0)
                notes.Controls.Add(NotesPara("●  " + note.Title, 10.5F, Theme.TextHi, noteWidth, bold: true, margin: new Padding(0, 0, 0, 2)));
            if (note.Detail.Length > 0)
                notes.Controls.Add(NotesPara(note.Detail, 9.5F, Theme.TextLo, noteWidth,
                    margin: new Padding(note.Title.Length > 0 ? 18 : 0, 0, 0, 10)));
        }
        if (releaseNotes.Count == 0)
            notes.Controls.Add(NotesPara("本次更新未附说明。", 9.5F, Theme.TextLo, noteWidth, margin: new Padding(0, 0, 0, 10)));
        var scroller = new Panel
        {
            AutoScroll = true,
            Size = new Size(NotesDialogWidth, Math.Min(notes.GetPreferredSize(Size.Empty).Height, MaxNotesHeight)),
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        scroller.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(scroller.Handle);
        scroller.Controls.Add(notes);
        return scroller;
    }

    /// <summary>Manual check with no newer release: the current version, then its notes when it is the latest release.</summary>
    private static Form BuildUpToDateDialog(IReadOnlyList<ReleaseNote>? currentNotes)
    {
        const int width = NotesDialogWidth;
        var dlg = NewDialog("检查更新");
        var root = NewNotesRoot();
        root.Controls.Add(NotesPara("当前已是最新版本", 16F, Theme.Accent, width, bold: true));
        root.Controls.Add(NotesPara($"v{Updater.CurrentVersion}" + (currentNotes != null ? " · 本版本更新内容" : ""),
            9F, Theme.TextLo, width, margin: new Padding(0, 4, 0, 0)));
        if (currentNotes != null)
        {
            root.Controls.Add(new Panel { Size = new Size(width, 1), BackColor = Theme.Border, Margin = new Padding(0, 14, 0, 12) });
            root.Controls.Add(BuildNotesScroller(currentNotes));
        }

        var ok = Pill("确定", 104, primary: true);
        ok.DialogResult = DialogResult.OK;
        var buttons = Flow(FlowDirection.LeftToRight);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 16, 0, 0);
        buttons.Controls.Add(ok);
        root.Controls.Add(buttons);

        dlg.Controls.Add(root);
        dlg.AcceptButton = ok;
        dlg.CancelButton = ok;
        return dlg;
    }

    /// <summary>Update prompt: version headline and download size, the release notes, then a restart note.
    /// Returns DialogResult.Yes to update.</summary>
    private static Form BuildUpdatePrompt(UpdateInfo info)
    {
        const int width = NotesDialogWidth;
        var dlg = NewDialog("发现新版本");
        var root = NewNotesRoot();

        root.Controls.Add(NotesPara($"{info.Tag} 已发布", 16F, Theme.Accent, width, bold: true));
        root.Controls.Add(NotesPara($"当前版本 v{Updater.CurrentVersion} · 更新包约 {info.Size / 1024.0 / 1024.0:F1} MB",
            9F, Theme.TextLo, width, margin: new Padding(0, 4, 0, 0)));
        root.Controls.Add(new Panel { Size = new Size(width, 1), BackColor = Theme.Border, Margin = new Padding(0, 14, 0, 12) });
        root.Controls.Add(BuildNotesScroller(info.Notes));

        root.Controls.Add(NotesPara("更新时会关闭程序，覆盖安装后自动重启。", 8.5F, Theme.TextLo, width, margin: new Padding(0, 6, 0, 0)));

        var ok = Pill("现在更新", 104, primary: true);
        var later = Pill("暂不更新", 104);
        ok.DialogResult = DialogResult.Yes;
        later.DialogResult = DialogResult.No;
        later.Margin = new Padding(8, 0, 0, 0);
        var buttons = Flow(FlowDirection.LeftToRight);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 16, 0, 0);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(later);
        root.Controls.Add(buttons);

        dlg.Controls.Add(root);
        dlg.AcceptButton = ok;
        dlg.CancelButton = later;
        return dlg;
    }

    /// <summary>Shows the available version on the update button, or the check action otherwise.</summary>
    private void RefreshUpdateTip()
    {
        if (IsDisposed) return;
        _headerBar.UpdateTip = _knownNewVersion != null ? $"有新版本 v{_knownNewVersion}" : "检查更新";
        _headerBar.UpdateAvailable = _knownNewVersion != null;
    }

    /// <summary>Downloads and extracts an update before exiting for the replacement script.</summary>
    private async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        string zipPath;
        try
        {
            int lastPercent = -1;
            string source = "";
            var progress = new Progress<int>(p =>
            {
                if (IsDisposed || p == lastPercent) return;
                lastPercent = p;
                _headerBar.UpdateTip = $"下载中 {p}%";
                SetStatus($"正在下载更新 {info.Tag}（{source}） {p}%", Theme.Warn);
            });
            SetStatus($"正在下载更新 {info.Tag}...", Theme.Warn);
            zipPath = await Updater.DownloadAsync(info, progress,
                s => { source = s; lastPercent = -1; },
                // Show connection and source-switch stages when no byte progress is available.
                stage => Ui(() =>
                {
                    if (IsDisposed) return;
                    _headerBar.UpdateTip = "下载中...";
                    SetStatus($"更新 {info.Tag} · {stage}", Theme.Warn);
                }));
        }
        catch (Exception e)
        {
            RefreshUpdateTip();
            SetStatus("更新下载失败", Theme.Accent);
            Log.Write($"更新下载失败：{e.Message}", "WARN");
            using (var dlg = BuildDownloadFailedDialog(info.Tag))
                dlg.ShowDialog(Form.ActiveForm ?? this);
            return;
        }

        string newRoot;
        try
        {
            _headerBar.UpdateTip = "解压中...";
            SetStatus("正在解压更新...", Theme.Warn);
            newRoot = await Task.Run(() => Updater.ExtractAndVerify(zipPath));
        }
        catch (Exception e)
        {
            RefreshUpdateTip();
            SetStatus("更新包校验失败", Theme.Accent);
            Log.Write($"更新包解压/校验失败：{e.Message}", "WARN");
            ThemedDialog.Show(Form.ActiveForm ?? this, $"更新包无法使用：{e.Message}\n\n当前版本未受影响。", "更新");
            return;
        }

        // Replacement must wait until this process exits.
        try
        {
            Updater.LaunchReplacerAndExit(newRoot);
        }
        catch (Exception e)
        {
            RefreshUpdateTip();
            ThemedDialog.Show(Form.ActiveForm ?? this, $"无法启动更新程序：{e.Message}\n\n当前版本未受影响。", "更新");
            return;
        }

        Log.Write("更新就绪，正在退出以完成替换");
        Close();
    }

    /// <summary>Shown when GitHub and every mirror fail: points users to the QQ group for the latest version.</summary>
    private static Form BuildDownloadFailedDialog(string tag)
    {
        const int width = 400;
        var dlg = NewDialog("下载更新失败");
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(22, 18, 22, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));

        Label Line(string text, float size, Color color, bool bold = false, int bottom = 0, bool center = false) => new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Anchor = center ? AnchorStyles.None : AnchorStyles.Left,
            ForeColor = color,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, 0, 0, bottom),
        };

        root.Controls.Add(Line($"无法下载 {tag}", 12F, Theme.TextHi, bold: true, bottom: 6));
        root.Controls.Add(Line("GitHub 与各镜像源均无法连接，当前版本未受影响。\n请加入 QQ 群获取最新版本：", 9.5F, Theme.TextLo, bottom: 14));
        root.Controls.Add(Line("QQ 群", 9F, Theme.TextLo, bottom: 2, center: true));
        root.Controls.Add(Line(Updater.QqGroup, 22F, Theme.Accent, bold: true, bottom: 6, center: true));

        var copyBtn = Pill("复制群号", 104, primary: true);
        var closeBtn = Pill("关闭", 84);
        closeBtn.DialogResult = DialogResult.Cancel;
        closeBtn.Margin = new Padding(8, 0, 0, 0);
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(Updater.QqGroup);
                copyBtn.Text = "已复制";
            }
            catch (Exception ex)
            {
                Log.Write($"复制 QQ 群号失败：{ex.Message}", "WARN");
                copyBtn.Text = "复制失败";
            }
        };
        var buttons = Flow(FlowDirection.LeftToRight);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 14, 0, 0);
        buttons.Controls.Add(copyBtn);
        buttons.Controls.Add(closeBtn);
        root.Controls.Add(buttons);

        dlg.Controls.Add(root);
        dlg.CancelButton = closeBtn;
        return dlg;
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
            ThemedDialog.Show(Form.ActiveForm ?? this, $"无法打开链接：{e.Message}\n\n{url}", "提示");
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
        box.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(box.Handle);

        // Clearing the display does not remove the session log on disk.
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

    // Curve Optimizer editor

    /// <summary>Builds the per-core CO editor and its action buttons.</summary>
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
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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

        grid.Controls.Add(MakeStepButton("+", () => StepRange(0, CoSlotsPerColumn, +1)), 0, 0);
        var ccd1Plus = MakeStepButton("+", () => StepRange(CoSlotsPerColumn, CoDisplaySlotCount, +1));
        ccd1Plus.Enabled = _coSlotCount > CoSlotsPerColumn;
        grid.Controls.Add(ccd1Plus, 1, 0);

        for (int r = 0; r < CoSlotsPerColumn; r++)
        {
            grid.Controls.Add(MakeCoreCell(r), 0, r + 1);
            grid.Controls.Add(MakeCoreCell(CoSlotsPerColumn + r), 1, r + 1);
        }

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
            _coCells[core].Enabled = false; // Fused-off or absent slots are fixed at zero.
        }
        f.Controls.Add(_coCells[core]);
        return f;
    }

    private static NumericUpDown NewCoCell()
    {
        var cell = new NumericUpDown
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
        DarkSpin.Apply(cell);
        return cell;
    }

    private PillButton MakeStepButton(string text, Action onClick)
    {
        var b = Ghost(text);
        b.Size = new Size(58, 24);
        b.Font = new Font(Theme.FontFamily, 11F, FontStyle.Bold);
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
        return flow;
    }

    /// <summary>Builds the PBO limit controls and their shared Apply action.</summary>
    private Control BuildParamBar()
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(14, 6, 14, 6);

        ConfigNumBox(_fmaxBox, 0, 7000, 25, 74);
        ConfigNumBox(_pptBox, 30, 1000, 5, 62);
        ConfigNumBox(_edcBox, 10, 1000, 5, 62);
        ConfigNumBox(_tdcBox, 10, 1000, 5, 62);
        _pptBox.Value = 142; // Replaced with live CPU limits during initialization.
        _edcBox.Value = 180;
        _tdcBox.Value = 160;

        // Scalar 1X maps to Auto.
        _scalarBox.Width = 70;
        _scalarBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _scalarBox.MaxDropDownItems = 10;
        _scalarBox.FlatStyle = FlatStyle.Flat;
        _scalarBox.BackColor = Theme.SurfaceAlt;
        _scalarBox.ForeColor = Theme.TextHi;
        _scalarBox.Font = new Font(Theme.MonoFamily, 10F);
        _scalarBox.Margin = new Padding(0, 5, 8, 0);
        DarkCombo.Apply(_scalarBox, center: true);
        for (int x = 1; x <= 10; x++) _scalarBox.Items.Add($"{x}X");
        SelectScalar(1);
        _scalarBox.SelectedIndexChanged += (_, _) => { if (!_scalarLoading) _scalarDirty = true; };
        _tips.SetToolTip(_scalarBox,
            "PBO Scalar：1X 对应 Auto。倍数越大，SMU 在 boost 时给的电压越高。\n" +
            "只在选项改动后点 Apply 才写入；5000 系写入时会同时打开 PBO。");

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
        };
        AddParamPair(flow, "FMax", _fmaxBox);
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
        DarkSpin.Apply(box);
    }

    /// <summary>Applies the shared style to a PBO numeric input.</summary>
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

    /// <summary>Selects and clamps the Scalar multiplier to 1–10.</summary>
    private void SelectScalar(int scalar)
    {
        _scalarLoading = true;
        _scalarBox.SelectedIndex = Math.Clamp(scalar, 1, 10) - 1;
        _scalarLoading = false;
    }

    private uint SelectedScalar() => (uint)(_scalarBox.SelectedIndex + 1);

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

    // Curve Shaper editor

    /// <summary>Builds the 5-by-3 CS grid and its action buttons.</summary>
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
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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

        grid.Controls.Add(new Label { Text = "", AutoSize = false, Width = CsRowLabelWidth, Height = 18, BackColor = Theme.Surface }, 0, 0);
        string[] cols = { "LOW", "MED", "HIGH" };
        for (int c = 0; c < 3; c++) grid.Controls.Add(CsHeader(cols[c], last: c == 2), c + 1, 0);

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

    /// <summary>Spaces CS temperature columns equally for headers and inputs.</summary>
    private const int CsColumnGap = 10;

    /// <summary>Fits the CS grid and action buttons within the card.</summary>
    private const int CsCellWidth = 46;

    /// <summary>Omits trailing spacing after the last CS column.</summary>
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

    /// <summary>Sets the width shared by CS row labels and the header spacer.</summary>
    private const int CsRowLabelWidth = 40;

    private static Label CsRowLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = CsRowLabelWidth,
        Height = 26,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        TextAlign = ContentAlignment.MiddleRight,
        Margin = new Padding(0, 5, 6, 5),
    };

    private static NumericUpDown NewCsCell(bool last)
    {
        var cell = new NumericUpDown
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
        DarkSpin.Apply(cell);
        return cell;
    }

    // CO editor behavior

    private void StepRange(int from, int to, int delta)
    {
        for (int i = from; i < to && i < _coCells.Count; i++)
            SetCell(i, (int)_coCells[i].Value + delta);
    }

    /// <summary>Clamps and sets one core’s CO input.</summary>
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
                // Resume the interrupted stage using the user’s offsets without another backoff.
                Log.Write("检测到手动应用 CO，测试沿用手动设定续跑", "WARN");
                SetStatus("已接管中断 CO · 开始后按此值续跑", Theme.Warn);
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
        }.Save();
        Log.Write("已保存 CO 配置到 co_profile.json");
        SetStatus("已保存 CO 配置", Theme.Success);
    }

    private void OnCoLoad()
    {
        var p = CoProfile.Load();
        for (int i = 0; i < _coSlotCount && i < p.Offsets.Count; i++) SetCell(i, p.Offsets[i]);
        Log.Write("已载入 CO 配置（未应用，确认后点 Apply）");
    }

    /// <summary>Applies FMax, PPT, EDC, and TDC limits together.</summary>
    private void OnPboApply()
    {
        uint fmax = (uint)_fmaxBox.Value;
        uint ppt = (uint)_pptBox.Value;
        uint edc = (uint)_edcBox.Value;
        uint tdc = (uint)_tdcBox.Value;
        // Zen3 has no FMax write message; sending message ID 0 would be unsafe.
        bool okF = !_fmaxWritable || RyzenSmu.SetFMax(fmax);
        bool okP = RyzenSmu.SetPptLimit(ppt);
        bool okE = RyzenSmu.SetEdcLimit(edc);
        bool okT = RyzenSmu.SetTdcLimit(tdc);
        uint scalar = SelectedScalar();   // Auto = 1X
        bool writeScalar = _scalarWritable && _scalarDirty;
        bool okS = !writeScalar || RyzenSmu.SetPboScalar(scalar);
        if (writeScalar && okS) _scalarDirty = false;
        bool ok = okF && okP && okE && okT && okS;
        // Record what was written so crash recovery restores the latest limits, even when changed mid-test.
        var rec = PboJournal.ReadLast() ?? new PboSnapshot();
        if (_fmaxWritable && okF) rec.FMax = fmax;
        if (okP) rec.Ppt = (int)ppt;
        if (okE) rec.Edc = (int)edc;
        if (okT) rec.Tdc = (int)tdc;
        PboJournal.Record(rec, PboJournal.ManualApply);
        string fmaxPart = _fmaxWritable ? $"FMax {fmax} MHz / " : "FMax 跳过（不支持）/ ";
        string scalarPart = !_scalarWritable ? " / Scalar 跳过（不支持）"
                          : writeScalar ? $" / Scalar {_scalarBox.Text}" : " / Scalar 未改动";
        Log.Write($"应用 PBO 限制: {fmaxPart}PPT {ppt} W / EDC {edc} A / TDC {tdc} A{scalarPart}" + (ok ? "" : "（部分写入失败，详见上方）"));
        SetStatus(ok ? "已应用 PBO 限制（FMax/PPT/EDC/TDC/Scalar）" : "PBO 限制应用部分失败", ok ? Theme.Success : Theme.Accent);
    }

    // Curve Shaper behavior

    private void OnCsApply()
    {
        var g = CsGridFromCells();
        bool ok = RyzenSmu.SetCurveShaper(g);
        if (ok)
        {
            // Cache applied CS values because older BIOS versions cannot read them back.
            _csState.FromGrid(g);
            _csState.Save();
            // Record what was written so crash recovery restores the latest CS, even when changed mid-test.
            CsJournal.Record(new CsSnapshot { Margins = new List<int>(_csState.Margins) }, CsJournal.ManualApply);
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
            // On older BIOS versions, all-zero readback does not mean the applied CS values were cleared.
            if (!_csState.ReadBroken) { _csState.ReadBroken = true; _csState.Save(); }
            _csTitleLabel.Text = "Curve Shaper（备用模式）";
            if (!_csState.HasMargins)
                return; // Keep editor values when no saved CS values exist.
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

    /// <summary>Loads saved CS values into the editor without writing hardware.</summary>
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

    /// <summary>Saves editor CS values without writing hardware.</summary>
    private void OnCsSave()
    {
        _csState.FromGrid(CsGridFromCells());
        _csState.Save();
        Log.Write("已保存 CS 配置到 cs_state.json");
        SetStatus("已保存 CS 配置", Theme.Success);
    }

    /// <summary>Enables CS only on supported CPUs and populates the editor.</summary>
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

    /// <summary>Populates the editor from current CPU values at startup.</summary>
    private void InitCoEditor()
    {
        OnCoRefresh();
        InitCsEditor();
        InitPboBoxes();
    }

    /// <summary>Initializes PPT, EDC, and TDC controls from current CPU limits.</summary>
    private void InitPboBoxes()
    {
        var (ppt, edc, tdc) = RyzenSmu.ReadPboLimits();
        if (ppt > 0) _pptBox.Value = Math.Clamp(ppt, (int)_pptBox.Minimum, (int)_pptBox.Maximum);
        if (edc > 0) _edcBox.Value = Math.Clamp(edc, (int)_edcBox.Minimum, (int)_edcBox.Maximum);
        if (tdc > 0) _tdcBox.Value = Math.Clamp(tdc, (int)_tdcBox.Minimum, (int)_tdcBox.Maximum);

        // Zen3 cannot write FMax; leave the other PBO limits available.
        _fmaxWritable = RyzenSmu.IsFMaxWriteSupported();
        if (!_fmaxWritable)
        {
            _fmaxBox.Enabled = false;
            Log.Write("当前 CPU 的 SMU 无 FMax 写入命令，已禁用该项；PPT / TDC / EDC 不受影响");
        }

        // Scalar zero means OC mode or an unavailable reading, so show Auto.
        float s = RyzenSmu.ReadPboScalar();
        SelectScalar(s >= 1 ? (int)Math.Round(s) : 1);
        _scalarWritable = RyzenSmu.IsPboScalarWriteSupported();
        if (!_scalarWritable)
        {
            _scalarBox.Enabled = false;
            Log.Write("当前 CPU 的 SMU 无 PBO Scalar 写入命令，已禁用该项");
        }
    }

    // Control factories

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

    /// <summary>Selects a mode in the dropdown, falling back to the first item.</summary>
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
            string ico = Path.Combine(Workspace.BaseDir, "ICON.ico");
            if (File.Exists(ico)) { Icon = new Icon(ico); return; }

            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("RyzenPboStudio.ICON.ico");
            if (stream != null) Icon = new Icon(stream);
        }
        catch (Exception e)
        {
            Log.Write($"加载图标失败: {e.Message}", "WARN");
        }
    }

    // Borderless window

    /// <summary>Restores minimize, system-menu, and drop-shadow behavior on the borderless window.
    /// </summary>
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

    /// <summary>Updates the rounded window region after resizing.</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width <= 0 || Height <= 0) return;
        using var path = Theme.RoundRect(new Rectangle(0, 0, Width, Height), 8);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    /// <summary>Draws a one-pixel rounded window border.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 8);
        e.Graphics.DrawPath(pen, path);
    }

    // Timers

    private void OnLogTimer(object? sender, EventArgs e)
    {
        bool any = false;
        while (_logQueue.TryDequeue(out var line))
        {
            // Mirror log lines into both page-specific textboxes.
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
                Ui(() => _offsetsLabel.Text = $"当前 CO: [{string.Join(", ", off)}]");
            }
            catch
            {
            }
        });
    }

    // Test control

    private void StartTest(object? sender, EventArgs e)
    {
        _startBtn.Enabled = false;

        if (!TryReadSettings(out int duration, out int singleRounds))
            return;

        // Check for y-cruncher before starting a stress test and show placement instructions if absent.
        try
        {
            YCruncher.FindExe();
        }
        catch (FileNotFoundException ex)
        {
            ThemedDialog.Show(this, ex.Message, "缺少 y-cruncher");
            _startBtn.Enabled = true;
            return;
        }

        // Only a Start click asks; resuming an interrupted test was already acknowledged when it first started.
        if (sender == _startBtn && !ConfirmTestRisk())
        {
            _startBtn.Enabled = true;
            return;
        }

        _testMode = SelectedMode();
        _durationSeconds = duration;
        _singleRounds = singleRounds; // SEQ and COMBO use their own plan.
        _runPlan = _multi.Clone();
        _stopRequested = false;
        _cts = new CancellationTokenSource();

        SetControlsEnabled(false);
        _stopBtn.Enabled = true;
        string perRound = _testMode is "SEQ" or "COMBO" ? "" : $" · 每轮 {duration} 秒";
        SetStatus($"运行中 · 模式 {_testMode} · 范围 {ScopeLabel(_testScope, _scopeCcd)}{perRound}", Theme.Warn);

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

    /// <summary>Reads the FMax and PPT/EDC/TDC limits the CPU is running; values that cannot be read stay null.</summary>
    private PboSnapshot ReadPboSnapshot()
    {
        var snap = new PboSnapshot();
        if (_fmaxWritable && RyzenSmu.ReadFMax() is var fmax and > 0) snap.FMax = fmax;
        if (RyzenSmu.ReadLivePboLimits() is { } lim)
        {
            snap.Ppt = lim.ppt;
            snap.Edc = lim.edc;
            snap.Tdc = lim.tdc;
        }
        return snap;
    }

    private static string DescribePbo(PboSnapshot s)
    {
        var parts = new List<string>();
        if (s.FMax is { } f) parts.Add($"FMax {f} MHz");
        if (s.Ppt is { } p) parts.Add($"PPT {p} W");
        if (s.Edc is { } e) parts.Add($"EDC {e} A");
        if (s.Tdc is { } t) parts.Add($"TDC {t} A");
        return parts.Count > 0 ? string.Join(" / ", parts) : "未读到";
    }

    /// <summary>
    /// Writes back only the recorded PBO limits that differ from what the CPU runs now (after a crash, or at boot the
    /// BIOS values). Writing FMax disturbs some power-plan states and board-level core frequency limits (e.g. ASUS), so
    /// matching values are left alone, and a value that cannot be read now is not written blindly. The unlocked-PBO
    /// value (999, or 1000 on ASUS) is compared like any other. Written values are shown in the PBO editor. Returns false when a
    /// write fails. context prefixes the log lines.
    /// </summary>
    private bool RestorePbo(PboSnapshot saved, string context)
    {
        if (saved.FMax == null && saved.Ppt == null && saved.Edc == null && saved.Tdc == null)
        {
            Log.Write($"{context}：没有可核对的 PBO 记录，未写入 PBO 参数");
            return true;
        }
        var now = ReadPboSnapshot();
        var changes = new List<string>();
        var written = new List<(NumericUpDown box, decimal value)>();
        bool compared = false, ok = true;

        if (saved.FMax is uint f && now.FMax is uint nf)
        {
            compared = true;
            if (nf != f)
            {
                bool done = RyzenSmu.SetFMax(f);
                ok &= done;
                if (done) written.Add((_fmaxBox, f));
                changes.Add($"FMax {nf} → {f} MHz");
            }
        }
        // PM Table limits are rounded floats; a 1 W / 1 A difference is reading noise.
        void Limit(string name, string unit, int? want, int? have, Func<uint, bool> set, NumericUpDown box)
        {
            if (want is not int w || have is not int h) return;
            compared = true;
            if (Math.Abs(h - w) <= 1) return;
            bool done = set((uint)w);
            ok &= done;
            if (done) written.Add((box, w));
            changes.Add($"{name} {h} → {w} {unit}");
        }
        Limit("PPT", "W", saved.Ppt, now.Ppt, RyzenSmu.SetPptLimit, _pptBox);
        Limit("EDC", "A", saved.Edc, now.Edc, RyzenSmu.SetEdcLimit, _edcBox);
        Limit("TDC", "A", saved.Tdc, now.Tdc, RyzenSmu.SetTdcLimit, _tdcBox);

        if (!compared)
            Log.Write($"{context}：未能读取当前 PBO 参数，未写入 PBO 参数", "WARN");
        else if (changes.Count == 0)
            Log.Write($"{context}：PBO 参数与记录一致（{DescribePbo(now)}），未写入");
        else
            Log.Write($"{context}：PBO 参数已写入 {string.Join("、", changes)}" + (ok ? "" : "（部分写入失败）"), ok ? "INFO" : "WARN");

        if (written.Count > 0)
            Ui(() =>
            {
                foreach (var (box, value) in written)
                    box.Value = Math.Clamp(value, box.Minimum, box.Maximum);
            });
        return ok;
    }

    /// <summary>Reads the CS margins the CPU runs; null margins when old BIOS cannot read them back.</summary>
    private static CsSnapshot ReadCsSnapshot()
    {
        var g = RyzenSmu.ReadCurveShaper(out bool readable);
        if (!readable) return new CsSnapshot();
        var s = new CsState();
        s.FromGrid(g);
        return new CsSnapshot { Margins = s.Margins };
    }

    /// <summary>
    /// After a crash, writes back the last recorded CS margins unless the CPU already runs them. Old BIOS cannot read
    /// CS back, so the record is written whenever a match cannot be confirmed; a record of unknown margins is skipped.
    /// </summary>
    private void RestoreCsAfterCrash()
    {
        if (!RyzenSmu.IsCurveShaperSupported() || CsJournal.ReadLast() is not { } saved) return;
        if (saved.Margins is not { Count: >= 15 } want)
        {
            Log.Write("死机恢复：最后一次 CS 记录无法读回，未写入 CS");
            return;
        }
        if (ReadCsSnapshot().Margins is { } now && now.SequenceEqual(want))
        {
            Log.Write("死机恢复：CS 与记录一致，未写入");
            return;
        }
        var g = new CsState { Margins = want }.ToGrid();
        bool ok = RyzenSmu.SetCurveShaper(g);
        Log.Write(ok ? "死机恢复：CS 已写入最后一次记录" : "死机恢复：CS 写入失败，详见上方", ok ? "INFO" : "WARN");
        if (ok)
            Ui(() =>
            {
                for (int t = 0; t < 5; t++)
                    for (int c = 0; c < 3; c++)
                    {
                        var cell = _csCells[t, c];
                        cell.Value = Math.Clamp(g[t, c], (int)cell.Minimum, (int)cell.Maximum);
                    }
            });
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

    /// <summary>Returns true only after all tests finish; process exit or CO write failure returns false.</summary>
    private bool RunTest()
    {
        // Check the previous crash marker before marking this run active.
        bool interrupted = Workspace.WasInterrupted();
        var prev = Workspace.LoadState();
        var lastEntry = Journal.ReadLastEntry();
        var lastApplied = lastEntry?.Offsets;
        // Resume from manually adopted offsets without an extra backoff.
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

            // Prefer the last persisted CO record over the state file after a crash.
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
                _runPlan = prev.MultiTest is { } plan && plan.IsValid(MultiTestNames) ? plan : LegacyPlan(prev);
            }
            else
            {
                _testMode = SelectedMode(); // Use current UI mode when persisted state is missing.
            }

            var preRecovery = new List<int>(offsets);
            if (manualTakeover)
            {
                Log.WriteCo("沿用手动应用的 CO：", offsets, level: "WARN");
            }
            else
            {
                var enabled = Enumerable.Range(0, offsets.Count).Where(i => !RyzenSmu.IsSlotDisabled(i)).ToList();
                if (enabled.All(i => offsets[i] >= Config.CoMax))
                {
                    Log.Write($"死机恢复：所有核心 CO 已达上限 +{Config.CoMax}（无可回退空间），大概率非 CO 不稳定导致，停止测试", "ERROR");
                    return false;
                }
                foreach (int i in enabled) offsets[i] = Math.Min(Config.CoMax, offsets[i] + Config.StepOnError);
                Log.WriteCo($"死机恢复，所有核心 CO +{Config.StepOnError}：", offsets, preRecovery, "WARN");
            }

            if (!Tuning.Apply(offsets, _testMode, seqResumePhase, "crash-recovery"))
            {
                Log.Write("死机恢复设置 CO 失败", "ERROR");
                return false;
            }
            RestoreCsAfterCrash();
            if (PboJournal.ReadLast() is { } savedPbo) RestorePbo(savedPbo, "死机恢复");

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
                _initialOffsetsLabel.Text = $"初始 CO: [{string.Join(", ", preRecovery)}]";
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
            var pbo = ReadPboSnapshot();
            PboJournal.Record(pbo, PboJournal.TestStart);
            if (RyzenSmu.IsCurveShaperSupported()) CsJournal.Record(ReadCsSnapshot(), CsJournal.TestStart);
            Log.Write($"当前 PBO 参数：{DescribePbo(pbo)}");

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
            _initialOffsetsLabel.Text = $"初始 CO: [{string.Join(", ", initSnapshot)}]";
            _offsetsLabel.Text = $"当前 CO: [{string.Join(", ", initSnapshot)}]";
        });

        var token = _cts!.Token;

        // EACH mode repeats the full test suite on each CCD.
        // Resume at the CCD saved before interruption.
        bool eachCcd = _testScope == "EACH" && CoreTopology.CcdCount > 1;
        int scopeStart = eachCcd ? Math.Clamp(_scopeCcd, 0, CoreTopology.CcdCount - 1) : 0;
        int scopeEnd = eachCcd ? CoreTopology.CcdCount - 1 : 0;

        bool allPassed = true;
        for (int pass = scopeStart; pass <= scopeEnd; pass++)
        {
            if (_stopRequested) break;
            if (eachCcd) _scopeCcd = pass;
            int ccd = eachCcd ? pass : _scopeCcd;
            var (scopeLogical, scopeCores) = ResolveScope(eachCcd ? "CCD" : _testScope, ccd);
            string scopeLabel = ScopeLabel(eachCcd ? "CCD" : _testScope, ccd);

            bool passOk = RunOnePass(scopeLogical, scopeCores, scopeLabel);
            seqResumePhase = null;
            if (!passOk) // Do not advance CCD after an incomplete pass.
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
                // Step names are unique within a plan, so the name identifies the step to resume.
                var phases = _runPlan.Sequence;

                int startIdx = 0;
                if (!string.IsNullOrEmpty(seqResumePhase))
                {
                    for (int i = 0; i < phases.Count; i++)
                        if (phases[i].Test == seqResumePhase) { startIdx = i; break; }
                }

                for (int pi = startIdx; pi < phases.Count; pi++)
                {
                    if (_stopRequested) break;
                    string name = phases[pi].Test;
                    int iters = phases[pi].Rounds;

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
                        MultiTest = _runPlan,
                        Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                    });

                    bool ok;
                    (ok, offsets) = YCruncher.RunStressTest(new[] { AlgoOf(name) }, iters, offsets, phases[pi].Seconds, _testMode, token, _autoAdjust, NotifyManualError, scopeLogical, scopeCores, scopeLabel);
                    if (!ok)
                    {
                        Log.Write($"{name} 测试未完成", "WARN");
                        passOk = false;
                        break;
                    }
                    testRound = 0;

                    if (pi < phases.Count - 1)
                    {
                        Log.Write($">>> {name} 测试完成，清理进程...");
                        YCruncher.Kill();
                    }
                }
            }
            else if (_testMode == "COMBO")
            {
                Log.Write($"\n>>> 开始组合测试 {string.Join("+", _runPlan.ComboTests)} ({_runPlan.ComboRounds} 轮)");
                Workspace.SaveState(new TestState
                {
                    Offsets = offsets,
                    TestRound = testRound,
                    TestMode = _testMode,
                    TestScope = _testScope,
                    ScopeCcd = _scopeCcd,
                    ScopeCores = _testScope == "CUSTOM" ? new List<int>(_customCores) : null,
                    DurationSeconds = _durationSeconds,
                    MultiTest = _runPlan,
                    Timestamp = DateTimeOffset.Now.ToUnixTimeSeconds(),
                });

                bool ok;
                var comboAlgos = _runPlan.ComboTests.Select(AlgoOf).ToArray();
                (ok, offsets) = YCruncher.RunStressTest(comboAlgos, _runPlan.ComboRounds, offsets, _runPlan.ComboSeconds, _testMode, token, _autoAdjust, NotifyManualError, scopeLogical, scopeCores, scopeLabel);
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
            Log.WriteCo("测试未完成，未生成最终结果。当前 CO：", offsets, level: "WARN");
            Workspace.ClearState();
            Workspace.ClearInProgress(); // Clear the marker to avoid a false backoff next run.
            var partialSnapshot = new List<int>(offsets);
            Ui(() => _offsetsLabel.Text = $"当前 CO: [{string.Join(", ", partialSnapshot)}]");
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
            sb.AppendLine(_testMode switch
            {
                "SEQ" => "测试项目: 顺序 " + string.Join(" → ", _runPlan.Sequence.Select(st => $"{st.Test} {st.Rounds} 轮 × {st.Seconds} 秒")),
                "COMBO" => $"测试项目: 组合 {string.Join("+", _runPlan.ComboTests)}，{_runPlan.ComboRounds} 轮，每项每轮 {_runPlan.ComboSeconds} 秒",
                _ => $"每轮时间: {_durationSeconds} 秒{Environment.NewLine}测试轮数: {_singleRounds}",
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
        Workspace.ClearInProgress(); // Normal completion or manual stop clears the marker.

        var finalSnapshot = new List<int>(offsets);
        Ui(() => _offsetsLabel.Text = $"最终 CO: [{string.Join(", ", finalSnapshot)}]");
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
            ThemedDialog.Show(this, "测试未跑完，未生成最终结果，原因见运行日志。", "测试未完成");
        else
            ThemedDialog.Show(this, "测试已停止，进程已清理完成。", "已停止");
    }

    // Single Core Boost and voltage alignment

    private PillButton _scbBtn = null!;
    private PillButton _alignBtn = null!;
    private CancellationTokenSource? _measureCts;
    private Task? _measureTask;
    private readonly ResultsPanel _resultsPanel = new() { Dock = DockStyle.Fill };

    private FlowLayoutPanel BuildMeasureRow(int btnWidth, int gap)
    {
        var row = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Dock = DockStyle.Bottom, Padding = new Padding(0, 6, 0, 0) };
        _scbBtn = Ghost("Single Core Boost");
        _alignBtn = Ghost("电压对齐");
        _scbBtn.Size = _alignBtn.Size = new Size(btnWidth, 32);
        _scbBtn.Margin = new Padding(0, 0, gap, 0);
        _alignBtn.Margin = new Padding(0);
        _tips.SetToolTip(_scbBtn, "逐核运行单核负载，测量当前 CO 下各核能跑到的频率 / 有效频率 / 电压");
        _tips.SetToolTip(_alignBtn, "全部核心 CO 统一设为起点（默认 0，检测到外频时可自定义）后逐核测量频率与电压，各 CCD 以 CPPC 最高核心中同频电压最低者为基准核，逐步降低 CO 对齐电压\n再以 CCD 为单位整体降低 CO 探明单核频率平台，复测全部核心后微调频率偏低的核心；出现计算错误的核心会回退并停止下调");
        _scbBtn.Click += (_, _) => StartMeasure("Single Core Boost", (win, ct) => SingleCoreBoost.Run(_monitorView, win, ct));
        _alignBtn.Click += (_, _) => OnAlignClick();
        row.Controls.Add(_scbBtn);
        row.Controls.Add(_alignBtn);
        return row;
    }

    private void OnAlignClick()
    {
        if (!VoltageAlign.Confirm(this, _monitorView, out int startCo)) return;
        int editorMin = (int)_coCells[0].Minimum;
        StartMeasure("电压对齐", (win, ct) =>
        {
            var final = VoltageAlign.Run(_monitorView, editorMin, startCo, win, ct);
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
            ThemedDialog.Show(this, "CPU 不支持 AVX2 / FMA，无法运行单核负载。", title);
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


    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private (string algo, int iters) GetTestConfig(string mode)
    {
        string algo = Array.Find(ModeOptions, o => o.mode == mode).algo;
        if (string.IsNullOrEmpty(algo)) algo = "VSTv3";
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
            ThemedDialog.Show(this, ex.Message, "参数错误");
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

    /// <summary>Returns the selected logical and physical cores; all-core scope returns
    /// (null, null) to use the unrestricted y-cruncher command.</summary>
    private (List<int>? logical, List<int>? physical) ResolveScope(string scope, int ccd)
    {
        switch (scope)
        {
            case "CCD":
            {
                var logical = CoreTopology.LogicalCoresOfCcd(ccd);
                if (logical.Count == 0) return (null, null); // Fall back to all cores when topology is unavailable.
                var slots = CoreTopology.PhysicalCoresOfCcd(ccd)
                    .Select(RyzenSmu.OsCoreToSlot).OrderBy(x => x).ToList();
                return (logical, slots);
            }
            case "CUSTOM":
            {
                if (_customCores.Count == 0) return (null, null);
                // Convert selected SMU slots to OS core IDs before resolving logical cores.
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
        if (_multiSetBtn != null) _multiSetBtn.Enabled = enabled;
        if (_stepSetBtn != null) _stepSetBtn.Enabled = enabled;
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

        // Clear the crash marker only on user close; otherwise resume and back off CO at next launch.
        if (userClosing)
            Workspace.ClearInProgress();
        OnClosingForAutostart(userClosing);
        YCruncher.Kill();
        base.OnFormClosing(e);
    }
}
