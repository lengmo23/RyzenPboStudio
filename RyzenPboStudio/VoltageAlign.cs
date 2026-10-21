using System.Text;
using ZenStates.Core;

namespace RyzenPboStudio;

internal enum AlignTarget { Gold, Silver }

/// <summary>Automatic voltage alignment: with all CO at 0 the 金核 / 银核 of each CCD should need the least voltage,
/// so every other core of that CCD gets negative CO until its Single Core Boost voltage, normalised to the target's
/// frequency, is within the noise floor of the target. Each CCD is aligned on its own. CO only moves negative, the
/// target stays 0, and every write goes through Tuning.Apply.</summary>
internal static class VoltageAlign
{
    private const int MaxIterations = 4;
    private const int MaxStepPerIteration = 10;   // CO counts per core per iteration

    // Initial CO-to-voltage estimate. 3–5 mV per count is the usual figure for Zen3/4/5; taking the upper end makes the
    // first step undershoot, which matters because moves are negative-only and an overshoot is never undone.
    private const double InitialK = 0.005;   // V per CO count
    private const double KMin = 0.002, KMax = 0.010;
    private const int MinSecantSpan = 2;     // a 1-count move is buried in measurement noise, keep the old k

    private const int Zen3CoMin = -30, Zen4CoMin = -50;

    private const string Mode = "ALIGN", Reason = "voltage-align";

    private static readonly string[] ResultColumns =
        { "CCD", "Core", "排名", "角色", "VID 全0", "ΔV 全0 (mV)", "VID 最终", "ΔV 最终 (mV)", "最终 CO", "备注" };

    private static string Name(AlignTarget t) => t == AlignTarget.Gold ? "金核" : "银核";

    /// <summary>Zen3 accepts CO down to -30, Zen4 / Zen5 down to -50.</summary>
    private static int GenerationCoMin()
    {
        lock (RyzenSmu.IoLock)
        {
            var c = RyzenSmu.SharedCpu.info.codeName;
            return c is Cpu.CodeName.Vermeer or Cpu.CodeName.Cezanne or Cpu.CodeName.Chagall
                     or Cpu.CodeName.Milan or Cpu.CodeName.Rembrandt
                ? Zen3CoMin : Zen4CoMin;
        }
    }

    /// <summary>Confirmation dialog with the target choice (default 金核).</summary>
    public static bool Confirm(IWin32Window owner, out AlignTarget target)
    {
        target = AlignTarget.Gold;
        int cores = Enumerable.Range(0, RyzenSmu.SlotCount).Count(s => !RyzenSmu.IsSlotDisabled(s));
        int minutes = (int)Math.Ceiling(cores * SingleCoreBoost.SecondsPerCore * (1 + MaxIterations) / 60.0);

        var text = new StringBuilder();
        text.Append("电压对齐将依次执行：\n\n");
        text.Append("1. 把所有核心的 Curve Optimizer 设为 0（Curve Shaper 保持不变）；\n");
        text.Append("2. 逐核运行单核负载（Single Core Boost），测量各核的单核电压；\n");
        text.Append($"3. 每个 CCD 分别以所选目标核为基准，给电压偏高的核心逐步加负压并重新测量，最多 {MaxIterations} 轮。\n\n");
        text.Append($"只会加负压，不会加正压；目标核保持 0；每轮每核最多 {MaxStepPerIteration} 档。\n");
        text.Append($"预计最长约 {minutes} 分钟，期间请不要运行其他程序。\n\n");
        text.Append("对齐结果会保留在 CPU 上并填入 CO 编辑器，但不会自动保存。\n");
        text.Append("对齐出的负压没有经过稳定性验证，必须再通过压力测试；单核负载下也可能死机重启（重启后 CO 写入失效）。");
        if (Workspace.WasInterrupted())
            text.Append("\n\n注意：存在上次压测异常中断的恢复点。对齐写入的负压会成为恢复基准，之后开始测试时将在对齐结果上 +" +
                        $"{Config.StepOnError} 续跑。");

        using var dlg = new Form
        {
            Text = "电压对齐",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9.5F),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
        };
        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Bg,
        };
        body.Controls.Add(new Label { Text = text.ToString(), AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 12) });
        body.Controls.Add(new Label { Text = "对齐目标（每个 CCD 各自选取）", AutoSize = true, ForeColor = Theme.TextLo });
        var gold = new RadioButton { Text = "金核：CCD 内 CPPC 排名第 2", AutoSize = true, Checked = true };
        var silver = new RadioButton { Text = "银核：CCD 内 CPPC 排名第 1", AutoSize = true };
        body.Controls.Add(gold);
        body.Controls.Add(silver);
        body.Controls.Add(new Label
        {
            Text = "CPPC 值相同时按核号排序，靠前的为银核；CPPC 不可用时（如 5000 系未开 CPPC），改用全 0 下 VID 最低（银核）/ 次低（金核）的核。",
            AutoSize = true,
            ForeColor = Theme.TextLo,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 4, 0, 12),
        });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, BackColor = Theme.Bg };
        Button Btn(string t, DialogResult r) => new()
        {
            Text = t,
            DialogResult = r,
            AutoSize = true,
            Padding = new Padding(10, 2, 10, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextHi,
        };
        var ok = Btn("开始对齐", DialogResult.OK);
        var cancel = Btn("取消", DialogResult.Cancel);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        body.Controls.Add(buttons);
        dlg.Controls.Add(body);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
        target = silver.Checked ? AlignTarget.Silver : AlignTarget.Gold;
        return true;
    }

    /// <summary>Runs the alignment. Returns the offsets last applied to the CPU (also after cancel), or null when
    /// nothing was applied.</summary>
    public static List<int>? Run(MonitorView? monitor, AlignTarget target, int editorMin, ResultsWindow win, CancellationToken ct)
    {
        Log.Write($"电压对齐开始：目标 {Name(target)}，各 CCD 独立对齐");
        using var scb = SingleCoreBoost.Open(monitor);
        if (scb == null) return null;

        int n = scb.SlotCount;
        int coMin = Math.Max(editorMin, GenerationCoMin());
        var slots = Enumerable.Range(0, n).Where(scb.IsUsable).ToList();
        var ccds = slots.GroupBy(scb.CcdOf).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.ToList());

        var offsets = Enumerable.Repeat(0, n).ToList();
        if (!Apply(offsets, "全部核心 CO 置 0（基线）"))
            return null;

        var baseline = MeasurePass(scb, slots, "全0基线", win, ct);
        if (ct.IsCancellationRequested)
            return Finish(scb, offsets, baseline, baseline, new Dictionary<int, int>(), 0, coMin, new HashSet<int>(), win, true);

        // Slope from the all-0 pass only: once CO differs per core the cross-core fit no longer measures the F/V curve
        double slope = FvAnalysis.Slope(baseline.Values.ToList(), out bool fallback);
        Log.Write($"F/V 斜率 {slope * 1000:F2} mV/MHz（{(fallback ? "自算值不可信，使用默认值" : "全 0 基线组内回归")}），" +
                  $"噪声底 {FvAnalysis.NoiseFloor * 1000:F0} mV，CO 下限 {coMin}");

        var targets = new Dictionary<int, int>();
        foreach (var (ccd, cores) in ccds)
        {
            int t = PickTarget(scb, cores, baseline, target, out string how);
            if (t < 0 || !baseline.TryGetValue(t, out var tr) || !tr.Valid)
            {
                Log.Write($"CCD{ccd}：找不到可用的对齐目标（{how}，或目标核测量无效），本 CCD 不调整", "WARN");
                continue;
            }
            targets[ccd] = t;
            Log.Write($"CCD{ccd}：对齐目标 Core {t}（{how}），VID {tr.Vid:F3} V @ {tr.Freq:F0} MHz");

            var lower = cores.Where(s => s != t && baseline.TryGetValue(s, out var r) && r.Valid
                                         && FvAnalysis.DeltaV(r, tr, slope) < -FvAnalysis.NoiseFloor).ToList();
            if (lower.Count > 0)
                Log.Write($"CCD{ccd}：全 0 下 Core {string.Join("、", lower)} 的同频电压比目标核低 {FvAnalysis.NoiseFloor * 1000:F0} mV 以上，" +
                          "目标核并非本 CCD 电压最低的核；这些核保持 0 不动", "WARN");
        }

        var k = slots.ToDictionary(s => s, _ => InitialK);
        var latest = new Dictionary<int, ScbRow>(baseline);
        var clamped = new HashSet<int>();
        int iterations = 0;

        for (int iter = 1; iter <= MaxIterations && !ct.IsCancellationRequested; iter++)
        {
            var next = new List<int>(offsets);
            var usedDv = new Dictionary<int, double>();
            foreach (var (ccd, t) in targets)
            {
                if (!latest.TryGetValue(t, out var tr) || !tr.Valid)
                {
                    Log.Write($"CCD{ccd}：目标核 Core {t} 本轮测量无效，本 CCD 本轮不调整", "WARN");
                    continue;
                }
                foreach (int s in ccds[ccd])
                {
                    if (s == t || !latest.TryGetValue(s, out var r) || !r.Valid) continue;
                    double dv = FvAnalysis.DeltaV(r, tr, slope);
                    if (dv <= FvAnalysis.NoiseFloor) continue;
                    if (offsets[s] <= coMin) { clamped.Add(s); continue; }

                    int step = Math.Clamp((int)Math.Round(dv / k[s]), 1, MaxStepPerIteration);
                    next[s] = Math.Max(offsets[s] - step, coMin);
                    if (next[s] == coMin) clamped.Add(s);
                    usedDv[s] = dv;
                }
            }
            if (usedDv.Count == 0) break;

            iterations = iter;
            Log.Write($"对齐第 {iter} 轮：" + string.Join("，", usedDv.Keys.OrderBy(s => s).Select(s =>
                $"Core {s} {offsets[s]}→{next[s]}（ΔV {usedDv[s] * 1000:F1} mV，k {k[s] * 1000:F1} mV/档）")));

            var before = offsets;
            offsets = next;
            if (!Apply(offsets, $"第 {iter} 轮"))
                break;

            // Re-measure every core of the touched CCDs, target included, so drift between passes cancels out
            var touched = usedDv.Keys.Select(scb.CcdOf).Distinct().OrderBy(c => c).SelectMany(c => ccds[c]).ToList();
            var pass = MeasurePass(scb, touched, $"对齐{iter}", win, ct);
            foreach (var (s, r) in pass) latest[s] = r;
            if (ct.IsCancellationRequested) break;

            // Secant update of V per CO count from the measured change. A non-positive response is noise, not a
            // reason to take bigger steps, so the previous k is kept then.
            foreach (var (s, dvBefore) in usedDv)
            {
                int span = before[s] - offsets[s];
                int t = targets[scb.CcdOf(s)];
                if (span < MinSecantSpan || !pass.TryGetValue(s, out var r) || !r.Valid
                    || !pass.TryGetValue(t, out var tr) || !tr.Valid) continue;
                double measured = (dvBefore - FvAnalysis.DeltaV(r, tr, slope)) / span;
                if (measured > 0) k[s] = Math.Clamp(measured, KMin, KMax);
            }
        }

        return Finish(scb, offsets, baseline, latest, targets, slope, coMin, clamped, win, ct.IsCancellationRequested, iterations);
    }

    private static bool Apply(List<int> offsets, string what)
    {
        bool ok = Tuning.Apply(offsets, Mode, null, Reason);
        Log.Write($"电压对齐写入 CO（{what}）: [{string.Join(", ", offsets)}]" + (ok ? "" : "（写入异常，CPU 上的实际 CO 请点 Refresh 核对）"),
                  ok ? "INFO" : "ERROR");
        return ok;
    }

    private static Dictionary<int, ScbRow> MeasurePass(SingleCoreBoost scb, List<int> slots, string tag, ResultsWindow win, CancellationToken ct)
    {
        win.SetTable(SingleCoreBoost.Columns);
        int done = 0;
        var rows = scb.Measure(slots,
            s => win.SetStatus($"{tag}：测量 Core {s}（{done + 1}/{slots.Count}）"),
            r =>
            {
                done++;
                win.AddRow(scb.Cells(r), SingleCoreBoost.RowColor(r));
                scb.LogRow(tag, r);
            }, ct);
        return rows.ToDictionary(r => r.Slot);
    }

    /// <summary>Target by CPPC rank inside the CCD; without CPPC, the lowest / second-lowest VID measured at all-0.</summary>
    private static int PickTarget(SingleCoreBoost scb, List<int> cores, Dictionary<int, ScbRow> baseline, AlignTarget target, out string how)
    {
        int want = target == AlignTarget.Gold ? 2 : 1;   // 银核 = rank 1, 金核 = rank 2, as in the monitor
        int byRank = cores.FirstOrDefault(s => scb.CcdRank(s) == want, -1);
        if (byRank >= 0)
        {
            how = $"{Name(target)}，CPPC 排名第 {want}";
            return byRank;
        }
        var byVid = cores.Where(s => baseline.TryGetValue(s, out var r) && r.Valid)
                         .OrderBy(s => baseline[s].Vid).ThenBy(s => s).ToList();
        how = $"CPPC 不可用，取全 0 下 VID {(want == 1 ? "最低" : "次低")}的核";
        return byVid.Count >= want ? byVid[want - 1] : -1;
    }

    private static List<int> Finish(SingleCoreBoost scb, List<int> offsets, Dictionary<int, ScbRow> baseline,
                                    Dictionary<int, ScbRow> latest, Dictionary<int, int> targets, double slope, int coMin,
                                    HashSet<int> clamped, ResultsWindow win, bool cancelled, int iterations = 0)
    {
        win.SetTable(ResultColumns);
        var sb = new StringBuilder();
        sb.Append(cancelled
            ? "电压对齐已取消：CPU 上保留最后一次写入的 CO"
            : $"电压对齐完成（{iterations} 轮调整）");

        var unconverged = new List<int>();
        foreach (int s in Enumerable.Range(0, scb.SlotCount).Where(scb.IsUsable))
        {
            int ccd = scb.CcdOf(s);
            bool isTarget = targets.TryGetValue(ccd, out int t) && t == s;
            baseline.TryGetValue(s, out var r0);
            latest.TryGetValue(s, out var r1);
            ScbRow? t0 = targets.ContainsKey(ccd) && baseline.TryGetValue(t, out var a) ? a : null;
            ScbRow? t1 = targets.ContainsKey(ccd) && latest.TryGetValue(t, out var b) ? b : null;

            double? dv0 = !isTarget && r0 is { Valid: true } && t0 is { Valid: true } ? FvAnalysis.DeltaV(r0, t0, slope) : null;
            double? dv1 = !isTarget && r1 is { Valid: true } && t1 is { Valid: true } ? FvAnalysis.DeltaV(r1, t1, slope) : null;

            string note;
            if (isTarget) note = "对齐目标（保持 0）";
            else if (!targets.ContainsKey(ccd)) note = cancelled ? "已取消" : "本 CCD 无对齐目标，未调整";
            else if (dv1 is not { } d1) note = "测量无效";
            else if (dv0 < -FvAnalysis.NoiseFloor && offsets[s] == 0) note = "低于目标，保持 0";
            else if (d1 <= FvAnalysis.NoiseFloor)
                note = offsets[s] == 0 ? "无需调整" : d1 < -FvAnalysis.NoiseFloor ? $"已对齐（过冲 {-d1 * 1000:F1} mV）" : "已对齐";
            else if (clamped.Contains(s)) note = $"到达 CO 下限 {coMin}";
            else note = cancelled ? "已取消，未对齐" : "未收敛（已达最大轮数）";

            if (!isTarget && dv1 > FvAnalysis.NoiseFloor) unconverged.Add(s);

            win.AddRow(new[]
            {
                ccd.ToString(),
                s.ToString(),
                SingleCoreBoost.RankLabel(scb.CcdRank(s)),
                isTarget ? "目标" : "",
                r0 is { Valid: true } ? $"{r0.Vid:F3}" : "-",
                dv0 is { } x0 ? $"{x0 * 1000:F1}" : "-",
                r1 is { Valid: true } ? $"{r1.Vid:F3}" : "-",
                dv1 is { } x1 ? $"{x1 * 1000:F1}" : "-",
                offsets[s].ToString(),
                note,
            }, isTarget ? MonTheme.Gold : clamped.Contains(s) || unconverged.Contains(s) ? Theme.Warn : null);
        }

        sb.Append($"\n最终 CO: [{string.Join(", ", offsets)}]");
        if (clamped.Count > 0) sb.Append($"\n到达 CO 下限 {coMin} 的核心：Core {string.Join("、", clamped.OrderBy(s => s))}");
        if (unconverged.Count > 0) sb.Append($"\n仍高于目标 {FvAnalysis.NoiseFloor * 1000:F0} mV 以上的核心：Core {string.Join("、", unconverged)}");
        sb.Append("\n结果已填入 CO 编辑器，未保存到 co_profile.json。对齐后的负压必须通过压力测试验证稳定性后再保存使用。");

        string text = sb.ToString();
        foreach (string line in text.Split('\n')) Log.Write(line, cancelled || unconverged.Count > 0 ? "WARN" : "INFO");
        win.SetSummary(text);
        return offsets;
    }
}
