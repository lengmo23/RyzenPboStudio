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
        string start = startCo == 0 ? L.T("all 0") : L.T("start");
        string final = L.T("final");
        return new[] { "CCD", "Core", L.T("Rank"), L.T("Role"), $"Core VID {start}", $"ΔV {start} (mV)", $"Core VID {final}", $"ΔV {final} (mV)",
                       L.T("Final CO"), L.T("Note") };
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
        text.Append(L.T("[Read before starting]\n"));
        text.Append(L.T("· Save your documents and work and close programs you don't need. The PC may freeze during the test and unsaved data will be lost;\n"));
        text.Append(L.T("· Close browsers, games, downloaders and other background programs, and pause Windows Update, antivirus scans and other background tasks;\n"));
        text.Append(L.T("· Do not use the PC during the test. Any extra load disturbs the single-core measurement and skews the results.\n\n"));

        text.Append(L.T("Voltage Alignment runs these steps automatically:\n"));
        text.Append(raised ? L.T("1. Set every core's CO to the start CO below, CS unchanged;\n")
                           : L.T("1. Set every core's CO to 0, CS unchanged;\n"));
        text.Append(L.T("2. Run Single Core Boost to record each core's frequency and voltage, and pick a reference core per CCD;\n"));
        text.Append(L.F("3. Voltage alignment: lower CO step by step on cores above the reference core until they converge to it (up to {0} rounds);\n", MaxIterations));
        text.Append(L.T("4. Frequency alignment: lower CO per CCD to find the single-core frequency plateau, re-measure all cores, then fine-tune cores that run low.\n"));
        text.Append(L.F("Takes up to about {0} min.", minutes));

        // What the result means, set apart in the warning colour between the procedure and the risk statement.
        var note = new StringBuilder();
        note.Append(L.T("[About the result]\n"));
        note.Append(L.T("· Voltage Alignment evens out the cores' single-core voltages; it does not find each core's CO limit;\n"));
        note.Append(L.T("· The result reflects voltages under the single-core test load; all-core load and daily use need different voltages;\n"));
        note.Append(L.T("· Starting from the result, raise or lower the CO of all cores based on stress tests and real use."));

        var risk = new StringBuilder();
        risk.Append(L.T("[Risk notice]\n"));
        risk.Append(L.T("· Voltage Alignment writes CO to the CPU; too low a voltage can make the system unstable or freeze. The CO written here is lost after a restart;\n"));
        risk.Append(L.T("· The result is based on single-core measurements only and is not verified for stability; stress test it before saving;\n"));
        risk.Append(L.T("· The result is filled into the CO editor and is not saved automatically;\n"));
        risk.Append(L.T("· Undervolting and overclocking carry hardware risk; you are responsible for any data loss or hardware damage."));
        if (Workspace.WasInterrupted())
            risk.Append(L.F("\n\nNote: an interrupted stress test was found. When a test starts later, it continues from this result with every core backed off by {0} steps.", Config.StepOnError));

        using var dlg = new Form
        {
            Text = L.T("Voltage Alignment"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9.5F),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18),
        };
        dlg.HandleCreated += (_, _) => DarkTitleBar.Apply(dlg.Handle);
        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Surface,
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
                Text = L.F("Core BCLK is {0:F2} MHz. With a raised BCLK some cores may need a positive CO to be stable, so enter a start CO: " +
                           "alignment starts with every core at this value and never ends above it; with a start above 0 each step lowers CO by at most " +
                           "{1}. The default is the highest positive CO now in effect.", bclk, MaxStepPerIterationPositive),
                AutoSize = true,
                MaximumSize = new Size(560, 0),
                ForeColor = Theme.Warn,
                Margin = new Padding(0, 0, 0, 6),
            });
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 12) };
            row.Controls.Add(new Label { Text = L.T("Start CO"), AutoSize = true, Margin = new Padding(0, 5, 8, 0) });
            startBox = new NumericUpDown
            {
                Minimum = 0,
                Maximum = MaxStartCo,
                Value = Math.Min(current, MaxStartCo),
                Width = 62,
                BackColor = Theme.SurfaceAlt,
                ForeColor = Theme.TextHi,
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font(Theme.FontFamily, 10F),
            };
            DarkSpin.Apply(startBox);
            row.Controls.Add(startBox);
            body.Controls.Add(row);
        }

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, BackColor = Theme.Surface };
        PillButton Btn(string t, DialogResult r, bool primary) => new()
        {
            Text = t,
            DialogResult = r,
            Size = new Size(98, 32),
            Radius = 8,
            BackColor = Theme.Surface,
            Normal = primary ? Theme.Accent : Theme.SurfaceAlt,
            Hover = primary ? Theme.AccentHover : Theme.SurfaceHover,
            ForeColor = primary ? Theme.AccentText : Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9.5F, primary ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(primary ? 0 : 8, 0, 0, 0),
        };
        var ok = Btn(L.T("Start"), DialogResult.OK, primary: true);
        var cancel = Btn(L.T("Cancel"), DialogResult.Cancel, primary: false);
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
        Log.Write(L.T("Voltage Alignment started"));
        using var scb = SingleCoreBoost.Open(monitor);
        if (scb == null) return null;

        int n = scb.SlotCount;
        int coMin = Math.Max(editorMin, GenerationCoMin());
        var slots = Enumerable.Range(0, n).Where(scb.IsUsable).ToList();
        var ccds = slots.GroupBy(scb.CcdOf).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.ToList());
        int maxStep = startCo > 0 ? MaxStepPerIterationPositive : MaxStepPerIteration;
        if (IsBclkRaised(scb.Bclk))
            Log.Write(L.F("Core BCLK {0:F2} MHz, start CO {1}, max {2} per step", scb.Bclk, CoLabel(startCo), maxStep));

        // Every core starts at the same CO so the cross-core F/V fit stays valid; the start write is not logged,
        // later writes log only the cores they change relative to it
        var offsets = Enumerable.Repeat(startCo, n).ToList();
        if (!Tuning.Apply(offsets, Mode, null, Reason))
        {
            Log.Write(L.F("Failed to set CO to {0}", CoLabel(startCo)), "ERROR");
            return null;
        }
        lastApplied = new List<int>(offsets);

        var stopped = new HashSet<int>();
        var baseline = MeasurePass(scb, slots, L.F("Baseline (all CO {0})", CoLabel(startCo)), win, ct);
        if (ct.IsCancellationRequested)
            return Finish(scb, offsets, baseline, baseline, new Dictionary<int, int>(), 0, coMin, new HashSet<int>(), stopped, startCo, win, true);

        // Slope from the common-start pass only: once CO differs per core the cross-core fit no longer measures the F/V curve
        double slope = FvAnalysis.Slope(baseline.Values.ToList(), out _);
        if (baseline.Values.Any(r => r.Valid && r.CoreVid <= 0)) Log.Write(L.T("Some cores have no Core VID, using VID"), "WARN");

        var targets = new Dictionary<int, int>();
        foreach (var (ccd, cores) in ccds)
        {
            int t = PickTarget(scb, cores, baseline, slope);
            if (t < 0 || !baseline.TryGetValue(t, out var tr) || !tr.Valid)
            {
                Log.Write(L.F("CCD{0}: no usable reference core, skipped", ccd), "WARN");
                continue;
            }
            targets[ccd] = t;
            Log.Write(L.F("CCD{0} reference core: Core {1}, {2:F3} V @ {3:F0} MHz", ccd, t, tr.AlignV, tr.Freq));
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
            else if (stopped.Add(s)) Log.Write(L.F("Core {0}: computation error at the start CO {1}, not lowered", s, CoLabel(startCo)), "WARN");
        }

        for (int iter = 1; iter <= MaxIterations && !ct.IsCancellationRequested; iter++)
        {
            var next = new List<int>(offsets);
            var usedDv = new Dictionary<int, double>();
            foreach (var (ccd, t) in targets)
            {
                if (!latest.TryGetValue(t, out var tr) || !tr.Valid)
                {
                    Log.Write(L.F("CCD{0}: reference Core {1} measurement invalid, round skipped", ccd, t), "WARN");
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
            if (!Apply(next, L.F("Alignment round {0}", iter)))
                return null;
            offsets = next;

            // Re-measure every core of the touched CCDs, target included, so drift between passes cancels out
            var touched = usedDv.Keys.Select(scb.CcdOf).Distinct().OrderBy(c => c).SelectMany(c => ccds[c]).ToList();
            var pass = MeasurePass(scb, touched, L.F("Voltage alignment round {0}", iter), win, ct);
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
                    Log.Write(L.F("Core {0}: computation error, back to {1} and no further lowering", s, CoLabel(g.Co)), "WARN");
                }
                else Log.Write(L.F("Core {0}: computation error, no further lowering", s), "WARN");
            }
            if (anyReverted)
            {
                if (!Apply(reverted, L.F("Alignment round {0} error back-off", iter))) return null;
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
        Log.Write(L.T("Frequency search started"));

        foreach (var (ccd, cores) in ccds)
        {
            if (ct.IsCancellationRequested) break;
            var probeCores = cores.Where(s => scb.CcdRank(s) is 1 or 2).OrderBy(s => scb.CcdRank(s)).ToList();
            foreach (int s in cores.OrderByDescending(s => latest.TryGetValue(s, out var r) && r.Valid ? r.Freq : 0))
            {
                if (probeCores.Count >= 3) break;
                if (!probeCores.Contains(s)) probeCores.Add(s);
            }
            var baseline = MeasurePass(scb, probeCores, L.F("CCD{0} frequency search start", ccd), win, ct);
            if (ct.IsCancellationRequested) break;
            if (baseline.Count != probeCores.Count || baseline.Values.Any(r => !r.Valid || r.Errors > 0))
            {
                string skipped = L.F("CCD{0}: start measurement invalid, frequency search skipped", ccd);
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
            string stopReason = L.T("CO lower limit reached");
            string probes = string.Join(L.T(", "), probeCores);
            Log.Write(L.F("CCD{0}: probe cores Core {1}, start {2:F0} MHz", ccd, probes, peakMHz));

            for (int round = 1; round <= MaxCoarseRounds && !ct.IsCancellationRequested; round++)
            {
                if (fmaxMHz > 0 && peakMHz >= fmaxMHz - 25)
                {
                    stopReason = L.T("FMax reached");
                    break;
                }
                int shift = Math.Min(maxShift, currentShift + CoarseFrequencyStep);
                if (shift == currentShift) break;
                if (!SetShift(shift, L.F("CCD{0} all -{1}", ccd, shift))) return false;

                var pass = MeasurePass(scb, probeCores, L.F("CCD{0} frequency search round {1}", ccd, round), win, ct);
                if (ct.IsCancellationRequested) break;
                if (pass.Count != probeCores.Count || pass.Values.Any(r => !r.Valid || r.Errors > 0))
                {
                    stopReason = L.T("invalid measurement");
                    Log.Write(L.F("CCD{0}: invalid measurement, search stopped", ccd), "WARN");
                    break;
                }

                double mhz = pass.Values.Max(r => r.Freq);
                observed[shift] = mhz;
                lastValidShift = shift;
                Log.Write(L.F("CCD{0}: all -{1}, {2:F0} MHz", ccd, shift, mhz));
                noGain = mhz >= peakMHz + PlateauGainMHz ? 0 : noGain + 1;
                peakMHz = Math.Max(peakMHz, mhz);
                if (noGain >= PlateauRounds)
                {
                    stopReason = L.T("frequency at its peak");
                    break;
                }
                if (round == MaxCoarseRounds) stopReason = L.T("round limit reached");
            }

            if (ct.IsCancellationRequested) break;
            int center = observed.Where(p => p.Value >= peakMHz - NearPeakMHz).Min(p => p.Key);
            int selectionLimit = lastValidShift;
            for (int shift = Math.Max(0, center - FineFrequencyStep);
                 shift <= Math.Min(lastValidShift, center + FineFrequencyStep) && !ct.IsCancellationRequested;
                 shift += FineFrequencyStep)
            {
                if (observed.ContainsKey(shift)) continue;
                if (!SetShift(shift, L.F("CCD{0} all -{1}", ccd, shift))) return false;
                var pass = MeasurePass(scb, probeCores, L.F("CCD{0} frequency fine-tune -{1}", ccd, shift), win, ct);
                if (ct.IsCancellationRequested) break;
                if (pass.Count != probeCores.Count || pass.Values.Any(r => !r.Valid || r.Errors > 0))
                {
                    stopReason = L.T("invalid measurement");
                    selectionLimit = Math.Min(selectionLimit, Math.Max(0, shift - FineFrequencyStep));
                    Log.Write(L.F("CCD{0}: invalid measurement, search stopped", ccd), "WARN");
                    break;
                }
                observed[shift] = pass.Values.Max(r => r.Freq);
                Log.Write(L.F("CCD{0}: all -{1}, {2:F0} MHz", ccd, shift, observed[shift]));
            }

            if (ct.IsCancellationRequested) break;
            // Deepest shift that measured valid, less a small margin: past the frequency plateau extra CO still
            // lowers voltage, so the search keeps most of it instead of going back to where the plateau began
            int deepestValid = observed.Keys.Where(s => s <= selectionLimit).Max();
            int chosenShift = Math.Max(0, deepestValid - FrequencyBackoff);
            if (!SetShift(chosenShift, L.F("CCD{0} chosen all -{1}", ccd, chosenShift))) return false;
            var final = MeasurePass(scb, cores, L.F("CCD{0} re-measure all cores", ccd), win, ct);
            if (ct.IsCancellationRequested) break;
            if (final.Count != cores.Count || final.Values.Any(r => !r.Valid || r.Errors > 0))
            {
                if (!SetShift(0, L.F("CCD{0} restore voltage alignment result", ccd))) return false;
                string skipped = L.F("CCD{0}: re-measurement invalid, voltage alignment result restored", ccd);
                Log.Write(skipped, "WARN");
                notes.Add(skipped);
                continue;
            }
            foreach (var (s, row) in final) latest[s] = row;
            double topMHz = final.Values.Max(r => r.Freq);
            string note = L.F("CCD{0}: chose all -{1} ({2}), peak {3:F0} MHz", ccd, chosenShift, stopReason, topMHz);
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
            Log.Write(L.F("CCD{0}: every core reaches the target frequency", ccd));
            return true;
        }
        string activeList = string.Join(L.T(", "), active.OrderBy(s => s));
        Log.Write(L.F("CCD{0} per-core frequency alignment: target {1:F0} MHz, Core {2}", ccd, targetMHz, activeList));

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
                    Log.Write(L.F("CCD{0} Core {1}: CO lower limit reached, back to {2}", ccd, s, p.Co), "WARN");
                }
                else Log.Write(L.F("CCD{0} Core {1}: CO lower limit reached", ccd, s), "WARN");
                active.Remove(s);
            }
            if (!ApplyReverts(L.F("CCD{0} back-off", ccd))) return false;
            if (active.Count == 0) break;

            var stepped = active.OrderBy(s => s).ToList();
            var next = new List<int>(offsets);
            foreach (int s in stepped) next[s] = offsets[s] - FreqAlignStep;
            if (!Apply(next, L.F("CCD{0} frequency alignment round {1}", ccd, round))) return false;
            for (int i = 0; i < offsets.Count; i++) offsets[i] = next[i];

            var pass = MeasurePass(scb, stepped, L.F("CCD{0} per-core frequency alignment round {1}", ccd, round), win, ct);
            if (ct.IsCancellationRequested) break;

            foreach (int s in stepped)
            {
                bool hasPending = pending.TryGetValue(s, out var p);
                if (!pass.TryGetValue(s, out var r) || !r.Valid || r.Errors > 0)
                {
                    // Back to the last step that measured valid and actually helped
                    int back = hasPending ? p.Co : offsets[s] + FreqAlignStep;
                    Log.Write(L.F("CCD{0} Core {1}: invalid measurement, back to {2}", ccd, s, back), "WARN");
                    revertTo[s] = back;
                    pending.Remove(s);
                    active.Remove(s);
                }
                else if (r.Freq >= targetMHz - FreqAlignTolMHz)
                {
                    latest[s] = r;
                    pending.Remove(s);
                    active.Remove(s);
                    Log.Write(L.F("CCD{0} Core {1}: {2}, {3:F0} MHz, on target", ccd, s, offsets[s], r.Freq));
                }
                else if (hasPending)
                {
                    double gain2 = r.Freq - p.MHz;
                    pending.Remove(s);
                    if (gain2 < FreqAlignMinGain2StepMHz)
                    {
                        Log.Write(L.F("CCD{0} Core {1}: no frequency gain, back to {2}", ccd, s, p.Co));
                        revertTo[s] = p.Co;
                        active.Remove(s);
                    }
                    else
                    {
                        latest[s] = r;
                        Log.Write(L.F("CCD{0} Core {1}: {2}, {3:F0} MHz", ccd, s, offsets[s], r.Freq));
                    }
                }
                else if (r.Freq - latest[s].Freq < FreqAlignMinGainMHz)
                {
                    pending[s] = (offsets[s] + FreqAlignStep, latest[s].Freq);
                    Log.Write(L.F("CCD{0} Core {1}: {2}, {3:F0} MHz", ccd, s, offsets[s], r.Freq));
                }
                else latest[s] = r;
            }
            if (!ApplyReverts(L.F("CCD{0} back-off", ccd))) return false;
        }

        // Rounds ran out before a weak step was confirmed: do not leave unconfirmed negative CO behind
        if (!ct.IsCancellationRequested && pending.Count > 0)
        {
            foreach (var (s, p) in pending)
            {
                revertTo[s] = p.Co;
                Log.Write(L.F("CCD{0} Core {1}: back to {2}", ccd, s, p.Co));
            }
            pending.Clear();
            if (!ApplyReverts(L.F("CCD{0} back-off", ccd))) return false;
        }

        var changed = cores.Where(s => offsets[s] != startCo[s]).ToList();
        var lines = new List<string> { changed.Count == 0 ? L.F("CCD{0} per-core frequency alignment: no changes", ccd)
                                                         : L.F("CCD{0} per-core frequency alignment:", ccd) };
        lines.AddRange(changed.Select(s =>
            $"Core {s,-3} {startCo[s],3} → {offsets[s],3}   {startMHz[s]:F0} → {latest[s].Freq:F0} MHz"));
        var lagging = valid.Where(s => latest[s].Freq < targetMHz - FreqAlignTolMHz).OrderBy(s => s).ToList();
        if (lagging.Count > 0) lines.Add(L.T("Below target: Core ") + string.Join(L.T(", "), lagging));
        foreach (string line in lines) Log.Write(line);
        notes.Add(string.Join("\n", lines));
        return true;
    }

    // Last CO written in this run, so each write logs only the cores it changed
    private static List<int>? lastApplied;

    private static bool Apply(List<int> offsets, string what)
    {
        bool ok = Tuning.Apply(offsets, Mode, null, Reason);
        if (ok) Log.WriteCo(what + L.T(":"), offsets, lastApplied);
        else Log.Write(L.F("{0}: write failed", what), "ERROR");
        lastApplied = ok ? new List<int>(offsets) : null;
        return ok;
    }

    private static Dictionary<int, ScbRow> MeasurePass(SingleCoreBoost scb, List<int> slots, string tag, ResultsPanel win, CancellationToken ct)
    {
        win.SetTable(SingleCoreBoost.Columns);
        string title = L.F("Single Core Boost · {0}", tag);
        Log.Write($"—— {title} ——");   // one title per pass; the core rows below carry no tag
        int done = 0;
        var rows = scb.Measure(slots,
            s => win.SetStatus(L.F("{0}: Core {1} ({2}/{3})", title, s, done + 1, slots.Count)),
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
            ? L.T("Voltage Alignment cancelled, current CO kept")
            : L.F("Voltage Alignment finished ({0} rounds)", iterations));
        if (startCo != 0) sb.Append(L.F(", start CO {0}", CoLabel(startCo)));

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
            if (isTarget) note = L.T("Reference core");
            else if (!targets.ContainsKey(ccd)) note = cancelled ? L.T("Cancelled") : L.T("No reference on this CCD, unchanged");
            else if (stopped.Contains(s)) note = L.T("Computation error, lowering stopped");
            else if (dv1 is not { } d1) note = L.T("Invalid measurement");
            else if (dv0 < -FvAnalysis.NoiseFloor && offsets[s] == startCo) note = L.F("Below target, kept at {0}", CoLabel(startCo));
            else if (d1 <= FvAnalysis.NoiseFloor)
            {
                double over = -d1 * 1000;
                note = offsets[s] == startCo ? L.T("No change needed")
                     : d1 < -FvAnalysis.NoiseFloor ? L.F("Aligned (overshoot {0:F1} mV)", over) : L.T("Aligned");
            }
            else if (clamped.Contains(s)) note = L.F("Reached the CO lower limit {0}", coMin);
            else note = cancelled ? L.T("Cancelled, not aligned") : L.T("Not converged (round limit reached)");

            if (!isTarget && !stopped.Contains(s) && dv1 > FvAnalysis.NoiseFloor) unconverged.Add(s);

            win.AddRow(new[]
            {
                ccd.ToString(),
                s.ToString(),
                SingleCoreBoost.RankLabel(scb.CcdRank(s)),
                isTarget ? L.T("Ref") : "",
                r0 is { Valid: true } ? $"{r0.AlignV:F3}" : "-",
                dv0 is { } x0 ? $"{x0 * 1000:F1}" : "-",
                r1 is { Valid: true } ? $"{r1.AlignV:F3}" : "-",
                dv1 is { } x1 ? $"{x1 * 1000:F1}" : "-",
                offsets[s].ToString(),
                note,
            }, isTarget ? Theme.Gold : clamped.Contains(s) || stopped.Contains(s) || unconverged.Contains(s) ? Theme.Warn : null);
        }

        if (frequencyNotes != null)
            foreach (string note in frequencyNotes) sb.Append($"\n{note}");
        // Per-core final CO is a table column, so the summary leaves it out; the log keeps it.
        string sep = L.T(", ");
        if (clamped.Count > 0) sb.Append(L.T("\nReached the CO lower limit: Core ") + string.Join(sep, clamped.OrderBy(s => s)));
        if (stopped.Count > 0) sb.Append(L.T("\nLowering stopped after computation errors: Core ") + string.Join(sep, stopped.OrderBy(s => s)));
        if (unconverged.Count > 0) sb.Append(L.T("\nNot converged: Core ") + string.Join(sep, unconverged));
        sb.Append(L.T("\nThe result is in the CO editor (not saved); pass a stress test before saving it."));

        string text = sb.ToString();
        string level = cancelled || unconverged.Count > 0 ? "WARN" : "INFO";
        foreach (string line in text.Split('\n')) Log.Write(line, level);
        Log.WriteCo(L.T("Voltage Alignment final CO:"), offsets, level: level);
        win.SetSummary(text);
        return offsets;
    }
}
