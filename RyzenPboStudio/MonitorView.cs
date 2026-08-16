using System.Diagnostics;
using OpenHardwareMonitor.Hardware;   // GroupAffinity
using ZenStates.Core;

namespace RyzenPboStudio;

// 监控界面专用深色配色（与主程序 Theme 区分，保留 ZenStates 监控原始观感）
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

/// <summary>双缓冲 + 1px 边框的面板，用作 CCD 容器，区分相邻面板并消除刷新闪烁。</summary>
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

/// <summary>双缓冲 Label，避免文字频繁更新（占用/HOT 每 500ms 刷新）时闪烁。</summary>
internal sealed class DbLabel : Label
{
    public DbLabel() => DoubleBuffered = true;
}

/// <summary>直接绘制标题和数值，避免嵌套 Label 在紧凑 TableLayout 中被压成零高度。</summary>
internal sealed class StatCellControl : Control
{
    private string title;
    private readonly bool center;

    /// <summary>格子标题。异步外频时 BCLK 格要改标成 "BCLK/BCLK2"，故可写。</summary>
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

/// <summary>嵌入主程序的每核监控面板（移植自 MonitorTest）：复用 RyzenSmu 的共享 Cpu，
/// 仅在可见时后台轮询，所有 Cpu 访问在 RyzenSmu.IoLock 下串行，避免与负压读写抢占 SMU。</summary>
internal sealed class MonitorView : UserControl
{
    private readonly Cpu cpu;
    private readonly uint cores, ccds, coresPerCcd;
    private readonly int[] slotOs;        // 槽位 → OS 物理核序号（屏蔽槽 -1）
    private readonly bool[] slotDisabled; // 槽位是否熔丝屏蔽
    private double bclk;                 // CPU 时钟域外频：异步外频时即 BCLK2，核心频率按它换算
    private double bclkPll;              // CG PLL 里的基准外频（BCLK1）；与 bclk 不等即为异步外频
    private readonly double p0DefMHz;    // P0 定义频率（P-state 0 寄存器，按 BCLK=100 参考），用于反推真实外频
    private readonly double regBaseMHz;  // 注册表 ~MHz：HAL 启动时实测写入，已含外频
    private readonly string installedMemory;
    private readonly double p0BaseMHz;   // P0 标称基频，用于 ΔAPERF/ΔMPERF×P0 算忙时频率（本机 TSC 不可读）
    private readonly bool hasPtVolt;
    private readonly int perCoreVoltIdx;
    private readonly TelVoltCalib telCalib;
    private const int SingleCcdVoltIdx = 0x4D4 / 4;
    private const int DualCcdVoltIdx   = 0x4F4 / 4;
    // PM Table 头部的全局 VDDCR_CPU 遥测四元组：0xC0 请求 VID / 0xC4 实测电压(TEL) / 0xC8 电流 / 0xCC 功率。
    // 由 0xC4×0xC8=0xCC 验证（SOC 组 0xD4.. 与 MISC 组 0xE8.. 结构相同、验算同样成立），
    // 位于表头，单/双 CCD 版本布局一致。逐核 0x4D4/0x4F4 那组是各核经 LDO 后的 die 电压，
    // 恒低于上游请求 VID 约 10mV，取其最大值并不等于整体 VID。
    private const int CpuVidIdx = 0xC0 / 4;
    private const int CpuTelIdx = 0xC4 / 4;
    /// <summary>身份条／CCD 行／限制条共用的栅格列数，取限制条格数；三条共用同一次取整才能逐条对齐。</summary>
    private const int StripCols = 8;

    private Thread? worker;
    private volatile bool running;

    // 身份条（CPU / 主板 / 内存 / 显卡）与限制条（BCLK / TEL/VID / Vdroop / THM / TDC / EDC / PPT / Fmax）数值标签
    private StatCellControl _cpuVal = null!, _moboVal = null!, _memVal = null!, _gpuVal = null!;
    private StatCellControl _bclkVal = null!, _telVidVal = null!, _vdroopVal = null!, _thmVal = null!, _tdcVal = null!, _edcVal = null!, _pptVal = null!, _fmaxVal = null!;
    private CcdView[] ccdViews = null!;

    public MonitorView()
    {
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
        installedMemory = SystemInfo.GetInstalledMemory();

        // 注册表 ~MHz 由 HAL 在启动时以独立时基实测后写入，已经含外频；P0 定义频率是按 BCLK=100
        // 参考的标称值。两者相除即真实外频，见 ResolveBclk()。
        regBaseMHz = SystemInfo.GetBaseFrequencyMHz();
        try
        {
            lock (RyzenSmu.IoLock)
                (bclkPll, bclk) = ResolveBclk();
        }
        catch { bclkPll = bclk = 0; }
        p0BaseMHz = regBaseMHz > 0 ? regBaseMHz
                  : (p0DefMHz > 0 && bclk > 0 ? p0DefMHz * bclk / 100.0 : 0);
        hasPtVolt   = cpu.info.codeName == Cpu.CodeName.GraniteRidge;
        perCoreVoltIdx = ccds == 1 ? SingleCcdVoltIdx : DualCcdVoltIdx;

        uint tv = 0;
        try { lock (RyzenSmu.IoLock) tv = cpu.GetTableVersion().TableVersion; }
        catch { /* 版本读不到则校准仅本次会话内有效 */ }
        telCalib = new TelVoltCalib(tv);

        BuildUi();
        FillIdentityStrip();
    }

    private const uint P0DefMsr = 0xC0010064;   // P-State 0 Definition

    /// <summary>P-state 0 定义寄存器里的标称频率（按 BCLK=100MHz 参考）。Zen5(家族1Ah+) 为
    /// fid[11:0]×5MHz，更早代际用 fid/dfs 算倍频×100。读不到或该 P-state 未使能返回 0。
    /// 调用方需持有 IoLock。</summary>
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
    /// 取 (BCLK1, CPU 时钟域外频)。cpu.GetBclk() 读的是 CG PLL 配置寄存器，即同步域的基准外频；
    /// 主板走外置时钟发生器时 CPU_CLK 不由该 PLL 定频，异步外频下它仍是 100，与 CPU 实际外频无关。
    /// CPU 侧因此改用 注册表~MHz ÷ P0定义频率 反推——外频档位都是 0.05MHz 的整数倍，按此吸附掉
    /// ~MHz 只有整数精度带来的零头；反推不成立时依次退回 HWiNFO 的 Bus Clock 与 PLL 读数。
    /// 调用方需持有 IoLock。
    /// </summary>
    private (double Pll, double Core) ResolveBclk()
    {
        double pll = 0;
        double? raw = cpu.GetBclk();
        if (raw is > 50 and < 200) pll = raw.Value;

        double core = 0;
        if (regBaseMHz > 0 && p0DefMHz > 0)
        {
            // 范围卡在实际可用的外频区间内：若某代际 P0 定义的不是基频、或 ~MHz 写的是别的口径，
            // 比值会明显跑飞，此时宁可退回下面两级（至少不比现状差）也不显示一个错的外频。
            double derived = regBaseMHz / p0DefMHz * 100.0;
            if (derived is >= 90 and <= 130) core = Math.Round(Math.Round(derived / 0.05) * 0.05, 2);
        }
        if (core <= 0 && HwInfoReader.ReadBusClock() is > 50 and < 200 and double hw) core = hw;
        if (core <= 0) core = pll;

        return (pll, core);
    }

    private void BuildUi()
    {
        BackColor = MonTheme.Bg;
        Font = new Font("Consolas", 9f);
        AutoScroll = false;   // 不做左右滚动，窗口宽度直接容纳监控

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = MonTheme.Bg,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));   // 身份条：CPU|主板 / 内存|显卡（两行）
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // CCD 每核监控
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));   // 限制条

        root.Controls.Add(BuildIdentityStrip(), 0, 0);
        root.Controls.Add(BuildCcdRow(), 0, 1);
        root.Controls.Add(BuildLimitStrip(), 0, 2);
        Controls.Add(root);
    }

    /// <summary>顶部身份条：2×2 两行（CPU|主板 / 内存|显卡），主板等长型号可完整显示。
    /// 每格跨半条栅格，左右分界与 CCD 面板、限制条对齐。</summary>
    private Control BuildIdentityStrip()
    {
        var t = NewStripGrid(rows: 2, bottomMargin: 4);
        int half = StripCols / 2;
        _cpuVal  = StatCellAt(t, 0,    0, "AMD CPU", center: false, span: half);
        _moboVal = StatCellAt(t, half, 0, "主板",    center: false, span: half);
        _memVal  = StatCellAt(t, 0,    1, "内存",    center: false, span: half);
        _gpuVal  = StatCellAt(t, half, 1, "显卡",    center: false, span: half);
        return t;
    }

    /// <summary>底部限制条：BCLK / TEL/VID / Vdroop / THM / TDC / EDC / PPT / Fmax 八格，
    /// 即 StripCols 栅格的一格一列。身份条与 CCD 行跨 4 列，三条共用同一套取整，竖缝逐条对齐。</summary>
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

    /// <summary>建一条 StripCols 列的等宽栅格。三条横条都用它，列边界因而落在同一批像素上——
    /// 各按自己的列数独立取整时，8 列的第 4 列右缘与 2 列的中缝会差几个像素。</summary>
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

    /// <summary>静态身份信息不依赖监控线程，界面建立后立即显示。</summary>
    private void FillIdentityStrip()
    {
        _cpuVal.Text = string.IsNullOrWhiteSpace(cpu.info.cpuName)
            ? SystemInfo.GetCpuName()
            : cpu.info.cpuName.Trim();
        _moboVal.Text = SystemInfo.GetMotherboard();
        _memVal.Text = installedMemory;
        _gpuVal.Text = SystemInfo.GetGpuName();
        UpdateBclkCell();
    }

    /// <summary>BCLK 格：同步外频只显示一个值；异步外频（CPU 域与 PLL 基准不一致）时
    /// 标题改成 BCLK/BCLK2，值显示 "基准/CPU 域"。</summary>
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

    /// <summary>单元格：上方暗色标题 + 下方数值。center 时居中（限制条），否则左对齐（身份条）。返回数值标签供刷新。</summary>
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

    /// <summary>CCD 行：金银核排名（CPPC）+ 各 CCD 面板横向并排。</summary>
    private Control BuildCcdRow()
    {
        // CPPC（金银核排名）：MSR 0xC00102B0 (CPPC_CAP1)
        //   bits[31:24]=Highest, bits[23:16]=Nominal。换算成与事件查看器事件 55
        //   一致的 MaximumPerformancePercent = Highest / Nominal × 100
        uint[] perf = new uint[cores];
        lock (RyzenSmu.IoLock)
        {
            for (int i = 0; i < cores; i++)
            {
                ulong cap1 = ReadMsr(i, 0xC00102B0);
                uint highest = (uint)((cap1 >> 24) & 0xFF);
                uint nominal = (uint)((cap1 >> 16) & 0xFF);
                // 截断（整数除法），与事件 55 MaximumPerformancePercent 逐核一致；四舍五入会多 1
                perf[i] = nominal > 0 ? highest * 100 / nominal : highest;
            }
        }

        // 金银核：按 CPPC 全局排名取前两个，先银后金 —— 排名第一的标银(✦)，第二的标金(★)，
        // 只标这两个（不标铜核）。CPPC 并列同分时按核号从小到大，即同分中的第一个为银、第二个为金。
        // 按核索引定位而非按数值匹配，避免并列同分时标出多个。
        int goldCore = -1, silverCore = -1;
        var ranked = Enumerable.Range(0, (int)cores)
            .Where(i => (slotDisabled.Length <= i || !slotDisabled[i]) && perf[i] > 0)
            .OrderByDescending(i => perf[i]).ThenBy(i => i)
            .ToList();
        if (ranked.Count > 0) silverCore = ranked[0];
        if (ranked.Count > 1) goldCore = ranked[1];

        // 始终显示两栏（与上下两条等宽对齐）；单 CCD 机型时 CCD#1 显示空表而非隐藏。
        // 与身份条／限制条共用 StripCols 栅格，每个面板跨 StripCols/CCD 数 列，竖缝逐条对齐。
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
        // 控件加入尚未显示的 Form 时不一定触发 VisibleChanged；
        // 句柄创建是监控真正可用的可靠时点。
        FillIdentityStrip();
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

    /// <summary>读物理核槽位 i 的第 thread 个 SMT 线程的 MSR（thread 默认 0）。
    /// APERF/MPERF/TSC 是每逻辑线程独立计数的，负载只跑在第二个线程时必须读到它才不会漏。</summary>
    private ulong ReadMsr(int i, uint msr, int thread = 0)
    {
        try
        {
            int os = i >= 0 && i < slotOs.Length ? slotOs[i] : i;
            if (os < 0) return 0;   // 熔丝屏蔽槽无 OS 逻辑核
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

    // 后台采样：每秒一窗。FREQ=HW P-state FID 快照(0xC0010293，回退 ΔAPERF/ΔMPERF×P0)，EFFREQ=有效频率(ΔAPERF/Δt)。
    // APERF/MPERF/TSC 逐 SMT 线程采样（每逻辑线程独立计数，只读线程 0 会漏掉只跑在第二线程上的负载），
    // 每核取最忙线程作为该核读数。只在窗口首尾各采一次（不在 40ms 循环里读），避免高频 affinity 读 MSR
    // 反复唤醒空闲核、把它们拉到 boost 污染频率读数。窗口内仅采 PM Table 每核电压峰值。
    private void Worker()
    {
        int n = (int)cores;
        int tpc = (int)Math.Max(1u, cpu.info.topology.threadsPerCore);

        while (running)
        {
            int lp = n * tpc;
            var startA = new ulong[lp]; var startM = new ulong[lp]; var startT = new ulong[lp];
            var lastA  = new ulong[lp]; var lastM  = new ulong[lp]; var lastT  = new ulong[lp];
            var maxVolt = new double[n];
            var psSnap = new ulong[n];
            float[]? ptSnap = null;
            double maxCpuVid = 0, maxCpuTel = 0;   // 全局 VDDCR_CPU：VID 窗口峰值 + 同帧配对的实测电压

            // 窗口起点：各核每个 SMT 线程的 APERF / MPERF / TSC 累计计数
            lock (RyzenSmu.IoLock)
            {
                for (int i = 0; i < n; i++)
                    for (int t = 0; t < tpc; t++)
                    {
                        int x = i * tpc + t;
                        startA[x] = ReadMsr(i, 0xC00000E8, t);
                        startM[x] = ReadMsr(i, 0xC00000E7, t);
                        startT[x] = ReadMsr(i, 0x10, t);
                    }
            }

            // 窗口内：仅采 PM Table 每核电压峰值（一次 SMU 调用，不逐核唤醒核心）
            var swWin = Stopwatch.StartNew();
            do
            {
                if (hasPtVolt)
                {
                    lock (RyzenSmu.IoLock)
                    {
                        bool pmRefreshed = false;
                        try { pmRefreshed = cpu.RefreshPowerTable() == SMU.Status.OK; }
                        catch { /* 本轮 PM Table 不可用 */ }
                        if (pmRefreshed && cpu.powerTable?.Table is { } tbl && tbl.Length > perCoreVoltIdx + n)
                        {
                            for (int i = 0; i < n; i++)
                            {
                                double v = tbl[perCoreVoltIdx + i];
                                if (v > maxVolt[i]) maxVolt[i] = v;
                            }
                            // TEL 取 VID 峰值那一帧的配对值：两者各自取峰会落在不同帧，
                            // 噪声放大后会出现 TEL > VID 的倒挂（Vdroop 为负）。
                            if (tbl.Length > CpuTelIdx && tbl[CpuVidIdx] > maxCpuVid)
                            {
                                maxCpuVid = tbl[CpuVidIdx];
                                maxCpuTel = tbl[CpuTelIdx];
                            }
                            ptSnap = (float[])tbl.Clone();   // 供 TEL 校准/本地读取（lock 外使用）
                        }
                    }
                }
                Thread.Sleep(40);
            } while (swWin.ElapsedMilliseconds < 500 && running);   // 贴近 HWiNFO ~500ms 刷新节奏

            if (!running) break;
            double winSec = swWin.Elapsed.TotalSeconds;

            // 窗口终点：各核每个 SMT 线程的 APERF / MPERF / TSC
            lock (RyzenSmu.IoLock)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int t = 0; t < tpc; t++)
                    {
                        int x = i * tpc + t;
                        lastA[x] = ReadMsr(i, 0xC00000E8, t);
                        lastM[x] = ReadMsr(i, 0xC00000E7, t);
                        lastT[x] = ReadMsr(i, 0x10, t);
                    }
                    psSnap[i] = ReadMsr(i, 0xC0010293);   // HW P-state 为物理核共享，读线程 0 即可
                }
            }

            var busyFreq = new double[n];
            var effFreq  = new double[n];
            var occ      = new double[n];
            double p0Hz = p0BaseMHz * 1e6;
            for (int i = 0; i < n; i++)
            {
                // 物理核的读数取各 SMT 线程中最忙的那个：单线程负载时另一线程处于 halt、计数几乎不涨，
                // 若只看线程 0 会把「负载跑在第二线程」的核误判为空闲（有效频率≈0、占用≈0、不着色）。
                double bestEff = 0, bestOcc = 0;
                ulong bestA = 0, bestM = 0;
                for (int t = 0; t < tpc; t++)
                {
                    int x = i * tpc + t;
                    ulong dA = lastA[x] - startA[x];
                    ulong dM = lastM[x] - startM[x];
                    ulong dT = lastT[x] - startT[x];

                    // EFFREQ 有效频率（含空闲）：ΔAPERF/Δt
                    double eff = winSec > 0 ? dA / winSec / 1e6 : 0;

                    // 占用率：ΔMPERF/ΔTSC；本机 TSC 不可读(=0) 时用 ΔMPERF/(P0基频×Δt)
                    double oc = dT > 0 ? Math.Min(100.0, (double)dM / dT * 100.0)
                              : (p0Hz > 0 && winSec > 0 ? Math.Min(100.0, dM / (p0Hz * winSec) * 100.0) : 0);

                    if (eff > bestEff) bestEff = eff;
                    if (oc > bestOcc) bestOcc = oc;
                    if (dM > bestM) { bestM = dM; bestA = dA; }
                }

                effFreq[i] = bestEff;
                occ[i] = bestOcc;

                // FREQ 忙时频率(≈ HWiNFO Core Clock)：ΔAPERF/ΔMPERF × P0基频（活动时平均实频）。
                // MPERF 或 P0 不可用时回退有效频率，避免显示为空。
                busyFreq[i] = (bestM > 0 && p0BaseMHz > 0) ? (double)bestA / bestM * p0BaseMHz : effFreq[i];
            }

            // FREQ 优先用 HW P-state FID 快照（MSR 0xC0010293，与 HWiNFO "Core N Clock" 同源同口径）：
            // Zen5(家族1Ah+) 频率 = fid[11:0]×5MHz；更早代际用 fid/dfs 算倍频×100。快照恒为离散 boost 档
            // （如 5725/5450），而 APERF/MPERF 平均含升降频斜坡会偏离档位。MSR 读 0（不可读）时保留平均值回退。
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

            // 若 HWiNFO 在运行并开启共享内存，FREQ 直接采用其 Core Clock 原值，与 HWiNFO 完全一致；否则保留自算忙时频率
            var hwClocks = HwInfoReader.ReadCoreClocks();
            if (hwClocks != null)
                for (int i = 0; i < n; i++)
                {
                    int os = slotOs[i];
                    if (os >= 0 && hwClocks.TryGetValue(os, out double mhz)) busyFreq[i] = mhz;
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
                // AMD CO（设定值，每秒读一次）
                co = new int[n];
                for (int i = 0; i < n; i++)
                {
                    uint ccd = (uint)i / coresPerCcd, core = (uint)i % coresPerCcd;
                    try
                    {
                        uint? raw = cpu.GetPsmMarginSingleCore(cpu.MakeCoreMask(core, ccd, 0));
                        co[i] = raw.HasValue ? (short)(raw.Value & 0xffff) : 0;
                    }
                    catch { co[i] = 0; }
                }

                // 温度
                ccdTemp = new float[ccds];
                for (uint c = 0; c < ccds; c++)
                {
                    try { ccdTemp[c] = cpu.GetSingleCcdTemperature(c) ?? 0; }
                    catch { ccdTemp[c] = 0; }
                }
                try { tctl = cpu.GetCpuTemperature() ?? 0; }
                catch { tctl = 0; }

                pptCurrent = tdcCurrent = edcCurrent = 0;
                pptLimit = tdcLimit = edcLimit = thmLimit = 0;
                if (hasPtVolt && cpu.powerTable?.Table is { } tbl && tbl.Length > RyzenSmu.GnrEdcCurrentIdx)
                {
                    pptCurrent = tbl[RyzenSmu.GnrPptCurrentIdx];
                    pptLimit   = (int)Math.Round(tbl[RyzenSmu.GnrPptLimitIdx]);
                    tdcCurrent = tbl[RyzenSmu.GnrTdcCurrentIdx];
                    tdcLimit   = (int)Math.Round(tbl[RyzenSmu.GnrTdcLimitIdx]);
                    edcCurrent = tbl[RyzenSmu.GnrEdcCurrentIdx];
                    edcLimit   = (int)Math.Round(tbl[RyzenSmu.GnrEdcLimitIdx]);
                    thmLimit   = (int)Math.Round(tbl[RyzenSmu.GnrThmLimitIdx]);
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
                catch { /* 保留最近一次有效 BCLK */ }

                fclk = cpu.powerTable?.FCLK ?? 0;
                uclk = cpu.powerTable?.UCLK ?? 0;
            }

            // TEL/VID：直读 PM Table 头部的全局 VDDCR_CPU 遥测（0xC0 请求 VID / 0xC4 实测电压），
            // 与 HYDRA 的 VID / TEL 同源，不依赖 HWiNFO。
            double peakVid = maxCpuVid is > 0.3 and < 2.0 ? maxCpuVid : 0;
            double telVolt = peakVid > 0 && maxCpuTel is > 0.3 and < 2.0 ? maxCpuTel : 0;
            // 只有 VID 与 TEL 同源于全局遥测四元组时 Vdroop 才有意义；走回退路径时两者口径不同，不显示。
            bool vdroopValid = peakVid > 0 && telVolt > 0;

            // 回退：表里读不到全局遥测（非 GraniteRidge / 表布局不同）时，VID 用各核峰值近似，
            // TEL 用 HWiNFO 的 SVI3 读数并喂给校准器收敛出本地索引。
            if (peakVid <= 0)
                for (int i = 0; i < n; i++) if (maxVolt[i] > peakVid) peakVid = maxVolt[i];
            if (telVolt <= 0)
            {
                double telHw = HwInfoReader.ReadCpuTelemetryVoltage() ?? 0;
                if (ptSnap != null)
                {
                    if (telCalib.Index < 0 && telHw > 0) telCalib.Feed(ptSnap, telHw);
                    if (telCalib.Index >= 0 && telCalib.Index < ptSnap.Length) telVolt = ptSnap[telCalib.Index];
                }
                if (telVolt <= 0) telVolt = telHw;
            }

            var fFreq = busyFreq; var fEff = effFreq; var fVolt = maxVolt; var fCo = co; var fOcc = occ; var fTemp = ccdTemp;
            try
            {
                if (!IsHandleCreated) continue;
                BeginInvoke(() =>
                {
                    _memVal.Text    = FormatMemory(fclk, uclk);
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
                            if ((uint)i / coresPerCcd == c) { sum += fOcc[i]; cnt++; }
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

// 单个 CCD 面板：标题 + 表格（行=CPPC/AMD CO/EFFREQ/FREQ/VID，列=各核）。
// 表格列用 Fill 模式填满半幅面板，使左右边缘与上方身份条/下方限制条对齐。
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
            Dock = DockStyle.Fill,   // 纵向填满面板；行高在 LayoutRows 里按可用高度分配，避免 2K 屏下方留白
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = HeaderH,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,   // 列按权重填满面板宽度
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
        // 开启 DataGridView 内部双缓冲，避免每 500ms 刷新单元格时闪烁
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

        string[] rowNames = { "CPPC", "AMD CO", "EFFREQ", "FREQ", "VID" };
        foreach (var rn in rowNames)
        {
            int r = dgv.Rows.Add();
            var c0 = dgv.Rows[r].Cells[0];
            c0.Value = rn;
            c0.Style.ForeColor = MonTheme.Dim;
            c0.Style.Alignment = DataGridViewContentAlignment.MiddleLeft;
        }

        // CPPC 行为静态硬件值，填一次（空 CCD 不填，保持空白）
        if (exists)
            for (int k = 0; k < coreCount; k++)
            {
                int gc = baseCore + k;
                dgv.Rows[RowCppc].Cells[k + 1].Value = slotDisabled.Length > gc && slotDisabled[gc] ? "-" : (perf.Length > gc ? perf[gc] : 0).ToString();
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

        Container.Controls.Add(dgv);     // Dock=Fill 先加，占标题下方剩余空间
        Container.Controls.Add(title);   // Dock=Top 后加，置于顶部

        dgv.SizeChanged += (_, _) => LayoutRows();   // 面板高度变化（含 2K/DPI 缩放）时重分配行高
        // SizeChanged 通常早于 handle 创建，那一次 LayoutRows 会因 !IsHandleCreated 直接返回；
        // 此后窗口尺寸不变就不会再有 SizeChanged，行高永远停在默认值、面板下方留白。
        // handle 就绪与面板真正显示时各补算一次，覆盖启动即最终尺寸的情形。
        dgv.HandleCreated += (_, _) => LayoutRows();
        dgv.VisibleChanged += (_, _) => { if (dgv.Visible) LayoutRows(); };
    }

    /// <summary>把表头以外的可用高度平均分给各数据行，使表格纵向填满面板、消除下方留白。
    /// 极小窗口下不低于原始行高（宁可裁剪也不重叠）。</summary>
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
            dgv.Rows[RowEff].Cells[col].Value  = eff[gc]  > 0 ? eff[gc].ToString("F0")  : "-";
            dgv.Rows[RowFreq].Cells[col].Value = freq[gc] > 0 ? freq[gc].ToString("F0") : "-";
            dgv.Rows[RowVid].Cells[col].Value  = volt[gc] > 0 ? volt[gc].ToString("F3") : "-";

            // 该核有负载即把列头染红，占用越高越红（全核满载=整片红，单核负载=只有那一列红）
            var hc = dgv.Columns[col].HeaderCell;
            Color lc = LoadHeaderColor(gc < occ.Length ? occ[gc] : 0);
            if (hc.Style.BackColor != lc) hc.Style.BackColor = lc;
        }
    }

    /// <summary>按占用率(0-100)把列头底色由默认灰渐变到红。低于 8% 视为空闲，保持原色避免轻微波动泛红。</summary>
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
