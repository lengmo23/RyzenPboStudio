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
    private readonly PillButton _startBtn = Primary(L.T("▶  Start"));
    private readonly PillButton _stopBtn = Ghost(L.T("■  Stop"));
    private readonly TextBox _logBox = new(); // Log textbox on the LOGGING page.
    private readonly TextBox _logBoxPbo = new(); // Mirrors log content on the PBO Tuning page.
    private readonly System.Windows.Forms.Timer _logTimer = new() { Interval = 100 };
    private Form _infoDialog = null!;
    private bool _updateBusy; // Prevent concurrent update attempts.
    private Version? _knownNewVersion; // Keep a declined update visible on the update button.

    // Align test controls and the combined Start/Stop buttons to one width.
    private const int TestRowWidth = 368;

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
    private volatile bool _restartRequested; // A manual CO or CS change during the test asked to rerun it from the start.
    private volatile string? _restartReason; // What was changed: "CO" or "Curve Shaper".
    private Task? _testTask;
    private bool _testCompletedNormally;
    private string _testMode = "VT3";
    private string _selectedMode = "VT3";
    private int _durationSeconds = Config.DefaultDuration;
    private int _singleRounds = Config.DefaultBkt;

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
        (L.T("YC018 — Pi Benchmark 5b / 10b (memory-heavy, sized by installed memory)"), YCruncher.BenchMode, "bench"),
    };

    public MainForm(bool autostartLaunch = false)
    {
        _autostartLaunch = autostartLaunch;
        Config.StepOnError = _multi.BackoffStep; // Before crash recovery reads it.
        Text = "AMD Ryzen PBO Studio";
        ClientSize = new Size(1420, 790);
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        MaximizeBox = false;
        BackColor = Theme.Bg;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 9F);

        TrySetIcon();
        BuildUi();
        FormClosed += (_, _) => _infoDialog.Dispose();
        SetStatus(L.T("Ready"), Theme.Success);
        Shown += async (_, _) => await RunStartupAsync();
        Load += (_, _) => FitToScreen();

        Log.OnLine += line => _logQueue.Enqueue(line);
        _logTimer.Tick += OnLogTimer;
        _logTimer.Start();

        Log.Write(L.T("Program started"));
        if (Workspace.WasInterrupted())
        {
            var st = Workspace.LoadState();
            string resumeHint = st != null && st.TestMode == "SEQ" && !string.IsNullOrEmpty(st.SeqPhase)
                ? L.F(" · continues from the interrupted {0} stage", st.SeqPhase)
                : "";
            // A manual CO change after interruption takes ownership of the offsets; resume the stage without backoff.
            if (Journal.ReadLastEntry()?.Reason == Journal.ManualTakeover)
            {
                SetStatus(L.F("Last test was interrupted · CO taken over manually, Start continues with it{0}", resumeHint), Theme.Warn);
                Log.Write(L.F("Last test was interrupted; CO was taken over manually and will not be adjusted automatically{0}", resumeHint), "WARN");
            }
            else
            {
                SetStatus(L.F("Last test was interrupted · Start backs off and restores CO{0}", resumeHint), Theme.Warn);
                Log.Write(L.F("Last test was interrupted (likely a crash or power loss): Start uses the pre-crash CO +{0}{1}", Config.StepOnError, resumeHint), "WARN");
            }
        }
    }

    /// <summary>Startup steps run in order so boot-apply and the silent update check never race the editor refresh.</summary>
    private async Task RunStartupAsync()
    {
        try
        {
            await InitCoEditorAsync();
            if (IsDisposed) return;
            await OnAutostartShownAsync();
            if (IsDisposed) return;
            // Check silently; prompt only when an update is available. Skips itself once a resumed test runs.
            if (AppSettings.Current.AutoCheckUpdate) await RunUpdateFlowAsync(manual: false);
        }
        catch (Exception e)
        {
            Log.Write(L.F("Startup error: {0}", e.Message), "ERROR");
        }
    }

    // UI construction

    private MonitorView? _monitorView;
    private readonly SideRail _sideRail = new();
    private Panel _workspaceHost = null!;
    private TableLayoutPanel _rootLayout = null!;
    private Panel _pageHost = null!;
    private const int MonitorH = 256; // Keeps CCD rows tall enough to show the VID row.
    private float _uiScale = 1f;      // Total layout scale applied by FitToScreen (DPI x fit).
    private readonly HeaderBar _headerBar = new() { Dock = DockStyle.Top, Height = 42 };
    private Control _pboPanel = null!;
    private Control _testingPanel = null!;
    private Control _loggingPanel = null!;

    private enum Page { Pbo, Testing, Memory, Logging }
    private Page? _currentPage;

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
        _uiScale = (dpi > 1.01f ? dpi : 1f) * Math.Min(fit, 1f);

        // Pre-size hidden pages to their final bounds so the first and every later switch triggers no resize or layout.
        PerformLayout();
        _workspaceHost.PerformLayout();
        _rootLayout.PerformLayout();
        if (_workspaceHost.DisplayRectangle is { Width: > 0, Height: > 0 } wsRect)
            _memoryPanel.Bounds = wsRect;
        if (_pageHost.DisplayRectangle is { Width: > 0, Height: > 0 } pageRect)
        {
            _testingPanel.Bounds = pageRect;
            _loggingPanel.Bounds = pageRect;
        }

        Location = new Point(
            wa.Left + Math.Max(0, (wa.Width - Width) / 2),
            wa.Top + Math.Max(0, (wa.Height - Height) / 2));
    }

    /// <summary>Scales control fonts recursively because Scale only resizes the layout.</summary>
    private static void ScaleAllFonts(Control root, float s)
    {
        foreach (Control c in root.Controls) ScaleAllFonts(c, s);
        root.Font = new Font(root.Font.FontFamily, Math.Max(1f, root.Font.Size * s), root.Font.Style);
    }

    private void BuildUi()
    {
        var rightShell = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };

        _workspaceHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            Padding = new Padding(16, 10, 16, 14),
            Margin = new Padding(0),
        };

        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Bg,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, MonitorH));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rootLayout = workspace;

        var monitorArea = BuildMonitorArea();
        monitorArea.Margin = new Padding(0, 0, 0, 10);

        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
        _pboPanel = BuildPboPanel();
        _pboPanel.Dock = DockStyle.Fill;
        _testingPanel = BuildTestingPanel();
        _testingPanel.Dock = DockStyle.Fill;
        _memoryPanel = BuildMemoryPanel();
        _memoryPanel.Dock = DockStyle.Fill;
        _loggingPanel = BuildLogCard(_logBox, L.T("Full Run Log"), L.T("Clear log"));
        _loggingPanel.Dock = DockStyle.Fill;
        _pageHost.Controls.Add(_pboPanel);
        _pageHost.Controls.Add(_testingPanel);
        _pageHost.Controls.Add(_loggingPanel);

        workspace.Controls.Add(monitorArea, 0, 0);
        workspace.Controls.Add(_pageHost, 0, 1);

        // Keep _memoryPanel as a full-height sibling of _rootLayout so switching to/from Memory OC never resizes either.
        _workspaceHost.Controls.Add(workspace);
        _workspaceHost.Controls.Add(_memoryPanel);

        rightShell.Controls.Add(_workspaceHost);
        rightShell.Controls.Add(_headerBar); // Later Dock=Top claims the top edge of rightShell.

        Controls.Add(rightShell);
        Controls.Add(_sideRail); // Later Dock=Left claims the full height on the left edge.

        InitBootApplyToggle();
        _infoDialog = BuildInfoDialog();
        _headerBar.NoteLinkClicked += OpenUrl;
        _sideRail.PboClicked += (_, _) => ShowPage(Page.Pbo);
        _sideRail.TestingClicked += (_, _) => ShowPage(Page.Testing);
        _sideRail.MemoryClicked += (_, _) => ShowPage(Page.Memory);
        _sideRail.LoggingClicked += (_, _) => ShowPage(Page.Logging);
        _sideRail.InfoClicked += (_, _) => _infoDialog.ShowDialog(this);
        _sideRail.SettingsClicked += (_, _) => ShowSettingsDialog();
        _sideRail.UpdateClicked += (_, _) => _ = RunUpdateFlowAsync(manual: true);
        _sideRail.SponsorClicked += (_, _) =>
        {
            using var dlg = BuildSponsorDialog();
            dlg.ShowDialog(this);
        };

        ShowPage(Page.Pbo);
    }

    private const int PboFooterH = 26;

    /// <summary>Builds the PBO tuning page with CO, CS, PBO limits, attribution, and a live log sidebar.</summary>
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
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 800));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var left = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 6, 0),
        };
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));        // CO | CS
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));         // PBO limits & Scalar
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, PboFooterH)); // Attribution

        var coCs = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 8),
        };
        coCs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 418));
        coCs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        coCs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var coCard = BuildCoCard();
        coCard.Margin = new Padding(0, 0, 6, 0);
        coCard.Padding = new Padding(16, 10, 16, 10);
        var csCard = BuildCsCard();
        csCard.Margin = new Padding(6, 0, 0, 0);
        csCard.Padding = new Padding(16, 10, 16, 10);
        coCs.Controls.Add(coCard, 0, 0);
        coCs.Controls.Add(csCard, 1, 0);

        var credit = new Label
        {
            Text = L.T("Curve Optimizer / Curve Shaper are implemented with code from the open-source projects ZenStates-Core / SMUDebugTool"),
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

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Bg,
            Margin = new Padding(6, 0, 0, 0),
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));        // Run Log
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));         // Write to BIOS bar
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, PboFooterH)); // Aligns with attribution row

        var logCard = BuildLogCard(_logBoxPbo, L.T("Run Log"), L.T("Clear log"));
        logCard.Margin = new Padding(0, 0, 0, 8);
        logCard.Padding = new Padding(14, 10, 14, 12);

        right.Controls.Add(logCard, 0, 0);
        right.Controls.Add(BuildPboBiosBar(), 0, 1);

        page.Controls.Add(left, 0, 0);
        page.Controls.Add(right, 1, 0);
        return page;
    }

    /// <summary>Builds the TESTING page with configuration console on the left and measurement results on the right.</summary>
    private Control BuildTestingPanel()
    {
        var cfg = BuildConfigCard();
        var results = NewCard();
        results.Padding = new Padding(16, 12, 16, 12);

        InitMeasureButtons();
        _resultsPanel.HeaderActions.Controls.Add(_scbBtn);
        _resultsPanel.HeaderActions.Controls.Add(_alignBtn);
        results.Controls.Add(_resultsPanel);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 417));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        cfg.Margin = new Padding(0, 0, 5, 0);
        results.Margin = new Padding(5, 0, 0, 0);
        top.Controls.Add(cfg, 0, 0);
        top.Controls.Add(results, 1, 0);
        return top;
    }

    /// <summary>Switches the content page; the monitor is hidden on the MEMORY page.</summary>
    private PageFade? _pageFade;

    private void ShowPage(Page page)
    {
        if (_currentPage == page) return;
        bool switchingMemory = _currentPage.HasValue && ((_currentPage == Page.Memory) != (page == Page.Memory));
        var panel = page switch { Page.Testing => _testingPanel, Page.Memory => _memoryPanel, Page.Logging => _loggingPanel, _ => _pboPanel };

        // Fade only the area that actually changes: skip the monitor bar when switching between PBO / Testing / Logging.
        PageFade? fade = null;
        if (_currentPage.HasValue && Visible && WindowState != FormWindowState.Minimized && ActiveForm == this)
        {
            _pageFade?.Close();
            fade = PageFade.FromScreen(switchingMemory ? _workspaceHost : _pageHost);
            if (fade != null)
            {
                _pageFade = fade;
                fade.FormClosed += (_, _) => { if (_pageFade == fade) _pageFade = null; };
                fade.Cover(this);
            }
        }

        Control revealTarget;
        if (page == Page.Memory)
        {
            _memoryPanel.Visible = true;
            _memoryPanel.BringToFront();
            _rootLayout.Visible = false;
            revealTarget = _memoryPanel;
        }
        else
        {
            _pboPanel.Visible = page == Page.Pbo;
            _testingPanel.Visible = page == Page.Testing;
            _loggingPanel.Visible = page == Page.Logging;
            panel.BringToFront();
            if (!_rootLayout.Visible)
            {
                _rootLayout.Visible = true;
                _rootLayout.BringToFront();
            }
            _memoryPanel.Visible = false;
            revealTarget = switchingMemory ? _rootLayout : panel;
        }

        _currentPage = page;
        _sideRail.SetActivePage((int)page);
        _headerBar.PageTitle = page switch
        {
            Page.Testing => L.T("Stability Test"),
            Page.Memory => L.T("Memory OC"),
            Page.Logging => L.T("Run Log"),
            _ => L.T("PBO Tuning"),
        };
        if (page == Page.Memory)
            _headerBar.SetPageNote(L.T("Memory OC powered by Cat-girl-memoryOC"), CoDevHandle, CoDevDouyinUrl);
        else
            _headerBar.SetPageNote(null, null, null);

        fade?.Reveal(revealTarget);
    }

    /// <summary>Builds the persistent telemetry deck (or an error card if hardware initialization failed).</summary>
    private Control BuildMonitorArea()
    {
        try
        {
            _monitorView = new MonitorView(_headerBar, _sideRail) { Dock = DockStyle.Fill, Margin = new Padding(0) };
            return _monitorView;
        }
        catch (Exception ex)
        {
            var errCard = NewCard();
            errCard.Margin = new Padding(0);
            errCard.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Theme.Warn,
                BackColor = Theme.Surface,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = L.T("Monitor initialization failed: ") + ex.Message,
            });
            return errCard;
        }
    }

    private Control BuildConfigCard()
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(16, 12, 16, 12);

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

        static Panel SectionDivider() => new()
        {
            Size = new Size(TestRowWidth, 1),
            BackColor = Theme.Border,
            Margin = new Padding(0, 9, 0, 8),
        };

        var modeTitle = new Label { Text = L.T("Single Test"), AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 5), Font = new Font(Theme.FontFamily, 9.5F, FontStyle.Bold) };

        _modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeCombo.FlatStyle = FlatStyle.Flat;
        _modeCombo.Width = TestRowWidth;
        _modeCombo.BackColor = Theme.SurfaceAlt;
        _modeCombo.ForeColor = Theme.TextHi;
        _modeCombo.Font = new Font(Theme.FontFamily, 9.5F);
        _modeCombo.Margin = new Padding(0);
        DarkCombo.Apply(_modeCombo);
        foreach (var opt in ModeOptions) _modeCombo.Items.Add(opt.display);
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_modeCombo.SelectedIndex < 0) return;
            _selectedMode = ModeOptions[_modeCombo.SelectedIndex].mode;
            RestyleModeButtons();
        };
        var fields = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        AddFieldPair(fields, L.T("Sec/round"), _durationBox, 66);
        AddFieldPair(fields, L.T("Rounds"), _roundsBox, 60);
        _durationBox.Text = _durationSeconds.ToString();
        _roundsBox.Text = _singleRounds.ToString();

        var comboTitle = new Label { Text = L.T("Multi-Test"), AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 5), Font = new Font(Theme.FontFamily, 9.5F, FontStyle.Bold) };
        const int modeGap = 8;
        _multiSetBtn = BuildMenuButton(L.T("Set the tests, rounds and time per round of the sequential and combined tests"), ShowMultiTestDialog);
        int modeBtnWidth = (TestRowWidth - _multiSetBtn.Width - 2 * modeGap) / 2;
        _seqBtn = MakeModeButton(L.T("Sequential"), "SEQ", modeBtnWidth, SeqTip());
        _comboBtn = MakeModeButton(L.T("Combined"), "COMBO", modeBtnWidth, ComboTip());
        _seqBtn.Margin = new Padding(0, 0, modeGap, 0);
        _comboBtn.Margin = new Padding(0, 0, modeGap, 0);
        var modeRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0) };
        modeRow.Controls.Add(_seqBtn);
        modeRow.Controls.Add(_comboBtn);
        modeRow.Controls.Add(_multiSetBtn);

        var adjTitle = new Label { Text = L.T("CO Adjustment"), AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 5), Font = new Font(Theme.FontFamily, 9.5F, FontStyle.Bold) };
        const int adjGap = 8;
        _stepSetBtn = BuildMenuButton(L.T("Set the CO step backed off in auto mode"), ShowStepDialog);
        int adjBtnWidth = (TestRowWidth - _stepSetBtn.Width - 2 * adjGap) / 2;
        _autoAdjBtn = MakeAdjustButton(L.T("Auto"), true, AutoAdjustTip());
        _manualAdjBtn = MakeAdjustButton(L.T("Manual"), false, L.T("Errors are only reported"));
        _autoAdjBtn.Width = adjBtnWidth;
        _manualAdjBtn.Width = adjBtnWidth;
        _autoAdjBtn.Margin = new Padding(0, 0, adjGap, 0);
        _manualAdjBtn.Margin = new Padding(0, 0, adjGap, 0);
        _stepSetBtn.Margin = new Padding(0);
        var adjRow = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Surface, WrapContents = false, Margin = new Padding(0) };
        adjRow.Controls.Add(_autoAdjBtn);
        adjRow.Controls.Add(_manualAdjBtn);
        adjRow.Controls.Add(_stepSetBtn);

        var scopeTitle = new Label { Text = L.T("Test Scope"), AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 5), Font = new Font(Theme.FontFamily, 9.5F, FontStyle.Bold) };
        var scopeRow1 = BuildScopeRow(new (string, string, string)[]
        {
            (L.T("All cores"), "ALL", L.T("All cores are stress tested")),
            (L.T("Custom…"), "CUSTOM", L.T("Pick the physical cores to stress test")),
        });
        var scopeRow2 = BuildCcdScopeRow();
        if (scopeRow2 != null) scopeRow1.Margin = new Padding(0, 0, 0, 6);

        // Divider + primary Start / Stop actions docked at the bottom.
        var bottomDock = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 6, 0, 0),
        };
        var dividerHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        var bottomDivider = SectionDivider();
        bottomDivider.Margin = new Padding(0);
        dividerHost.Controls.Add(bottomDivider);
        const int runGap = 10;
        int stopWidth = 124;
        int startWidth = TestRowWidth - stopWidth - runGap;
        var runRow = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = Theme.Surface,
            WrapContents = false,
            Dock = DockStyle.Bottom,
            Padding = new Padding(0),
        };
        _startBtn.Normal = Theme.Accent;
        _startBtn.Hover = Theme.AccentHover;
        _startBtn.Outline = Color.Empty;
        _startBtn.ForeColor = Theme.AccentText;
        _startBtn.Radius = 8;
        _startBtn.Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold);
        _startBtn.Size = new Size(startWidth, 36);
        _stopBtn.Size = new Size(stopWidth, 36);
        _stopBtn.Radius = 8;
        _stopBtn.Normal = Theme.SurfaceAlt;
        _stopBtn.Hover = Theme.Danger;
        _stopBtn.ForeColor = Theme.TextHi;
        _stopBtn.Font = new Font(Theme.FontFamily, 9.5F, FontStyle.Bold);
        _startBtn.Margin = new Padding(0, 0, runGap, 0);
        _stopBtn.Margin = new Padding(0);
        _stopBtn.Enabled = false;
        _startBtn.Click += StartTest;
        _stopBtn.Click += StopTest;
        runRow.Controls.Add(_startBtn);
        runRow.Controls.Add(_stopBtn);
        bottomDock.Controls.Add(runRow);
        bottomDock.Controls.Add(dividerHost);

        body.Controls.Add(modeTitle);
        body.Controls.Add(_modeCombo);
        body.Controls.Add(fields);
        body.Controls.Add(SectionDivider());
        body.Controls.Add(comboTitle);
        body.Controls.Add(modeRow);
        body.Controls.Add(SectionDivider());
        body.Controls.Add(adjTitle);
        body.Controls.Add(adjRow);
        body.Controls.Add(SectionDivider());
        body.Controls.Add(scopeTitle);
        body.Controls.Add(scopeRow1);
        if (scopeRow2 != null) body.Controls.Add(scopeRow2);

        SelectMode(_selectedMode);
        RestyleAdjustButtons();
        RestyleScopeButtons();

        // Dock order keeps title at the top, divider + Start/Stop row fixed at the bottom, and body between them.
        card.Controls.Add(body);
        card.Controls.Add(bottomDock);
        card.Controls.Add(SectionTitle(L.T("Test Settings")));
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
                L.F("Stress CCD{0} only (physical cores {1})\nWith the other CCD idle, the tested CCD gets the whole power budget\nand runs faster than under an all-core load, so results don't carry over to all cores", c, CoreList(cores))));
        }
        specs.Add((L.T("Each CCD"), "EACH",
            L.T("Runs the full rounds on each CCD in turn (CCD0 → CCD1 …)")));
        return BuildScopeRow(specs);
    }

    private static string CoreList(IEnumerable<int> cores) => string.Join(", ", cores);

    /// <summary>Changes the test scope; cancelling custom selection preserves the current scope.</summary>
    private PillButton MakeScopeButton(string text, string tag, int width, string tip)
    {
        var b = new PillButton
        {
            Text = text,
            Tag = tag,
            Font = new Font(Theme.FontFamily, 9.25F),
            Height = 30,
            Width = width,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.SurfaceHover,
            ForeColor = Theme.TextMid,
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

    private static readonly Color ChoiceActiveBg = Color.FromArgb(0x10, 0x28, 0x1E);
    private static readonly Color ChoiceActiveHover = Color.FromArgb(0x16, 0x35, 0x28);
    private static readonly Color ChoiceActiveText = Color.FromArgb(0x4A, 0xDE, 0x80);

    private static void StyleChoicePill(PillButton? b, bool selected)
    {
        if (b == null) return;
        b.Normal = selected ? ChoiceActiveBg : Theme.SurfaceAlt;
        b.Hover = selected ? ChoiceActiveHover : Theme.SurfaceHover;
        b.Outline = selected ? Theme.Success : Color.Empty;
        b.ForeColor = selected ? ChoiceActiveText : Theme.TextMid;
        b.Invalidate();
    }

    private void RestyleScopeButtons()
    {
        string active = _testScope switch
        {
            "CCD" => $"CCD{_scopeCcd}",
            _ => _testScope,
        };
        foreach (var b in _scopeBtns)
            StyleChoicePill(b, (string)b.Tag! == active);
    }

    /// <summary>Selects physical cores, excluding fused-off slots; requires at least one selection.</summary>
    private bool PromptCustomCores()
    {
        int slots = _coSlotCount > 0 ? _coSlotCount : RyzenSmu.SlotCount;
        if (slots <= 0) { ThemedDialog.Show(this, L.T("Cannot read the core topology, so a custom scope is unavailable."), L.T("Notice")); return false; }

        using var dlg = new Form
        {
            Text = L.T("Select Cores"),
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
                Text = L.F("Core {0}", i) + (disabled ? L.T(" (disabled)") : ""),
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

        var okBtn = Primary(L.T("OK"));
        okBtn.Size = new Size(92, 32);
        okBtn.Radius = 8;
        okBtn.BackColor = Theme.Surface;
        okBtn.DialogResult = DialogResult.OK;
        okBtn.Margin = new Padding(0);
        var cancelBtn = Ghost(L.T("Cancel"));
        cancelBtn.Size = new Size(88, 32);
        cancelBtn.Radius = 8;
        cancelBtn.BackColor = Theme.Surface;
        cancelBtn.DialogResult = DialogResult.Cancel;
        cancelBtn.Margin = new Padding(8, 0, 0, 0);
        var btnRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Right, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = new Padding(14, 6, 14, 14) };
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
            ThemedDialog.Show(this, L.T("Select at least one core."), L.T("Notice"));
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
            Font = new Font(Theme.FontFamily, 9.25F),
            Height = 30,
            Width = TestRowWidth,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.SurfaceHover,
            ForeColor = Theme.TextMid,
            Margin = new Padding(0, 0, 0, 2),
        };
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) => { _autoAdjust = auto; RestyleAdjustButtons(); };
        return b;
    }

    private void RestyleAdjustButtons()
    {
        foreach (var b in new[] { _autoAdjBtn, _manualAdjBtn })
            if (b != null) StyleChoicePill(b, (bool)b.Tag! == _autoAdjust);
    }

    /// <summary>Reports a manual-mode error without changing CO offsets.</summary>
    private void NotifyManualError(List<int> crashedPhysical)
    {
        string cores = crashedPhysical.Count > 0
            ? string.Join(", ", crashedPhysical)
            : L.T("unknown (y-cruncher crashed)");
        Ui(() =>
        {
            SetStatus(L.F("Manual mode: physical cores [{0}] failed, CO not adjusted", cores), Theme.Danger);
            if (AppSettings.Current.ExpertMode) return;
            ThemedDialog.Show(this,
                L.F("An error was detected and the test stopped.\n\nFailing physical cores: {0}\n\n" +
                    "Manual mode is on, so CO was not changed. Adjust CO on the PBO Tuning page, then start the test again.", cores),
                L.T("Manual mode · error detected"));
        });
    }

    /// <summary>Selects sequential or combined test mode.</summary>
    private PillButton MakeModeButton(string text, string mode, int width, string tip)
    {
        var b = new PillButton
        {
            Text = text,
            Tag = mode,
            Font = new Font(Theme.FontFamily, 9.25F),
            Height = 30,
            Width = width,
            Radius = 8,
            AutoSize = false,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.SurfaceHover,
            ForeColor = Theme.TextMid,
            Margin = new Padding(0, 0, 0, 2),
        };
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) => { _selectedMode = mode; RestyleModeButtons(); };
        return b;
    }

    private void RestyleModeButtons()
    {
        foreach (var b in new[] { _seqBtn, _comboBtn })
            if (b != null) StyleChoicePill(b, (string)b.Tag! == _selectedMode);
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
            Text = L.T("About AMD Ryzen PBO Studio"),
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
                Radius = 8,
                Font = new Font(Theme.FontFamily, 10F),
                BackColor = Theme.Surface,
                Normal = Theme.SurfaceAlt,
                Hover = Theme.SurfaceHover,
                Outline = Theme.Border,
                ForeColor = Theme.TextHi,
                Margin = new Padding(4, 0, 4, 0),
            };
            b.Click += (_, _) => OpenUrl(url);
            return b;
        }

        Line("AMD Ryzen PBO Studio", 20F, Theme.Accent, bold: true);
        Line(L.F("{0} · Build {1}", HeaderBar.DisplayVersion, buildDate), 9.5F, Theme.TextLo);

        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Anchor = AnchorStyles.None,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 14, 0, 0),
        };
        links.Controls.Add(LinkButton(L.T("Project page"), Updater.HomePage));
        content.Controls.Add(links);

        content.Controls.Add(new Panel
        {
            Size = new Size(contentWidth - 40, 1),
            Anchor = AnchorStyles.None,
            BackColor = Theme.Border,
            Margin = new Padding(0, 20, 0, 4),
        });

        LinkLabel LinkLine(string text, int start, int length, string url)
        {
            var link = new LinkLabel
            {
                Text = text,
                LinkArea = new LinkArea(start, length),
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
            link.LinkClicked += (_, _) => OpenUrl(url);
            content.Controls.Add(link);
            return link;
        }

        Line(L.T("Developer"), 16F, Theme.Accent, bold: true, top: 10);
        const string devName = "lengmo23 (", devHandle = "@冷漠OC";
        LinkLine(devName + devHandle + ")", devName.Length, devHandle.Length, DouyinUrl);

        Line(L.T("Co-developer"), 16F, Theme.Accent, bold: true, top: 14);
        LinkLine(CoDevHandle + L.T(" (Memory OC)"), 0, CoDevHandle.Length, CoDevDouyinUrl);

        Line(L.T("Special Thanks"), 16F, Theme.Accent, bold: true, top: 14);
        Line("@古腾古腾咕咚咚" + L.T(" (guidance and suggestions)"), 10.5F, Theme.TextHi);
        Line("@阿肝" + L.T(" (feature ideas and testing)"), 10.5F, Theme.TextHi);
        Line("@Yee" + L.T(" (core testing)"), 10.5F, Theme.TextHi);
        Line(copyright, 8.5F, Theme.TextLo, top: 26);

        dialog.Controls.Add(content);
        return dialog;
    }

    private const string DouyinUrl = "https://v.douyin.com/6VGnQBTxwIQ/";
    private const string CoDevHandle = "@纯爱出演小猪";
    private const string CoDevDouyinUrl = "https://v.douyin.com/yMpmAidKqdY/";

    private const int QrHeight = 420;

    /// <summary>Sponsor dialog: a short thank-you note above the Alipay and WeChat payment codes.</summary>
    private static Form BuildSponsorDialog()
    {
        const int width = 640;   // fits both codes side by side at QrHeight
        var dlg = NewDialog(L.T("Support the Author"));
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

        root.Controls.Add(Para(L.T("Buy the author a coffee"), 18F, Theme.Accent, bold: true));
        root.Controls.Add(Para("Buy Me a Coffee", 9.5F, Theme.TextLo, top: 2));
        root.Controls.Add(Para(
            L.T("AMD Ryzen PBO Studio is an open-source tool I develop on my own in my spare time; every feature is the result of many rounds of refinement. " +
                "If it saved you hours of tuning or got more performance out of your PC, feel free to buy me a coffee!\n" +
                "Every bit of support keeps me maintaining and improving it, and helps more people get a better tuning tool."),
            10F, Theme.TextHi, top: 18, body: true));
        root.Controls.Add(Para(L.T("Support is entirely optional and doesn't unlock anything. Thanks to everyone who uses it!"), 9F, Theme.TextLo, top: 14));

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
        if (UpdateBlocked(manual)) return;

        _updateBusy = true;
        _sideRail.UpdateTip = L.T("Checking for updates...");
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
                    ThemedDialog.Show(Form.ActiveForm ?? this, L.F("Update check failed: {0}", e.Message), L.T("Check for Updates"));
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

            Log.Write(L.F("New version {0} available (current v{1})", info.Tag, Updater.CurrentVersion));
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
            if (UpdateBlocked(notify: true)) return;

            await DownloadAndApplyAsync(info);
        }
        finally
        {
            _updateBusy = false;
            RefreshUpdateTip();
        }
    }

    /// <summary>True while a stress test, a single-core measurement or a BIOS memory write must not be cut short by the
    /// exit an update needs.</summary>
    private bool UpdateBlocked(bool notify)
    {
        if (_testTask is { IsCompleted: false })
        {
            if (notify)
                ThemedDialog.Show(Form.ActiveForm ?? this, L.T("Cannot update while a test is running.\n\nStop the test, let it finish cleaning up, then try again."), L.T("Testing"));
            return true;
        }
        if (_measureTask is { IsCompleted: false } || (_memOcPage?.IsWriting ?? false) || _pboBiosWriting)
        {
            if (notify)
                ThemedDialog.Show(Form.ActiveForm ?? this, L.T("Cannot update while a measurement or a BIOS write is running.\n\nWait for it to finish, then try again."), L.T("Update"));
            return true;
        }
        return false;
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
            notes.Controls.Add(NotesPara(L.T("This update has no release notes."), 9.5F, Theme.TextLo, noteWidth, margin: new Padding(0, 0, 0, 10)));
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
        var dlg = NewDialog(L.T("Check for Updates"));
        var root = NewNotesRoot();
        root.Controls.Add(NotesPara(L.T("You're up to date"), 16F, Theme.Accent, width, bold: true));
        root.Controls.Add(NotesPara($"v{Updater.CurrentVersion}" + (currentNotes != null ? L.T(" · What's new in this version") : ""),
            9F, Theme.TextLo, width, margin: new Padding(0, 4, 0, 0)));
        if (currentNotes != null)
        {
            root.Controls.Add(new Panel { Size = new Size(width, 1), BackColor = Theme.Border, Margin = new Padding(0, 14, 0, 12) });
            root.Controls.Add(BuildNotesScroller(currentNotes));
        }

        var ok = Pill(L.T("OK"), 104, primary: true);
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
        var dlg = NewDialog(L.T("New Version"));
        var root = NewNotesRoot();

        root.Controls.Add(NotesPara(L.F("{0} is available", info.Tag), 16F, Theme.Accent, width, bold: true));
        double sizeMb = info.Size / 1024.0 / 1024.0;
        root.Controls.Add(NotesPara(L.F("Current version v{0} · update about {1:F1} MB", Updater.CurrentVersion, sizeMb),
            9F, Theme.TextLo, width, margin: new Padding(0, 4, 0, 0)));
        root.Controls.Add(new Panel { Size = new Size(width, 1), BackColor = Theme.Border, Margin = new Padding(0, 14, 0, 12) });
        root.Controls.Add(BuildNotesScroller(info.Notes));

        root.Controls.Add(NotesPara(L.T("The program closes during the update and restarts when it's installed."), 8.5F, Theme.TextLo, width, margin: new Padding(0, 6, 0, 0)));

        var ok = Pill(L.T("Update now"), 104, primary: true);
        var later = Pill(L.T("Later"), 104);
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
        _sideRail.UpdateTip = _knownNewVersion != null ? L.F("New version v{0}", _knownNewVersion) : L.T("Check for updates");
        _sideRail.UpdateAvailable = _knownNewVersion != null;
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
                _sideRail.UpdateTip = L.F("Downloading {0}%", p);
                SetStatus(L.F("Downloading update {0} ({1}) {2}%", info.Tag, source, p), Theme.Warn);
            });
            SetStatus(L.F("Downloading update {0}...", info.Tag), Theme.Warn);
            zipPath = await Updater.DownloadAsync(info, progress,
                s => { source = s; lastPercent = -1; },
                // Show connection and source-switch stages when no byte progress is available.
                stage => Ui(() =>
                {
                    if (IsDisposed) return;
                    _sideRail.UpdateTip = L.T("Downloading...");
                    SetStatus(L.F("Update {0} · {1}", info.Tag, stage), Theme.Warn);
                }));
        }
        catch (Exception e)
        {
            RefreshUpdateTip();
            SetStatus(L.T("Update download failed"), Theme.Danger);
            Log.Write(L.F("Update download failed: {0}", e.Message), "WARN");
            using (var dlg = BuildDownloadFailedDialog(info.Tag))
                dlg.ShowDialog(Form.ActiveForm ?? this);
            return;
        }

        string newRoot;
        try
        {
            _sideRail.UpdateTip = L.T("Extracting...");
            SetStatus(L.T("Extracting the update..."), Theme.Warn);
            newRoot = await Task.Run(() => Updater.ExtractAndVerify(zipPath));
        }
        catch (Exception e)
        {
            RefreshUpdateTip();
            SetStatus(L.T("Update package verification failed"), Theme.Danger);
            Log.Write(L.F("Failed to extract or verify the update: {0}", e.Message), "WARN");
            ThemedDialog.Show(Form.ActiveForm ?? this, L.F("The update package can't be used: {0}\n\nThe installed version is unchanged.", e.Message), L.T("Update"));
            return;
        }

        // A test, measurement or memory write started during the download keeps the installed version.
        if (UpdateBlocked(notify: true))
        {
            RefreshUpdateTip();
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
            ThemedDialog.Show(Form.ActiveForm ?? this, L.F("Cannot start the updater: {0}\n\nThe installed version is unchanged.", e.Message), L.T("Update"));
            return;
        }

        Log.Write(L.T("Update ready, exiting to finish installing"));
        _skipExitConfirm = true;
        Close();
    }

    /// <summary>Shown when GitHub and every mirror fail: points users to the QQ group for the latest version.</summary>
    private static Form BuildDownloadFailedDialog(string tag)
    {
        const int width = 400;
        var dlg = NewDialog(L.T("Download Failed"));
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

        root.Controls.Add(Line(L.F("Cannot download {0}", tag), 12F, Theme.TextHi, bold: true, bottom: 6));
        root.Controls.Add(Line(L.T("GitHub and every mirror are unreachable; the installed version is unchanged.\nJoin the QQ group to get the latest version:"), 9.5F, Theme.TextLo, bottom: 14));
        root.Controls.Add(Line(L.T("QQ group"), 9F, Theme.TextLo, bottom: 2, center: true));
        root.Controls.Add(Line(Updater.QqGroup, 22F, Theme.Accent, bold: true, bottom: 6, center: true));

        var copyBtn = Pill(L.T("Copy number"), 104, primary: true);
        var closeBtn = Pill(L.T("Close"), 84);
        closeBtn.DialogResult = DialogResult.Cancel;
        closeBtn.Margin = new Padding(8, 0, 0, 0);
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(Updater.QqGroup);
                copyBtn.Text = L.T("Copied");
            }
            catch (Exception ex)
            {
                Log.Write(L.F("Failed to copy the QQ group number: {0}", ex.Message), "WARN");
                copyBtn.Text = L.T("Copy failed");
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
            ThemedDialog.Show(Form.ActiveForm ?? this, L.F("Cannot open the link: {0}\n\n{1}", e.Message, url), L.T("Notice"));
        }
    }

    /// <summary>Builds a log card for either page; separate textboxes mirror the same log
    /// because one control cannot belong to two parents.</summary>
    private Control BuildLogCard(TextBox box, string titleText, string clearText)
    {
        var card = NewCard();
        card.Margin = new Padding(0);
        card.Padding = new Padding(14, 10, 14, 12);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 0, 0, 6),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        header.Controls.Add(new Label
        {
            Text = titleText,
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        // Clearing the display does not remove the session log on disk.
        var clearBtn = HeaderActionBtn(clearText, 50, primary: false, ClearLog);
        clearBtn.Margin = new Padding(6, 3, 0, 0);
        _tips.SetToolTip(clearBtn, L.T("Clears the displayed log; the log file on disk is unaffected"));
        header.Controls.Add(clearBtn, 1, 0);

        box.Multiline = true;
        box.ReadOnly = true;
        box.ScrollBars = ScrollBars.Vertical;
        box.WordWrap = true;
        box.MaxLength = 0;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.WellBg;
        box.ForeColor = Color.FromArgb(0xD8, 0xDE, 0xE9);
        box.Font = new Font(Theme.FontFamily, box == _logBoxPbo ? 9.25F : 9.75F);
        box.Dock = DockStyle.Fill;
        box.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(box.Handle);

        var terminal = new Card
        {
            Dock = DockStyle.Fill,
            Fill = Theme.WellBg,
            Stroke = Theme.BorderSubtle,
            Radius = 8,
            BackColor = Theme.Surface,
            Padding = new Padding(10, 8, 6, 8),
            Margin = new Padding(0),
        };
        terminal.Controls.Add(box);

        card.Controls.Add(terminal);
        card.Controls.Add(header);
        return card;
    }

    private static PillButton HeaderActionBtn(string text, int width, bool primary, Action onClick)
    {
        var b = new PillButton
        {
            Text = text,
            Size = new Size(width, 26),
            Radius = 6,
            Font = new Font(Theme.FontFamily, 8.5F, primary ? FontStyle.Bold : FontStyle.Regular),
            BackColor = Theme.Surface,
            Normal = primary ? Theme.Accent : Theme.SurfaceAlt,
            Hover = primary ? Theme.AccentHover : Theme.SurfaceHover,
            ForeColor = primary ? Theme.AccentText : Theme.TextHi,
            Margin = new Padding(0, 0, 4, 0),
        };
        // width is a minimum; the button grows with the translated text.
        b.Width = Math.Max(width, TextRenderer.MeasureText(text, b.Font).Width + 20);
        b.Click += (_, _) => onClick();
        return b;
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
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 4, 0, 0),
        };
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // The button column is centered in the space right of the grid (anchored Top only).
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
            Margin = new Padding(0, 0, 6, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // The second column has no right margin, so the centered button column sees only visible space.

        grid.Controls.Add(MakeStepButton("+", () => StepRange(0, CoSlotsPerColumn, +1)), 0, 0);
        var ccd1Plus = MakeStepButton("+", () => StepRange(CoSlotsPerColumn, CoDisplaySlotCount, +1));
        ccd1Plus.Enabled = _coSlotCount > CoSlotsPerColumn;
        ccd1Plus.Margin = ccd1Plus.Margin with { Right = 0 };
        grid.Controls.Add(ccd1Plus, 1, 0);

        for (int r = 0; r < CoSlotsPerColumn; r++)
        {
            grid.Controls.Add(MakeCoreCell(r), 0, r + 1);
            grid.Controls.Add(MakeCoreCell(CoSlotsPerColumn + r), 1, r + 1);
        }

        grid.Controls.Add(MakeStepButton("-", () => StepRange(0, CoSlotsPerColumn, -1)), 0, CoSlotsPerColumn + 1);
        var ccd1Minus = MakeStepButton("-", () => StepRange(CoSlotsPerColumn, CoDisplaySlotCount, -1));
        ccd1Minus.Enabled = _coSlotCount > CoSlotsPerColumn;
        ccd1Minus.Margin = ccd1Minus.Margin with { Right = 0 };
        grid.Controls.Add(ccd1Minus, 1, CoSlotsPerColumn + 1);
        return grid;
    }

    private Control MakeCoreCell(int core)
    {
        bool disabled = core >= _coSlotCount || RyzenSmu.IsSlotDisabled(core);
        if (disabled)
            _coCells[core].Enabled = false; // Fused-off or absent slots are fixed at zero.

        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0, 0, 12, 0),
        };
        f.Controls.Add(new Label
        {
            Text = $"Core {core}",
            AutoSize = false,
            Width = 56,
            ForeColor = disabled ? Theme.TextLo : Theme.TextHi,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 8.75F),
            Margin = new Padding(0, 6, 4, 0),
        });
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
            Font = new Font(Theme.FontFamily, 10F),
            Margin = new Padding(0, 2, 0, 2),
        };
        DarkSpin.Apply(cell);
        return cell;
    }

    private PillButton MakeStepButton(string text, Action onClick)
    {
        var b = Ghost(text);
        b.BackColor = Theme.Surface;
        b.Size = new Size(58, 24);
        b.Radius = 6;
        b.Font = new Font(Theme.FontFamily, 10.5F, FontStyle.Bold);
        b.Margin = new Padding(60, 0, 12, 4);
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
            Anchor = AnchorStyles.Top,
            Margin = new Padding(0, 32, 0, 0),
        };
        PillButton Btn(string text, Action click, string? tip = null)
        {
            var b = Ghost(text);
            b.BackColor = Theme.Surface;
            b.Size = new Size(96, 32);
            b.Radius = 7;
            b.Margin = new Padding(0, 0, 0, 8);
            b.Click += (_, _) => click();
            if (tip != null) _tips.SetToolTip(b, tip);
            return b;
        }
        flow.Controls.Add(Btn(L.T("Apply"), OnCoApply, tip: L.T("Write the current CO settings to the CPU")));
        flow.Controls.Add(Btn(L.T("Refresh"), () => _ = OnCoRefresh(), tip: L.T("Read CO and FMax from the CPU again")));
        flow.Controls.Add(Btn(L.T("Save"), OnCoSave, tip: L.T("Save the current CO settings to co_profile.json")));
        flow.Controls.Add(Btn(L.T("Load"), OnCoLoad, tip: L.T("Load the saved CO settings (not written to the CPU)")));
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
        _scalarBox.Font = new Font(Theme.FontFamily, 10F);
        _scalarBox.Margin = new Padding(0, 6, 6, 0);   // text centered level with the 23 px number boxes
        DarkCombo.Apply(_scalarBox, center: true);
        for (int x = 1; x <= 10; x++) _scalarBox.Items.Add($"{x}X");
        SelectScalar(1);
        _scalarBox.SelectedIndexChanged += (_, _) => { if (!_scalarLoading) _scalarDirty = true; };
        _tips.SetToolTip(_scalarBox,
            L.T("PBO Scalar: 1X is Auto. A higher multiplier lets the SMU use more voltage when boosting.\n" +
                "Written only when changed and Apply is clicked; on 5000 series, writing it also enables PBO."));

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

        var apply = Ghost(L.T("Apply"));
        apply.Size = new Size(84, 30);
        apply.Radius = 7;
        apply.BackColor = Theme.Surface;
        apply.Margin = new Padding(12, 4, 0, 0);
        apply.Click += (_, _) => OnPboApply();
        _tips.SetToolTip(apply, L.T("Writes FMax / PPT / EDC / TDC and Scalar to the CPU"));
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
        box.Font = new Font(Theme.FontFamily, 10F);
        box.Margin = new Padding(0, 7, 8, 0);
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
            Font = new Font(Theme.FontFamily, 8.75F),
            Margin = new Padding(0, 11, 4, 0),
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
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(0, 4, 0, 0),
        };
        bodyTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        // The button column is centered in the space right of the grid (anchored Top only).
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
            Margin = new Padding(14, 32, 0, 0),
        };
        PillButton Btn(string text, Action click, string? tip = null)
        {
            var b = Ghost(text);
            b.BackColor = Theme.Surface;
            b.Size = new Size(96, 32);
            b.Radius = 7;
            b.Margin = new Padding(0, 0, 0, 8);
            b.Click += (_, _) => click();
            if (tip != null) _tips.SetToolTip(b, tip);
            return b;
        }
        _csApplyBtn = Btn(L.T("Apply"), OnCsApply, tip: L.T("Write the current Curve Shaper settings to the CPU"));
        _csRefreshBtn = Btn(L.T("Refresh"), () => _ = OnCsRefresh(), tip: L.T("Read Curve Shaper from the CPU again"));
        _csSaveBtn = Btn(L.T("Save"), OnCsSave, tip: L.T("Save the current CS settings to cs_state.json"));
        _csLoadBtn = Btn(L.T("Load"), OnCsLoad, tip: L.T("Load the saved CS settings (not written to the CPU)"));
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
    private const int CsCellWidth = 48;

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
        Font = new Font(Theme.FontFamily, 8.25F, FontStyle.Bold),
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
        ForeColor = Theme.TextMid,
        BackColor = Theme.Surface,
        Font = new Font(Theme.FontFamily, 8.5F, FontStyle.Bold),
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
            Font = new Font(Theme.FontFamily, 10F),
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

    private bool _coApplying;
    private bool _changePromptMuted; // The mid-test change prompt stays muted until the app exits.

    private async void OnCoApply()
    {
        if (_coApplying || _bootApplying || !_editorsReady) return;
        _coApplying = true;
        try
        {
            var vals = _coCells.Take(_coSlotCount).Select(c => (int)c.Value).ToList();
            // Manual changes during a test do not take ownership; crash recovery still backs off one step.
            bool testRunning = _testTask is { IsCompleted: false };
            string reason = testRunning ? Journal.ManualInTest : Journal.ManualTakeover;
            SetStatus(L.T("Writing CO..."), Theme.Warn);
            Log.Write(L.T("Writing CO..."));
            long started = Environment.TickCount64;
            // Per-core write and readback can take seconds while the SMU is busy under load.
            bool ok = await Task.Run(() => Tuning.Apply(vals, "MANUAL", null, reason));
            double secs = (Environment.TickCount64 - started) / 1000.0;
            Log.WriteCo(ok ? L.F("Manual CO apply ({0:F1} s):", secs) : L.F("Manual CO apply failed ({0:F1} s):", secs),
                        vals, level: ok ? "INFO" : "ERROR");
            if (IsDisposed) return;
            if (ok && Workspace.WasInterrupted())
            {
                if (testRunning)
                    Log.Write(L.T("Manual CO apply detected: later back-offs start from these values"), "WARN");
                else
                {
                    // Resume the interrupted stage using the user’s offsets without another backoff.
                    Log.Write(L.T("Manual CO apply detected: the test continues with these values"), "WARN");
                    SetStatus(L.T("Interrupted CO taken over · Start continues with it"), Theme.Warn);
                    return;
                }
            }
            SetStatus(ok ? L.T("Curve Optimizer applied") : L.T("CO apply failed"), ok ? Theme.Success : Theme.Danger);

            // The test may have ended while the write ran.
            if (ok && testRunning && _testTask is { IsCompleted: false }) PromptRestartAfterManualChange("CO");
        }
        catch (Exception e)
        {
            Log.Write(L.F("{0} error: {1}", "Curve Optimizer", e.Message), "ERROR");
            if (!IsDisposed) SetStatus(L.T("CO apply failed"), Theme.Danger);
        }
        finally
        {
            _coApplying = false;
        }
    }

    /// <summary>After a manual CO or CS change during a test, asks whether to rerun it from the first step and round.</summary>
    private void PromptRestartAfterManualChange(string what)
    {
        if (_changePromptMuted) return;
        if (AppSettings.Current.ExpertMode)
        {
            Log.Write(L.F("{0} was changed manually during the test; the test continues", what), "WARN");
            return;
        }
        bool restart = ThemedDialog.Ask(this,
            L.F("{0} was changed manually during the test. Restart the test?\nA restart begins at the first round of the first test.", what),
            L.F("{0} changed manually", what), L.T("Restart"), L.T("Continue"),
            L.T("Don't ask again this session, continue"), out _changePromptMuted);
        if (restart && _testTask is { IsCompleted: false } && !_stopRequested)
        {
            _restartReason = what;
            _restartRequested = true;
            _cts?.Cancel();
            _stopBtn.Enabled = false;
            SetStatus(L.T("Restarting the test..."), Theme.Warn);
        }
    }

    private async Task OnCoRefresh()
    {
        try
        {
            int n = _coSlotCount;
            // Off the UI thread: the reads wait for IoLock, which a CO write or the monitor may hold.
            var (off, fmax) = await Task.Run(() => (RyzenSmu.ReadOffsets(n), RyzenSmu.ReadFMax()));
            if (IsDisposed) return;
            for (int i = 0; i < _coSlotCount && i < off.Count; i++) SetCell(i, off[i]);
            if (fmax > 0) _fmaxBox.Value = Math.Clamp(fmax, (uint)_fmaxBox.Minimum, (uint)_fmaxBox.Maximum);
        }
        catch (Exception e)
        {
            Log.Write(L.F("{0} error: {1}", "Curve Optimizer", e.Message), "ERROR");
        }
    }

    private void OnCoSave()
    {
        new CoProfile
        {
            Offsets = _coCells.Take(_coSlotCount).Select(c => (int)c.Value).ToList(),
        }.Save();
        Log.Write(L.T("CO profile saved to co_profile.json"));
        SetStatus(L.T("CO profile saved"), Theme.Success);
    }

    private void OnCoLoad()
    {
        var p = CoProfile.Load();
        for (int i = 0; i < _coSlotCount && i < p.Offsets.Count; i++) SetCell(i, p.Offsets[i]);
        Log.Write(L.T("CO profile loaded (not applied; click Apply to confirm)"));
    }

    /// <summary>Applies FMax, PPT, EDC, and TDC limits together.</summary>
    private bool _pboApplying;

    private async void OnPboApply()
    {
        if (_pboApplying || _bootApplying || !_editorsReady) return;
        _pboApplying = true;
        try
        {
            uint fmax = (uint)_fmaxBox.Value;
            uint ppt = (uint)_pptBox.Value;
            uint edc = (uint)_edcBox.Value;
            uint tdc = (uint)_tdcBox.Value;
            uint scalar = SelectedScalar();   // Auto = 1X
            string scalarText = _scalarBox.Text;
            bool writeScalar = _scalarWritable && _scalarDirty;
            bool fmaxWritable = _fmaxWritable;
            SetStatus(L.T("Writing PBO limits..."), Theme.Warn);
            // Off the UI thread: the writes wait for IoLock, which a CO write or the monitor may hold.
            var (okF, okP, okE, okT, okS) = await Task.Run(() =>
            (
                // Zen3 has no FMax write message; sending message ID 0 would be unsafe.
                !fmaxWritable || RyzenSmu.SetFMax(fmax),
                RyzenSmu.SetPptLimit(ppt),
                RyzenSmu.SetEdcLimit(edc),
                RyzenSmu.SetTdcLimit(tdc),
                !writeScalar || RyzenSmu.SetPboScalar(scalar)
            ));
            if (writeScalar && okS) _scalarDirty = false;
            bool ok = okF && okP && okE && okT && okS;
            // Record what was written so crash recovery restores the latest limits, even when changed mid-test.
            var rec = PboJournal.ReadLast() ?? new PboSnapshot();
            if (fmaxWritable && okF) rec.FMax = fmax;
            if (okP) rec.Ppt = (int)ppt;
            if (okE) rec.Edc = (int)edc;
            if (okT) rec.Tdc = (int)tdc;
            PboJournal.Record(rec, PboJournal.ManualApply);
            string fmaxPart = fmaxWritable ? $"FMax {fmax} MHz / " : L.T("FMax skipped (unsupported) / ");
            string scalarPart = !_scalarWritable ? L.T(" / Scalar skipped (unsupported)")
                              : writeScalar ? $" / Scalar {scalarText}" : L.T(" / Scalar unchanged");
            Log.Write(L.T("Applied PBO limits: ") + $"{fmaxPart}PPT {ppt} W / EDC {edc} A / TDC {tdc} A{scalarPart}" +
                      (ok ? "" : L.T(" (some writes failed, see above)")));
            if (IsDisposed) return;
            SetStatus(ok ? L.T("PBO limits applied (FMax/PPT/EDC/TDC/Scalar)") : L.T("PBO limits partly failed"),
                      ok ? Theme.Success : Theme.Danger);
        }
        catch (Exception e)
        {
            Log.Write(L.F("{0} error: {1}", "PBO", e.Message), "ERROR");
            if (!IsDisposed) SetStatus(L.T("PBO limits partly failed"), Theme.Danger);
        }
        finally
        {
            _pboApplying = false;
        }
    }

    // Curve Shaper behavior

    private bool _csApplying;

    private async void OnCsApply()
    {
        if (_csApplying || _bootApplying || !_editorsReady) return;
        _csApplying = true;
        try
        {
            var g = CsGridFromCells();
            bool testRunning = _testTask is { IsCompleted: false };
            SetStatus(L.T("Writing Curve Shaper..."), Theme.Warn);
            // Off the UI thread: the write waits for IoLock, which a CO write or the monitor may hold.
            bool ok = await Task.Run(() => RyzenSmu.SetCurveShaper(g));
            if (ok)
            {
                // Cache applied CS values because older BIOS versions cannot read them back.
                _csState.FromGrid(g);
                _csState.Save();
                // Record what was written so crash recovery restores the latest CS, even when changed mid-test.
                CsJournal.Record(new CsSnapshot { Margins = new List<int>(_csState.Margins) }, CsJournal.ManualApply);
            }
            Log.Write(L.T("Manual Curve Shaper apply") + (_csState.ReadBroken ? L.T(" (fallback mode)") : "") +
                      (ok ? "" : L.T(" (write error, see above)")));
            if (IsDisposed) return;
            SetStatus(ok ? L.T("Curve Shaper applied") : L.T("Curve Shaper apply failed"), ok ? Theme.Success : Theme.Danger);
            // The test may have ended while the write ran.
            if (ok && testRunning && _testTask is { IsCompleted: false }) PromptRestartAfterManualChange("Curve Shaper");
        }
        catch (Exception e)
        {
            Log.Write(L.F("{0} error: {1}", "Curve Shaper", e.Message), "ERROR");
            if (!IsDisposed) SetStatus(L.T("Curve Shaper apply failed"), Theme.Danger);
        }
        finally
        {
            _csApplying = false;
        }
    }

    private int[,] CsGridFromCells()
    {
        var g = new int[5, 3];
        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
                g[t, c] = (int)_csCells[t, c].Value;
        return g;
    }

    private async Task OnCsRefresh()
    {
        try
        {
            // Off the UI thread: the read waits for IoLock, which a CO write or the monitor may hold.
            var (g, hasInterface) = await Task.Run(() => (RyzenSmu.ReadCurveShaper(out bool h), h));
            if (IsDisposed) return;

            if (!hasInterface)
            {
                // On older BIOS versions, all-zero readback does not mean the applied CS values were cleared.
                if (!_csState.ReadBroken) { _csState.ReadBroken = true; _csState.Save(); }
                _csTitleLabel.Text = L.T("Curve Shaper (fallback mode)");
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
        catch (Exception e)
        {
            Log.Write(L.F("{0} error: {1}", "Curve Shaper", e.Message), "ERROR");
        }
    }

    /// <summary>Loads saved CS values into the editor without writing hardware.</summary>
    private void OnCsLoad()
    {
        if (!_csState.HasMargins)
        {
            SetStatus(L.T("No CS profile to load"), Theme.Warn);
            return;
        }
        var g = _csState.ToGrid();
        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
            {
                var cell = _csCells[t, c];
                cell.Value = Math.Clamp(g[t, c], (int)cell.Minimum, (int)cell.Maximum);
            }
        Log.Write(L.T("CS profile loaded (not applied; click Apply to confirm)"));
        SetStatus(L.T("CS profile loaded"), Theme.Success);
    }

    /// <summary>Saves editor CS values without writing hardware.</summary>
    private void OnCsSave()
    {
        _csState.FromGrid(CsGridFromCells());
        _csState.Save();
        Log.Write(L.T("CS profile saved to cs_state.json"));
        SetStatus(L.T("CS profile saved"), Theme.Success);
    }

    /// <summary>Enables CS only on supported CPUs and loads the saved CS state; returns whether CS is supported.
    /// Runs before the first await at startup so an unsupported CPU never has CS controls enabled.</summary>
    private bool InitCsEditor()
    {
        bool ok = RyzenSmu.IsCurveShaperSupported();
        foreach (var cell in _csCells) cell.Enabled = ok;
        _csApplyBtn.Enabled = ok;
        _csRefreshBtn.Enabled = ok;
        _csSaveBtn.Enabled = ok;
        _csLoadBtn.Enabled = ok;
        _csTitleLabel.Text = ok ? "Curve Shaper" : L.T("Curve Shaper (not supported on this CPU)");
        if (ok) _csState = CsState.Load();
        return ok;
    }

    /// <summary>Populates the editor from current CPU values at startup.</summary>
    private async Task InitCoEditorAsync()
    {
        try
        {
            bool cs = InitCsEditor();
            await OnCoRefresh();
            if (cs && !IsDisposed) await OnCsRefresh();
            if (!IsDisposed) await InitPboBoxesAsync();
        }
        finally
        {
            _editorsReady = true;
        }
    }

    // Apply and Start stay blocked until the startup read has filled the editors and the FMax / Scalar write support,
    // so editor defaults are never written and Zen3 never gets an FMax write.
    private bool _editorsReady;

    /// <summary>Initializes PPT, EDC, and TDC controls from current CPU limits.</summary>
    private async Task InitPboBoxesAsync()
    {
        try
        {
            // Off the UI thread: the reads wait for IoLock, which the monitor may hold.
            var (limits, fmaxWritable, s, scalarWritable) = await Task.Run(() =>
                (RyzenSmu.ReadPboLimits(), RyzenSmu.IsFMaxWriteSupported(), RyzenSmu.ReadPboScalar(), RyzenSmu.IsPboScalarWriteSupported()));
            if (IsDisposed) return;

            var (ppt, edc, tdc) = limits;
            if (ppt > 0) _pptBox.Value = Math.Clamp(ppt, (int)_pptBox.Minimum, (int)_pptBox.Maximum);
            if (edc > 0) _edcBox.Value = Math.Clamp(edc, (int)_edcBox.Minimum, (int)_edcBox.Maximum);
            if (tdc > 0) _tdcBox.Value = Math.Clamp(tdc, (int)_tdcBox.Minimum, (int)_tdcBox.Maximum);

            // Zen3 cannot write FMax; leave the other PBO limits available.
            _fmaxWritable = fmaxWritable;
            if (!_fmaxWritable)
            {
                _fmaxBox.Enabled = false;
                Log.Write(L.T("This CPU's SMU has no FMax write command, so FMax is disabled; PPT / TDC / EDC are unaffected"));
            }

            // Scalar zero means OC mode or an unavailable reading, so show Auto.
            SelectScalar(s >= 1 ? (int)Math.Round(s) : 1);
            _scalarWritable = scalarWritable;
            if (!_scalarWritable)
            {
                _scalarBox.Enabled = false;
                Log.Write(L.T("This CPU's SMU has no PBO Scalar write command, so Scalar is disabled"));
            }
        }
        catch (Exception e)
        {
            // Write support unknown: never send FMax / Scalar.
            _fmaxWritable = false;
            _scalarWritable = false;
            Log.Write(L.F("{0} error: {1}", "PBO", e.Message), "ERROR");
            if (IsDisposed) return;
            _fmaxBox.Enabled = false;
            _scalarBox.Enabled = false;
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
        ForeColor = Theme.TextHi,
        BackColor = Theme.Surface,
        Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold),
        Padding = new Padding(0, 0, 0, 2),
    };

    private static PillButton Primary(string text) => new()
    {
        Text = text,
        Normal = Theme.Accent,
        Hover = Theme.AccentHover,
        ForeColor = Theme.AccentText,
        Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold),
        Size = new Size(140, 38),
        Radius = 8,
        BackColor = Theme.Bg,
        AutoSize = false,
    };

    private static PillButton Ghost(string text) => new()
    {
        Text = text,
        Normal = Theme.SurfaceAlt,
        Hover = Theme.SurfaceHover,
        ForeColor = Theme.TextHi,
        Font = new Font(Theme.FontFamily, 9.5F),
        Size = new Size(130, 38),
        Radius = 8,
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
            Log.Write(L.F("Failed to load the icon: {0}", e.Message), "WARN");
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
        using var path = Theme.RoundRect(new Rectangle(0, 0, Width, Height), 10);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    /// <summary>Draws a one-pixel rounded window border.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;
        using var pen = new Pen(Theme.Border);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), 10);
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

    // Test control

    private void StartTest(object? sender, EventArgs e)
    {
        // FMax support is still unknown until the startup reads finish.
        if (!_editorsReady) return;
        _startBtn.Enabled = false;

        if (!TryReadSettings(out int duration, out int singleRounds))
        {
            _startBtn.Enabled = true;
            return;
        }

        // Check for y-cruncher before starting a stress test and show placement instructions if absent.
        try
        {
            YCruncher.FindExe();
        }
        catch (FileNotFoundException ex)
        {
            ThemedDialog.Show(this, ex.Message, L.T("y-cruncher missing"));
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
        string perRound = _testMode is "SEQ" or "COMBO" ? "" : L.F(" · {0} s per round", duration);
        string scopeText = ScopeLabel(_testScope, _scopeCcd);
        SetStatus(L.F("Running · Mode {0} · Scope {1}{2}", _testMode, scopeText, perRound), Theme.Warn);

        if (sender == _startBtn) AutoStart.ResetResumeCount();   // manual start resets the auto-resume streak
        _testTask = Task.Run(RunTestThread);
    }

    private void StopTest(object? sender, EventArgs e)
    {
        _stopRequested = true;
        _restartRequested = false;
        _cts?.Cancel();
        _measureCts?.Cancel();
        SetStatus(L.T("Stopping..."), Theme.Warn);
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
        return parts.Count > 0 ? string.Join(" / ", parts) : L.T("not read");
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
            Log.Write(L.F("{0}: no PBO record to compare, PBO values not written", context));
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
            Log.Write(L.F("{0}: cannot read the current PBO values, PBO values not written", context), "WARN");
        else if (changes.Count == 0)
            {
                string pboNow = DescribePbo(now);
                Log.Write(L.F("{0}: PBO values match the record ({1}), not written", context, pboNow));
            }
        else
            {
                string changed = string.Join(L.T(", "), changes);
                Log.Write(L.F("{0}: PBO values written: {1}", context, changed) + (ok ? "" : L.T(" (some writes failed)")), ok ? "INFO" : "WARN");
            }

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
            Log.Write(L.T("Crash recovery: the last CS record cannot be read back, CS not written"));
            return;
        }
        if (ReadCsSnapshot().Margins is { } now && now.SequenceEqual(want))
        {
            Log.Write(L.T("Crash recovery: CS matches the record, not written"));
            return;
        }
        var g = new CsState { Margins = want }.ToGrid();
        bool ok = RyzenSmu.SetCurveShaper(g);
        Log.Write(ok ? L.T("Crash recovery: CS restored from the last record") : L.T("Crash recovery: CS write failed, see above"), ok ? "INFO" : "WARN");
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
            Log.Write(L.F("Test error: {0}", e.Message), "ERROR");
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
                ? L.T("Last test was interrupted with a kernel power event (likely a crash or power loss), recovering")
                : L.T("Last test did not end normally, recovering"), "WARN");

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
                Log.WriteCo(L.T("Keeping the manually applied CO:"), offsets, level: "WARN");
            }
            else
            {
                var enabled = Enumerable.Range(0, offsets.Count).Where(i => !RyzenSmu.IsSlotDisabled(i)).ToList();
                if (enabled.All(i => offsets[i] >= Config.CoMax))
                {
                    Log.Write(L.F("Crash recovery: every core is already at the +{0} CO limit (no room to back off); the cause is most likely not CO, stopping the test", Config.CoMax), "ERROR");
                    return false;
                }
                foreach (int i in enabled) offsets[i] = Math.Min(Config.CoMax, offsets[i] + Config.StepOnError);
                Log.WriteCo(L.F("Crash recovery, all cores CO +{0}:", Config.StepOnError), offsets, preRecovery, "WARN");
            }

            if (!Tuning.Apply(offsets, _testMode, seqResumePhase, "crash-recovery"))
            {
                Log.Write(L.T("Crash recovery failed to set CO"), "ERROR");
                return false;
            }
            RestoreCsAfterCrash();
            if (PboJournal.ReadLast() is { } savedPbo) RestorePbo(savedPbo, L.T("Crash recovery"));

            if (_testMode == "SEQ" && !string.IsNullOrEmpty(seqResumePhase))
                Log.Write(L.F("Resuming: sequential test from the interrupted {0} stage", seqResumePhase));
            else
                Log.Write(L.F("Resuming: {0} from round {1}", _testMode, testRound));
            Ui(() =>
            {
                SelectMode(_testMode);
                ApplySettingsToUi();
                RestyleScopeButtons();
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
            Log.WriteCo(L.T("Current CO:"), offsets);
            var pbo = ReadPboSnapshot();
            PboJournal.Record(pbo, PboJournal.TestStart);
            if (RyzenSmu.IsCurveShaperSupported()) CsJournal.Record(ReadCsSnapshot(), CsJournal.TestStart);
            Log.Write(L.T("Current PBO values: ") + DescribePbo(pbo));

            // Persist baseline offsets for recovery before the first test callback.
            Journal.Record(offsets, _testMode, null, "baseline");
        }

        // The Pi benchmark always runs on all cores
        if (_testMode == YCruncher.BenchMode && _testScope != "ALL")
        {
            {
                string ignored = ScopeLabel(_testScope, _scopeCcd);
                Log.Write(L.F("YC018 always uses all cores; the selected scope ({0}) is ignored", ignored), "WARN");
            }
            _testScope = "ALL";
            Ui(RestyleScopeButtons);
        }

        var token = _cts!.Token;

        // EACH mode repeats the full test suite on each CCD.
        // Resume at the CCD saved before interruption.
        bool eachCcd = _testScope == "EACH" && CoreTopology.CcdCount > 1;
        int scopeStart = eachCcd ? Math.Clamp(_scopeCcd, 0, CoreTopology.CcdCount - 1) : 0;
        int scopeEnd = eachCcd ? CoreTopology.CcdCount - 1 : 0;

        bool allPassed = true;
        for (int pass = scopeStart; pass <= scopeEnd; pass++)
        {
            if (_stopRequested || _restartRequested) break;
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
                Log.Write(L.F(">>> CCD{0} test done, cleaning up...", pass));
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
                    if (_stopRequested || _restartRequested) break;
                    string name = phases[pi].Test;
                    int iters = phases[pi].Rounds;

                    Log.Write(L.F("\n>>> Starting {0} ({1} rounds)", name, iters));
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
                        Log.Write(L.F("{0} not completed", name), "WARN");
                        passOk = false;
                        break;
                    }
                    testRound = 0;

                    if (pi < phases.Count - 1)
                    {
                        Log.Write(L.F(">>> {0} done, cleaning up...", name));
                        YCruncher.Kill();
                    }
                }
            }
            else if (_testMode == "COMBO")
            {
                {
                    string comboNames = string.Join("+", _runPlan.ComboTests);
                    Log.Write(L.F("\n>>> Starting combined test {0} ({1} rounds)", comboNames, _runPlan.ComboRounds));
                }
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
                if (ok) Log.Write(L.T("Combined test complete!"));
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
                if (ok) Log.Write(L.T("All tests complete!"));
                else passOk = false;
            }
            return passOk;
        }

        // Include CO applied manually during the test, which the stress loop only reads back on backoff.
        YCruncher.RefreshBaselineFromCpu(offsets, log: false);

        if (_restartRequested)
        {
            Log.WriteCo(_restartReason == "Curve Shaper" ? L.T("Curve Shaper changed manually, restarting the test. Current CO:")
                                                         : L.T("CO changed manually, restarting the test:"), offsets, level: "WARN");
            Workspace.ClearState();
            Workspace.ClearInProgress();
            return false;
        }

        // An incomplete run must not produce final results unless the user stopped it.
        if (!allPassed && !_stopRequested)
        {
            Log.WriteCo(L.T("Test not completed, no final result. Current CO:"), offsets, level: "WARN");
            Workspace.ClearState();
            Workspace.ClearInProgress(); // Clear the marker to avoid a false backoff next run.
            return false;
        }

        // A stopped run has not passed: no pass summary and no final result.
        if (_stopRequested)
        {
            Workspace.ClearState();
            Workspace.ClearInProgress();
            return true;
        }

        Log.Write("\n" + new string('=', 50));
        Log.WriteCo(L.T("Passing PBO values:"), offsets);
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
            sb.AppendLine(L.T("Finished: ") + $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(L.T("Mode: ") + _testMode);
            sb.AppendLine(L.T("Scope: ") + ScopeLabel(_testScope, _scopeCcd));
            sb.AppendLine();
            sb.AppendLine(_testMode switch
            {
                "SEQ" => L.T("Tests: sequential ") +
                         string.Join(" → ", _runPlan.Sequence.Select(st => L.F("{0} {1} rounds × {2} s", st.Test, st.Rounds, st.Seconds))),
                "COMBO" => L.T("Tests: combined ") + string.Join("+", _runPlan.ComboTests) +
                           L.F(", {0} rounds, {1} s per test per round", _runPlan.ComboRounds, _runPlan.ComboSeconds),
                _ => L.F("Time per round: {0} s", _durationSeconds) + Environment.NewLine +
                     L.F("Rounds: {0}", _singleRounds),
            });
            sb.AppendLine();
            for (int i = 0; i < offsets.Count; i++)
                sb.AppendLine(L.F("Physical core {0}: {1}", i, offsets[i]));
            File.WriteAllText(Workspace.FinalOffsets, sb.ToString(), Encoding.UTF8);
            Log.Write(L.T("Final result saved to ") + Workspace.FinalOffsets);
        }
        catch (Exception e)
        {
            Log.Write(L.F("Failed to save the result: {0}", e.Message), "WARN");
        }

        Workspace.ClearState();
        Workspace.ClearInProgress(); // Normal completion clears the marker.

        return true;
    }

    private void TestFinished()
    {
        SetStatus(L.T("Cleaning up processes..."), Theme.Warn);
        YCruncher.Kill();

        SetControlsEnabled(true);
        _stopBtn.Enabled = false;
        if (_restartRequested)
        {
            _restartRequested = false;
            StartTest(null, EventArgs.Empty);
            return;
        }
        SetStatus(L.T("Ready"), Theme.Success);
        OnTestFinishedForAutostart();

        if (AppSettings.Current.ExpertMode && !_stopRequested)
        {
            bool ok = _testCompletedNormally;
            Log.Write(ok ? L.T("Test complete!") : L.T("Test Not Completed"), ok ? "INFO" : "WARN");
            SetStatus(ok ? L.T("Test complete!") : L.T("Test Not Completed"), ok ? Theme.Success : Theme.Warn);
        }
        else if (_testCompletedNormally && !_stopRequested)
            ThemedDialog.Show(this, L.T("Test complete!"), L.T("Done"));
        else if (!_stopRequested)
            ThemedDialog.Show(this, L.T("The test did not finish and produced no final result; see the run log."), L.T("Test Not Completed"));
        else
            Log.Write(L.T("Test stopped and processes cleaned up"));
    }

    // Single Core Boost and voltage alignment

    private PillButton _scbBtn = null!;
    private PillButton _alignBtn = null!;
    private CancellationTokenSource? _measureCts;
    private Task? _measureTask;
    private readonly ResultsPanel _resultsPanel = new() { Dock = DockStyle.Fill };

    private void InitMeasureButtons()
    {
        // Width follows the translated text so longer languages are not clipped.
        PillButton MeasureBtn(string text, int minWidth)
        {
            var font = new Font(Theme.FontFamily, 8.75F, FontStyle.Bold);
            return new PillButton
            {
                Text = text,
                Size = new Size(Math.Max(minWidth, TextRenderer.MeasureText(text, font).Width + 24), 30),
                Radius = 7,
                Font = font,
                BackColor = Theme.Surface,
                Normal = Theme.AccentSoft,
                Hover = Theme.AccentSoftHover,
                Outline = Theme.Accent,
                ForeColor = Theme.TextHi,
            };
        }
        _scbBtn = MeasureBtn(L.T("Single Core Boost"), 88);
        _alignBtn = MeasureBtn(L.T("Voltage Align"), 88);
        _scbBtn.Margin = new Padding(0, 0, 8, 0);
        _alignBtn.Margin = new Padding(0);
        _tips.SetToolTip(_scbBtn, L.T("Runs a single-core load on each core and measures its frequency / effective frequency / voltage at the current CO"));
        _tips.SetToolTip(_alignBtn, L.T("Sets every core's CO to the start value (0 by default, adjustable when a raised BCLK is detected) and measures each core's frequency and voltage; per CCD, the CPPC-top core with the lowest voltage at the same frequency is the reference, and CO is lowered step by step to align voltages\nThen lowers CO per CCD to find the single-core frequency plateau, re-measures all cores and fine-tunes cores that run low; cores with computation errors are backed off and no longer lowered"));
        _scbBtn.Click += (_, _) => StartMeasure(L.T("Single Core Boost"), (win, ct) => SingleCoreBoost.Run(_monitorView, win, ct));
        _alignBtn.Click += (_, _) => OnAlignClick();
    }

    private void OnAlignClick()
    {
        if (!VoltageAlign.Confirm(this, _monitorView, out int startCo)) return;
        int editorMin = (int)_coCells[0].Minimum;
        StartMeasure(L.T("Voltage Alignment"), (win, ct) =>
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
            ThemedDialog.Show(this, L.T("The CPU does not support AVX2 / FMA, so the single-core load cannot run."), title);
            return;
        }

        var cts = new CancellationTokenSource();
        _measureCts = cts;
        var win = _resultsPanel;
        win.Begin(title);

        SetControlsEnabled(false);
        _stopBtn.Enabled = true;
        SetStatus(L.F("{0} running", title), Theme.Warn);

        _measureTask = Task.Run(() =>
        {
            bool ok = false;
            try { ok = work(win, cts.Token); }
            catch (Exception e) { Log.Write(L.F("{0} error: {1}", title, e.Message), "ERROR"); }
            finally
            {
                bool cancelled = cts.IsCancellationRequested;
                if (cancelled) Log.Write(L.F("{0} cancelled", title), "WARN");
                win.Finish(cancelled ? L.T("Cancelled") : ok ? L.T("Done") : L.T("Failed, see the run log"), ok && !cancelled);
                Ui(() =>
                {
                    SetControlsEnabled(true);
                    _stopBtn.Enabled = false;
                    SetStatus(L.T("Ready"), Theme.Success);
                });
            }
        });
    }


    private void SetStatus(string text, Color color)
    {
        _headerBar.SetStatus(text, color);
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
            duration = ReadPositiveInt(_durationBox.Text, L.T("Time per round"));
            singleRounds = ReadPositiveInt(_roundsBox.Text, L.T("Rounds"));
            return true;
        }
        catch (Exception ex)
        {
            ThemedDialog.Show(this, ex.Message, L.T("Invalid Value"));
            return false;
        }
    }

    private static int ReadPositiveInt(string raw, string label)
    {
        if (!int.TryParse(raw.Trim(), out int v))
            throw new InvalidOperationException(L.F("{0} must be a positive integer", label));
        if (v <= 0)
            throw new InvalidOperationException(L.F("{0} must be greater than 0", label));
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
        "CUSTOM" => L.T("Custom cores ") + $"[{string.Join(", ", _customCores)}]",
        "EACH" => L.T("Each CCD in turn"),
        _ => L.T("All cores"),
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

        // A BIOS write is never cut off by a user close.
        if (userClosing && ((_memOcPage?.IsWriting ?? false) || _pboBiosWriting))
        {
            ThemedDialog.Show(this, L.T("Settings are being written to the BIOS. Exit after the write finishes."), L.T("Confirm"));
            e.Cancel = true;
            return;
        }

        // A single-core measurement or voltage alignment may be writing CO: stop it first, then exit once it has ended.
        if (userClosing && _measureTask is { IsCompleted: false } measure)
        {
            e.Cancel = true;
            if (!ThemedDialog.Ask(this, L.T("A measurement is running. Stop it and exit?"), L.T("Confirm"), L.T("Exit"), L.T("Cancel")))
                return;
            _measureCts?.Cancel();
            SetStatus(L.T("Stopping..."), Theme.Danger);
            _ = measure.ContinueWith(_ => Ui(Close), TaskScheduler.Default);
            return;
        }

        if (_testTask is { IsCompleted: false })
        {
            // Do not block system shutdown with a confirmation dialog.
            if (userClosing)
            {
                if (!ThemedDialog.Ask(this, L.T("A test is running. Exit anyway?"), L.T("Confirm"), L.T("Exit"), L.T("Cancel")))
                {
                    e.Cancel = true;
                    return;
                }
            }
            _stopRequested = true;
            _cts?.Cancel();
        }
        else if (userClosing && !ConfirmExit())
        {
            e.Cancel = true;
            return;
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
