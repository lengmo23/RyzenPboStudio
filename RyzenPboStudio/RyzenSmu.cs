using System.Runtime.InteropServices;
using System.Threading;
using ZenStates.Core;

namespace RyzenPboStudio;

/// <summary>通过 ZenStates-Core 直接经 SMU 邮箱读写物理核心 Curve Optimizer 负压。</summary>
internal static class RyzenSmu
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string? lpPathName);

    private static readonly object Sync = new();
    private static Cpu? _cpu;

    /// <summary>PawnIO 驱动是否已安装（ZenStates-Core 的硬性前置）。</summary>
    public static bool IsPawnIoInstalled => PawnIo.IsInstalled;

    /// <summary>访问共享 Cpu 的运行期串行锁：监控轮询与负压读写共用同一 Cpu/SMU 邮箱，必须互斥，避免并发损坏邮箱事务。</summary>
    public static object IoLock { get; } = new();

    /// <summary>取得进程内共享的 Cpu 实例（供监控界面复用，避免重复加载驱动与并发抢占 SMU）。调用方在 IoLock 下使用。</summary>
    public static Cpu SharedCpu => GetCpu();

    /// <summary>惰性创建并复用单个 Cpu 实例：加载驱动 + 握手 SMU 开销大，且 IODriver 为进程级单例。</summary>
    private static Cpu GetCpu()
    {
        lock (Sync)
        {
            if (_cpu != null) return _cpu;

            if (!PawnIo.IsInstalled)
                throw new InvalidOperationException("未检测到 PawnIO 驱动。");

            // ZenStates-Core 的 IODriver 用裸名 LoadLibrary("inpoutx64.dll")，
            // 把工具包目录临时加入 DLL 搜索路径，让它能找到随包释放的 inpoutx64.dll。
            string dllDir = Path.Combine(ToolBundle.RootDirectory, "ryzen-smu-cli-0.1.3");
            SetDllDirectory(dllDir);
            try
            {
                _cpu = new Cpu();
            }
            finally
            {
                SetDllDirectory(null); // 复位，避免影响后续 DLL 解析
            }
            return _cpu;
        }
    }

    /// <summary>线性物理核心索引 → (ccd, core)。按 ccd-major 连续排列，与原 ryzen-smu-cli 排序一致。</summary>
    private static (uint ccd, uint core) MapIndex(Cpu cpu, int index)
    {
        var topo = cpu.info.topology;
        uint coresPerCcd = topo.physicalCores > 0 && topo.ccds > 0
            ? topo.physicalCores / topo.ccds
            : Math.Max(1u, topo.coresPerCcx);
        if (coresPerCcd == 0) coresPerCcd = 8;
        return ((uint)index / coresPerCcd, (uint)index % coresPerCcd);
    }

    // ── 槽位模型：槽位 = CCD×每CCD槽数+槽内序号（含熔丝屏蔽核，与 SMU MakeCoreMask 寻址一致）──
    private static bool[]? _slotDisabled;   // 各槽位是否被熔丝屏蔽
    private static int[]? _slotOsCore;      // 槽位 → OS 物理核序号（屏蔽槽 = -1）

    /// <summary>按 coreDisableMap 建槽位表（一次缓存）。调用方需持有 IoLock。</summary>
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

    /// <summary>核心槽位总数（含屏蔽核）；拓扑不可用时回退 OS 物理核数。</summary>
    public static int SlotCount
    {
        get
        {
            try { lock (IoLock) { EnsureSlotMap(GetCpu()); return _slotDisabled!.Length; } }
            catch { return Math.Max(2, SystemInfo.GetPhysicalCoreCount()); }
        }
    }

    /// <summary>槽位是否被熔丝屏蔽（越界/拓扑不可用返回 false）。</summary>
    public static bool IsSlotDisabled(int slot)
    {
        try { lock (IoLock) { EnsureSlotMap(GetCpu()); return slot >= 0 && slot < _slotDisabled!.Length && _slotDisabled[slot]; } }
        catch { return false; }
    }

    /// <summary>槽位 → OS 物理核序号（屏蔽槽返回 -1；拓扑不可用时原样返回）。</summary>
    public static int SlotToOsCore(int slot)
    {
        try { lock (IoLock) { EnsureSlotMap(GetCpu()); return slot >= 0 && slot < _slotOsCore!.Length ? _slotOsCore[slot] : slot; } }
        catch { return slot; }
    }

    private static bool IsSlotDisabledNoLock(Cpu cpu, int slot)
    {
        EnsureSlotMap(cpu);
        return slot >= 0 && slot < _slotDisabled!.Length && _slotDisabled[slot];
    }

    // SetPsmMargin 编码的逆：CO 负压存于返回值低 16 位补码。
    private static int DecodeMargin(uint raw) => (short)(raw & 0xffff);

    /// <summary>
    /// 读单核 CO 负压；失败返回 null。SMU 邮箱被其它软件（HWiNFO / Ryzen Master）或本进程其它命令占用时
    /// 事务会返回非 OK，ZenStates 随即把 args 清零，直接当 0 用会让读数在真值与 0 之间乱跳，所以重试几次再放弃。
    /// 调用方需持有 <see cref="IoLock"/>。
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

    /// <summary>读取当前各物理核心负压值；读取失败的核心返回 0。</summary>
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
            Log.Write($"读取负压失败: {e.Message}，使用默认值 0", "WARN");
            return Enumerable.Repeat(0, numCores).ToList();
        }
    }

    /// <summary>读取当前 FMax（最大频率，MHz）；失败返回 0。</summary>
    public static uint ReadFMax()
    {
        try { lock (IoLock) return GetCpu().GetFMax(); }
        catch (Exception e)
        {
            Log.Write($"读取 FMax 失败: {e.Message}", "WARN");
            return 0;
        }
    }

    /// <summary>设置 FMax（最大频率，MHz）。</summary>
    public static bool SetFMax(uint frequency)
    {
        try { lock (IoLock) return GetCpu().SetFMax(frequency); }
        catch (Exception e)
        {
            Log.Write($"设置 FMax 异常: {e.Message}", "ERROR");
            return false;
        }
    }

    // 上次手动应用的 TDC/EDC 限制（A）：SMU 无对应读取接口，缓存供监控显示「上限」。
    public static int? LastTdcLimit { get; private set; }
    public static int? LastEdcLimit { get; private set; }

    // PBO 解锁时 SMU 把表头的 PPT / TDC / EDC 上限报成 999 哨兵（Vermeer 实测），那不是可写回参数框的有效值。
    private const float PtLimitSentinel = 999f;

    /// <summary>PM Table 中逐世代、逐型号浮动的偏移。表头上 PPT / TDC / THM 的位置分两代：
    /// Zen4/Zen5（Raphael、DragonRange、GraniteRidge 三代一致）与 Zen3（Vermeer 另起一套 limit/value 顺排）。
    /// EDC、每核电压段与 VDDCR_CPU 遥测组还要再逐型号浮动——遥测组起点 Raphael 在 0xB8、DragonRange 在 0xBC、
    /// GraniteRidge 在 0xC0，同为 Zen4 也能差一个 float，按代号写死覆盖不全。</summary>
    internal sealed class PtLayout
    {
        public int PptLimitIdx;
        public int PptCurrentIdx;
        public int TdcLimitIdx;
        public int TdcCurrentIdx;
        public int ThmLimitIdx;
        public int EdcLimitIdx;
        public int EdcCurrentIdx;
        public int CpuVidIdx;        // 遥测组 {VID, TEL, I, P} 的起点
        public int CpuTelIdx;        // 组内第 2 项，实测电压：TEL × I = P 恒成立
        public int PerCoreVoltIdx;   // 每核电压段起点；-1 表示未探到，此时每核 VID 不显示
    }

    private static bool IsPtVolt(float v) => v is >= 0.20f and <= 1.60f;
    private static bool IsPtTemp(float v) => v is >= 15f and <= 115f;

    /// <summary>该槽位是否被熔丝屏蔽。每核段按槽位排列、屏蔽槽填 0，探测时必须跳过这些位置，
    /// 否则 9900X3D / 7900X 这类带空洞的型号永远凑不出 cores 个连续的合法电压。
    /// 槽位表尚未建立时按全部有效处理（保持与旧判据一致）。</summary>
    private static bool IsSlotMaskedNoLock(int slot) =>
        _slotDisabled is { } d && slot >= 0 && slot < d.Length && d[slot];

    /// <summary>探测 PM Table 布局，探不中返回 null（调用方走回退路径）。
    /// 先按 Zen4/Zen5 表头认，不中再按 Zen3 表头认；两套判据在 7945HX / 9950X / 5700X 三份实机转储上
    /// 各自唯一命中且互不误认。每核电压段两代共用一套探测，见 ProbePerCoreVolt。
    /// 每核段探不中时 PerCoreVoltIdx 为 -1，调用方应继续重试而不是锁定这份残缺布局。</summary>
    internal static PtLayout? ProbePtLayout(float[]? t, int cores)
    {
        if (t == null || cores <= 0 || t.Length < 70) return null;

        var lay = ProbeZen4Header(t) ?? ProbeZen3Header(t);
        if (lay == null) return null;

        lay.PerCoreVoltIdx = ProbePerCoreVolt(t, cores);
        return lay;
    }

    /// <summary>Zen4 / Zen5 表头：PPT / TDC / THM 在固定绝对索引，EDC 与遥测组随型号浮动。
    /// 遥测组用表头两个镜像值定位：idx19 与组内 VID、idx20 与组内功率都是逐位相等的同一个 float，
    /// 不需要容差，因此不会被空闲态的低电流噪声干扰。</summary>
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

    /// <summary>Zen3（Vermeer）表头：PPT / TDC / THM / FIT / EDC / VID 六组 {limit, value} 自 idx0 顺排，
    /// 与 Zen4 整体错位，故不能共用绝对索引。Zen3 表头没有 Zen4 那对 idx19 / idx20 镜像，遥测组改用
    /// idx11（VID_VALUE）的镜像定位并以 TEL × I = P 验算。先校验表头形状再找镜像，避免在别代表上误认。</summary>
    private static PtLayout? ProbeZen3Header(float[] t)
    {
        if (!IsPtTemp(t[4]) || !IsPtTemp(t[5])) return null;                                   // THM {上限, 当前}
        if (t[10] is not (> 0.3f and < 2.0f) || t[11] is not (> 0.3f and < 2.0f)) return null; // VID {上限, 当前}
        if (t[1] <= 0 || t[3] <= 0 || t[9] <= 0) return null;                                  // PPT / TDC / EDC 当前值

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

    /// <summary>每核电压段用「cores 个电压紧跟同样长的温度段」定位——两段在表里始终相邻，该组合在
    /// 7800X3D / 7945HX / 9950X / 5700X 四份实机转储上均唯一命中。cores 传的是槽位数而非有效核数：
    /// 每核段按槽位排列，屏蔽槽填 0，故这些位置不参与匹配。探不中返回 -1。</summary>
    private static int ProbePerCoreVolt(float[] t, int cores)
    {
        for (int i = 24; i + 2 * cores <= t.Length; i++)
        {
            if (IsPtVolt(t[i - 1])) continue;   // 段首之前必须断开，避免落在长电压段的中间
            bool ok = true;
            for (int k = 0; k < cores && ok; k++) ok = IsSlotMaskedNoLock(k) || IsPtVolt(t[i + k]);
            for (int k = 0; k < cores && ok; k++) ok = IsSlotMaskedNoLock(k) || IsPtTemp(t[i + cores + k]);
            if (ok) return i;
        }
        return -1;
    }

    /// <summary>设置 PPT（持续功率上限，W）。</summary>
    public static bool SetPptLimit(uint watts)
    {
        try { lock (IoLock) return GetCpu().SetPPTLimit(watts) == SMU.Status.OK; }
        catch (Exception e) { Log.Write($"设置 PPT 异常: {e.Message}", "ERROR"); return false; }
    }

    /// <summary>设置 TDC（VDD 持续电流上限，A）。</summary>
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

    /// <summary>设置 EDC（VDD 峰值电流上限，A）。</summary>
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

    /// <summary>读取系统配置的 PPT 上限(W) 与 THM 上限(°C)；读不到返回 (0, 0)。</summary>
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

    /// <summary>读取硅片熔丝默认 PPT(W) 与 TDC(A)，用于初始化参数框；读不到返回 (0, 0)。</summary>
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

    /// <summary>读取当前 PBO 上限（PPT W / EDC A / TDC A）；Granite Ridge 优先使用实时 PM Table。</summary>
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
                    // 哨兵上限说明 PBO 已解锁，表里没有可写回参数框的数值，退回熔丝／系统上限。
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

    /// <summary>设置所有物理核心负压。</summary>
    public static bool SetOffsets(IReadOnlyList<int> offsets)
    {
        try
        {
            lock (IoLock)
            {
                var cpu = GetCpu();
                for (int i = 0; i < offsets.Count; i++)
                {
                    if (IsSlotDisabledNoLock(cpu, i)) continue;
                    var (ccd, core) = MapIndex(cpu, i);
                    if (!cpu.SetPsmMarginSingleCore(cpu.MakeCoreMask(core, ccd, 0), offsets[i]))
                        Log.Write($"设置负压警告: 核心 {i} (CCD{ccd} Core{core}) 写入返回 false", "WARN");
                }
                return true;
            }
        }
        catch (Exception e)
        {
            Log.Write($"设置负压异常: {e.Message}", "ERROR");
            return false;
        }
    }

    /// <summary>当前 CPU 的 SMU 是否有「设置全核 boost 上限」这条消息。
    /// Zen3（Vermeer）继承 Zen2 的消息表，那里只有读（GetBoostLimitFrequency 0x6E）没有写，
    /// Rsmu 与 MP1 两个 ID 都是 0——而 SetBoostLimitAllCore 并不校验 ID，会照发一条消息 0 给 SMU，
    /// 故必须在调用前拦下。0x70 自 Zen4 才出现，Zen5 由 Zen4Settings 继承。</summary>
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

    /// <summary>Curve Shaper 是否受支持（仅 Zen4/Zen5 桌面；线程撕裂者等无此命令）。</summary>
    public static bool IsCurveShaperSupported()
    {
        try { lock (IoLock) return GetCpu().IsCurveShaperSupported(); }
        catch { return false; }
    }

    /// <summary>读取 5 个频率档的 Curve Shaper margin，返回 [tier, col]（col: 0=低温 1=中温 2=高温）；失败返回全 0。</summary>
    /// <param name="interfaceAvailable">读取接口是否可用。新 BIOS 下每档 raw arg 低位带 tier 编号(0..4)，
    /// 故 raw 不会全 0；旧 BIOS 无读取接口时 raw 全 0（CS 电压已生效但读不回），此时返回 false。</param>
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
                    grid[t, 0] = (sbyte)((raw[t] >> 8) & 0xFF);    // 低温
                    grid[t, 1] = (sbyte)((raw[t] >> 16) & 0xFF);   // 中温
                    grid[t, 2] = (sbyte)((raw[t] >> 24) & 0xFF);   // 高温
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"读取 Curve Shaper 失败: {e.Message}", "WARN");
        }
        return grid;
    }

    /// <summary>写入 5 个频率档的 Curve Shaper margin。grid[tier, col]（col: 0=低温 1=中温 2=高温）。</summary>
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
                    // 5 档连续下发，给 SMU 邮箱留出结算时间，降低响应寄存器读到过渡值的概率
                    if (t > 0) Thread.Sleep(2);
                    var st = cpu.SetCurveShaperMargin(grid[t, 2], grid[t, 1], grid[t, 0], t);
                    if (st != SMU.Status.OK)
                    {
                        // 邮箱瞬时抖动（返回值不属于任何已定义状态码），重试一次同样的写入再判定
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
