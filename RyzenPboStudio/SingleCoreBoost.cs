using System.Diagnostics;
using System.Text;
using OpenHardwareMonitor.Hardware;   // GroupAffinity / ThreadAffinity
using ZenStates.Core;

namespace RyzenPboStudio;

/// <summary>Single Core Boost reading of one core. Telemetry values are medians over the sample window: background
/// threads briefly wake other cores and spike the global rail, and a median ignores such sparse spikes.</summary>
internal sealed record ScbRow(
    int Slot, int OsCore, int Ccd,
    double Freq, double EffFreq, double Vid, double Tel, double CoreVid, double Temp, double Power,
    double Occ, int BusiestSlot, long Errors, int Frames, bool Valid)
{
    /// <summary>Voltage compared by voltage alignment: the core's own voltage from the PM table per-core segment,
    /// or the global VDDCR_CPU VID when that segment was not found (CoreVid = 0).</summary>
    public double AlignV => CoreVid > 0 ? CoreVid : Vid;
}

/// <summary>Single Core Boost: runs the synthetic load on one physical core at a time and records what that core
/// reaches under the current CO. It is a measurement, not a stress test: it never writes CO and does not touch the
/// crash-recovery flag. All SMU / MSR access is serialised through RyzenSmu.IoLock, and the monitor runs in quiet
/// mode for the whole session: it keeps updating from the PM table but skips the per-core MSR reads that wake idle cores.</summary>
internal sealed class SingleCoreBoost : IDisposable
{
    /// <summary>Brute-force share of the synthetic load; 25% matched R23's effective load on a 9950X (ratio 1.009).</summary>
    public const int LoadHeavyPct = 25;

    // About 3.3 s per core, so 16 cores finish within a minute. Every core gets the same timing, so the
    // short settle biases all cores alike and the per-core comparison used for alignment still holds.
    private const int ReadyTimeoutMs = 40_000;
    private const int ReadyPollMs = 100;
    private const int SettleMs = 1_000;   // after readiness, let the clock settle before sampling
    private const int SampleMs = 2_000;   // ~20 frames for the medians
    private const int FrameMs = 100;

    /// <summary>Below this occupancy the load did not land on the target core.</summary>
    private const double MinOcc = 85.0;

    /// <summary>Per-core power floor for "this core runs the load". Occupancy alone passes a thread that holds the
    /// core without doing work (9950X: idle core ~2.2 W, single-core load above 12 W).</summary>
    private const double MinLoadW = 5.0;

    /// <summary>Rough per-core duration (ready + settle + sample), for time estimates in the UI.</summary>
    public const int SecondsPerCore = 4;

    public static readonly string[] Columns =
        { "Core", "Freq", "EffFreq", "VID", "TEL", "CoreV", "Temp", "Power" };

    private static double? tscMHzCache;

    private readonly Cpu cpu;
    private readonly MonitorView? monitor;
    private readonly int[] slotOs, slotLogical;
    private readonly bool[] slotDisabled;
    private readonly int[] ccdRank;
    private readonly double bclk;
    private readonly double refMHz;
    private RyzenSmu.PtLayout? layout;
    private int powIdx = -1, c0Idx = -1;
    private SyntheticLoad? load;

    public int SlotCount { get; }
    public int CoresPerCcd { get; }

    /// <summary>CPPC MaximumPerformancePercent per slot; 0 when CPPC cannot rank cores.</summary>
    public uint[] Cppc { get; }

    public static bool IsSupported => SyntheticLoad.IsSupported;

    private SingleCoreBoost(MonitorView? monitor)
    {
        this.monitor = monitor;
        cpu = RyzenSmu.SharedCpu;
        SlotCount = RyzenSmu.SlotCount;

        lock (RyzenSmu.IoLock)
        {
            // Same slot model as RyzenSmu: slot = ccd × coresPerCcd + index, fused-off slots included
            var topo = cpu.info.topology;
            uint per = topo.physicalCores > 0 && topo.ccds > 0 ? topo.physicalCores / topo.ccds : Math.Max(1u, topo.coresPerCcx);
            CoresPerCcd = per > 0 ? (int)per : 8;
        }

        slotOs = new int[SlotCount];
        slotLogical = new int[SlotCount];
        slotDisabled = new bool[SlotCount];
        for (int s = 0; s < SlotCount; s++)
        {
            slotDisabled[s] = RyzenSmu.IsSlotDisabled(s);
            slotOs[s] = slotDisabled[s] ? -1 : RyzenSmu.SlotToOsCore(s);
            // Pin to the first SMT thread only: a whole-core mask lets Windows bounce the thread between siblings
            slotLogical[s] = slotOs[s] >= 0
                ? CoreTopology.LogicalCoresOfPhysical(new[] { slotOs[s] }).DefaultIfEmpty(-1).Min()
                : -1;
        }

        var perf = monitor?.CppcPerf ?? Array.Empty<uint>();
        Cppc = Enumerable.Range(0, SlotCount).Select(s => s < perf.Length && !slotDisabled[s] ? perf[s] : 0u).ToArray();
        ccdRank = new int[SlotCount];
        foreach (var g in Enumerable.Range(0, SlotCount).Where(IsUsable).GroupBy(CcdOf))
        {
            var ranked = g.Where(s => Cppc[s] > 0).OrderByDescending(s => Cppc[s]).ThenBy(s => s).ToList();
            if (ranked.Select(s => Cppc[s]).Distinct().Count() <= 1) continue;   // all equal: no ranking
            for (int i = 0; i < ranked.Count; i++) ccdRank[ranked[i]] = i + 1;
        }

        bclk = ResolveBclk(monitor);

        tscMHzCache ??= CalibrateTsc();
        refMHz = tscMHzCache > 0 ? tscMHzCache.Value : SystemInfo.GetBaseFrequencyMHz();
    }

    /// <summary>CPU-domain BCLK in MHz: the monitor's value, then the PLL; 100 when none is sane.</summary>
    public static double ResolveBclk(MonitorView? monitor)
    {
        double b = monitor?.CoreBclk ?? 0;
        if (b is not (> 50 and < 200))
        {
            try { lock (RyzenSmu.IoLock) b = RyzenSmu.SharedCpu.GetBclk() ?? 0; } catch { b = 0; }
        }
        return b is > 50 and < 200 ? b : 100;
    }

    /// <summary>CPU-domain BCLK used to scale the PM table's per-core clocks.</summary>
    public double Bclk => bclk;

    /// <summary>Puts the monitor in quiet mode and probes the PM table. Returns null (reason logged) when sampling is impossible.</summary>
    public static SingleCoreBoost? Open(MonitorView? monitor)
    {
        if (!IsSupported)
        {
            Log.Write("CPU 不支持 AVX2 / FMA，无法运行 Single Core Boost", "ERROR");
            return null;
        }

        monitor?.BeginQuiet();
        SingleCoreBoost? scb = null;
        try
        {
            scb = new SingleCoreBoost(monitor);
            for (int k = 0; k < 5 && !scb.ProbeLayout(); k++) Thread.Sleep(300);
            if (scb.layout == null)
            {
                Log.Write("PM Table 读取失败，无法测量", "ERROR");
                scb.Dispose();
                return null;
            }
            if (scb.powIdx < 0)
                Log.Write("未读到每核功耗，改为固定等待", "WARN");
            return scb;
        }
        catch (Exception e)
        {
            Log.Write($"单核测量初始化失败：{e.Message}", "ERROR");
            if (scb != null) scb.Dispose();
            else monitor?.EndQuiet();
            return null;
        }
    }

    public bool IsUsable(int slot) =>
        slot >= 0 && slot < SlotCount && !slotDisabled[slot] && slotLogical[slot] is >= 0 and < 64;

    public int CcdOf(int slot) => slot / CoresPerCcd;

    /// <summary>CPPC rank within the CCD (1 = silver core, 2 = gold core); zero when unavailable.
    /// The two top cores usually share one CPPC value, so ties go by core index.</summary>
    public int CcdRank(int slot) => slot >= 0 && slot < SlotCount ? ccdRank[slot] : 0;

    private ulong Mask(int slot) => 1UL << slotLogical[slot];

    /// <summary>Measures the given slots in order. Unusable slots are skipped; on cancel the partial row is dropped.</summary>
    public List<ScbRow> Measure(IEnumerable<int> slots, Action<int>? onCore, Action<ScbRow>? onRow, CancellationToken ct)
    {
        var list = slots.Where(IsUsable).ToList();
        var rows = new List<ScbRow>();
        if (list.Count == 0 || ct.IsCancellationRequested) return rows;

        load ??= new SyntheticLoad(Mask(list[0]), LoadHeavyPct);
        foreach (int slot in list)
        {
            if (ct.IsCancellationRequested) break;
            onCore?.Invoke(slot);
            load.SetTarget(Mask(slot));

            ScbRow? r = null;
            for (int attempt = 0; attempt < 2 && !ct.IsCancellationRequested; attempt++)
            {
                if (!WaitForLoad(slot, ct) && !ct.IsCancellationRequested)
                    Log.Write($"Core {slot}：负载就绪超时", "WARN");
                long err0 = load.Errors;
                if (ct.WaitHandle.WaitOne(SettleMs)) break;
                r = Sample(slot, ct);
                if (ct.IsCancellationRequested) { r = null; break; }
                r = r with { Errors = load.Errors - err0 };
                if (r.Valid || attempt == 1) break;
                Log.Write($"Core {slot}：采样无效，重测", "WARN");
            }
            if (r == null) break;

            rows.Add(r);
            onRow?.Invoke(r);
        }
        return rows;
    }

    private float[]? ReadTable()
    {
        lock (RyzenSmu.IoLock)
        {
            try
            {
                if (cpu.RefreshPowerTable() != SMU.Status.OK) return null;
                var t = cpu.powerTable?.Table;
                return t is { Length: > 0 } && !Utils.AllZero(t) ? (float[])t.Clone() : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Probes the PM table via RyzenSmu.ProbePtLayout and derives the per-core power segment (directly before
    /// per-core voltage) and C0 segment (two segments after per-core frequency), as measured on Granite Ridge.</summary>
    private bool ProbeLayout()
    {
        var t = ReadTable();
        if (t == null) return false;
        var lay = RyzenSmu.ProbePtLayout(t, SlotCount);
        if (lay == null) return false;
        if (layout is { PerCoreVoltIdx: >= 0 } && lay.PerCoreVoltIdx < 0) return true;   // keep the complete layout

        layout = lay;
        powIdx = lay.PerCoreVoltIdx >= 0 ? Segment(t, lay.PerCoreVoltIdx - SlotCount, 0f, 120f) : -1;
        c0Idx = lay.PerCoreFreqIdx >= 0 ? Segment(t, lay.PerCoreFreqIdx + 2 * SlotCount, 0f, 100f) : -1;
        return true;
    }

    /// <summary>Returns start if it holds a slot-long segment within [lo, hi] (fused-off slots ignored), else -1.</summary>
    private int Segment(float[] t, int start, float lo, float hi)
    {
        if (start < 0 || start + SlotCount > t.Length) return -1;
        for (int k = 0; k < SlotCount; k++)
            if (!slotDisabled[k] && (t[start + k] < lo || t[start + k] > hi)) return -1;
        return start;
    }

    /// <summary>Waits until the target core is actually loaded; moving the thread takes a scheduler tick or two.</summary>
    private bool WaitForLoad(int slot, CancellationToken ct)
    {
        if (powIdx < 0) ProbeLayout();
        if (powIdx < 0) return !ct.WaitHandle.WaitOne(1000);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ReadyTimeoutMs && !ct.IsCancellationRequested)
        {
            if (ReadTable() is { } t && IsLoaded(t, slot)) return true;
            if (ct.WaitHandle.WaitOne(ReadyPollMs)) return false;
        }
        return false;
    }

    /// <summary>Loaded = power above the floor and at least twice any other core, which rejects a busy machine.</summary>
    private bool IsLoaded(float[] t, int slot)
    {
        if (powIdx < 0 || t.Length < powIdx + SlotCount) return false;
        double me = t[powIdx + slot], other = 0;
        for (int k = 0; k < SlotCount; k++)
            if (k != slot && !slotDisabled[k] && t[powIdx + k] > other) other = t[powIdx + k];
        return me >= MinLoadW && me >= other * 2;
    }

    /// <summary>Reads APERF / MPERF / TSC in one affinity binding so the three form one snapshot.</summary>
    private bool ReadCounters(int slot, out ulong aperf, out ulong mperf, out ulong tsc)
    {
        aperf = mperf = tsc = 0;
        lock (RyzenSmu.IoLock)
        {
            try
            {
                var prev = ThreadAffinity.Set(GroupAffinity.Single(0, slotLogical[slot]));
                if (prev == GroupAffinity.Undefined) return false;
                try
                {
                    uint eax = 0, edx = 0;
                    if (cpu.ReadMsr(0xC00000E8, ref eax, ref edx)) aperf = ((ulong)edx << 32) | eax;
                    if (cpu.ReadMsr(0xC00000E7, ref eax, ref edx)) mperf = ((ulong)edx << 32) | eax;
                    if (cpu.ReadMsr(0x10, ref eax, ref edx)) tsc = ((ulong)edx << 32) | eax;
                }
                finally { ThreadAffinity.Set(prev); }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>TSC rate from an idle reference core; 0 when TSC is unreadable (PawnIO returns 0 on some machines),
    /// in which case the registry ~MHz serves as the MPERF rate.</summary>
    private double CalibrateTsc()
    {
        int refSlot = Enumerable.Range(0, SlotCount).FirstOrDefault(IsUsable, -1);
        if (refSlot < 0) return 0;
        if (!ReadCounters(refSlot, out _, out _, out ulong t0) || t0 == 0) return 0;
        long tick0 = Stopwatch.GetTimestamp();
        Thread.Sleep(3000);
        if (!ReadCounters(refSlot, out _, out _, out ulong t1) || t1 <= t0) return 0;
        double sec = (Stopwatch.GetTimestamp() - tick0) / (double)Stopwatch.Frequency;
        return sec > 0 ? (t1 - t0) / sec / 1e6 : 0;
    }

    /// <summary>Samples slot for SampleMs. Frequency follows the main program's priority: PM table CORE_FREQ × BCLK/100,
    /// then ΔAPERF/ΔMPERF on the loaded core (already awake, no observer effect).</summary>
    private ScbRow Sample(int slot, CancellationToken ct)
    {
        int n = SlotCount, os = slotOs[slot];
        double ghzToMhz = 1000.0 * bclk / 100.0;   // SMU reports per-core clocks against a 100 MHz reference

        // Counters only at the window edges: binding repeatedly would wake cores and disturb the telemetry
        ReadCounters(slot, out ulong a0, out ulong m0, out ulong t0);
        long tick0 = Stopwatch.GetTimestamp();

        var vids = new List<double>(); var tels = new List<double>(); var coreVs = new List<double>(); var temps = new List<double>();
        var pows = new List<double>(); var freqs = new List<double>(); var effs = new List<double>();
        var c0Self = new List<double>();
        var c0Sum = new double[n];

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < SampleMs && !ct.IsCancellationRequested)
        {
            if (ReadTable() is { } t && layout is { } lay)
            {
                if (t.Length > lay.CpuTelIdx)
                {
                    vids.Add(t[lay.CpuVidIdx]);
                    tels.Add(t[lay.CpuTelIdx]);
                }
                if (lay.PerCoreVoltIdx >= 0 && t.Length > lay.PerCoreVoltIdx + n + slot)
                {
                    coreVs.Add(t[lay.PerCoreVoltIdx + slot]);
                    temps.Add(t[lay.PerCoreVoltIdx + n + slot]);
                }
                if (powIdx >= 0 && t.Length > powIdx + slot)
                    pows.Add(t[powIdx + slot]);
                if (lay.PerCoreFreqIdx >= 0 && t.Length > lay.PerCoreFreqIdx + n + slot)
                {
                    freqs.Add(t[lay.PerCoreFreqIdx + slot] * ghzToMhz);
                    effs.Add(t[lay.PerCoreFreqIdx + n + slot] * ghzToMhz);
                }
                if (c0Idx >= 0 && t.Length >= c0Idx + n)
                {
                    for (int k = 0; k < n; k++) c0Sum[k] += t[c0Idx + k];
                    c0Self.Add(t[c0Idx + slot]);
                }
            }
            ct.WaitHandle.WaitOne(FrameMs);
        }

        ReadCounters(slot, out ulong a1, out ulong m1, out ulong t1);
        double sec = (Stopwatch.GetTimestamp() - tick0) / (double)Stopwatch.Frequency;

        // A failed MSR read returns 0; only monotonic deltas are used
        ulong dA = a1 >= a0 ? a1 - a0 : 0, dM = m1 >= m0 ? m1 - m0 : 0, dT = t1 >= t0 ? t1 - t0 : 0;
        double counterFreq = dM > 0 && refMHz > 0 ? (double)dA / dM * refMHz : 0;
        double occ = dT > 0 ? Math.Min(100.0, (double)dM / dT * 100.0)
                   : (refMHz > 0 && sec > 0 ? Math.Min(100.0, dM / (refMHz * 1e6 * sec) * 100.0) : 0);
        if (dM == 0 && c0Self.Count > 0) occ = Median(c0Self);

        double freq = freqs.Count > 0 ? Median(freqs) : counterFreq;
        double eff = effs.Count > 0 ? Median(effs) : (sec > 0 ? dA / sec / 1e6 : 0);

        double temp = Median(temps);
        if (temps.Count == 0)
        {
            try { lock (RyzenSmu.IoLock) temp = cpu.GetCpuTemperature() ?? 0; } catch { temp = 0; }
        }

        int busiest = -1;
        for (int k = 0; k < n; k++)
            if (c0Sum[k] > 0 && (busiest < 0 || c0Sum[k] > c0Sum[busiest])) busiest = k;

        // Full occupancy does not prove the load landed; also require per-core power when it is available
        double pow = Median(pows);
        bool loaded = pows.Count == 0 || pow >= MinLoadW;
        int frames = vids.Count;

        return new ScbRow(slot, os, CcdOf(slot), freq, eff, Median(vids), Median(tels), Median(coreVs), temp, pow,
                          occ, busiest, 0, frames, frames > 0 && occ >= MinOcc && loaded);
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return 0;
        var a = v.ToArray();
        Array.Sort(a);
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2.0;
    }

    private static string InvalidReason(ScbRow r) =>
        r.BusiestSlot >= 0 && r.BusiestSlot != r.Slot ? $"负载在 Core {r.BusiestSlot}"
        : r.Occ < MinOcc ? $"占用 {r.Occ:F0}%"
        : $"功耗 {r.Power:F1} W";

    public static string RankLabel(int rank) => rank switch { 1 or 2 => "#1", > 0 => $"#{rank}", _ => "-" };

    /// <summary>Table row; invalid rows and computation errors show only through RowColor, details go to the log.</summary>
    public string[] Cells(ScbRow r) => new[]
    {
        r.Slot.ToString(),
        $"{r.Freq:F0}",
        $"{r.EffFreq:F0}",
        $"{r.Vid:F3}",
        $"{r.Tel:F3}",
        r.CoreVid > 0 ? $"{r.CoreVid:F3}" : "-",
        $"{r.Temp:F1}",
        $"{r.Power:F1}",
    };

    public static Color? RowColor(ScbRow r) => !r.Valid ? Theme.TextLo : r.Errors > 0 ? Theme.Warn : null;

    /// <summary>One data line per core; the pass it belongs to is logged once before the rows.</summary>
    public void LogRow(ScbRow r)
    {
        var sb = new StringBuilder($"CCD{r.Ccd} Core {r.Slot,-2} {RankLabel(CcdRank(r.Slot)),-5} " +
                                   $"FREQ {r.Freq,5:F0}  EFF {r.EffFreq,5:F0}  VID {r.Vid:F3}  TEL {r.Tel:F3}  CORE V {r.CoreVid:F3}  " +
                                   $"TEMP {r.Temp,4:F1}  PWR {r.Power,4:F1}W");
        if (r.Errors > 0) sb.Append($"  ERR {r.Errors}");
        if (!r.Valid) sb.Append($"  无效（{InvalidReason(r)}）");
        Log.Write(sb.ToString(), r.Valid && r.Errors == 0 ? "INFO" : "WARN");
    }

    /// <summary>Button flow: measure every usable core once, fill the window and log a summary.</summary>
    public static bool Run(MonitorView? monitor, ResultsPanel win, CancellationToken ct)
    {
        Log.Write("Single Core Boost 开始");
        using var scb = Open(monitor);
        if (scb == null) return false;

        var slots = Enumerable.Range(0, scb.SlotCount).Where(scb.IsUsable).ToList();
        win.SetTable(Columns);
        int done = 0;
        var rows = scb.Measure(slots,
            s => win.SetStatus($"测量中：Core {s}（{done + 1}/{slots.Count}）"),
            r =>
            {
                done++;
                win.AddRow(scb.Cells(r), RowColor(r));
                scb.LogRow(r);
            }, ct);

        string text = scb.Summary(rows, ct.IsCancellationRequested);
        foreach (string line in text.Split('\n')) Log.Write(line);
        win.SetSummary(text);
        return true;
    }

    /// <summary>Per-CCD summary. Computation errors are a hint only, SCB is not a stress test.</summary>
    private string Summary(List<ScbRow> rows, bool cancelled)
    {
        var sb = new StringBuilder();
        var valid = rows.Where(r => r.Valid).ToList();
        int total = Enumerable.Range(0, SlotCount).Count(IsUsable);
        sb.Append($"Single Core Boost {(cancelled ? "已取消" : "完成")}：有效 {valid.Count}/{total} 核");
        foreach (var g in valid.GroupBy(r => r.Ccd).OrderBy(g => g.Key))
        {
            var lo = g.MinBy(r => r.Vid)!;
            var hi = g.MaxBy(r => r.Vid)!;
            sb.Append($"\nCCD{g.Key}：频率 {g.Min(r => r.Freq):F0}–{g.Max(r => r.Freq):F0} MHz，" +
                      $"VID {lo.Vid:F3} V（Core {lo.Slot}）– {hi.Vid:F3} V（Core {hi.Slot}）");
        }
        var errs = rows.Where(r => r.Errors > 0).ToList();
        if (errs.Count > 0)
            sb.Append($"\n计算错误：{string.Join("、", errs.Select(r => $"Core {r.Slot} ×{r.Errors}"))}");
        var invalid = rows.Where(r => !r.Valid).Select(r => r.Slot).ToList();
        if (invalid.Count > 0)
            sb.Append($"\n无效：Core {string.Join("、", invalid)}");
        return sb.ToString();
    }

    public void Dispose()
    {
        load?.Dispose();
        load = null;
        monitor?.EndQuiet();
    }
}
