using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenHardwareMonitor.Hardware;   // GroupAffinity
using ZenStates.Core;

namespace RyzenPboStudio;

// Monitor palette is separate from the main UI theme.
internal static class MonTheme
{
    public static readonly Color Bg     = Color.FromArgb(24, 26, 30);
    public static readonly Color Panel  = Color.FromArgb(32, 35, 41);
    public static readonly Color Grid   = Color.FromArgb(48, 52, 60);
    public static readonly Color Fg     = Color.FromArgb(222, 224, 228);
    public static readonly Color Dim    = Color.FromArgb(140, 144, 150);
    public static readonly Color Gold   = Color.FromArgb(255, 196, 77);
    public static readonly Color Silver = Color.FromArgb(190, 196, 206);
}

/// <summary>Double-buffered CCD panel with a one-pixel border.</summary>
internal sealed class MonBox : Panel
{
    public MonBox()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = MonTheme.Panel;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(MonTheme.Grid);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Double-buffered label to avoid flicker during frequent updates.</summary>
internal sealed class DbLabel : Label
{
    public DbLabel() => DoubleBuffered = true;
}

/// <summary>Draws text directly so compact layouts cannot collapse nested labels.</summary>
internal sealed class StatCellControl : Control
{
    private string title;
    private readonly bool center;

    /// <summary>Writable title for switching between BCLK and BCLK/BCLK2.</summary>
    public string Title
    {
        get => title;
        set { if (title != value) { title = value; Invalidate(); } }
    }
    private readonly Font titleFont = new("Consolas", 8f);
    private readonly Font valueFont;

    public StatCellControl(string title, bool center)
    {
        this.title = title;
        this.center = center;
        valueFont = new Font("Consolas", center ? 10f : 9.5f,
            center ? FontStyle.Bold : FontStyle.Regular);
        DoubleBuffered = true;
        BackColor = MonTheme.Panel;
        ForeColor = MonTheme.Fg;
        Text = "—";
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        int width = Math.Max(0, ClientSize.Width - 16);
        var align = center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
        var common = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                     | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | align;
        TextRenderer.DrawText(e.Graphics, title, titleFont,
            new Rectangle(8, 1, width, 15), MonTheme.Dim, common);
        TextRenderer.DrawText(e.Graphics, Text, valueFont,
            new Rectangle(8, 16, width, Math.Max(0, ClientSize.Height - 17)), ForeColor, common);
        using var pen = new Pen(MonTheme.Grid);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            titleFont.Dispose();
            valueFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Polls the shared CPU only while visible.
/// Serializes CPU access under RyzenSmu.IoLock to avoid SMU mailbox contention.</summary>
internal sealed class MonitorView : UserControl
{
    private readonly Cpu cpu;
    private readonly uint cores, ccds, coresPerCcd;
    private readonly int[] slotOs; // SMU slot to OS physical-core ID; -1 if disabled.
    private readonly bool[] slotDisabled; // Fused-off SMU slots.
    private double bclk; // CPU-domain BCLK; equals BCLK2 on asynchronous systems.
    private double bclkPll; // PLL reference BCLK; a mismatch indicates asynchronous clocking.
    private readonly double p0DefMHz; // P0 frequency at a 100 MHz reference BCLK.
    private readonly double regBaseMHz; // Boot-time registry frequency already includes BCLK.
    private readonly string installedMemory;
    private readonly double p0BaseMHz; // P0 base frequency for the no-TSC FREQ fallback.
    // PM Table segment positions vary by CPU and are probed by RyzenSmu.ProbePtLayout.
    // The per-core voltage segment is shown as Core VID.
    private RyzenSmu.PtLayout? ptLayout;
    private const int PtGiveUpWindows = 6; // ~3 s of consecutive 500 ms windows before reporting a PM Table problem.
    /// <summary>Shared grid width keeps CCD panels and the limit strip aligned.</summary>
    private const int StripCols = 8;

    private Thread? worker;
    private volatile bool running;
    private volatile bool quiet, quietAck;

    /// <summary>CPPC MaximumPerformancePercent per slot; all zero when CPPC cannot rank cores.</summary>
    public uint[] CppcPerf { get; private set; } = Array.Empty<uint>();

    /// <summary>Latest resolved CPU-domain BCLK (MHz); 0 when unknown.</summary>
    public double CoreBclk => bclk;

    /// <summary>Quiet mode for Single Core Boost: the poll loop keeps updating but skips the core-bound APERF/MPERF
    /// reads that wake idle cores; EFFREQ and occupancy come from the PM table instead. Returns once a poll round has
    /// started in quiet mode (no core-bound read still in flight) or the timeout passes.</summary>
    public void BeginQuiet(int timeoutMs = 3000)
    {
        quietAck = false;
        quiet = true;
        var sw = Stopwatch.StartNew();
        while (!quietAck && worker is { IsAlive: true } && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(20);
    }

    public void EndQuiet() => quiet = false;

    private readonly HeaderBar? _header;
    private string _cpuLine = "", _gpuOsLine = "";
    private StatCellControl _bclkVal = null!, _telVidVal = null!, _vdroopVal = null!, _thmVal = null!, _tdcVal = null!, _edcVal = null!, _pptVal = null!, _fmaxVal = null!;
    private CcdView[] ccdViews = null!;

    public MonitorView(HeaderBar? header = null)
    {
        _header = header;
        cpu = RyzenSmu.SharedCpu;

        var topo = cpu.info.topology;
        cores       = topo.physicalCores > 0 ? topo.physicalCores : topo.cores;
        ccds        = topo.ccds > 0 ? topo.ccds : 1;
        coresPerCcd = cores > 0 && ccds > 0 ? Math.Max(1u, cores / ccds) : 8;
        slotOs = new int[cores];
        slotDisabled = new bool[cores];
        for (int i = 0; i < cores; i++)
        {
            slotOs[i] = RyzenSmu.SlotToOsCore(i);
            slotDisabled[i] = RyzenSmu.IsSlotDisabled(i);
        }
        try
        {
            lock (RyzenSmu.IoLock)
                p0DefMHz = ReadP0DefMHz();
        }
        catch { p0DefMHz = 0; }
        string memVendor = SystemInfo.GetMemoryVendor();
        string memSize = SystemInfo.GetInstalledMemory();
        installedMemory = memVendor.Length > 0 && memSize != "—" ? $"{memVendor} {memSize}" : memSize;

        // Registry ~MHz includes BCLK; divide by the P0 frequency at 100 MHz to infer actual BCLK.
        regBaseMHz = SystemInfo.GetBaseFrequencyMHz();
        try
        {
            lock (RyzenSmu.IoLock)
                (bclkPll, bclk) = ResolveBclk();
        }
        catch { bclkPll = bclk = 0; }
        p0BaseMHz = regBaseMHz > 0 ? regBaseMHz
                  : (p0DefMHz > 0 && bclk > 0 ? p0DefMHz * bclk / 100.0 : 0);

        BuildUi();
        FillHeader();
    }

    private const uint P0DefMsr = 0xC0010064;   // P-State 0 Definition

    /// <summary>Reads P-state 0 frequency at a 100 MHz reference BCLK.
    /// Zen5 uses fid[11:0] * 5 MHz; earlier generations use fid/dfs * 100 MHz.
    /// Returns zero when unavailable; caller holds IoLock.</summary>
    private double ReadP0DefMHz()
    {
        uint eax = 0, edx = 0;
        if (!cpu.ReadMsr(P0DefMsr, ref eax, ref edx)) return 0;
        if ((edx & 0x8000_0000) == 0) return 0;   // PstateEn=0
        if (cpu.info.family >= Cpu.Family.FAMILY_1AH)
            return (eax & 0xFFF) * 5.0;
        double fid = eax & 0xFF, dfs = (eax >> 8) & 0x3F;
        return dfs > 0 ? 25.0 * fid / (12.5 * dfs) * 100.0 : 0;
    }

    /// <summary>
    /// Returns (PLL BCLK, CPU-domain BCLK). External-clock boards may report 100 MHz from the PLL,
    /// so infer CPU BCLK from registry ~MHz / P0 and round to 0.05 MHz.
    /// Falls back to PLL; caller holds IoLock.
    /// </summary>
    private (double Pll, double Core) ResolveBclk()
    {
        double pll = 0;
        double? raw = cpu.GetBclk();
        if (raw is > 50 and < 200) pll = raw.Value;

        double core = 0;
        if (regBaseMHz > 0 && p0DefMHz > 0)
        {
            double derived = regBaseMHz / p0DefMHz * 100.0;
            if (derived is >= 90 and <= 130) core = Math.Round(Math.Round(derived / 0.05) * 0.05, 2);
        }
        if (core <= 0) core = pll;

        return (pll, core);
    }

    private void BuildUi()
    {
        BackColor = MonTheme.Bg;
        Font = new Font("Consolas", 9f);
        AutoScroll = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = MonTheme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        root.Controls.Add(BuildCcdRow(), 0, 0);
        root.Controls.Add(BuildLimitStrip(), 0, 1);
        Controls.Add(root);
    }

    /// <summary>Builds the limit strip on the same grid used by the CCD panels.
    /// Shared column rounding keeps vertical borders aligned.</summary>
    private Control BuildLimitStrip()
    {
        var t = NewStripGrid(rows: 1, topMargin: 4);
        _bclkVal   = StatCell(t, 0, "BCLK", center: true);
        _telVidVal = StatCell(t, 1, "TEL/VID", center: true);
        _vdroopVal = StatCell(t, 2, "Vdroop", center: true);
        _thmVal    = StatCell(t, 3, "THM/LIMIT", center: true);
        _tdcVal    = StatCell(t, 4, "TDC/LIMIT", center: true);
        _edcVal    = StatCell(t, 5, "EDC/LIMIT", center: true);
        _pptVal    = StatCell(t, 6, "PPT/LIMIT", center: true);
        _fmaxVal   = StatCell(t, 7, "Fmax LIMIT", center: true);
        return t;
    }

    /// <summary>Creates the equal-width grid shared by CCD panels and limits.</summary>
    private static TableLayoutPanel NewStripGrid(int rows, int topMargin = 0, int bottomMargin = 0)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = StripCols,
            RowCount = rows,
            BackColor = MonTheme.Bg,
            Margin = new Padding(0, topMargin, 0, bottomMargin),
        };
        for (int i = 0; i < StripCols; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / StripCols));
        for (int i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        return t;
    }

    /// <summary>Builds the hardware header before monitor polling starts.
    /// Polling later supplies memory clock and time values.</summary>
    private void FillHeader()
    {
        string name = string.IsNullOrWhiteSpace(cpu.info.cpuName) ? SystemInfo.GetCpuName() : cpu.info.cpuName.Trim();
        name = Regex.Replace(name, @"\s+\d+-Core Processor$", "");   // AMD Ryzen 9 9950X 16-Core Processor → AMD Ryzen 9 9950X
        _cpuLine = $"{name} Stepping {cpu.info.stepping}";

        string bios = SystemInfo.GetBiosVersion();
        string agesa = cpu.systemInfo?.AgesaVersion ?? "";
        if (agesa.Length == 0) agesa = SystemInfo.GetAgesaVersion();
        agesa = agesa.Split(' ')[^1];   // ComboAm5PI 1.2.7.0 → 1.2.7.0
        string agesaText = agesa.Length > 0 ? $"AGESA {agesa}" : "";
        string biosPart = bios.Length > 0 ? $"BIOS {bios}" + (agesaText.Length > 0 ? $" ({agesaText})" : "") : agesaText;

        string gpu = SystemInfo.GetGpuName();
        string driver = gpu == "—" ? "" : SystemInfo.GetGpuDriverVersion(gpu);
        _gpuOsLine = $"{gpu}{(driver.Length > 0 ? $" ({driver})" : "")}   {SystemInfo.GetOsVersion()}";

        _header?.SetLine(0, $"{_cpuLine}   {installedMemory}");
        _header?.SetLine(1, $"{SystemInfo.GetMotherboard()}   {biosPart}");
        _header?.SetLine(2, $"{_gpuOsLine}   {DateTime.Now:yyyy/MM/dd HH:mm}");
        UpdateBclkCell();
    }

    /// <summary>Shows one BCLK on synchronous systems, or BCLK/BCLK2 for separate PLL
    /// and CPU clock domains.</summary>
    private void UpdateBclkCell()
    {
        bool async = bclkPll > 0 && bclk > 0 && Math.Abs(bclk - bclkPll) >= 0.05;
        _bclkVal.Title = async ? "BCLK/BCLK2" : "BCLK";
        _bclkVal.Text = async ? $"{bclkPll:F2}/{bclk:F2}"
                      : bclk > 0 ? $"{bclk:F2} MHz" : "--";
    }

    private string FormatMemory(double fclk, double uclk)
    {
        string clocks = fclk > 0
            ? $"FCLK {fclk:F0} · UCLK {uclk:F0} MHz"
            : "";
        if (installedMemory == "—") return clocks.Length > 0 ? clocks : "—";
        return clocks.Length > 0 ? $"{installedMemory} · {clocks}" : installedMemory;
    }

    /// <summary>Builds a labeled value cell and returns its value label for updates.</summary>
    private static StatCellControl StatCell(TableLayoutPanel parent, int col, string title, bool center) => StatCellAt(parent, col, 0, title, center);

    private static StatCellControl StatCellAt(TableLayoutPanel parent, int col, int row, string title, bool center, int span = 1)
    {
        bool last = col + span >= parent.ColumnCount;
        var cell = new StatCellControl(title, center)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(col == 0 ? 0 : 3, row == 0 ? 0 : 3, last ? 0 : 3, 0),
        };
        parent.Controls.Add(cell, col, row);
        if (span > 1) parent.SetColumnSpan(cell, span);
        return cell;
    }

    /// <summary>Builds CCD panels and CPPC-based core ranking.</summary>
    private Control BuildCcdRow()
    {
        // CPPC_CAP1 MSR 0xC00102B0 encodes Highest in bits 31:24 and Nominal in bits 23:16.
        // Highest / Nominal * 100 matches Event 55 MaximumPerformancePercent.
        uint[] perf = new uint[cores];
        lock (RyzenSmu.IoLock)
        {
            for (int i = 0; i < cores; i++)
            {
                ulong cap1 = ReadMsr(i, 0xC00102B0);
                uint highest = (uint)((cap1 >> 24) & 0xFF);
                uint nominal = (uint)((cap1 >> 16) & 0xFF);
                // Truncate to match Event 55; rounding can differ by one.
                perf[i] = nominal > 0 ? highest * 100 / nominal : highest;
            }
        }

        // Zen3 lacks CPPC MSRs, so use ACPI _CPC values recorded in Event 55.
        if (perf.All(v => v == 0))
        {
            int tpc = (int)Math.Max(1u, cpu.info.topology.threadsPerCore);
            if (CppcReader.Read((int)cores, tpc) is { } fromLog) perf = fromLog;
        }

        // Equal CPPC values cannot rank cores; show "-" instead of assigning gold/silver markers.
        var livePerf = Enumerable.Range(0, (int)cores)
            .Where(i => slotDisabled.Length <= i || !slotDisabled[i])
            .Select(i => perf[i]).ToArray();
        if (livePerf.Distinct().Count() <= 1) Array.Clear(perf);
        CppcPerf = perf;

        // Rank ties by core index so exactly one silver and one gold marker are shown.
        int goldCore = -1, silverCore = -1;
        var ranked = Enumerable.Range(0, (int)cores)
            .Where(i => (slotDisabled.Length <= i || !slotDisabled[i]) && perf[i] > 0)
            .OrderByDescending(i => perf[i]).ThenBy(i => i)
            .ToList();
        if (ranked.Count > 0) silverCore = ranked[0];
        if (ranked.Count > 1) goldCore = ranked[1];

        int displayCcds = (int)Math.Max(2u, ccds);
        var grid = NewStripGrid(rows: 1);
        int span = Math.Max(1, StripCols / displayCcds);

        ccdViews = new CcdView[displayCcds];
        for (int c = 0; c < displayCcds; c++)
        {
            bool exists = c < ccds;
            var view = new CcdView((uint)c, coresPerCcd, goldCore, silverCore, perf, exists, slotDisabled);
            view.Container.Dock = DockStyle.Fill;
            view.Container.Margin = new Padding(c == 0 ? 0 : 3, 2, c == displayCcds - 1 ? 0 : 3, 2);
            grid.Controls.Add(view.Container, c * span, 0);
            grid.SetColumnSpan(view.Container, span);
            ccdViews[c] = view;
        }
        return grid;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) StartWorker();
        else StopWorker();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // VisibleChanged may not fire before the parent form is shown.
        // Handle creation is the reliable point to begin monitoring.
        FillHeader();
        if (Visible) StartWorker();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopWorker();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopWorker();
        base.Dispose(disposing);
    }

    private void StartWorker()
    {
        if (worker is { IsAlive: true }) return;
        running = true;
        worker = new Thread(Worker) { IsBackground = true, Name = "MonitorPoll" };
        worker.Start();
    }

    private void StopWorker()
    {
        running = false;
        var w = worker;
        worker = null;
        try { w?.Join(800); } catch { /* ignore */ }
    }

    /// <summary>Reads one SMT thread’s MSR from a physical core slot.
    /// APERF, MPERF, and TSC are thread-local, so both siblings may need sampling.</summary>
    private ulong ReadMsr(int i, uint msr, int thread = 0)
    {
        try
        {
            int os = i >= 0 && i < slotOs.Length ? slotOs[i] : i;
            if (os < 0) return 0; // Fused-off slots have no OS thread.
            int tpc = (int)Math.Max(1u, cpu.info.topology.threadsPerCore);
            if (thread < 0 || thread >= tpc) return 0;
            int logicalIndex = os * tpc + thread;
            uint eax = 0, edx = 0;
            return cpu.ReadMsrTx(msr, ref eax, ref edx, GroupAffinity.Single(0, logicalIndex))
                ? ((ulong)edx << 32) | eax
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>Reads APERF, MPERF, and TSC under one affinity binding
    /// to keep their snapshots aligned.</summary>
    private bool ReadCounters(int i, int thread, out ulong aperf, out ulong mperf, out ulong tsc)
    {
        aperf = mperf = tsc = 0;
        try
        {
            int os = i >= 0 && i < slotOs.Length ? slotOs[i] : i;
            if (os < 0) return false; // Fused-off slots have no OS thread.
            int tpc = (int)Math.Max(1u, cpu.info.topology.threadsPerCore);
            if (thread < 0 || thread >= tpc) return false;

            var prev = ThreadAffinity.Set(GroupAffinity.Single(0, os * tpc + thread));
            if (prev == GroupAffinity.Undefined) return false;
            try
            {
                uint eax = 0, edx = 0;
                if (cpu.ReadMsr(0xC00000E8, ref eax, ref edx)) aperf = ((ulong)edx << 32) | eax;
                if (cpu.ReadMsr(0xC00000E7, ref eax, ref edx)) mperf = ((ulong)edx << 32) | eax;
                if (cpu.ReadMsr(0x10,       ref eax, ref edx)) tsc   = ((ulong)edx << 32) | eax;
            }
            finally { ThreadAffinity.Set(prev); }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Logs a sustained PM Table read failure and asks the user to reinstall PawnIO.</summary>
    private void ReportPawnIoFault()
    {
        Log.Write("无法读取 PM Table，可能是 PawnIO 驱动异常", "ERROR");
        if (!IsHandleCreated) return;
        BeginInvoke(() => ThemedDialog.Show(FindForm(),
            "无法读取 CPU 遥测数据（PM Table），Core VID、频率、TEL 等读数不可用。\n\n" +
            "可能是 PawnIO 驱动异常，请尝试卸载后重新安装：\n" +
            "1. 在 Windows「设置 → 应用 → 已安装的应用」中卸载 PawnIO；\n" +
            "2. 重启电脑；\n" +
            "3. 从 https://pawnio.eu 下载最新版安装，再重新打开本程序。",
            "PawnIO 驱动异常"));
    }

    // Prefer PM Table per-core frequency, effective frequency, and C0 activity without waking idle cores.
    // If its frequency segment is absent, use HW P-state or APERF/MPERF for FREQ and APERF/TSC for EFFREQ.
    // Pinned MSR reads wake idle cores, so sample counters only at the window boundaries when needed.
    private void Worker()
    {
        int n = (int)cores;
        int tpc = (int)Math.Max(1u, cpu.info.topology.threadsPerCore);

        // Refresh CO every two seconds to reduce SMU contention; retain cached values on read failure.
        var coCache = new int[n];
        var coClock = Stopwatch.StartNew();
        long coNextMs = 0;

        // Calibrate TSC over a long window because registry ~MHz becomes stale after a BCLK change.
        int tscRefSlot = -1;
        long tscBaseTick = 0;
        ulong tscBase = 0;
        double tscMHz = 0;

        int ptFails = 0, noVoltWins = 0;
        bool ptReported = false; // Report once per session.

        while (running)
        {
            // Latched once per round so a round never mixes core-bound and PM-table-only readings
            bool q = quiet;
            if (q) quietAck = true;
            // Avoid pinned counter reads during Single Core Boost or when PM Table frequency is available.
            bool readCounters = !q && ptLayout is not { PerCoreFreqIdx: >= 0 };

            int lp = n * tpc;
            var startA = new ulong[lp]; var startM = new ulong[lp]; var startT = new ulong[lp];
            var lastA  = new ulong[lp]; var lastM  = new ulong[lp]; var lastT  = new ulong[lp];
            var startTick = new long[lp]; var lastTick = new long[lp];
            var maxVolt = new double[n];
            var psSnap = new ulong[n];
            float[]? ptSnap = null;
            double maxCpuVid = 0, maxCpuTel = 0; // Peak VID paired with TEL from the same frame.

            // Capture each SMT thread's counters at the start of the window.
            if (readCounters)
            lock (RyzenSmu.IoLock)
            {
                for (int i = 0; i < n; i++)
                    for (int t = 0; t < tpc; t++)
                    {
                        int x = i * tpc + t;
                        startTick[x] = Stopwatch.GetTimestamp();
                        ReadCounters(i, t, out startA[x], out startM[x], out startT[x]);
                    }
            }

            // One PM Table sample covers per-core voltage peaks without waking each core.
            var swWin = Stopwatch.StartNew();
            do
            {
                lock (RyzenSmu.IoLock)
                {
                    bool pmRefreshed = false;
                    try { pmRefreshed = cpu.RefreshPowerTable() == SMU.Status.OK; }
                    catch { /* PM Table unavailable in this window. */ }
                    if (pmRefreshed && cpu.powerTable?.Table is { } tbl)
                    {
                        // Keep probing incomplete PM Table layouts; fused-off or sleeping cores can hide a segment for one frame.
                        if (ptLayout is not { PerCoreVoltIdx: >= 0, PerCoreFreqIdx: >= 0 })
                            ptLayout = RyzenSmu.ProbePtLayout(tbl, n) ?? ptLayout;
                        if (ptLayout is { } lay)
                        {
                            if (lay.PerCoreVoltIdx >= 0 && tbl.Length > lay.PerCoreVoltIdx + n)
                                for (int i = 0; i < n; i++)
                                {
                                    double v = tbl[lay.PerCoreVoltIdx + i];
                                    if (v > maxVolt[i]) maxVolt[i] = v;
                                }
                            // Pair TEL with the frame containing peak VID; independent peaks can invert Vdroop.
                            if (tbl[lay.CpuVidIdx] > maxCpuVid)
                            {
                                maxCpuVid = tbl[lay.CpuVidIdx];
                                maxCpuTel = tbl[lay.CpuTelIdx];
                            }
                        }
                        ptSnap = (float[])tbl.Clone(); // Snapshot for per-core frequency outside the lock.
                    }
                }
                Thread.Sleep(40);
            } while (swWin.ElapsedMilliseconds < 500 && running);

            if (!running) break;

            // An unreadable PM Table points to the driver; a readable one without the segment to an unsupported layout.
            ptFails = ptSnap == null ? ptFails + 1 : 0;
            noVoltWins = ptSnap != null && ptLayout is not { PerCoreVoltIdx: >= 0 } ? noVoltWins + 1 : 0;
            if (ptFails == PtGiveUpWindows && !ptReported)
            {
                ptReported = true;
                ReportPawnIoFault();
            }
            if (noVoltWins == PtGiveUpWindows && !ptReported)
            {
                ptReported = true;
                Log.Write("未识别到 Core VID 数据，此 CPU 的 PM Table 布局可能不兼容", "WARN");
            }

            // Capture the matching counters at the end of the window.
            if (readCounters)
            lock (RyzenSmu.IoLock)
            {
                // Skip pinned per-core MSR reads when PM Table provides frequency; pinning wakes idle cores.
                bool needMsrFreq = ptLayout is not { PerCoreFreqIdx: >= 0 };
                for (int i = 0; i < n; i++)
                {
                    for (int t = 0; t < tpc; t++)
                    {
                        int x = i * tpc + t;
                        lastTick[x] = Stopwatch.GetTimestamp();
                        ReadCounters(i, t, out lastA[x], out lastM[x], out lastT[x]);
                    }
                    if (needMsrFreq) psSnap[i] = ReadMsr(i, 0xC0010293); // HW P-state is shared by the physical core.
                }
            }

            // Restart TSC calibration after a counter reset or a >10% registry mismatch.
            if (tscRefSlot < 0)
                for (int i = 0; i < n; i++)
                    if (startT[i * tpc] > 0 && lastT[i * tpc] > 0) { tscRefSlot = i; break; }
            if (tscRefSlot >= 0 && lastT[tscRefSlot * tpc] is var refTsc and > 0)
            {
                long refTick = lastTick[tscRefSlot * tpc];
                if (tscBase == 0 || refTsc < tscBase) { tscBase = refTsc; tscBaseTick = refTick; }
                else
                {
                    double calSec = (refTick - tscBaseTick) / (double)Stopwatch.Frequency;
                    if (calSec >= 2.0)
                    {
                        double mhz = (refTsc - tscBase) / calSec / 1e6;
                        if (p0BaseMHz <= 0 || Math.Abs(mhz - p0BaseMHz) / p0BaseMHz < 0.10) tscMHz = mhz;
                        else { tscBase = refTsc; tscBaseTick = refTick; tscMHz = 0; }
                    }
                }
            }

            var busyFreq = new double[n];
            var effFreq  = new double[n];
            var occ      = new double[n];
            // APERF and MPERF use the TSC frequency reference.
            double tscFreqMHz = tscMHz > 0 ? tscMHz : p0BaseMHz;
            double p0Hz = tscFreqMHz * 1e6;
            for (int i = 0; i < n; i++)
            {
                // Use the busiest SMT sibling or a load on the second thread appears idle.
                double bestEff = 0, bestOcc = 0;
                ulong bestA = 0, bestM = 0;
                for (int t = 0; t < tpc; t++)
                {
                    int x = i * tpc + t;
                    // Reject non-monotonic counters to avoid unsigned underflow and bogus MHz readings.
                    ulong dA = lastA[x] >= startA[x] ? lastA[x] - startA[x] : 0;
                    ulong dM = lastM[x] >= startM[x] ? lastM[x] - startM[x] : 0;
                    ulong dT = lastT[x] >= startT[x] ? lastT[x] - startT[x] : 0;
                    // Use each thread’s own sample interval; serial per-core reads skew a shared interval.
                    double sec = (lastTick[x] - startTick[x]) / (double)Stopwatch.Frequency;

                    // EFFREQ = delta APERF / delta TSC * TSC frequency, including idle time.
                    // Use wall time only when TSC is unavailable; that fallback is sensitive to sampling delay.
                    double eff = dT > 0 && tscFreqMHz > 0 ? (double)dA / dT * tscFreqMHz
                               : (sec > 0 ? dA / sec / 1e6 : 0);

                    // Activity = delta MPERF / delta TSC; use P0 frequency and wall time if TSC is unavailable.
                    double oc = dT > 0 ? Math.Min(100.0, (double)dM / dT * 100.0)
                              : (p0Hz > 0 && sec > 0 ? Math.Min(100.0, dM / (p0Hz * sec) * 100.0) : 0);

                    if (eff > bestEff) bestEff = eff;
                    if (oc > bestOcc) bestOcc = oc;
                    if (dM > bestM) { bestM = dM; bestA = dA; }
                }

                // Unknown counter values display as "-"; a PM Table zero is a valid idle reading.
                effFreq[i] = bestEff > 0 ? bestEff : double.NaN;
                occ[i] = bestOcc;

                // FREQ = delta APERF / delta MPERF * TSC frequency while active.
                // Fall back to EFFREQ if MPERF or the reference frequency is unavailable.
                busyFreq[i] = (bestM > 0 && tscFreqMHz > 0) ? (double)bestA / bestM * tscFreqMHz : bestEff;
            }

            // HW P-state is the last fallback: Zen5 uses fid * 5 MHz, earlier CPUs use fid/dfs * 100 MHz.
            // Its pinned read wakes idle cores, so it must not be the primary source.
            double bclkCorr = bclk > 0 ? bclk / 100.0 : 1.0;
            for (int i = 0; i < n; i++)
            {
                uint ps = (uint)psSnap[i];
                if (ps == 0) continue;
                double snapMHz;
                if (cpu.info.family >= Cpu.Family.FAMILY_1AH)
                    snapMHz = (ps & 0xFFF) * 5.0 * bclkCorr;
                else
                {
                    double fid = ps & 0xFF, dfs = (ps >> 8) & 0x3F;
                    snapMHz = dfs > 0 ? 25.0 * fid / (12.5 * dfs) * 100.0 * bclkCorr : 0;
                }
                if (snapMHz > 0) busyFreq[i] = snapMHz;
            }

            // PM Table frequency does not require pinning or waking idle cores.
            // The SMU reports it at 100 MHz BCLK; scale by actual BCLK.
            if (ptLayout is { PerCoreFreqIdx: >= 0 } ptl && ptSnap is { } pts && pts.Length > ptl.PerCoreFreqIdx + n)
            {
                for (int i = 0; i < n; i++)
                {
                    double mhz = pts[ptl.PerCoreFreqIdx + i] * 1000.0 * bclkCorr;
                    if (mhz > 0) busyFreq[i] = mhz;
                }

                // Without counters, read EFFREQ and C0 activity from the adjacent PM Table segments.
                if (!readCounters && pts.Length > ptl.PerCoreFreqIdx + 3 * n)
                    for (int i = 0; i < n; i++)
                    {
                        effFreq[i] = pts[ptl.PerCoreFreqIdx + n + i] * 1000.0 * bclkCorr;
                        float c0 = pts[ptl.PerCoreFreqIdx + 2 * n + i];
                        if (c0 is >= 0f and <= 100f) occ[i] = c0;
                    }
            }

            int[] co;
            float[] ccdTemp;
            float tctl;
            float pptCurrent, tdcCurrent, edcCurrent;
            int pptLimit, tdcLimit, edcLimit, thmLimit;
            uint fmaxCur;
            double fclk, uclk;
            lock (RyzenSmu.IoLock)
            {
                // CO is a set value, so reuse its cached reading between two-second refreshes.
                if (coClock.ElapsedMilliseconds >= coNextMs)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (slotDisabled[i]) continue; // Avoid mailbox reads for disabled slots.
                        uint ccd = (uint)i / coresPerCcd, core = (uint)i % coresPerCcd;
                        try
                        {
                            // Preserve the last valid CO reading instead of flashing zero on read failure.
                            int? margin = RyzenSmu.TryReadMargin(cpu, ccd, core);
                            if (margin.HasValue) coCache[i] = margin.Value;
                        }
                        catch { /* Keep the last valid value. */ }
                    }
                    coNextMs = coClock.ElapsedMilliseconds + 2000;
                }
                co = (int[])coCache.Clone();

                ccdTemp = new float[ccds];
                for (uint c = 0; c < ccds; c++)
                {
                    try { ccdTemp[c] = RyzenSmu.ReadCcdTemperature(c); }
                    catch { ccdTemp[c] = 0; }
                }
                try { tctl = cpu.GetCpuTemperature() ?? 0; }
                catch { tctl = 0; }

                pptCurrent = tdcCurrent = edcCurrent = 0;
                pptLimit = tdcLimit = edcLimit = thmLimit = 0;
                if (ptLayout is { } ptLay && cpu.powerTable?.Table is { } tbl && tbl.Length > ptLay.EdcCurrentIdx)
                {
                    pptCurrent = tbl[ptLay.PptCurrentIdx];
                    pptLimit   = (int)Math.Round(tbl[ptLay.PptLimitIdx]);
                    tdcCurrent = tbl[ptLay.TdcCurrentIdx];
                    tdcLimit   = (int)Math.Round(tbl[ptLay.TdcLimitIdx]);
                    edcCurrent = tbl[ptLay.EdcCurrentIdx];
                    edcLimit   = (int)Math.Round(tbl[ptLay.EdcLimitIdx]);
                    thmLimit   = (int)Math.Round(tbl[ptLay.ThmLimitIdx]);
                }
                else
                {
                    Cpu.SystemPowerLimit? sysLim = null;
                    try { sysLim = cpu.GetSystemPowerLimit(); } catch { /* ignore */ }
                    pptLimit = sysLim?.PowerLimit ?? 0;
                    thmLimit = sysLim?.TemperatureLimit ?? 0;
                    tdcLimit = RyzenSmu.LastTdcLimit ?? 0;
                    edcLimit = RyzenSmu.LastEdcLimit ?? 0;
                }
                try { fmaxCur = cpu.GetFMax(); } catch { fmaxCur = 0; }

                try
                {
                    var (livePll, liveCore) = ResolveBclk();
                    if (liveCore is > 50 and < 200) bclk = liveCore;
                    if (livePll is > 50 and < 200) bclkPll = livePll;
                }
                catch { /* Keep the last valid BCLK. */ }

                fclk = cpu.powerTable?.FCLK ?? 0;
                uclk = cpu.powerTable?.UCLK ?? 0;
            }

            // Read global requested VID and measured TEL from the PM Table.
            double peakVid = maxCpuVid is > 0.3 and < 2.0 ? maxCpuVid : 0;
            double telVolt = peakVid > 0 && maxCpuTel is > 0.3 and < 2.0 ? maxCpuTel : 0;
            // Show Vdroop only when VID and TEL come from the same telemetry group.
            bool vdroopValid = peakVid > 0 && telVolt > 0;

            // Without global telemetry, approximate VID from per-core peaks.
            if (peakVid <= 0)
                for (int i = 0; i < n; i++) if (maxVolt[i] > peakVid) peakVid = maxVolt[i];

            var fFreq = busyFreq; var fEff = effFreq; var fVolt = maxVolt; var fCo = co; var fOcc = occ; var fTemp = ccdTemp;
            try
            {
                if (!IsHandleCreated) continue;
                BeginInvoke(() =>
                {
                    _header?.SetLine(0, $"{_cpuLine}   {FormatMemory(fclk, uclk)}");
                    _header?.SetLine(2, $"{_gpuOsLine}   {DateTime.Now:yyyy/MM/dd HH:mm}");
                    UpdateBclkCell();
                    _telVidVal.Text = $"{(telVolt > 0 ? telVolt.ToString("F3") : "--")} / {(peakVid > 0 ? peakVid.ToString("F3") : "--")} V";
                    _vdroopVal.Text = vdroopValid ? $"{(peakVid - telVolt) / peakVid * 100.0:F1} %" : "--";
                    _thmVal.Text    = $"{(tctl > 0 ? tctl.ToString("F0") : "--")} / {(thmLimit > 0 ? thmLimit.ToString() : "--")}";
                    _tdcVal.Text    = $"{(tdcCurrent > 0 ? tdcCurrent.ToString("F0") : "--")} / {(tdcLimit > 0 ? tdcLimit.ToString() : "--")}";
                    _edcVal.Text    = $"{(edcCurrent > 0 ? edcCurrent.ToString("F0") : "--")} / {(edcLimit > 0 ? edcLimit.ToString() : "--")}";
                    _pptVal.Text    = $"{(pptCurrent > 0 ? pptCurrent.ToString("F0") : "--")} / {(pptLimit > 0 ? pptLimit.ToString() : "--")}";
                    _fmaxVal.Text   = fmaxCur > 0 ? fmaxCur.ToString() : "--";
                    for (uint c = 0; c < ccds; c++)
                    {
                        double sum = 0; int cnt = 0;
                        for (int i = 0; i < n; i++)
                            if ((uint)i / coresPerCcd == c && !slotDisabled[i]) { sum += fOcc[i]; cnt++; }
                        ccdViews[c].UpdateHeader(cnt > 0 ? sum / cnt : 0, fTemp[c]);
                        ccdViews[c].UpdateData(fFreq, fEff, fVolt, fCo, fOcc, coresPerCcd);
                    }
                });
            }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
        }
    }
}

// One CCD panel contains per-core CPPC, CO, EFFREQ, FREQ, and VID rows.
// Fill the panel width so its edges align with the limit strip.
internal sealed class CcdView
{
    private const int RowCppc = 0, RowCo = 1, RowEff = 2, RowFreq = 3, RowVid = 4;
    private const int RowH = 26, HeaderH = 28;

    public Panel Container { get; }
    private readonly Label title;
    private readonly DataGridView dgv;
    private readonly uint ccd;
    private readonly int coreCount;
    private readonly int baseCore;
    private readonly bool[] slotDisabled;

    public CcdView(uint ccd, uint coresPerCcd, int goldCore, int silverCore, uint[] perf, bool exists, bool[] slotDisabled)
    {
        this.ccd = ccd;
        coreCount = (int)coresPerCcd;
        baseCore = (int)(ccd * coresPerCcd);
        this.slotDisabled = slotDisabled;

        Container = new MonBox { Padding = new Padding(10, 6, 10, 6) };

        dgv = new DataGridView
        {
            Dock = DockStyle.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = HeaderH,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            ScrollBars = ScrollBars.None,
            BackgroundColor = MonTheme.Panel,
            BorderStyle = BorderStyle.None,
            GridColor = MonTheme.Grid,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = MonTheme.Grid,
                ForeColor = MonTheme.Fg,
                SelectionBackColor = MonTheme.Grid,
                SelectionForeColor = MonTheme.Fg,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 10f, FontStyle.Bold),
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = MonTheme.Panel,
                ForeColor = MonTheme.Fg,
                SelectionBackColor = MonTheme.Panel,
                SelectionForeColor = MonTheme.Fg,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 10f),
            },
            RowTemplate = { Height = RowH },
        };
        // Double-buffer the grid to avoid flicker during frequent refreshes.
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(dgv, true);

        var sensorCol = NewCol("Sensor", "Sensor");
        sensorCol.FillWeight = 92;
        sensorCol.DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft };
        dgv.Columns.Add(sensorCol);
        for (int k = 0; k < coreCount; k++)
        {
            int gc = baseCore + k;
            bool isGold   = exists && gc == goldCore;
            bool isSilver = exists && gc == silverCore;
            string hdr = $"C{gc:00}" + (isGold ? "★" : isSilver ? "✦" : "");
            var col = NewCol("c" + k, hdr);
            col.FillWeight = 66;
            if (isGold) col.HeaderCell.Style.ForeColor = MonTheme.Gold;
            else if (isSilver) col.HeaderCell.Style.ForeColor = MonTheme.Silver;
            bool dis = slotDisabled.Length > gc && slotDisabled[gc];
            if (dis)
            {
                col.HeaderCell.Style.ForeColor = MonTheme.Dim;
                col.DefaultCellStyle = new DataGridViewCellStyle(dgv.DefaultCellStyle) { ForeColor = MonTheme.Dim, SelectionForeColor = MonTheme.Dim };
            }
            dgv.Columns.Add(col);
        }

        string[] rowNames = { "CPPC", "AMD CO", "EFFREQ", "FREQ", "Core VID" };
        foreach (var rn in rowNames)
        {
            int r = dgv.Rows.Add();
            var c0 = dgv.Rows[r].Cells[0];
            c0.Value = rn;
            c0.Style.ForeColor = MonTheme.Dim;
            c0.Style.Alignment = DataGridViewContentAlignment.MiddleLeft;
        }

        if (exists)
            for (int k = 0; k < coreCount; k++)
            {
                int gc = baseCore + k;
                // Zero means CPPC is unavailable, not that this core ranks last.
                // Display "-" for missing rankings.
                bool noData = slotDisabled.Length > gc && slotDisabled[gc];
                uint v = perf.Length > gc ? perf[gc] : 0;
                dgv.Rows[RowCppc].Cells[k + 1].Value = noData || v == 0 ? "-" : v.ToString();
            }

        title = new DbLabel
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = MonTheme.Panel,
            ForeColor = exists ? MonTheme.Fg : MonTheme.Dim,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Font = new Font("Consolas", 11f, FontStyle.Bold),
            Text = $"CCD#{ccd}",
        };

        Container.Controls.Add(dgv);
        Container.Controls.Add(title);

        dgv.SizeChanged += (_, _) => LayoutRows();
        // SizeChanged can fire before handle creation and never repeat.
        // Recalculate row heights when the handle exists and the panel becomes visible.
        dgv.HandleCreated += (_, _) => LayoutRows();
        dgv.VisibleChanged += (_, _) => { if (dgv.Visible) LayoutRows(); };
    }

    /// <summary>Distributes available height across data rows without overlapping them.
    /// Very small windows retain the minimum row height.</summary>
    private bool layingOut;
    private void LayoutRows()
    {
        if (layingOut || !dgv.IsHandleCreated) return;
        int rowCount = dgv.Rows.Count;
        int avail = dgv.ClientSize.Height - dgv.ColumnHeadersHeight;
        if (rowCount == 0 || avail <= 0) return;
        layingOut = true;
        try
        {
            int each = Math.Max(RowH, avail / rowCount);
            int used = 0;
            for (int r = 0; r < rowCount; r++)
            {
                int h = r == rowCount - 1 ? Math.Max(RowH, avail - used) : each;
                dgv.Rows[r].Height = h;
                used += h;
            }
        }
        finally { layingOut = false; }
    }

    private static DataGridViewTextBoxColumn NewCol(string name, string header) => new()
    {
        Name = name,
        HeaderText = header,
        Resizable = DataGridViewTriState.False,
        SortMode = DataGridViewColumnSortMode.NotSortable,
    };

    public void UpdateHeader(double occ, float hot)
    {
        title.Text = $"CCD#{ccd}    占用 {occ,4:F1}%    HOT {(hot > 0 ? hot.ToString("F1") + "°C" : "--")}";
    }

    public void UpdateData(double[] freq, double[] eff, double[] volt, int[] co, double[] occ, uint coresPerCcd)
    {
        for (int k = 0; k < coreCount; k++)
        {
            int gc = baseCore + k;
            int col = k + 1;
            if (slotDisabled.Length > gc && slotDisabled[gc])
            {
                dgv.Rows[RowCo].Cells[col].Value = "-";
                dgv.Rows[RowEff].Cells[col].Value = "-";
                dgv.Rows[RowFreq].Cells[col].Value = "-";
                dgv.Rows[RowVid].Cells[col].Value = "-";
                continue;
            }
            dgv.Rows[RowCo].Cells[col].Value   = co[gc].ToString();
            dgv.Rows[RowEff].Cells[col].Value  = double.IsNaN(eff[gc]) ? "-" : eff[gc].ToString("F0"); // NaN means unavailable; zero means asleep.
            dgv.Rows[RowFreq].Cells[col].Value = freq[gc] > 0 ? freq[gc].ToString("F0") : "-";
            dgv.Rows[RowVid].Cells[col].Value  = volt[gc] > 0 ? volt[gc].ToString("F3") : "-";

            var hc = dgv.Columns[col].HeaderCell;
            Color lc = LoadHeaderColor(gc < occ.Length ? occ[gc] : 0);
            if (hc.Style.BackColor != lc) hc.Style.BackColor = lc;
        }
    }

    /// <summary>Blends the header from gray to red above 8% activity.</summary>
    private static Color LoadHeaderColor(double occ)
    {
        double t = Math.Clamp(occ / 100.0, 0, 1);
        if (t < 0.08) return MonTheme.Grid;
        var a = MonTheme.Grid;
        return Color.FromArgb(
            (int)(a.R + (198 - a.R) * t),
            (int)(a.G + (42  - a.G) * t),
            (int)(a.B + (46  - a.B) * t));
    }
}
