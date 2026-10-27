using System.Text;
using ZenStates.Core;

namespace RyzenPboStudio;

/// <summary>Aligns each CCD's cores to its highest-ranked, lowest-voltage core at a common frequency.
/// The target stays at CO 0 during voltage alignment; frequency probing then shifts the CCD together.
/// All CO writes go through Tuning.Apply.</summary>
internal static class VoltageAlign
{
    private const int MaxIterations = 8;
    private const int MaxStepPerIteration = 10;   // negative CO counts per core per iteration
    private const int MaxStepPerIterationPositive = 4;  // a positive start means the margin below it is thin
    private const double OcBclkMHz = 100.2;       // CPU BCLK above this counts as raised; leaves room for 100.05 readings
    private const int MaxStartCo = 30;            // same ceiling as the CO editor
    private const int OvershootBackoff = 1;       // a core overshot below the target gets this many counts back per iteration
    private const int CoarseFrequencyStep = 4;
    private const int FineFrequencyStep = 2;
    private const int MaxCoarseRounds = 12;
    private const int PlateauRounds = 2;
    private const double PlateauGainMHz = 10;
    private const double NearPeakMHz = 5;
    private const int FrequencyBackoff = 2;   // final CCD-wide shift = deepest valid shift minus this

    // Per-core frequency alignment after the plateau search
    private const double FreqAlignTolMHz = 15;      // within this of the CCD's best core counts as aligned
    private const int FreqAlignStep = 2;             // extra CO counts per lagging core per round
    private const int MaxFreqAlignRounds = 4;        // so at most 8 counts beyond the CCD-wide shift
    private const double FreqAlignMinGainMHz = 5;    // one step gaining less is kept on probation for one more step
    private const double FreqAlignMinGain2StepMHz = 10;   // two steps gaining less: not voltage-limited, undo both

    // Start at 5 mV per CO count to avoid a large voltage overshoot on the first step.
    private const double InitialK = 0.005;   // V per CO count
    private const double KMin = 0.002, KMax = 0.010;
    private const int MinSecantSpan = 2;     // a 1-count move is buried in measurement noise, keep the old k

    private const int Zen3CoMin = -30, Zen4CoMin = -50;

    private const string Mode = "ALIGN", Reason = "voltage-align";

    private static string[] ResultColumns(int startCo)
    {
        string start = startCo == 0 ? "全0" : "起点";
        return new[] { "CCD", "Core", "排名", "角色", $"Core VID {start}", $"ΔV {start} (mV)", "Core VID 最终", "ΔV 最终 (mV)", "最终 CO", "备注" };
    }

    private static bool IsBclkRaised(double bclk) => bclk > OcBclkMHz;

    private static string CoLabel(int co) => co > 0 ? $"+{co}" : co.ToString();

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

    /// <summary>Confirmation dialog; the target core is chosen from the starting measurement, not by the user.
    /// Under raised BCLK the user sets the common starting CO, since the positive CO a core needs depends on the BCLK.</summary>
    public static bool Confirm(IWin32Window owner, MonitorView? monitor, out int startCo)
    {
        startCo = 0;
        double bclk = SingleCoreBoost.ResolveBclk(monitor);
        bool raised = IsBclkRaised(bclk);
        int cores = Enumerable.Range(0, RyzenSmu.SlotCount).Count(s => !RyzenSmu.IsSlotDisabled(s));
        int ccdCount = Math.Max(1, (int)RyzenSmu.SharedCpu.info.topology.ccds);
        int minutes = (int)Math.Ceiling((cores * (2 + MaxIterations + MaxFreqAlignRounds) + ccdCount * 3 * (1 + MaxCoarseRounds + 3))
                                             * SingleCoreBoost.SecondsPerCore / 60.0);

        var text = new StringBuilder();
        text.Append("【开始前必看】\n");
        text.Append("· 请先保存正在编辑的文档和工作进度，并关闭不需要的程序。测试过程中可能出现冻屏死机，未保存的数据将会丢失；\n");
        text.Append("· 请关闭浏览器、游戏、下载工具及其他后台程序，并暂停 Windows 更新、杀毒扫描等可能在后台运行的任务；\n");
        text.Append("· 测试期间请勿操作电脑。任何额外负载都会干扰单核测量，导致结果失真。\n\n");

        text.Append("电压对齐将按以下流程自动执行：\n");
        text.Append(raised ? "1. 将全部核心的 CO 统一设为下方的起点 CO，CS 保持不变；\n" : "1. 将全部核心的 CO 归零，CS 保持不变；\n");
        text.Append("2. 运行 Single Core Boost，逐核采集频率与电压，并为每个 CCD 选定基准核；\n");
        text.Append($"3. 电压对齐：对电压高于基准核的核心逐步降低 CO，使其向基准核收敛（最多 {MaxIterations} 轮）；\n");
        text.Append("4. 频率对齐：以 CCD 为单位整体降低 CO，探明单核频率平台；复测全部核心后，再对频率偏低的核心单独微调。\n");
        text.Append($"预计耗时最长约 {minutes} 分钟。");

        // What the result means, set apart in the warning colour between the procedure and the risk statement.
        var note = new StringBuilder();
        note.Append("【结果说明】\n");
        note.Append("· 电压对齐让各核心在单核负载下的电压趋于一致，并非寻找每个核心的极限 CO；\n");
        note.Append("· 对齐结果对应单核测试负载下的电压，全核负载与日常使用的电压需求会有所不同；\n");
        note.Append("· 请以对齐结果为基础，根据压力测试结果和实际使用情况，自行整体增加或减少全核 CO。");

        var risk = new StringBuilder();
        risk.Append("【风险声明】\n");
        risk.Append("· 电压对齐过程中会向 CPU 写入 CO，CPU 电压过低可能导致系统不稳定、冻屏死机，重启后本次写入的 CO 自动失效；\n");
        risk.Append("· 对齐结果仅基于单核负载测量，尚未经过稳定性验证，保存使用前务必完成压力测试；\n");
        risk.Append("· 结果将填入 CO 编辑器，不会自动保存；\n");
        risk.Append("· 降压与超频存在硬件风险，由此造成的数据丢失或硬件损坏由用户自行承担。");
        if (Workspace.WasInterrupted())
            risk.Append($"\n\n注意：检测到上次压测中断的恢复点。之后开始测试时，将以本次对齐结果为基础，统一回退 {Config.StepOnError} 档后继续。");

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
        dlg.HandleCreated += (_, _) => DarkTitleBar.Apply(dlg.Handle);
        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Bg,
        };
        body.Controls.Add(new Label { Text = text.ToString(), AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 12) });
        body.Controls.Add(new Label { Text = note.ToString(), AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Theme.Warn, Margin = new Padding(0, 0, 0, 12) });
        body.Controls.Add(new Label { Text = risk.ToString(), AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 12) });

        NumericUpDown? startBox = null;
        if (raised)
        {
            // Default to the highest positive CO now in effect, usually what the BIOS needs for this BCLK
            int current = Math.Max(0, RyzenSmu.ReadOffsets(RyzenSmu.SlotCount).DefaultIfEmpty(0).Max());
            body.Controls.Add(new Label
            {
                Text = $"检测到核心外频 {bclk:F2} MHz。外频提高后部分核心可能需要正 CO 才能稳定，请填写起点 CO：" +
                       "对齐从全部核心统一设为该值开始，结果不会高于该值；起点大于 0 时单步下调上限为 " +
                       $"{MaxStepPerIterationPositive} 档。默认值为当前生效 CO 中的最大正值。",
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                ForeColor = Theme.Warn,
                Margin = new Padding(0, 0, 0, 6),
            });
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 12) };
            row.Controls.Add(new Label { Text = "起点 CO", AutoSize = true, Margin = new Padding(0, 5, 8, 0) });
            startBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = MaxStartCo,
                Value = Math.Min(current, MaxStartCo),
                Width = 58,
                BackColor = Theme.SurfaceAlt,
                ForeColor = Theme.TextHi,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font(Theme.MonoFamily, 10F),
            };
            row.Controls.Add(startBox);
            body.Controls.Add(row);
        }

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
        startCo = startBox != null ? (int)startBox.Value : 0;
        return true;
    }

    /// <summary>Runs the alignment. Returns the offsets last applied to the CPU (also after cancel), or null when
    /// nothing was applied.</summary>
    public static List<int>? Run(MonitorView? monitor, int editorMin, int startCo, ResultsPanel win, CancellationToken ct)
    {
        Log.Write("电压对齐开始");
        using var scb = SingleCoreBoost.Open(monitor);
        if (scb == null) return null;

        int n = scb.SlotCount;
        int coMin = Math.Max(editorMin, GenerationCoMin());
        var slots = Enumerable.Range(0, n).Where(scb.IsUsable).ToList();
        var ccds = slots.GroupBy(scb.CcdOf).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.ToList());
        int maxStep = startCo > 0 ? MaxStepPerIterationPositive : MaxStepPerIteration;
        if (IsBclkRaised(scb.Bclk))
            Log.Write($"核心外频 {scb.Bclk:F2} MHz，起点 CO {CoLabel(startCo)}，单步下调上限 {maxStep} 档");

        // Every core starts at the same CO so the cross-core F/V fit stays valid; the start write is not logged,
        // later writes log only the cores they change relative to it
        var offsets = Enumerable.Repeat(startCo, n).ToList();
        if (!Tuning.Apply(offsets, Mode, null, Reason))
        {
            Log.Write($"CO 置 {CoLabel(startCo)} 失败", "ERROR");
            return null;
        }
        lastApplied = new List<int>(offsets);

        var stopped = new HashSet<int>();
        var baseline = MeasurePass(scb, slots, $"基准测量（CO 全 {CoLabel(startCo)}）", win, ct);
        if (ct.IsCancellationRequested)
            return Finish(scb, offsets, baseline, baseline, new Dictionary<int, int>(), 0, coMin, new HashSet<int>(), stopped, startCo, win, true);

        // Slope from the common-start pass only: once CO differs per core the cross-core fit no longer measures the F/V curve
        double slope = FvAnalysis.Slope(baseline.Values.ToList(), out _);
        if (baseline.Values.Any(r => r.Valid && r.CoreVid <= 0)) Log.Write("部分核心无 Core VID，改用 VID", "WARN");

        var targets = new Dictionary<int, int>();
        foreach (var (ccd, cores) in ccds)
        {
            int t = PickTarget(scb, cores, baseline, slope);
            if (t < 0 || !baseline.TryGetValue(t, out var tr) || !tr.Valid)
            {
                Log.Write($"CCD{ccd}：无可用目标核，跳过", "WARN");
                continue;
            }
            targets[ccd] = t;
            Log.Write($"CCD{ccd} 目标核：Core {t}，{tr.AlignV:F3} V @ {tr.Freq:F0} MHz");
        }

        var k = slots.ToDictionary(s => s, _ => InitialK);
        var latest = new Dictionary<int, ScbRow>(baseline);
        var clamped = new HashSet<int>();
        int iterations = 0;

        // Last CO each core measured without computation errors, with that measurement
        var lastGood = new Dictionary<int, (int Co, ScbRow Row)>();
        foreach (var (s, r) in baseline)
        {
            if (!r.Valid) continue;
            if (r.Errors == 0) lastGood[s] = (startCo, r);
            else if (stopped.Add(s)) Log.Write($"Core {s}：起点 CO {CoLabel(startCo)} 下计算出错，不做下调", "WARN");
        }

        for (int iter = 1; iter <= MaxIterations && !ct.IsCancellationRequested; iter++)
        {
            var next = new List<int>(offsets);
            var usedDv = new Dictionary<int, double>();
            foreach (var (ccd, t) in targets)
            {
                if (!latest.TryGetValue(t, out var tr) || !tr.Valid)
                {
                    Log.Write($"CCD{ccd}：目标核 Core {t} 测量无效，本轮跳过", "WARN");
                    continue;
                }
                foreach (int s in ccds[ccd])
                {
                    if (s == t || stopped.Contains(s) || !latest.TryGetValue(s, out var r) || !r.Valid) continue;
                    double dv = FvAnalysis.DeltaV(r, tr, slope);
                    // Overshot below the target: back off one count per round, never above the start
                    if (dv < -FvAnalysis.NoiseFloor && offsets[s] < startCo)
                    {
                        next[s] = offsets[s] + OvershootBackoff;
                        clamped.Remove(s);
                        usedDv[s] = dv;
                        continue;
                    }
                    if (dv <= FvAnalysis.NoiseFloor) continue;
                    if (offsets[s] <= coMin) { clamped.Add(s); continue; }

                    int step = Math.Clamp((int)Math.Round(dv / k[s]), 1, maxStep);
                    next[s] = Math.Max(offsets[s] - step, coMin);
                    if (next[s] == coMin) clamped.Add(s);
                    usedDv[s] = dv;
                }
            }
            if (usedDv.Count == 0) break;

            iterations = iter;
            var before = offsets;
            if (!Apply(next, $"对齐第 {iter} 轮"))
                return null;
            offsets = next;

            // Re-measure every core of the touched CCDs, target included, so drift between passes cancels out
            var touched = usedDv.Keys.Select(scb.CcdOf).Distinct().OrderBy(c => c).SelectMany(c => ccds[c]).ToList();
            var pass = MeasurePass(scb, touched, $"电压对齐 第 {iter} 轮", win, ct);
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

            // A computation error means the core went past its stable point: return it to the CO it last
            // measured clean at and stop lowering it
            var reverted = new List<int>(offsets);
            bool anyReverted = false;
            foreach (var (s, r) in pass)
            {
                if (!r.Valid) continue;
                if (r.Errors == 0) { lastGood[s] = (offsets[s], r); continue; }
                if (!stopped.Add(s)) continue;
                if (lastGood.TryGetValue(s, out var g) && g.Co != offsets[s])
                {
                    reverted[s] = g.Co;
                    latest[s] = g.Row;
                    clamped.Remove(s);
                    anyReverted = true;
                    Log.Write($"Core {s}：计算出错，回退至 {CoLabel(g.Co)} 并停止下调", "WARN");
                }
                else Log.Write($"Core {s}：计算出错，停止下调", "WARN");
            }
            if (anyReverted)
            {
                if (!Apply(reverted, $"对齐第 {iter} 轮 报错回退")) return null;
                offsets = reverted;
            }
        }

        var frequencyNotes = new List<string>();
        if (!ct.IsCancellationRequested && !ProbeFrequencyPlatforms(scb, ccds, offsets, latest, coMin, win, ct, frequencyNotes))
            return null;

        return Finish(scb, offsets, baseline, latest, targets, slope, coMin, clamped, stopped, startCo, win,
                      ct.IsCancellationRequested, iterations, frequencyNotes);
    }

    /// <summary>Finds each CCD's frequency plateau with a short representative-core probe, then checks every core.</summary>
    private static bool ProbeFrequencyPlatforms(SingleCoreBoost scb, Dictionary<int, List<int>> ccds,
                                                 List<int> offsets, Dictionary<int, ScbRow> latest, int coMin,
                                                 ResultsPanel win, CancellationToken ct, List<string> notes)
    {
        // FMax is set against a 100 MHz reference; measured clocks already include BCLK
        double fmaxMHz = RyzenSmu.ReadFMax() * scb.Bclk / 100.0;
        Log.Write("频率搜索开始");

        foreach (var (ccd, cores) in ccds)
        {
            if (ct.IsCancellationRequested) break;
            var probeCores = cores.Where(s => scb.CcdRank(s) is 1 or 2).OrderBy(s => scb.CcdRank(s)).ToList();
            foreach (int s in cores.OrderByDescending(s => latest.TryGetValue(s, out var r) && r.Valid ? r.Freq : 0))
            {
                if (probeCores.Count >= 3) break;
                if (!probeCores.Contains(s)) probeCores.Add(s);
            }
            var baseline = MeasurePass(scb, probeCores, $"CCD{ccd} 频率搜索 起点", win, ct);
            if (ct.IsCancellationRequested) break;
            if (baseline.Count != probeCores.Count || baseline.Values.Any(r => !r.Valid || r.Errors > 0))
            {
                string skipped = $"CCD{ccd}：起点测量无效，跳过频率搜索";
                Log.Write(skipped, "WARN");
                notes.Add(skipped);
                continue;
            }

            var startingCo = cores.Select(s => offsets[s]).ToArray();
            int maxShift = cores.Min(s => offsets[s] - coMin);
            int currentShift = 0;
            bool SetShift(int shift, string what)
            {
                if (shift == currentShift) return true;
                var next = new List<int>(offsets);
                for (int i = 0; i < cores.Count; i++) next[cores[i]] = startingCo[i] - shift;
                if (!Apply(next, what)) return false;
                for (int i = 0; i < offsets.Count; i++) offsets[i] = next[i];
                currentShift = shift;
                return true;
            }

            var observed = new Dictionary<int, double> { [0] = baseline.Values.Max(r => r.Freq) };
            double peakMHz = observed[0];
            int lastValidShift = 0;
            int noGain = 0;
            string stopReason = "已达 CO 下限";
            Log.Write($"CCD{ccd}：采样核心 Core {string.Join("、", probeCores)}，起点 {peakMHz:F0} MHz");

            for (int round = 1; round <= MaxCoarseRounds && !ct.IsCancellationRequested; round++)
            {
                if (fmaxMHz > 0 && peakMHz >= fmaxMHz - 25)
                {
                    stopReason = "已达 FMax";
                    break;
                }
                int shift = Math.Min(maxShift, currentShift + CoarseFrequencyStep);
                if (shift == currentShift) break;
                if (!SetShift(shift, $"CCD{ccd} 整体 -{shift} 档")) return false;

                var pass = MeasurePass(scb, probeCores, $"CCD{ccd} 频率搜索 第 {round} 轮", win, ct);
                if (ct.IsCancellationRequested) break;
                if (pass.Count != probeCores.Count || pass.Values.Any(r => !r.Valid || r.Errors > 0))
                {
                    stopReason = "测量无效";
                    Log.Write($"CCD{ccd}：测量无效，停止搜索", "WARN");
                    break;
                }

                double mhz = pass.Values.Max(r => r.Freq);
                observed[shift] = mhz;
                lastValidShift = shift;
                Log.Write($"CCD{ccd}：整体 -{shift} 档，{mhz:F0} MHz");
                noGain = mhz >= peakMHz + PlateauGainMHz ? 0 : noGain + 1;
                peakMHz = Math.Max(peakMHz, mhz);
                if (noGain >= PlateauRounds)
                {
                    stopReason = "频率已到顶";
                    break;
                }
                if (round == MaxCoarseRounds) stopReason = "已达轮数上限";
            }

            if (ct.IsCancellationRequested) break;
            int center = observed.Where(p => p.Value >= peakMHz - NearPeakMHz).Min(p => p.Key);
            int selectionLimit = lastValidShift;
            for (int shift = Math.Max(0, center - FineFrequencyStep);
                 shift <= Math.Min(lastValidShift, center + FineFrequencyStep) && !ct.IsCancellationRequested;
                 shift += FineFrequencyStep)
            {
                if (observed.ContainsKey(shift)) continue;
                if (!SetShift(shift, $"CCD{ccd} 整体 -{shift} 档")) return false;
                var pass = MeasurePass(scb, probeCores, $"CCD{ccd} 频率细调 -{shift} 档", win, ct);
                if (ct.IsCancellationRequested) break;
                if (pass.Count != probeCores.Count || pass.Values.Any(r => !r.Valid || r.Errors > 0))
                {
                    stopReason = "测量无效";
                    selectionLimit = Math.Min(selectionLimit, Math.Max(0, shift - FineFrequencyStep));
                    Log.Write($"CCD{ccd}：测量无效，停止搜索", "WARN");
                    break;
                }
                observed[shift] = pass.Values.Max(r => r.Freq);
                Log.Write($"CCD{ccd}：整体 -{shift} 档，{observed[shift]:F0} MHz");
            }

            if (ct.IsCancellationRequested) break;
            // Deepest shift that measured valid, less a small margin: past the frequency plateau extra CO still
            // lowers voltage, so the search keeps most of it instead of going back to where the plateau began
            int deepestValid = observed.Keys.Where(s => s <= selectionLimit).Max();
            int chosenShift = Math.Max(0, deepestValid - FrequencyBackoff);
            if (!SetShift(chosenShift, $"CCD{ccd} 选定整体 -{chosenShift} 档")) return false;
            var final = MeasurePass(scb, cores, $"CCD{ccd} 全核复测", win, ct);
            if (ct.IsCancellationRequested) break;
            if (final.Count != cores.Count || final.Values.Any(r => !r.Valid || r.Errors > 0))
            {
                if (!SetShift(0, $"CCD{ccd} 恢复电压对齐结果")) return false;
                string skipped = $"CCD{ccd}：全核复测无效，已恢复电压对齐结果";
                Log.Write(skipped, "WARN");
                notes.Add(skipped);
                continue;
            }
            foreach (var (s, row) in final) latest[s] = row;
            string note = $"CCD{ccd}：选定整体 -{chosenShift} 档（{stopReason}），最高 {final.Values.Max(r => r.Freq):F0} MHz";
            Log.Write(note);
            notes.Add(note);

            if (!AlignCcdFrequencies(scb, ccd, cores, offsets, latest, coMin, win, ct, notes)) return false;
        }
        return true;
    }

    /// <summary>
    /// Approximate frequency alignment inside one CCD: every core more than FreqAlignTolMHz below the CCD's best core
    /// gets FreqAlignStep more negative CO per round and only those cores are re-measured. One step gaining less than
    /// FreqAlignMinGainMHz is kept on probation, since a single short measurement is noisy near the plateau; if the
    /// next step still leaves the two-step gain under FreqAlignMinGain2StepMHz, both steps are undone and the core stops.
    /// An invalid / erroneous measurement, the CO floor or running out of rounds also undo an unconfirmed step.
    /// Returns false only when a CO write fails.
    /// </summary>
    private static bool AlignCcdFrequencies(SingleCoreBoost scb, int ccd, List<int> cores, List<int> offsets,
                                            Dictionary<int, ScbRow> latest, int coMin, ResultsPanel win,
                                            CancellationToken ct, List<string> notes)
    {
        var valid = cores.Where(s => latest.TryGetValue(s, out var r) && r.Valid).ToList();
        if (valid.Count < 2) return true;
        double targetMHz = valid.Max(s => latest[s].Freq);
        var startCo = cores.ToDictionary(s => s, s => offsets[s]);
        var startMHz = valid.ToDictionary(s => s, s => latest[s].Freq);
        var active = valid.Where(s => latest[s].Freq < targetMHz - FreqAlignTolMHz).ToHashSet();
        if (active.Count == 0)
        {
            Log.Write($"CCD{ccd}：各核频率均已达标");
            return true;
        }
        Log.Write($"CCD{ccd} 逐核频率对齐：目标 {targetMHz:F0} MHz，Core {string.Join("、", active.OrderBy(s => s))}");

        // Core → CO and frequency before an unconfirmed weak step; the step is kept until the next one decides
        var pending = new Dictionary<int, (int Co, double MHz)>();
        var revertTo = new Dictionary<int, int>();

        bool ApplyReverts(string what)
        {
            if (revertTo.Count == 0) return true;
            var back = new List<int>(offsets);
            foreach (var (s, co) in revertTo) back[s] = co;
            revertTo.Clear();
            if (!Apply(back, what)) return false;
            for (int i = 0; i < offsets.Count; i++) offsets[i] = back[i];
            return true;
        }

        for (int round = 1; round <= MaxFreqAlignRounds && active.Count > 0 && !ct.IsCancellationRequested; round++)
        {
            foreach (int s in active.Where(s => offsets[s] - FreqAlignStep < coMin).ToList())
            {
                if (pending.Remove(s, out var p))
                {
                    revertTo[s] = p.Co;
                    Log.Write($"CCD{ccd} Core {s}：已达 CO 下限，回退至 {p.Co}", "WARN");
                }
                else Log.Write($"CCD{ccd} Core {s}：已达 CO 下限", "WARN");
                active.Remove(s);
            }
            if (!ApplyReverts($"CCD{ccd} 回退")) return false;
            if (active.Count == 0) break;

            var stepped = active.OrderBy(s => s).ToList();
            var next = new List<int>(offsets);
            foreach (int s in stepped) next[s] = offsets[s] - FreqAlignStep;
            if (!Apply(next, $"CCD{ccd} 频率对齐第 {round} 轮")) return false;
            for (int i = 0; i < offsets.Count; i++) offsets[i] = next[i];

            var pass = MeasurePass(scb, stepped, $"CCD{ccd} 逐核频率对齐 第 {round} 轮", win, ct);
            if (ct.IsCancellationRequested) break;

            foreach (int s in stepped)
            {
                bool hasPending = pending.TryGetValue(s, out var p);
                if (!pass.TryGetValue(s, out var r) || !r.Valid || r.Errors > 0)
                {
                    // Back to the last step that measured valid and actually helped
                    int back = hasPending ? p.Co : offsets[s] + FreqAlignStep;
                    Log.Write($"CCD{ccd} Core {s}：测量无效，回退至 {back}", "WARN");
                    revertTo[s] = back;
                    pending.Remove(s);
                    active.Remove(s);
                }
                else if (r.Freq >= targetMHz - FreqAlignTolMHz)
                {
                    latest[s] = r;
                    pending.Remove(s);
                    active.Remove(s);
                    Log.Write($"CCD{ccd} Core {s}：{offsets[s]} 档，{r.Freq:F0} MHz，已达标");
                }
                else if (hasPending)
                {
                    double gain2 = r.Freq - p.MHz;
                    pending.Remove(s);
                    if (gain2 < FreqAlignMinGain2StepMHz)
                    {
                        Log.Write($"CCD{ccd} Core {s}：频率无提升，回退至 {p.Co}");
                        revertTo[s] = p.Co;
                        active.Remove(s);
                    }
                    else
                    {
                        latest[s] = r;
                        Log.Write($"CCD{ccd} Core {s}：{offsets[s]} 档，{r.Freq:F0} MHz");
                    }
                }
                else if (r.Freq - latest[s].Freq < FreqAlignMinGainMHz)
                {
                    pending[s] = (offsets[s] + FreqAlignStep, latest[s].Freq);
                    Log.Write($"CCD{ccd} Core {s}：{offsets[s]} 档，{r.Freq:F0} MHz");
                }
                else latest[s] = r;
            }
            if (!ApplyReverts($"CCD{ccd} 回退")) return false;
        }

        // Rounds ran out before a weak step was confirmed: do not leave unconfirmed negative CO behind
        if (!ct.IsCancellationRequested && pending.Count > 0)
        {
            foreach (var (s, p) in pending)
            {
                revertTo[s] = p.Co;
                Log.Write($"CCD{ccd} Core {s}：回退至 {p.Co}");
            }
            pending.Clear();
            if (!ApplyReverts($"CCD{ccd} 回退")) return false;
        }

        var changed = cores.Where(s => offsets[s] != startCo[s]).ToList();
        var lines = new List<string> { $"CCD{ccd} 逐核频率对齐：" + (changed.Count == 0 ? "无调整" : "") };
        lines.AddRange(changed.Select(s =>
            $"Core {s,-3} {startCo[s],3} → {offsets[s],3}   {startMHz[s]:F0} → {latest[s].Freq:F0} MHz"));
        var lagging = valid.Where(s => latest[s].Freq < targetMHz - FreqAlignTolMHz).OrderBy(s => s).ToList();
        if (lagging.Count > 0) lines.Add($"未达标：Core {string.Join("、", lagging)}");
        foreach (string line in lines) Log.Write(line);
        notes.Add(string.Join("\n", lines));
        return true;
    }

    // Last CO written in this run, so each write logs only the cores it changed
    private static List<int>? lastApplied;

    private static bool Apply(List<int> offsets, string what)
    {
        bool ok = Tuning.Apply(offsets, Mode, null, Reason);
        if (ok) Log.WriteCo($"{what}：", offsets, lastApplied);
        else Log.Write($"{what}：写入失败", "ERROR");
        lastApplied = ok ? new List<int>(offsets) : null;
        return ok;
    }

    private static Dictionary<int, ScbRow> MeasurePass(SingleCoreBoost scb, List<int> slots, string tag, ResultsPanel win, CancellationToken ct)
    {
        win.SetTable(SingleCoreBoost.Columns);
        string title = $"Single Core Boost · {tag}";
        Log.Write($"—— {title} ——");   // one title per pass; the core rows below carry no tag
        int done = 0;
        var rows = scb.Measure(slots,
            s => win.SetStatus($"{title}：Core {s}（{done + 1}/{slots.Count}）"),
            r =>
            {
                done++;
                win.AddRow(scb.Cells(r), SingleCoreBoost.RowColor(r));
                scb.LogRow(r);
            }, ct);
        return rows.ToDictionary(r => r.Slot);
    }

    /// <summary>
    /// Picks the highest-CPPC core needing the least voltage at a common frequency.
    /// Without a usable CPPC ranking, considers every valid core; ties within the noise floor favor higher frequency.
    /// </summary>
    private static int PickTarget(SingleCoreBoost scb, List<int> cores, Dictionary<int, ScbRow> baseline, double slope)
    {
        var valid = cores.Where(s => baseline.TryGetValue(s, out var r) && r.Valid).ToList();
        if (valid.Count == 0) return -1;

        uint topCppc = cores.Max(s => scb.Cppc[s]);
        var top = topCppc > 0 ? valid.Where(s => scb.Cppc[s] == topCppc).ToList() : new List<int>();
        bool byCppc = top.Count > 0 && top.Count < valid.Count;   // all cores equal: CPPC cannot rank this CCD
        var candidates = byCppc ? top : valid;

        double refMHz = valid.Max(s => baseline[s].Freq);
        double Norm(int s) => baseline[s].AlignV - slope * (baseline[s].Freq - refMHz);
        double best = candidates.Min(Norm);
        int pick = candidates.Where(s => Norm(s) <= best + FvAnalysis.NoiseFloor)
                             .OrderByDescending(s => baseline[s].Freq).ThenBy(s => s).First();

        return pick;
    }

    private static List<int> Finish(SingleCoreBoost scb, List<int> offsets, Dictionary<int, ScbRow> baseline,
                                    Dictionary<int, ScbRow> latest, Dictionary<int, int> targets, double slope, int coMin,
                                    HashSet<int> clamped, HashSet<int> stopped, int startCo, ResultsPanel win,
                                    bool cancelled, int iterations = 0, List<string>? frequencyNotes = null)
    {
        win.SetTable(ResultColumns(startCo), wideLast: true); // Free-text notes occupy the last column.
        var sb = new StringBuilder();
        sb.Append(cancelled
            ? "电压对齐已取消，保留当前 CO"
            : $"电压对齐完成（{iterations} 轮）");
        if (startCo != 0) sb.Append($"，起点 CO {CoLabel(startCo)}");

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
            if (isTarget) note = "电压对齐基准核";
            else if (!targets.ContainsKey(ccd)) note = cancelled ? "已取消" : "本 CCD 无对齐目标，未调整";
            else if (stopped.Contains(s)) note = "计算出错，已停止下调";
            else if (dv1 is not { } d1) note = "测量无效";
            else if (dv0 < -FvAnalysis.NoiseFloor && offsets[s] == startCo) note = $"低于目标，保持 {CoLabel(startCo)}";
            else if (d1 <= FvAnalysis.NoiseFloor)
                note = offsets[s] == startCo ? "无需调整" : d1 < -FvAnalysis.NoiseFloor ? $"已对齐（过冲 {-d1 * 1000:F1} mV）" : "已对齐";
            else if (clamped.Contains(s)) note = $"到达 CO 下限 {coMin}";
            else note = cancelled ? "已取消，未对齐" : "未收敛（已达最大轮数）";

            if (!isTarget && !stopped.Contains(s) && dv1 > FvAnalysis.NoiseFloor) unconverged.Add(s);

            win.AddRow(new[]
            {
                ccd.ToString(),
                s.ToString(),
                SingleCoreBoost.RankLabel(scb.CcdRank(s)),
                isTarget ? "目标" : "",
                r0 is { Valid: true } ? $"{r0.AlignV:F3}" : "-",
                dv0 is { } x0 ? $"{x0 * 1000:F1}" : "-",
                r1 is { Valid: true } ? $"{r1.AlignV:F3}" : "-",
                dv1 is { } x1 ? $"{x1 * 1000:F1}" : "-",
                offsets[s].ToString(),
                note,
            }, isTarget ? MonTheme.Gold : clamped.Contains(s) || stopped.Contains(s) || unconverged.Contains(s) ? Theme.Warn : null);
        }

        if (frequencyNotes != null)
            foreach (string note in frequencyNotes) sb.Append($"\n{note}");
        sb.Append("\n最终 CO：");
        foreach (int s in Enumerable.Range(0, scb.SlotCount).Where(scb.IsUsable)) sb.Append($"\nCore {s,-3} {offsets[s],3}");
        if (clamped.Count > 0) sb.Append($"\n达到 CO 下限：Core {string.Join("、", clamped.OrderBy(s => s))}");
        if (stopped.Count > 0) sb.Append($"\n计算出错已停止下调：Core {string.Join("、", stopped.OrderBy(s => s))}");
        if (unconverged.Count > 0) sb.Append($"\n未收敛：Core {string.Join("、", unconverged)}");
        sb.Append("\n结果已填入 CO 编辑器（未保存），请先通过压力测试再保存。");

        string text = sb.ToString();
        foreach (string line in text.Split('\n')) Log.Write(line, cancelled || unconverged.Count > 0 ? "WARN" : "INFO");
        win.SetSummary(text);
        return offsets;
    }
}
