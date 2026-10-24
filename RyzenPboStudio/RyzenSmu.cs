using System.Runtime.InteropServices;
using System.Threading;
using ZenStates.Core;

namespace RyzenPboStudio;

/// <summary>Reads and writes per-core Curve Optimizer offsets through the SMU mailbox.</summary>
internal static class RyzenSmu
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? lpPathName);

    private static readonly object Sync = new();
    private static Cpu? _cpu;

    /// <summary>Whether the PawnIO driver required by ZenStates-Core is installed.</summary>
    public static bool IsPawnIoInstalled => PawnIo.IsInstalled;

    /// <summary>Serializes monitor and CO access to the shared CPU and SMU mailbox.</summary>
    public static object IoLock { get; } = new();

    /// <summary>Returns the shared CPU instance; callers must hold IoLock.</summary>
    public static Cpu SharedCpu => GetCpu();

    /// <summary>Creates one CPU instance because driver initialization is costly and IODriver is process-wide.</summary>
    private static Cpu GetCpu()
    {
        lock (Sync)
        {
            if (_cpu != null) return _cpu;

            if (!PawnIo.IsInstalled)
                throw new InvalidOperationException("未检测到 PawnIO 驱动。");

            // IODriver loads inpoutx64.dll by bare name, so include the protected bundle directory
            // in the DLL search path while initializing ZenStates-Core.
            string dllDir = Path.Combine(ToolBundle.RootDirectory, "ryzen-smu-cli-0.1.3");
            SetDllDirectory(dllDir);
            try
            {
                _cpu = new Cpu();
            }
            finally
            {
                SetDllDirectory(null); // Restore the DLL search path.
            }
            return _cpu;
        }
    }

    /// <summary>Maps a linear physical-core index to its CCD and core index.</summary>
    private static (uint ccd, uint core) MapIndex(Cpu cpu, int index)
    {
        var topo = cpu.info.topology;
        uint coresPerCcd = topo.physicalCores > 0 && topo.ccds > 0
            ? topo.physicalCores / topo.ccds
            : Math.Max(1u, topo.coresPerCcx);
        if (coresPerCcd == 0) coresPerCcd = 8;
        return ((uint)index / coresPerCcd, (uint)index % coresPerCcd);
    }

    // SMU slots include fused-off cores and follow CCD-major MakeCoreMask addressing.
    private static bool[]? _slotDisabled; // Fused-off status by SMU slot.
    private static int[]? _slotOsCore; // SMU slot to OS core; -1 when disabled.

    /// <summary>Builds and caches SMU slots from coreDisableMap; caller holds IoLock.</summary>
    private static void EnsureSlotMap(Cpu cpu)
    {
        if (_slotDisabled != null) return;
        var topo = cpu.info.topology;
        int ccds = (int)Math.Max(1u, topo.ccds);
        int perCcd = topo.physicalCores > 0 ? (int)(topo.physicalCores / Math.Max(1u, topo.ccds)) : 8;
        if (perCcd <= 0) perCcd = 8;
        int n = ccds * perCcd;
        var dis = new bool[n];
        var os = new int[n];
        int next = 0;
        for (int s = 0; s < n; s++)
        {
            int ccd = s / perCcd;
            uint map = topo.coreDisableMap != null && ccd < topo.coreDisableMap.Length ? topo.coreDisableMap[ccd] : 0;
            dis[s] = ((map >> (s % perCcd)) & 1) != 0;
            os[s] = dis[s] ? -1 : next++;
        }
        _slotDisabled = dis;
        _slotOsCore = os;
    }

    /// <summary>Counts SMU slots including disabled cores; falls back to OS core count.</summary>
    public static int SlotCount
    {
        get
        {
            try { lock (IoLock) { EnsureSlotMap(GetCpu()); return _slotDisabled!.Length; } }
            catch { return Math.Max(2, SystemInfo.GetPhysicalCoreCount()); }
        }
    }

    /// <summary>Returns whether a slot is fused off; false when topology is unavailable.</summary>
    public static bool IsSlotDisabled(int slot)
    {
        try { lock (IoLock) { EnsureSlotMap(GetCpu()); return slot >= 0 && slot < _slotDisabled!.Length && _slotDisabled[slot]; } }
        catch { return false; }
    }

    /// <summary>Maps an SMU slot to an OS core; disabled slots return -1.</summary>
    public static int SlotToOsCore(int slot)
    {
        try { lock (IoLock) { EnsureSlotMap(GetCpu()); return slot >= 0 && slot < _slotOsCore!.Length ? _slotOsCore[slot] : slot; } }
        catch { return slot; }
    }

    /// <summary>Maps an OS core to its SMU slot; fused-off cores make these indices differ.
    /// Use this mapping before indexing CO arrays by OS core number.
    /// Falls back to the input index when topology is unavailable.</summary>
    public static int OsCoreToSlot(int osCore)
    {
        try
        {
            lock (IoLock)
            {
                EnsureSlotMap(GetCpu());
                for (int s = 0; s < _slotOsCore!.Length; s++)
                    if (_slotOsCore[s] == osCore) return s;
            }
        }
        catch { }
        return osCore;
    }

    private static bool IsSlotDisabledNoLock(Cpu cpu, int slot)
    {
        EnsureSlotMap(cpu);
        return slot >= 0 && slot < _slotDisabled!.Length && _slotDisabled[slot];
    }

    // CO is encoded as a signed 16-bit value in the SMU response.
    private static int DecodeMargin(uint raw) => (short)(raw & 0xffff);

    /// <summary>
    /// Reads one core’s CO offset, retrying transient mailbox failures; returns null on failure.
    /// Caller holds <see cref="IoLock"/>.
    /// </summary>
    public static int? TryReadMargin(Cpu cpu, uint ccd, uint core)
    {
        uint mask = cpu.MakeCoreMask(core, ccd, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint? raw = cpu.GetPsmMarginSingleCore(mask);
            if (raw.HasValue) return DecodeMargin(raw.Value);
        }
        return null;
    }

    /// <summary>Reads all physical-core CO offsets; failed reads produce zero.</summary>
    public static List<int> ReadOffsets(int numCores)
    {
        try
        {
            lock (IoLock)
            {
                var cpu = GetCpu();
                var result = new List<int>(numCores);
                for (int i = 0; i < numCores; i++)
                {
                    if (IsSlotDisabledNoLock(cpu, i)) { result.Add(0); continue; }
                    var (ccd, core) = MapIndex(cpu, i);
                    result.Add(TryReadMargin(cpu, ccd, core) ?? 0);
                }
                return result;
            }
        }
        catch (Exception e)
        {
            Log.Write($"读取 CO 失败: {e.Message}，使用默认值 0", "WARN");
            return Enumerable.Repeat(0, numCores).ToList();
        }
    }

    /// <summary>Reads FMax in MHz; returns zero on failure.</summary>
    public static uint ReadFMax()
    {
        try { lock (IoLock) return GetCpu().GetFMax(); }
        catch (Exception e)
        {
            Log.Write($"读取 FMax 失败: {e.Message}", "WARN");
            return 0;
        }
    }

    /// <summary>Sets FMax in MHz.</summary>
    public static bool SetFMax(uint frequency)
    {
        try { lock (IoLock) return GetCpu().SetFMax(frequency); }
        catch (Exception e)
        {
            Log.Write($"设置 FMax 异常: {e.Message}", "ERROR");
            return false;
        }
    }

    /// <summary>Reads PBO Scalar (1–10); zero means OC mode or an unavailable value.</summary>
    public static float ReadPboScalar()
    {
        try { lock (IoLock) return GetCpu().GetPBOScalar(); }
        catch (Exception e)
        {
            Log.Write($"读取 PBO Scalar 失败: {e.Message}", "WARN");
            return 0;
        }
    }

    /// <summary>Sets PBO Scalar; Zen2/Zen3 first enable PBO through MP1 SetPBO_EN.</summary>
    public static bool SetPboScalar(uint scalar)
    {
        try
        {
            lock (IoLock)
            {
                var status = GetCpu().SetPBOScalar(scalar);
                if (status != SMU.Status.OK) Log.Write($"设置 PBO Scalar 返回 {status}", "WARN");
                return status == SMU.Status.OK;
            }
        }
        catch (Exception e)
        {
            Log.Write($"设置 PBO Scalar 异常: {e.Message}", "ERROR");
            return false;
        }
    }

    /// <summary>Checks for a nonzero RSMU or MP1 Scalar write message before enabling the UI.</summary>
    public static bool IsPboScalarWriteSupported()
    {
        try
        {
            lock (IoLock)
            {
                var smu = GetCpu().smu;
                return smu.Rsmu.SMU_MSG_SetPBOScalar != 0 || smu.Mp1Smu.SMU_MSG_SetPBOScalar != 0;
            }
        }
        catch (Exception e)
        {
            Log.Write($"检测 PBO Scalar 写入支持失败: {e.Message}", "WARN");
            return false;
        }
    }

    // Cache manually applied TDC/EDC limits because the SMU has no readback command.
    public static int? LastTdcLimit { get; private set; }
    public static int? LastEdcLimit { get; private set; }

    // An unlocked PBO reports limit sentinel 999, which must not populate writable controls.
    private const float PtLimitSentinel = 999f;

    /// <summary>PM Table offsets vary by generation and model.
    /// Probe EDC, per-core segments, and VDDCR_CPU telemetry at runtime.</summary>
    internal sealed class PtLayout
    {
        public int PptLimitIdx;
        public int PptCurrentIdx;
        public int TdcLimitIdx;
        public int TdcCurrentIdx;
        public int ThmLimitIdx;
        public int EdcLimitIdx;
        public int EdcCurrentIdx;
        public int CpuVidIdx; // Start of {VID, TEL, I, P} telemetry group.
        public int CpuTelIdx; // TEL is measured voltage and satisfies TEL * I = P.
        public int PerCoreVoltIdx; // Per-core voltage segment; -1 when absent.
        public int PerCoreFreqIdx; // Per-core frequency segment; -1 triggers MSR fallback.
    }

    private static bool IsPtVolt(float v) => v is >= 0.20f and <= 1.60f;
    private static bool IsPtTemp(float v) => v is >= 15f and <= 115f;

    /// <summary>Skips fused-off slots, whose PM Table per-core entries are zero.
    /// Treats all slots as valid until the slot map exists.</summary>
    private static bool IsSlotMaskedNoLock(int slot) =>
        _slotDisabled is { } d && slot >= 0 && slot < d.Length && d[slot];

    /// <summary>Probes Zen4/Zen5 and then Zen3 PM Table layouts; returns null on failure.
    /// An incomplete per-core layout retains a negative index so callers can retry.
    /// </summary>
    internal static PtLayout? ProbePtLayout(float[]? t, int cores)
    {
        if (t == null || cores <= 0 || t.Length < 70) return null;

        var lay = ProbeZen4Header(t) ?? ProbeZen3Header(t);
        if (lay == null) return null;

        lay.PerCoreVoltIdx = ProbePerCoreVolt(t, cores);
        lay.PerCoreFreqIdx = ProbePerCoreFreq(t, lay.PerCoreVoltIdx, cores);
        return lay;
    }

    /// <summary>Finds Zen4/Zen5 telemetry by matching header mirrors for VID and power.
    /// PPT, TDC, and THM use fixed header indices; EDC varies by model.</summary>
    private static PtLayout? ProbeZen4Header(float[] t)
    {
        float vidRef = t[19], pwrRef = t[20];
        if (vidRef is not (> 0.3f and < 2.0f) || pwrRef <= 0) return null;

        int baseIdx = -1;
        for (int i = 24; i + 4 < t.Length && i < 200; i++)
        {
            if (t[i] == vidRef && t[i + 3] == pwrRef && t[i + 2] > 0) { baseIdx = i; break; }
        }
        if (baseIdx < 0 || baseIdx + 16 >= t.Length) return null;

        return new PtLayout
        {
            PptLimitIdx   = 2,
            PptCurrentIdx = 3,
            TdcLimitIdx   = 8,
            TdcCurrentIdx = 9,
            ThmLimitIdx   = 10,
            EdcLimitIdx   = baseIdx + 15,
            EdcCurrentIdx = baseIdx + 16,
            CpuVidIdx     = baseIdx,
            CpuTelIdx     = baseIdx + 1,
        };
    }

    /// <summary>Validates the Zen3 header before locating its telemetry mirror.
    /// Confirms the group with TEL * current = power to avoid matching another layout.</summary>
    private static PtLayout? ProbeZen3Header(float[] t)
    {
        if (!IsPtTemp(t[4]) || !IsPtTemp(t[5])) return null; // Validate thermal limit and value.
        if (t[10] is not (> 0.3f and < 2.0f) || t[11] is not (> 0.3f and < 2.0f)) return null; // Validate VID limit and value.
        if (t[1] <= 0 || t[3] <= 0 || t[9] <= 0) return null; // Require positive PPT, TDC, and EDC values.

        float vidRef = t[11];
        int baseIdx = -1;
        for (int i = 12; i + 3 < t.Length && i < 200; i++)
        {
            if (t[i] != vidRef) continue;
            float tel = t[i + 1], cur = t[i + 2], pwr = t[i + 3];
            if (tel is not (> 0.3f and < 2.0f) || cur <= 0 || pwr <= 0) continue;
            if (Math.Abs(tel * cur - pwr) <= pwr * 0.005f) { baseIdx = i; break; }
        }
        if (baseIdx < 0) return null;

        return new PtLayout
        {
            PptLimitIdx   = 0,
            PptCurrentIdx = 1,
            TdcLimitIdx   = 2,
            TdcCurrentIdx = 3,
            ThmLimitIdx   = 4,
            EdcLimitIdx   = 8,
            EdcCurrentIdx = 9,
            CpuVidIdx     = baseIdx,
            CpuTelIdx     = baseIdx + 1,
        };
    }

    /// <summary>Finds a per-core voltage segment followed by an equal-length temperature segment.
    /// Skips fused-off slots and returns -1 when absent.</summary>
    private static int ProbePerCoreVolt(float[] t, int cores)
    {
        for (int i = 24; i + 2 * cores <= t.Length; i++)
        {
            if (IsPtVolt(t[i - 1])) continue; // Require a segment boundary to avoid matching inside a longer voltage range.
            bool ok = true;
            for (int k = 0; k < cores && ok; k++) ok = IsSlotMaskedNoLock(k) || IsPtVolt(t[i + k]);
            for (int k = 0; k < cores && ok; k++) ok = IsSlotMaskedNoLock(k) || IsPtTemp(t[i + cores + k]);
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>Finds per-core frequency and effective-frequency segments after temperature.
    /// Allows 10% sampling slack when comparing the adjacent segments;
    /// skips fused-off slots and returns -1 when absent.</summary>
    private static int ProbePerCoreFreq(float[] t, int voltIdx, int cores)
    {
        if (voltIdx < 0) return -1;
        for (int seg = 2; seg <= 6; seg++)
        {
            int f = voltIdx + seg * cores;
            if (f + 2 * cores > t.Length) break;
            bool ok = true;
            for (int k = 0; k < cores && ok; k++)
            {
                if (IsSlotMaskedNoLock(k)) continue;
                float freq = t[f + k], eff = t[f + cores + k];
                ok = freq is >= 0.2f and <= 7.5f && eff >= -0.01f && eff <= freq * 1.10f;
            }
            if (ok) return f;
        }
        return -1;
    }

    /// <summary>Sets the sustained PPT power limit in watts.</summary>
    public static bool SetPptLimit(uint watts)
    {
        try { lock (IoLock) return GetCpu().SetPPTLimit(watts) == SMU.Status.OK; }
        catch (Exception e) { Log.Write($"设置 PPT 异常: {e.Message}", "ERROR"); return false; }
    }

    /// <summary>Sets the sustained TDC current limit in amperes.</summary>
    public static bool SetTdcLimit(uint amps)
    {
        try
        {
            lock (IoLock)
            {
                bool ok = GetCpu().SetTDCVDDLimit(amps) == SMU.Status.OK;
                if (ok) LastTdcLimit = (int)amps;
                return ok;
            }
        }
        catch (Exception e) { Log.Write($"设置 TDC 异常: {e.Message}", "ERROR"); return false; }
    }

    /// <summary>Sets the peak EDC current limit in amperes.</summary>
    public static bool SetEdcLimit(uint amps)
    {
        try
        {
            lock (IoLock)
            {
                bool ok = GetCpu().SetEDCVDDLimit(amps) == SMU.Status.OK;
                if (ok) LastEdcLimit = (int)amps;
                return ok;
            }
        }
        catch (Exception e) { Log.Write($"设置 EDC 异常: {e.Message}", "ERROR"); return false; }
    }

    /// <summary>Reads configured PPT and thermal limits; returns (0, 0) on failure.</summary>
    public static (int pptLimit, int thmLimit) ReadSystemLimits()
    {
        try
        {
            lock (IoLock)
            {
                var lim = GetCpu().GetSystemPowerLimit();
                return lim.HasValue ? (lim.Value.PowerLimit, lim.Value.TemperatureLimit) : (0, 0);
            }
        }
        catch (Exception e) { Log.Write($"读取系统功率/温度上限失败: {e.Message}", "WARN"); return (0, 0); }
    }

    /// <summary>Reads fused PPT and TDC defaults for the editor; returns (0, 0) on failure.</summary>
    public static (int fusedPpt, int fusedTdc) ReadFusedLimits()
    {
        try
        {
            lock (IoLock)
            {
                var f = GetCpu().GetPboFusedLimits();
                return f.HasValue ? (f.Value.PowerLimit, f.Value.VrmVddTdcCurrent) : (0, 0);
            }
        }
        catch (Exception e) { Log.Write($"读取熔丝默认限制失败: {e.Message}", "WARN"); return (0, 0); }
    }

    /// <summary>Reads current PPT, EDC, and TDC limits; Granite Ridge prefers live PM Table values.</summary>
    public static (int pptLimit, int edcLimit, int tdcLimit) ReadPboLimits()
    {
        try
        {
            lock (IoLock)
            {
                var cpu = GetCpu();
                var topo = cpu.info.topology;
                int coreCount = (int)(topo.physicalCores > 0 ? topo.physicalCores : topo.cores);
                if (cpu.RefreshPowerTable() == SMU.Status.OK
                    && cpu.powerTable?.Table is { } tbl
                    && ProbePtLayout(tbl, coreCount) is { } lay && tbl.Length > lay.EdcLimitIdx)
                {
                    float ppt = tbl[lay.PptLimitIdx], edc = tbl[lay.EdcLimitIdx], tdc = tbl[lay.TdcLimitIdx];
                    // An unlocked PBO sentinel is not a writable limit; use fused or configured limits.
                    if (ppt < PtLimitSentinel && edc < PtLimitSentinel && tdc < PtLimitSentinel)
                        return ((int)Math.Round(ppt), (int)Math.Round(edc), (int)Math.Round(tdc));
                }

                var sys = cpu.GetSystemPowerLimit();
                var fused = cpu.GetPboFusedLimits();
                return (sys?.PowerLimit ?? fused?.PowerLimit ?? 0,
                        LastEdcLimit ?? 0,
                        LastTdcLimit ?? fused?.VrmVddTdcCurrent ?? 0);
            }
        }
        catch (Exception e) { Log.Write($"读取 PBO 上限失败: {e.Message}", "WARN"); return (0, 0, 0); }
    }

    private const int CoReadbackPolls = 3;          // readbacks after each write
    private const int CoMinWaitMs = 75;             // first wait between readbacks / rewrites
    private const int CoMaxWaitMs = 1000;           // the wait doubles up to this while the SMU stays busy
    private const int CoCoreTimeoutMs = 20_000;     // per core; a busy SMU clears long before this

    /// <summary>
    /// Writes each active core's CO and verifies its readback. A busy SMU mailbox makes writes or readbacks fail for
    /// a while, so a core is rewritten with a growing wait until its readback matches; only a core that still does not
    /// match after CoCoreTimeoutMs fails the call. Retries are not logged; the caller reports a failure.
    /// </summary>
    public static bool SetOffsets(IReadOnlyList<int> offsets)
    {
        try
        {
            lock (IoLock)
            {
                var cpu = GetCpu();

                bool WriteVerified(int i)
                {
                    var (ccd, core) = MapIndex(cpu, i);
                    uint mask = cpu.MakeCoreMask(core, ccd, 0);
                    long deadline = Environment.TickCount64 + CoCoreTimeoutMs;
                    int wait = CoMinWaitMs;
                    while (true)
                    {
                        cpu.SetPsmMarginSingleCore(mask, offsets[i]);   // the readback decides, not the return value
                        for (int poll = 0; poll < CoReadbackPolls; poll++)
                        {
                            Thread.Sleep(wait);
                            if (TryReadMargin(cpu, ccd, core) == offsets[i]) return true;
                        }
                        if (Environment.TickCount64 >= deadline) return false;
                        wait = Math.Min(wait * 2, CoMaxWaitMs);
                    }
                }

                for (int i = 0; i < offsets.Count; i++)
                    if (!IsSlotDisabledNoLock(cpu, i) && !WriteVerified(i)) return false;

                // Final pass: a core that drifted while the others were written is written again the same way
                for (int i = 0; i < offsets.Count; i++)
                {
                    if (IsSlotDisabledNoLock(cpu, i)) continue;
                    var (ccd, core) = MapIndex(cpu, i);
                    if (TryReadMargin(cpu, ccd, core) != offsets[i] && !WriteVerified(i)) return false;
                }
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Write($"设置 CO 异常: {e.Message}", "ERROR");
            return false;
        }
    }

    // Vermeer uses the Zen2 CCD temperature address, not Raphael’s 0x59B08 used for family 19h.
    private const uint VermeerCcdTempReg = 0x00059954;

    /// <summary>Reads CCD hotspot temperature; returns zero when unavailable or out of range.</summary>
    public static float ReadCcdTemperature(uint ccd)
    {
        try
        {
            lock (IoLock)
            {
                var cpu = GetCpu();
                if (cpu.info.codeName != Cpu.CodeName.Vermeer)
                    return cpu.GetSingleCcdTemperature(ccd) ?? 0;

                uint raw = 0;
                if (!cpu.ReadDwordEx(VermeerCcdTempReg + ccd * 4, ref raw)) return 0;
                float t = (raw & 0xfff) * 0.125f - 305.0f;
                return t is > 0f and < 125f ? t : 0;
            }
        }
        catch (Exception e)
        {
            Log.Write($"读取 CCD{ccd} 温度失败: {e.Message}", "WARN");
            return 0;
        }
    }

    /// <summary>Checks for the all-core boost-limit message before calling SetBoostLimitAllCore.
    /// Zen3 reports ID zero, which that command would otherwise send to the SMU.</summary>
    public static bool IsFMaxWriteSupported()
    {
        try
        {
            lock (IoLock)
            {
                var smu = GetCpu().smu;
                return smu.Rsmu.SMU_MSG_SetBoostLimitFrequencyAllCores != 0
                    || smu.Mp1Smu.SMU_MSG_SetBoostLimitFrequencyAllCores != 0;
            }
        }
        catch (Exception e)
        {
            Log.Write($"检测 FMax 写入支持失败: {e.Message}", "WARN");
            return false;
        }
    }

    /// <summary>Checks whether this CPU exposes Curve Shaper commands.</summary>
    public static bool IsCurveShaperSupported()
    {
        try { lock (IoLock) return GetCpu().IsCurveShaperSupported(); }
        catch { return false; }
    }

    /// <summary>Reads the 5-by-3 Curve Shaper grid; returns zeros on failure.</summary>
    /// <param name="interfaceAvailable">False when old BIOS readback returns all-zero raw arguments
    /// despite applied CS values; newer BIOS responses include a tier ID.</param>
    public static int[,] ReadCurveShaper(out bool interfaceAvailable)
    {
        var grid = new int[5, 3];
        interfaceAvailable = false;
        try
        {
            lock (IoLock)
            {
                var cpu = GetCpu();
                uint[] raw = cpu.GetAllCurveShaperMargins();
                interfaceAvailable = raw != null && raw.Any(x => x != 0);
                for (int t = 0; raw != null && t < 5 && t < raw.Length; t++)
                {
                    grid[t, 0] = (sbyte)((raw[t] >> 8) & 0xFF);
                    grid[t, 1] = (sbyte)((raw[t] >> 16) & 0xFF);
                    grid[t, 2] = (sbyte)((raw[t] >> 24) & 0xFF);
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"读取 Curve Shaper 失败: {e.Message}", "WARN");
        }
        return grid;
    }

    /// <summary>Writes the 5-by-3 Curve Shaper grid.</summary>
    public static bool SetCurveShaper(int[,] grid)
    {
        try
        {
            lock (IoLock)
            {
                var cpu = GetCpu();
                bool ok = true;
                for (int t = 0; t < 5; t++)
                {
                    // Pause between tier writes so the SMU mailbox can settle before readback.
                    if (t > 0) Thread.Sleep(2);
                    var st = cpu.SetCurveShaperMargin(grid[t, 2], grid[t, 1], grid[t, 0], t);
                    if (st != SMU.Status.OK)
                    {
                        // Retry one undefined mailbox response before declaring the write failed.
                        Thread.Sleep(5);
                        st = cpu.SetCurveShaperMargin(grid[t, 2], grid[t, 1], grid[t, 0], t);
                    }
                    if (st != SMU.Status.OK)
                    {
                        ok = false;
                        Log.Write($"Curve Shaper 写入失败: 频率档 {t} 返回 {st}", "WARN");
                    }
                }
                return ok;
            }
        }
        catch (Exception e)
        {
            Log.Write($"设置 Curve Shaper 异常: {e.Message}", "ERROR");
            return false;
        }
    }
}
