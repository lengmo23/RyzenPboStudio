using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using OpenHardwareMonitor.Hardware;   // GroupAffinity
using ZenStates.Core;

namespace RyzenPboStudio;

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
    private const int PtGiveUpMs = 3000; // Consecutive failing windows before reporting a PM Table problem.

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
    private readonly SideRail? _sideRail;
    private string _cpuLine = "", _boardLine = "", _biosLine = "", _gpuLine = "", _osLine = "";
    private KpiTelemetryStrip _kpiStrip = null!;
    private CcdTilePanel[] ccdPanels = null!;

    public MonitorView(HeaderBar? header = null, SideRail? sideRail = null)
    {
        _header = header;
        _sideRail = sideRail;
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
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Font = new Font(Theme.FontFamily, 9F);
        AutoScroll = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Bg,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _kpiStrip = new KpiTelemetryStrip
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
        };

        root.Controls.Add(_kpiStrip, 0, 0);
        root.Controls.Add(BuildCcdRow(), 0, 1);
        Controls.Add(root);
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
        // Drop the platform name: ComboAm5PI 1.2.7.0 → 1.2.7.0, ComboAM5PI 1.2.0.3 Patch A → 1.2.0.3 Patch A
        int sp = agesa.IndexOf(' ');
        if (sp >= 0) agesa = agesa[(sp + 1)..].Trim();
        string agesaText = agesa.Length > 0 ? $"AGESA {agesa}" : "";
        _biosLine = bios.Length > 0 ? $"BIOS {bios}" + (agesaText.Length > 0 ? $" ({agesaText})" : "") : agesaText;
        _boardLine = SystemInfo.GetMotherboard();

        string gpu = SystemInfo.GetGpuName();
        string driver = gpu == "—" ? "" : SystemInfo.GetGpuDriverVersion(gpu);
        _gpuLine = $"{gpu}{(driver.Length > 0 ? $" ({driver})" : "")}";
        _osLine = SystemInfo.GetOsVersion();

        _sideRail?.SetSpecs(_cpuLine, installedMemory, "", _boardLine, _biosLine, _gpuLine, _osLine);
        _header?.SetClock($"{DateTime.Now:yyyy/MM/dd HH:mm}");
        _kpiStrip?.UpdateBclk(bclk, bclkPll);
    }

    /// <summary>Builds CCD tile panels and CPPC-based core ranking.</summary>
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

        int displayCcds = (int)Math.Max(1u, ccds);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = displayCcds,
            RowCount = 1,
            BackColor = Theme.Bg,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int c = 0; c < displayCcds; c++)
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / displayCcds));

        ccdPanels = new CcdTilePanel[displayCcds];
        for (int c = 0; c < displayCcds; c++)
        {
            var panel = new CcdTilePanel((uint)c, coresPerCcd, goldCore, silverCore, perf, slotDisabled)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(c == 0 ? 0 : 4, 0, c == displayCcds - 1 ? 0 : 4, 0),
            };
            grid.Controls.Add(panel, c, 0);
            ccdPanels[c] = panel;
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

    /// <summary>Reads one SMT thread's MSR from a physical core slot.
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
        Log.Write(L.T("Cannot read the PM table; the PawnIO driver may be faulty"), "ERROR");
        if (!IsHandleCreated || AppSettings.Current.ExpertMode) return;
        BeginInvoke(() => ThemedDialog.Show(FindForm(),
            L.T("Cannot read CPU telemetry (PM table); Core VID, frequency, TEL and other readings are unavailable.\n\n" +
                "The PawnIO driver may be faulty. Try reinstalling it:\n" +
                "1. Uninstall PawnIO in Windows Settings → Apps → Installed apps;\n" +
                "2. Restart the PC;\n" +
                "3. Install the latest version from https://pawnio.eu and reopen this program."),
            L.T("PawnIO driver error")));
    }

    /// <summary>True when the probed layout loses no per-core segment the current layout already has.</summary>
    private static bool KeepsSegments(RyzenSmu.PtLayout probed, RyzenSmu.PtLayout? current) =>
        current == null
        || ((probed.PerCoreVoltIdx >= 0 || current.PerCoreVoltIdx < 0)
            && (probed.PerCoreFreqIdx >= 0 || current.PerCoreFreqIdx < 0));

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
            int windowMs = AppSettings.Current.MonitorIntervalMs;
            // At least three windows, so one slow read at a long interval is not reported as a driver fault.
            int giveUp = Math.Max(3, (PtGiveUpMs + windowMs - 1) / windowMs);
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
                        if (ptLayout is not { PerCoreVoltIdx: >= 0, PerCoreFreqIdx: >= 0 }
                            && RyzenSmu.ProbePtLayout(tbl, n, bclk) is { } probed && KeepsSegments(probed, ptLayout))
                            ptLayout = probed;
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
            } while (swWin.ElapsedMilliseconds < windowMs && running);

            if (!running) break;

            // An unreadable PM Table points to the driver; a readable one without the segment to an unsupported layout.
            ptFails = ptSnap == null ? ptFails + 1 : 0;
            noVoltWins = ptSnap != null && ptLayout is not { PerCoreVoltIdx: >= 0 } ? noVoltWins + 1 : 0;
            if (ptFails >= giveUp && !ptReported)
            {
                ptReported = true;
                ReportPawnIoFault();
            }
            if (noVoltWins >= giveUp && !ptReported)
            {
                ptReported = true;
                Log.Write(L.T("No Core VID data found; this CPU's PM table layout may not be supported"), "WARN");
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
                    // Use each thread's own sample interval; serial per-core reads skew a shared interval.
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
                // FREQEFF already includes the actual BCLK, unlike CORE_FREQ.
                if (!readCounters && pts.Length > ptl.PerCoreFreqIdx + 3 * n)
                    for (int i = 0; i < n; i++)
                    {
                        effFreq[i] = pts[ptl.PerCoreFreqIdx + n + i] * 1000.0;
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
            var curBclk = bclk; var curBclkPll = bclkPll;
            try
            {
                if (!IsHandleCreated || IsDisposed) continue;
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    _sideRail?.SetMemoryBus(installedMemory, fclk > 0 ? $"FCLK {fclk:F0} · UCLK {uclk:F0} MHz" : "");
                    _header?.SetClock($"{DateTime.Now:yyyy/MM/dd HH:mm}");
                    _kpiStrip.UpdateTelemetry(
                        curBclk, curBclkPll,
                        telVolt, peakVid, vdroopValid,
                        tctl, thmLimit,
                        tdcCurrent, tdcLimit,
                        edcCurrent, edcLimit,
                        pptCurrent, pptLimit,
                        fmaxCur);
                    for (uint c = 0; c < ccds; c++)
                    {
                        double sum = 0; int cnt = 0;
                        for (int i = 0; i < n; i++)
                            if ((uint)i / coresPerCcd == c && !slotDisabled[i]) { sum += fOcc[i]; cnt++; }
                        ccdPanels[c].UpdateData(cnt > 0 ? sum / cnt : 0, fTemp[c], fFreq, fEff, fVolt, fCo, fOcc);
                    }
                });
            }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }
        }
    }
}

/// <summary>Three grouped semantic telemetry capsules: Power/Thermal gauges, Voltage/Fmax, and BCLK.</summary>
internal sealed class KpiTelemetryStrip : Control
{
    private double _bclk, _bclkPll;
    private double _telVolt, _peakVid;
    private bool _vdroopValid;
    private float _tctl, _pptCur, _tdcCur, _edcCur;
    private int _thmLim, _pptLim, _tdcLim, _edcLim;
    private uint _fmax;

    private Font? _lblFont;
    private Font? _valFont;
    private Font? _pillFont;
    private Font? _heroFont;

    public KpiTelemetryStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        RebuildFonts(9F);
        Font = new Font(Theme.FontFamily, 9F);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RebuildFonts(Font.Size);
        Invalidate();
    }

    private void RebuildFonts(float basePt)
    {
        var a = _lblFont; var b = _valFont; var c = _pillFont; var d = _heroFont;
        (_lblFont, _valFont, _pillFont, _heroFont) = CreateFonts(basePt);
        a?.Dispose(); b?.Dispose(); c?.Dispose(); d?.Dispose();
    }

    private static (Font lbl, Font val, Font pill, Font hero) CreateFonts(float basePt)
    {
        float k = Math.Max(0.6f, basePt / 9f);
        return (
            new Font(Theme.FontFamily, 7.75F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 8.5F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 7.5F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 10.25F * k, FontStyle.Bold)
        );
    }

    public void UpdateBclk(double bclk, double bclkPll)
    {
        _bclk = bclk;
        _bclkPll = bclkPll;
        Invalidate();
    }

    public void UpdateTelemetry(
        double bclk, double bclkPll,
        double telVolt, double peakVid, bool vdroopValid,
        float tctl, int thmLim,
        float tdcCur, int tdcLim,
        float edcCur, int edcLim,
        float pptCur, int pptLim,
        uint fmax)
    {
        _bclk = bclk;
        _bclkPll = bclkPll;
        _telVolt = telVolt;
        _peakVid = peakVid;
        _vdroopValid = vdroopValid;
        _tctl = tctl;
        _thmLim = thmLim;
        _tdcCur = tdcCur;
        _tdcLim = tdcLim;
        _edcCur = edcCur;
        _edcLim = edcLim;
        _pptCur = pptCur;
        _pptLim = pptLim;
        _fmax = fmax;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (Width <= 30 || Height <= 12 || _lblFont == null || _valFont == null || _pillFont == null || _heroFont == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float s = Math.Max(0.7f, Height / 62f);
        int gap = Math.Max(4, (int)Math.Round(8 * s));
        int innerW = Math.Max(30, Width - gap * 2);

        int w1 = (int)Math.Round(innerW * 0.54f);
        int w2 = (int)Math.Round(innerW * 0.27f);
        int w3 = Math.Max(10, innerW - w1 - w2);

        var r1 = new Rectangle(0, 0, w1 - 1, Height - 1);
        var r2 = new Rectangle(w1 + gap, 0, w2 - 1, Height - 1);
        var r3 = new Rectangle(w1 + gap + w2 + gap, 0, w3 - 1, Height - 1);

        DrawCapsule(g, r1);
        DrawCapsule(g, r2);
        DrawCapsule(g, r3);

        PaintPowerLimits(g, r1, s);
        PaintVoltageFmax(g, r2, s);
        PaintBclk(g, r3, s);
    }

    private static void DrawCapsule(Graphics g, Rectangle r)
    {
        if (r.Width <= 2 || r.Height <= 2) return;
        using var path = Theme.RoundRect(r, 10);
        using var fill = new SolidBrush(Theme.Surface);
        using var border = new Pen(Theme.Border);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }

    private void PaintPowerLimits(Graphics g, Rectangle card, float s)
    {
        int padX = (int)Math.Round(12 * s);
        int colGap = (int)Math.Round(12 * s);
        int avail = Math.Max(20, card.Width - padX * 2 - colGap * 3);
        int colW = avail / 4;

        var items = new (string label, float cur, int lim, string unit)[]
        {
            ("PPT", _pptCur, _pptLim, "W"),
            ("TDC", _tdcCur, _tdcLim, "A"),
            ("EDC", _edcCur, _edcLim, "A"),
            ("THM", _tctl, _thmLim, "°C"),
        };

        var leftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var rightFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

        int rowY = card.Top + (int)Math.Round(12 * s);
        int rowH = (int)Math.Round(18 * s);
        int barY = card.Top + (int)Math.Round(39 * s);
        int barH = Math.Max(3, (int)Math.Round(5 * s));

        for (int i = 0; i < 4; i++)
        {
            int x = card.Left + padX + i * (colW + colGap);
            var it = items[i];

            string curStr = it.cur > 0 ? it.cur.ToString("F0") : "--";
            string limStr = it.lim > 0 ? it.lim.ToString() : "--";
            string valText = $"{curStr}/{limStr}{it.unit}";

            var lblSz = TextRenderer.MeasureText(g, it.label, _lblFont, Size.Empty, leftFlags);
            TextRenderer.DrawText(g, it.label, _lblFont, new Rectangle(x, rowY, lblSz.Width + 2, rowH), Theme.TextLo, leftFlags);
            int valX = x + lblSz.Width + 2;
            TextRenderer.DrawText(g, valText, _valFont, new Rectangle(valX, rowY, Math.Max(0, x + colW - valX), rowH), Theme.TextHi, rightFlags);

            double ratio = it.cur > 0 && it.lim > 0 ? Math.Clamp(it.cur / it.lim, 0.0, 1.0) : 0.0;
            Color barColor = ratio > 0.88 ? Theme.Danger : ratio >= 0.65 ? Theme.Warn : Theme.Telemetry;

            var trackRect = new Rectangle(x, barY, colW, barH);
            using var trackPath = Theme.RoundRect(trackRect, barH / 2);
            using var trackBrush = new SolidBrush(Theme.WellBg);
            g.FillPath(trackBrush, trackPath);

            if (ratio > 0.0)
            {
                int fillW = Math.Max(barH, (int)Math.Round(colW * ratio));
                var fillRect = new Rectangle(x, barY, Math.Min(colW, fillW), barH);
                using var fillPath = Theme.RoundRect(fillRect, barH / 2);
                using var fillBrush = new SolidBrush(barColor);
                g.FillPath(fillBrush, fillPath);
            }
        }
    }

    private void PaintVoltageFmax(Graphics g, Rectangle card, float s)
    {
        int padX = (int)Math.Round(12 * s);
        var leftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var centerFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        // Measured without EndEllipsis, which shrinks widths measured against an empty size; drawn without it so a
        // width a pixel short does not truncate a label to "T…".
        var lblFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        int topY = card.Top + (int)Math.Round(10 * s);
        int topH = (int)Math.Round(18 * s);
        int botY = card.Top + (int)Math.Round(32 * s);
        int botH = (int)Math.Round(20 * s);

        // Column widths come from fixed templates so the divider does not move as readings change; the divider sits
        // midway between the end of the TEL / VID column and the start of the Fmax column.
        var lblSz = TextRenderer.MeasureText(g, "TEL / VID", _lblFont, Size.Empty, lblFlags);
        int pillGap = (int)Math.Round(8 * s);
        int pillPad = (int)Math.Round(7 * s);
        int pillTplW = TextRenderer.MeasureText(g, "Vdroop 00.0%", _pillFont, Size.Empty, centerFlags).Width + pillPad * 2;
        int leftContentW = Math.Max(lblSz.Width + pillGap + pillTplW,
                                    TextRenderer.MeasureText(g, "0.000 / 0.000 V", _valFont, Size.Empty, lblFlags).Width);
        int rightContentW = TextRenderer.MeasureText(g, "0000 MHz", _valFont, Size.Empty, lblFlags).Width + 2;
        int leftEnd = card.Left + padX + leftContentW;
        int gap = Math.Max((int)Math.Round(8 * s), (card.Right - padX - rightContentW - leftEnd) / 2);
        int splitX = Math.Min(leftEnd + gap, card.Right - padX - rightContentW);
        int divPad = (int)Math.Round(12 * s);
        using (var div = new Pen(Theme.BorderSubtle))
            g.DrawLine(div, splitX, card.Top + divPad, splitX, card.Bottom - divPad);

        // Left: TEL / VID with the Vdroop pill right after the label
        int leftW = Math.Max(20, splitX - card.Left - padX - (int)Math.Round(4 * s));
        TextRenderer.DrawText(g, "TEL / VID", _lblFont, new Rectangle(card.Left + padX, topY, lblSz.Width + 2, topH), Theme.TextLo, lblFlags);

        string droopStr = _vdroopValid ? $"Vdroop {(_peakVid - _telVolt) / _peakVid * 100.0:F1}%" : "Vdroop --";
        var droopSz = TextRenderer.MeasureText(g, droopStr, _pillFont, Size.Empty, centerFlags);
        int pillX = card.Left + padX + lblSz.Width + pillGap;
        int pillW = Math.Min(Math.Max(20, splitX - (int)Math.Round(4 * s) - pillX), droopSz.Width + pillPad * 2);
        int pillH = Math.Max(14, (int)Math.Round(17 * s));
        var pillRect = new Rectangle(pillX, topY + (topH - pillH) / 2, pillW, pillH);
        if (pillRect.Width > 16)
        {
            using var pillPath = Theme.RoundRect(pillRect, pillH / 2);
            using var pillBg = new SolidBrush(_vdroopValid ? Theme.TelemetrySoft : Theme.WellBg);
            g.FillPath(pillBg, pillPath);
            TextRenderer.DrawText(g, droopStr, _pillFont, pillRect, _vdroopValid ? Theme.Telemetry : Theme.TextLo, centerFlags);
        }

        string voltText = $"{(_telVolt > 0 ? _telVolt.ToString("F3") : "--")} / {(_peakVid > 0 ? _peakVid.ToString("F3") : "--")} V";
        TextRenderer.DrawText(g, voltText, _valFont, new Rectangle(card.Left + padX, botY, leftW, botH), Theme.TextHi, leftFlags);

        // Right: Fmax ceiling
        int rightX = Math.Min(splitX + gap, card.Right - padX - rightContentW);
        int rightW = Math.Max(10, card.Right - padX - rightX);
        TextRenderer.DrawText(g, "Fmax", _lblFont, new Rectangle(rightX, topY, rightW, topH), Theme.TextLo, leftFlags);
        string fmaxText = _fmax > 0 ? $"{_fmax} MHz" : "--";
        TextRenderer.DrawText(g, fmaxText, _valFont, new Rectangle(rightX, botY, rightW, botH), Theme.TextHi, leftFlags);
    }

    private void PaintBclk(Graphics g, Rectangle card, float s)
    {
        bool async = _bclkPll > 0 && _bclk > 0 && Math.Abs(_bclk - _bclkPll) >= 0.05;
        string title = async ? "BCLK / BCLK2" : "BCLK";
        string val = async ? $"{_bclkPll:F2}/{_bclk:F2}"
                   : _bclk > 0 ? $"{_bclk:F2} MHz" : "--";

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
        var inner = Rectangle.Inflate(card, -(int)Math.Round(8 * s), 0);
        int topY = card.Top + (int)Math.Round(10 * s);
        int topH = (int)Math.Round(18 * s);
        int botY = card.Top + (int)Math.Round(29 * s);
        int botH = (int)Math.Round(24 * s);

        TextRenderer.DrawText(g, title, _lblFont, new Rectangle(inner.Left, topY, inner.Width, topH), Theme.TextLo, flags);
        TextRenderer.DrawText(g, val, _heroFont, new Rectangle(inner.Left, botY, inner.Width, botH), Theme.Telemetry, flags);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lblFont?.Dispose();
            _valFont?.Dispose();
            _pillFont?.Dispose();
            _heroFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Custom-painted double-buffered CCD card displaying per-core silicon telemetry tiles.</summary>
internal sealed class CcdTilePanel : Control
{
    private static readonly Color TileDisabledFill = Color.FromArgb(0x0D, 0x11, 0x18);
    private static readonly Color TileDisabledText = Color.FromArgb(0x47, 0x50, 0x62);
    private static readonly Color CoValueText = Color.FromArgb(0xFD, 0xBA, 0x74);
    private static readonly Color CoPillFill = Color.FromArgb(0x2B, 0x1D, 0x14);
    private static readonly Color CoPillStroke = Color.FromArgb(0xF9, 0x73, 0x16);

    private readonly uint _ccd;
    private readonly int _coreCount;
    private readonly int _baseCore;
    private readonly int _goldCore;
    private readonly int _silverCore;
    private readonly uint[] _perf;
    private readonly bool[] _slotDisabled;

    private double _ccdOcc;
    private float _ccdTemp;
    private double[] _freq;
    private double[] _eff;
    private double[] _volt;
    private int[] _co;
    private double[] _occ;

    private Font? _badgeFont;
    private Font? _occLblFont;
    private Font? _hdrMonoFont;
    private Font? _coreFont;
    private Font? _starFont;
    private Font? _cppcFont;
    private Font? _freqFont;
    private Font? _effTagFont;
    private Font? _effFont;
    private Font? _coFont;
    private Font? _vidFont;

    public CcdTilePanel(uint ccd, uint coresPerCcd, int goldCore, int silverCore, uint[] perf, bool[] slotDisabled)
    {
        _ccd = ccd;
        _coreCount = (int)coresPerCcd;
        _baseCore = (int)(ccd * coresPerCcd);
        _goldCore = goldCore;
        _silverCore = silverCore;
        _perf = perf;
        _slotDisabled = slotDisabled;

        _freq = Array.Empty<double>();
        _eff = Array.Empty<double>();
        _volt = Array.Empty<double>();
        _co = Array.Empty<int>();
        _occ = Array.Empty<double>();

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        RebuildFonts(9F);
        Font = new Font(Theme.FontFamily, 9F);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RebuildFonts(Font.Size);
        Invalidate();
    }

    private void RebuildFonts(float basePt)
    {
        var old = new[] { _badgeFont, _occLblFont, _hdrMonoFont, _coreFont, _starFont, _cppcFont, _freqFont, _effTagFont, _effFont, _coFont, _vidFont };
        (_badgeFont, _occLblFont, _hdrMonoFont, _coreFont, _starFont, _cppcFont, _freqFont, _effTagFont, _effFont, _coFont, _vidFont) = CreateFonts(basePt);
        foreach (var f in old) f?.Dispose();
    }

    private static (Font, Font, Font, Font, Font, Font, Font, Font, Font, Font, Font) CreateFonts(float basePt)
    {
        float k = Math.Max(0.6f, basePt / 9f);
        return (
            new Font(Theme.FontFamily, 10.0F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 9.0F * k),
            new Font(Theme.FontFamily, 10.0F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 9.0F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 8.0F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 9.0F * k),
            new Font(Theme.FontFamily, 12.0F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 6.5F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 9.0F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 10.0F * k, FontStyle.Bold),
            new Font(Theme.FontFamily, 9.0F * k)
        );
    }

    public void UpdateData(double occ, float hot, double[] freq, double[] eff, double[] volt, int[] co, double[] perCoreOcc)
    {
        _ccdOcc = occ;
        _ccdTemp = hot;
        _freq = freq;
        _eff = eff;
        _volt = volt;
        _co = co;
        _occ = perCoreOcc;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        if (Width <= 10 || Height <= 20 || _badgeFont == null || _occLblFont == null || _hdrMonoFont == null || _coreFont == null
            || _starFont == null || _cppcFont == null || _freqFont == null || _effTagFont == null
            || _effFont == null || _coFont == null || _vidFont == null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var cardRect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundRect(cardRect, 10))
        using (var bg = new SolidBrush(Theme.Surface))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
        }

        float s = Math.Max(0.7f, Height / 178f);
        int hdrH = Math.Max(26, (int)Math.Round(36 * s));
        PaintHeader(g, hdrH, s);

        using (var div = new Pen(Theme.BorderSubtle))
            g.DrawLine(div, 1, hdrH, Width - 2, hdrH);

        int padX = Math.Max(6, (int)Math.Round(9 * s));
        int padTop = Math.Max(4, (int)Math.Round(7 * s));
        int padBot = Math.Max(5, (int)Math.Round(8 * s));
        var tilesArea = new Rectangle(padX, hdrH + padTop, Math.Max(0, Width - padX * 2), Math.Max(0, Height - hdrH - padTop - padBot));
        if (tilesArea.Width <= 10 || tilesArea.Height <= 20 || _coreCount <= 0) return;

        int tileGap = Math.Max(3, (int)Math.Round(5 * s));
        int totalGaps = tileGap * (_coreCount - 1);
        int tileW = Math.Max(16, (tilesArea.Width - totalGaps) / _coreCount);
        int extra = Math.Max(0, tilesArea.Width - totalGaps - tileW * _coreCount);

        int x = tilesArea.Left;
        for (int k = 0; k < _coreCount; k++)
        {
            int w = tileW + (k < extra ? 1 : 0);
            var tileRect = new Rectangle(x, tilesArea.Top, w - 1, tilesArea.Height - 1);
            PaintCoreTile(g, tileRect, _baseCore + k, s);
            x += w + tileGap;
        }
    }

    private static readonly Color BadgeFill = Color.FromArgb(0x33, 0x17, 0x18);
    private static readonly Color BadgeStroke = Color.FromArgb(0x6B, 0x1F, 0x1C);
    private static readonly Color WarnSoft = Color.FromArgb(0x3A, 0x2E, 0x0A);
    private static readonly Color DangerSoft = Color.FromArgb(0x3A, 0x14, 0x14);

    /// <summary>Core-array badge and "CCD n" on the left; usage and HOT temperature chips on the right.</summary>
    private void PaintHeader(Graphics g, int hdrH, float s)
    {
        int padX = (int)Math.Round(12 * s);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        // Badge: one cell per core slot, two rows (four above eight cores); disabled slots are dimmed.
        int badge = Math.Max(18, (int)Math.Round(26 * s));
        var badgeRect = new Rectangle(padX, (hdrH - badge) / 2, badge, badge);
        using (var bp = Theme.RoundRect(badgeRect, (int)Math.Round(7 * s)))
        using (var bb = new SolidBrush(BadgeFill))
        using (var bs = new Pen(BadgeStroke))
        {
            g.FillPath(bb, bp);
            g.DrawPath(bs, bp);
        }
        if (_coreCount > 0)
        {
            int rows = _coreCount > 8 ? 4 : 2;
            int cols = (_coreCount + rows - 1) / rows;
            float gridW = badge * 0.62f, gridH = badge * 0.40f * rows / 2f;
            float gap = Math.Max(1f, 1.3f * s);
            float cw = (gridW - gap * (cols - 1)) / cols, ch = (gridH - gap * (rows - 1)) / rows;
            float gx = badgeRect.Left + (badge - gridW) / 2f, gy = badgeRect.Top + (badge - gridH) / 2f;
            using var on = new SolidBrush(Theme.AccentHover);
            using var off = new SolidBrush(Color.FromArgb(0x5A, 0x2A, 0x2A));
            for (int i = 0; i < _coreCount; i++)
            {
                int gc = _baseCore + i;
                bool disabled = _slotDisabled.Length > gc && _slotDisabled[gc];
                var cell = new RectangleF(gx + i % cols * (cw + gap), gy + i / cols * (ch + gap), cw, ch);
                using var cp = RoundRectF(cell, Math.Min(cw, ch) * 0.3f);
                g.FillPath(disabled ? off : on, cp);
            }
        }
        string ccdText = $"CCD {_ccd}";
        int ccdX = badgeRect.Right + (int)Math.Round(9 * s);
        var ccdSz = TextRenderer.MeasureText(g, ccdText, _badgeFont, Size.Empty, flags);
        TextRenderer.DrawText(g, ccdText, _badgeFont, new Rectangle(ccdX, 0, ccdSz.Width + 2, hdrH), Theme.TextHi, flags);

        // Chips, laid out from the right edge. Value widths come from templates so the chips do not resize as readings change.
        int chipH = Math.Max(18, (int)Math.Round(24 * s));
        int chipPad = (int)Math.Round(10 * s);
        int icon = Math.Max(10, (int)Math.Round(14 * s));
        int inGap = (int)Math.Round(6 * s);
        int chipY = (hdrH - chipH) / 2;
        int minLeft = ccdX + ccdSz.Width + (int)Math.Round(12 * s);

        Color heat = _ccdTemp <= 0 ? Theme.TextLo
                   : _ccdTemp > 82 ? Theme.Danger
                   : _ccdTemp >= 65 ? Theme.Warn
                   : Theme.Telemetry;
        Color heatSoft = _ccdTemp > 82 ? DangerSoft : _ccdTemp >= 65 ? WarnSoft : Theme.TelemetrySoft;
        string tempText = _ccdTemp > 0 ? $"{_ccdTemp:F1}°C" : "--";
        int right = Width - padX;
        var tempChip = Chip(g, right, "HOT", "000.0°C");
        if (tempChip.Left < minLeft) return;
        DrawChip(g, tempChip, heatSoft, heat, "HOT", heat, tempText, heat, (r, c) => DrawThermometer(g, r, c, s));

        double occ = Math.Clamp(_ccdOcc, 0, 100);
        string occLabel = L.T("Usage");
        var occChip = Chip(g, tempChip.Left - (int)Math.Round(8 * s), occLabel, "100.0%");
        if (occChip.Left < minLeft) return;
        Color occIcon = occ > 85 ? Theme.Accent : Theme.Telemetry;
        DrawChip(g, occChip, Theme.SurfaceAlt, occIcon, occLabel, Theme.TextMid, $"{occ:F1}%", Theme.TextHi, (r, c) => DrawActivity(g, r, c, s));

        Rectangle Chip(Graphics gr, int chipRight, string label, string valueTemplate)
        {
            int w = chipPad + icon + inGap
                  + TextRenderer.MeasureText(gr, label, _occLblFont, Size.Empty, flags).Width + inGap
                  + TextRenderer.MeasureText(gr, valueTemplate, _hdrMonoFont, Size.Empty, flags).Width + chipPad;
            return new Rectangle(chipRight - w, chipY, w, chipH);
        }

        void DrawChip(Graphics gr, Rectangle r, Color fill, Color iconColor, string label, Color labelColor, string value, Color valueColor,
                      Action<Rectangle, Color> drawIcon)
        {
            using (var cp = Theme.RoundRect(r, chipH / 2))
            using (var cb = new SolidBrush(fill))
                gr.FillPath(cb, cp);
            int x = r.Left + chipPad;
            drawIcon(new Rectangle(x, r.Top + (chipH - icon) / 2, icon, icon), iconColor);
            x += icon + inGap;
            var lblSz = TextRenderer.MeasureText(gr, label, _occLblFont, Size.Empty, flags);
            TextRenderer.DrawText(gr, label, _occLblFont, new Rectangle(x, r.Top, lblSz.Width + 1, chipH), labelColor, flags);
            x += lblSz.Width + inGap;
            TextRenderer.DrawText(gr, value, _hdrMonoFont, new Rectangle(x, r.Top, Math.Max(4, r.Right - chipPad - x), chipH), valueColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Pulse line icon for the usage chip.</summary>
    private static void DrawActivity(Graphics g, Rectangle r, Color c, float s)
    {
        float x = r.Left, y = r.Top, w = r.Width, h = r.Height;
        using var pen = new Pen(c, Math.Max(1.2f, 1.6f * s)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(x, y + h * 0.55f), new PointF(x + w * 0.25f, y + h * 0.55f), new PointF(x + w * 0.42f, y + h * 0.15f),
            new PointF(x + w * 0.60f, y + h * 0.88f), new PointF(x + w * 0.76f, y + h * 0.55f), new PointF(x + w, y + h * 0.55f),
        });
    }

    /// <summary>Thermometer icon for the HOT chip: outlined stem with a filled bulb.</summary>
    private static void DrawThermometer(Graphics g, Rectangle r, Color c, float s)
    {
        float cx = r.Left + r.Width / 2f;
        float stemW = r.Width * 0.34f, bulb = r.Width * 0.56f;
        float bulbTop = r.Bottom - bulb;
        using var pen = new Pen(c, Math.Max(1.1f, 1.4f * s));
        using var stem = RoundRectF(new RectangleF(cx - stemW / 2f, r.Top, stemW, bulbTop - r.Top + bulb * 0.3f), stemW / 2f);
        g.DrawPath(pen, stem);
        using var fill = new SolidBrush(c);
        g.FillEllipse(fill, cx - bulb / 2f, bulbTop, bulb, bulb);
        g.FillRectangle(fill, cx - stemW * 0.18f, r.Top + r.Height * 0.35f, stemW * 0.36f, bulbTop - r.Top - r.Height * 0.3f);
    }

    private static GraphicsPath RoundRectF(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(0.5f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void PaintCoreTile(Graphics g, Rectangle tile, int gc, float s)
    {
        if (tile.Width <= 4 || tile.Height <= 10) return;
        bool disabled = _slotDisabled.Length > gc && _slotDisabled[gc];
        double occ = !disabled && gc < _occ.Length ? Math.Clamp(_occ[gc], 0.0, 100.0) : 0.0;

        using var tilePath = Theme.RoundRect(tile, 7);
        using (var bg = new SolidBrush(disabled ? TileDisabledFill : Theme.WellBg))
            g.FillPath(bg, tilePath);

        if (!disabled)
        {
            // Top 3px load bar clipped inside the rounded top edge.
            int barH = Math.Max(2, (int)Math.Round(3 * s));
            var state = g.Save();
            g.SetClip(tilePath);
            using (var track = new SolidBrush(Theme.SurfaceAlt))
                g.FillRectangle(track, tile.Left, tile.Top, tile.Width + 1, barH);
            int loadW = (int)Math.Round((tile.Width + 1) * (occ / 100.0));
            if (loadW > 0)
            {
                var loadRect = new Rectangle(tile.Left, tile.Top, Math.Max(2, loadW), barH);
                using var grad = new LinearGradientBrush(
                    new Point(tile.Left, tile.Top),
                    new Point(tile.Left + tile.Width + 1, tile.Top),
                    Theme.Telemetry,
                    Theme.Accent);
                g.FillRectangle(grad, loadRect);
            }
            g.Restore(state);
        }

        Color borderColor = disabled ? Theme.BorderSubtle
                          : occ > 25 ? Color.FromArgb(110, Theme.Accent)
                          : Theme.BorderSubtle;
        using (var pen = new Pen(borderColor))
            g.DrawPath(pen, tilePath);

        int padX = tile.Width < (int)Math.Round(76 * s)
            ? Math.Max(2, (int)Math.Round(3 * s))
            : Math.Max(4, (int)Math.Round(6 * s));
        int innerW = Math.Max(4, tile.Width - padX * 2);
        var centerFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var leftFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var rightFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        string coreLabel = $"C{gc:00}";
        if (disabled)
        {
            var r1 = new Rectangle(tile.Left + padX, tile.Top + (int)Math.Round(tile.Height * 0.05f), innerW, (int)Math.Round(tile.Height * 0.16f));
            TextRenderer.DrawText(g, coreLabel, _coreFont, r1, TileDisabledText, leftFlags);
            TextRenderer.DrawText(g, "—", _freqFont, tile, TileDisabledText, centerFlags);
            return;
        }

        // Row 1: Core ID + Gold/Silver star on left, CPPC score on right.
        var row1 = new Rectangle(tile.Left + padX, tile.Top + (int)Math.Round(tile.Height * 0.05f), innerW, Math.Max(12, (int)Math.Round(tile.Height * 0.15f)));
        var coreSz = TextRenderer.MeasureText(g, coreLabel, _coreFont, Size.Empty, leftFlags);
        TextRenderer.DrawText(g, coreLabel, _coreFont, new Rectangle(row1.Left, row1.Top, coreSz.Width, row1.Height), Theme.TextMid, leftFlags | TextFormatFlags.NoClipping);

        bool isGold = gc == _goldCore;
        bool isSilver = gc == _silverCore;
        int starRight = row1.Left + coreSz.Width;
        if (isGold || isSilver)
        {
            string star = isGold ? "★" : "✦";
            Color starColor = isGold ? Theme.Gold : Theme.Silver;
            var starSz = TextRenderer.MeasureText(g, star, _starFont, Size.Empty, leftFlags);
            TextRenderer.DrawText(g, star, _starFont, new Rectangle(starRight, row1.Top, starSz.Width + 2, row1.Height), starColor, leftFlags);
            starRight += starSz.Width;
        }

        uint cppcVal = _perf.Length > gc ? _perf[gc] : 0;
        string cppcText = cppcVal > 0 ? cppcVal.ToString() : "-";
        int cppcLeft = Math.Max(starRight + 1, row1.Left);
        if (row1.Right - cppcLeft >= 10)
            TextRenderer.DrawText(g, cppcText, _cppcFont, new Rectangle(cppcLeft, row1.Top, row1.Right - cppcLeft, row1.Height), Theme.TextLo, rightFlags | TextFormatFlags.NoClipping);

        // Row 2: Hero FREQ
        double freq = gc < _freq.Length ? _freq[gc] : 0;
        string freqText = freq > 0 ? freq.ToString("F0") : "-";
        var row2 = new Rectangle(tile.Left + 2, tile.Top + (int)Math.Round(tile.Height * 0.21f), tile.Width - 4, Math.Max(16, (int)Math.Round(tile.Height * 0.23f)));
        TextRenderer.DrawText(g, freqText, _freqFont, row2, freq > 0 ? Theme.TextHi : Theme.TextLo, centerFlags);

        // Row 3: EFF + effective frequency
        double eff = gc < _eff.Length ? _eff[gc] : double.NaN;
        bool hasEff = !double.IsNaN(eff);
        string effText = hasEff ? eff.ToString("F0") : "-";
        // EFF and CO rows share this inset so their labels and values line up inside the CO pill.
        int textInset = Math.Max(2, (int)Math.Round(3 * s));
        var row3 = new Rectangle(tile.Left + padX + textInset, tile.Top + (int)Math.Round(tile.Height * 0.45f), innerW - textInset * 2, Math.Max(13, (int)Math.Round(tile.Height * 0.16f)));
        var effTagSz = TextRenderer.MeasureText(g, "EFF", _effTagFont, Size.Empty, leftFlags);
        TextRenderer.DrawText(g, "EFF", _effTagFont, new Rectangle(row3.Left, row3.Top, effTagSz.Width + 1, row3.Height), Theme.TextLo, leftFlags);
        int effValX = row3.Left + effTagSz.Width + 2;
        TextRenderer.DrawText(g, effText, _effFont, new Rectangle(effValX, row3.Top, Math.Max(4, row3.Right - effValX), row3.Height),
            hasEff && eff > 0 ? Theme.Telemetry : Theme.TextLo, rightFlags | TextFormatFlags.NoClipping);

        // Row 4: CO offset pill
        int co = gc < _co.Length ? _co[gc] : 0;
        const string coLabel = "CO";
        string coValue = co > 0 ? $"+{co}" : $"{co}";
        int pillH = Math.Max(14, (int)Math.Round(tile.Height * 0.145f));
        int pillY = tile.Top + (int)Math.Round(tile.Height * 0.64f);
        var coRect = new Rectangle(tile.Left + padX, pillY, innerW, pillH);
        using (var cp = Theme.RoundRect(coRect, 4))
        {
            using (var cb = new SolidBrush(CoPillFill))
                g.FillPath(cb, cp);
            using var cpen = new Pen(CoPillStroke);
            g.DrawPath(cpen, cp);
        }
        // Label left (EFF tag font), larger value right; NoClipping keeps the taller value glyphs inside a short pill.
        var coText = new Rectangle(row3.Left, coRect.Top, row3.Width, coRect.Height);
        Color coColor = CoValueText;
        var coTagSz = TextRenderer.MeasureText(g, coLabel, _effTagFont, Size.Empty, leftFlags);
        TextRenderer.DrawText(g, coLabel, _effTagFont, new Rectangle(coText.Left, coText.Top, coTagSz.Width + 1, coText.Height), coColor, leftFlags);
        int coValX = coText.Left + coTagSz.Width + 2;
        TextRenderer.DrawText(g, coValue, _coFont, new Rectangle(coValX, coText.Top, Math.Max(4, coText.Right - coValX), coText.Height),
            coColor, rightFlags | TextFormatFlags.NoClipping);

        // Row 5: Core VID
        double vid = gc < _volt.Length ? _volt[gc] : 0;
        string vidText = vid > 0 ? $"{vid:F3}V" : "-";
        var row5 = new Rectangle(tile.Left + 2, tile.Top + (int)Math.Round(tile.Height * 0.81f), tile.Width - 4, Math.Max(12, (int)Math.Round(tile.Height * 0.16f)));
        TextRenderer.DrawText(g, vidText, _vidFont, row5, vid > 0 ? Theme.TextMid : Theme.TextLo, centerFlags | TextFormatFlags.NoClipping);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _badgeFont?.Dispose();
            _occLblFont?.Dispose();
            _hdrMonoFont?.Dispose();
            _coreFont?.Dispose();
            _starFont?.Dispose();
            _cppcFont?.Dispose();
            _freqFont?.Dispose();
            _effTagFont?.Dispose();
            _effFont?.Dispose();
            _coFont?.Dispose();
            _vidFont?.Dispose();
        }
        base.Dispose(disposing);
    }
}
