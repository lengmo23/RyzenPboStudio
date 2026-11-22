using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>One step of the sequential test.</summary>
internal sealed class SeqStep
{
    [JsonPropertyName("test")] public string Test { get; set; } = "";
    [JsonPropertyName("rounds")] public int Rounds { get; set; }
    [JsonPropertyName("seconds")] public int Seconds { get; set; }
}

/// <summary>profiles\setting.json: sequential and combined test plans, and the auto-mode CO backoff step.</summary>
internal sealed class MultiTestSettings
{
    public const int MinSeqSteps = 2;
    public const int MaxSeqSteps = 6;
    public const int MinComboTests = 2;

    [JsonPropertyName("sequence")] public List<SeqStep> Sequence { get; set; } = new();
    [JsonPropertyName("combo_tests")] public List<string> ComboTests { get; set; } = new();
    [JsonPropertyName("combo_rounds")] public int ComboRounds { get; set; }
    [JsonPropertyName("combo_seconds")] public int ComboSeconds { get; set; }
    // Files written before the step existed keep the default.
    [JsonPropertyName("co_backoff_step")] public int BackoffStep { get; set; } = Config.DefaultStepOnError;
    // Set by "do not show again" in the risk notice shown before a test starts.
    [JsonPropertyName("risk_notice_dismissed")] public bool RiskNoticeDismissed { get; set; }

    public static bool IsStepValid(int step) => step is >= 1 and <= Config.MaxStepOnError;

    private static string FilePath => Path.Combine(Workspace.ProfilesDir, "setting.json");

    public static MultiTestSettings Default() => new()
    {
        Sequence =
        {
            new SeqStep { Test = "VT3", Rounds = Config.DefaultVt3, Seconds = Config.DefaultDuration },
            new SeqStep { Test = "BKT", Rounds = Config.DefaultBkt, Seconds = Config.DefaultDuration },
            new SeqStep { Test = "SVT", Rounds = Config.DefaultSvt, Seconds = Config.DefaultDuration },
        },
        ComboTests = { "VT3", "BKT", "SVT" },
        ComboRounds = 10,
        ComboSeconds = Config.DefaultDuration,
    };

    public MultiTestSettings Clone() => new()
    {
        Sequence = Sequence.Select(s => new SeqStep { Test = s.Test, Rounds = s.Rounds, Seconds = s.Seconds }).ToList(),
        ComboTests = new List<string>(ComboTests),
        ComboRounds = ComboRounds,
        ComboSeconds = ComboSeconds,
        BackoffStep = BackoffStep,
        RiskNoticeDismissed = RiskNoticeDismissed,
    };

    /// <summary>Known, non-repeating tests with positive counts; an empty plan would pass a test without running it.</summary>
    public bool IsValid(ICollection<string> known) =>
        Sequence is { Count: >= MinSeqSteps and <= MaxSeqSteps }
        && Sequence.All(s => s != null && known.Contains(s.Test) && s.Rounds > 0 && s.Seconds > 0)
        && Sequence.Select(s => s.Test).Distinct().Count() == Sequence.Count
        && ComboTests is { Count: >= MinComboTests }
        && ComboTests.All(known.Contains)
        && ComboTests.Distinct().Count() == ComboTests.Count
        && ComboRounds > 0 && ComboSeconds > 0;

    /// <summary>Saved plan and step, each falling back to its default when the file is missing or invalid.</summary>
    public static MultiTestSettings Load(ICollection<string> known)
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<MultiTestSettings>(File.ReadAllText(FilePath)) is { } s)
            {
                var r = s.IsValid(known) ? s : Default();
                r.BackoffStep = IsStepValid(s.BackoffStep) ? s.BackoffStep : Config.DefaultStepOnError;
                r.RiskNoticeDismissed = s.RiskNoticeDismissed;
                return r;
            }
        }
        catch { /* Fall back to defaults. */ }
        return Default();
    }

    public bool Save()
    {
        try
        {
            DurableIO.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Settings dialogs for the sequential and combined tests and the auto-mode CO step.</summary>
internal sealed partial class MainForm
{
    private MultiTestSettings _multi = MultiTestSettings.Load(MultiTestNames);
    // Plan of the running test; on crash recovery it comes from the state file, not the current settings.
    private MultiTestSettings _runPlan = null!;
    private PillButton _multiSetBtn = null!;
    private PillButton _stepSetBtn = null!;

    // The Pi benchmark is memory-bound and always runs on all cores, so it is not offered here.
    private static string[] MultiTestNames =>
        ModeOptions.Where(o => o.mode != YCruncher.BenchMode).Select(o => o.mode).ToArray();

    private static string AlgoOf(string test) => Array.Find(ModeOptions, o => o.mode == test).algo;

    /// <summary>Plan from a state file written before plans were stored in it.</summary>
    private static MultiTestSettings LegacyPlan(TestState st)
    {
        var plan = MultiTestSettings.Default();
        int seconds = st.DurationSeconds ?? Config.DefaultDuration;
        foreach (var step in plan.Sequence)
        {
            step.Seconds = seconds;
            if (st.IterationsMap != null && st.IterationsMap.TryGetValue(step.Test, out int n) && n > 0) step.Rounds = n;
        }
        plan.ComboSeconds = seconds;
        return plan;
    }

    private string SeqTip() =>
        L.T("Runs in order: ") +
        string.Join(" → ", _multi.Sequence.Select(s => L.F("{0} {1} rounds × {2} s", s.Test, s.Rounds, s.Seconds)));

    private string ComboTip() =>
        L.F("{0} combined, {1} rounds, each test runs {2} s in turn per round", ComboNames(), _multi.ComboRounds, _multi.ComboSeconds);

    private string ComboNames() => string.Join(" + ", _multi.ComboTests);

    private void RefreshMultiTips()
    {
        _tips.SetToolTip(_seqBtn, SeqTip());
        _tips.SetToolTip(_comboBtn, ComboTip());
    }

    /// <summary>Square ☰ button that opens a settings dialog.</summary>
    private PillButton BuildMenuButton(string tip, Action onClick)
    {
        var b = new PillButton
        {
            Text = "☰",
            Font = new Font("Segoe UI Symbol", 11F),
            Size = new Size(30, 30),
            Radius = 8,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.SurfaceHover,
            ForeColor = Theme.TextHi,
            Margin = new Padding(0, 0, 0, 2),
        };
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    private static string AutoAdjustTip() =>
        L.F("On an error, backs off the failing core's CO by one step (+{0}) and reruns the round\nuntil it passes or you stop it. If y-cruncher crashes, backs off every core in the test scope.\nCO backs off up to +{1}; the test stops at that limit.", Config.StepOnError, Config.CoMax);

    /// <summary>Risk notice before a test starts; "我已知晓" returns OK. dontShow is the "do not show again" box.</summary>
    private static Form BuildTestRiskDialog(out CheckBox dontShow)
    {
        const int width = 480;
        var dlg = NewDialog(L.T("Read before testing"));
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(22, 18, 22, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        root.Controls.Add(new Label
        {
            Text = L.T("Risk notice"),
            AutoSize = true,
            ForeColor = Theme.Accent,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 13F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 10),
        });
        foreach (string item in new[]
        {
            L.T("The test keeps writing negative CO to the CPU and runs heavy stress tests; with too little voltage you may see computation errors, freezes, blue screens or restarts."),
            L.T("A crash can lose unsaved data; save and close your work first."),
            L.T("Do not use the PC during the test, or the results may be affected."),
            L.T("Both CO and CS affect stability; passing the test does not guarantee stability in daily use."),
            L.T("Undervolting and overclocking carry hardware risk; you are responsible for any data loss or hardware damage."),
        })
        {
            // Bullet and text in separate columns so wrapped lines align with the text, not the bullet.
            var row = Flow(FlowDirection.LeftToRight);
            row.Margin = new Padding(0, 0, 0, 8);
            Label Cell(string t, int maxWidth) => new()
            {
                Text = t,
                AutoSize = true,
                MaximumSize = new Size(maxWidth, 0),
                ForeColor = Theme.TextHi,
                BackColor = Theme.Surface,
                Font = new Font(Theme.FontFamily, 9.5F),
                Margin = new Padding(0),
            };
            row.Controls.Add(Cell("·", 14));
            row.Controls.Add(Cell(item, width - 14));
            root.Controls.Add(row);
        }

        var check = new CheckBox
        {
            Text = L.T("Don't show again"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Theme.TextMid,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        var ok = Pill(L.T("I understand"), 112, primary: true);
        var cancel = Pill(L.T("Cancel"), 84);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = new Padding(8, 0, 0, 0);
        var buttons = Flow(FlowDirection.LeftToRight);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 8, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(check, 0, 0);
        footer.Controls.Add(buttons, 1, 0);
        root.Controls.Add(footer);

        dlg.Controls.Add(root);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        dontShow = check;
        return dlg;
    }

    /// <summary>Shows the risk notice unless it was dismissed for good; false cancels the start.</summary>
    private bool ConfirmTestRisk()
    {
        if (_multi.RiskNoticeDismissed || AppSettings.Current.ExpertMode) return true;
        using var dlg = BuildTestRiskDialog(out var dontShow);
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        if (dontShow.Checked)
        {
            _multi.RiskNoticeDismissed = true;
            if (!_multi.Save()) SetStatus(L.T("Failed to save \"Don't show again\"; it still applies until the program closes"), Theme.Danger);
        }
        return true;
    }

    private void ShowStepDialog()
    {
        using var dlg = NewDialog(L.T("Auto mode step"));

        var pick = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Width = 84,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 10F),
            Margin = new Padding(0, 8, 0, 0),
        };
        DarkCombo.Apply(pick, center: true);
        for (int s = 1; s <= Config.MaxStepOnError; s++) pick.Items.Add($"+{s}");
        pick.SelectedIndex = Config.StepOnError - 1;

        var okBtn = Pill(L.T("OK"), 96, primary: true);
        var cancelBtn = Pill(L.T("Cancel"), 96);
        okBtn.DialogResult = DialogResult.OK;
        cancelBtn.DialogResult = DialogResult.Cancel;
        cancelBtn.Margin = new Padding(8, 0, 0, 0);
        var buttons = Flow(FlowDirection.LeftToRight);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 14, 0, 0);
        buttons.Controls.Add(okBtn);
        buttons.Controls.Add(cancelBtn);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 14, 16, 14),
        };
        root.Controls.Add(Hint(L.T("CO steps to back off a failing core on each error")));
        root.Controls.Add(pick);
        root.Controls.Add(Hint(L.F("+1 to +{0}, default +{1}", Config.MaxStepOnError, Config.DefaultStepOnError)));
        root.Controls.Add(buttons);
        dlg.Controls.Add(root);
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        int step = pick.SelectedIndex + 1;
        if (step == Config.StepOnError) return;

        Config.StepOnError = _multi.BackoffStep = step;
        _tips.SetToolTip(_autoAdjBtn, AutoAdjustTip());
        Log.Write(L.F("Auto mode CO step: +{0}", step));
        if (!_multi.Save()) SetStatus(L.T("Failed to save the auto mode step; it still applies until the program closes"), Theme.Danger);
    }

    private sealed class StepRow
    {
        public string Test = "";
        public string Rounds = "";
        public string Seconds = "";
    }

    private static int? PositiveInt(string raw) => int.TryParse(raw.Trim(), out int v) && v > 0 ? v : null;

    private static string Minutes(long seconds) => $"{Math.Ceiling(seconds / 60.0):F0}";

    private static Form NewDialog(string title)
    {
        var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9F),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        dlg.HandleCreated += (_, _) => DarkTitleBar.Apply(dlg.Handle);
        return dlg;
    }

    private static PillButton Pill(string text, int width, bool primary = false) => new()
    {
        Text = text,
        Size = new Size(width, 30),
        Radius = 8,
        Font = new Font(Theme.FontFamily, 9.5F, primary ? FontStyle.Bold : FontStyle.Regular),
        BackColor = Theme.Surface,
        Normal = primary ? Theme.Accent : Theme.SurfaceAlt,
        Hover = primary ? Theme.AccentHover : Theme.SurfaceHover,
        ForeColor = primary ? Theme.AccentText : Theme.TextHi,
        Margin = new Padding(0),
    };

    private static Label Hint(string text = "") => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.TextLo,
        BackColor = Theme.Surface,
        Margin = new Padding(0, 6, 0, 0),
    };

    private static FlowLayoutPanel Flow(FlowDirection dir) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = dir,
        WrapContents = false,
        BackColor = Theme.Surface,
        Margin = new Padding(0),
    };

    private void ShowMultiTestDialog()
    {
        string[] names = MultiTestNames;
        using var dlg = NewDialog(L.T("Multi-test settings"));

        PillButton IconButton(string glyph) => new()
        {
            Text = glyph,
            Size = new Size(26, 26),
            Radius = 6,
            Font = new Font("Segoe UI Symbol", 9F),
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.SurfaceHover,
            ForeColor = Theme.TextHi,
            Margin = new Padding(4, 1, 0, 0),
        };

        // ── Page switch ──
        var seqTab = Pill(L.T("Sequential"), 120);
        var comboTab = Pill(L.T("Combined"), 120);
        comboTab.Margin = new Padding(8, 0, 0, 0);
        var tabRow = Flow(FlowDirection.LeftToRight);
        tabRow.Margin = new Padding(0, 0, 0, 12);
        tabRow.Controls.Add(seqTab);
        tabRow.Controls.Add(comboTab);

        // ── Sequential page ──
        var rows = new List<StepRow>();
        var stepList = Flow(FlowDirection.TopDown);
        var addBtn = Pill(L.T("+ Add step"), 110);
        addBtn.Margin = new Padding(0, 8, 0, 0);
        var seqEstimate = Hint();
        var seqPage = Flow(FlowDirection.TopDown);
        seqPage.Controls.Add(Hint(L.F("Runs in order; each test can appear once ({0} to {1} steps)", MultiTestSettings.MinSeqSteps, MultiTestSettings.MaxSeqSteps)));
        seqPage.Controls.Add(stepList);
        seqPage.Controls.Add(addBtn);
        seqPage.Controls.Add(seqEstimate);

        // ── Combined page ──
        var comboChecks = new Dictionary<string, CheckBox>();
        var checkGrid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 4, 0, 4),
        };
        for (int i = 0; i < names.Length; i++)
        {
            var cb = new CheckBox
            {
                Text = names[i],
                AutoSize = true,
                ForeColor = Theme.TextHi,
                BackColor = Theme.Surface,
                Margin = new Padding(0, 4, 24, 4),
            };
            comboChecks[names[i]] = cb;
            checkGrid.Controls.Add(cb, i % 4, i / 4);
        }
        var comboSecondsBox = new TextBox();
        var comboRoundsBox = new TextBox();
        var comboFields = Flow(FlowDirection.LeftToRight);
        comboFields.Margin = new Padding(0, 4, 0, 0);
        AddFieldPair(comboFields, L.T("Sec/round"), comboSecondsBox, 66);
        AddFieldPair(comboFields, L.T("Rounds"), comboRoundsBox, 60);
        var comboEstimate = Hint();
        var comboPage = Flow(FlowDirection.TopDown);
        comboPage.Controls.Add(Hint(L.F("In each round, the checked tests take turns running for Sec/round (pick at least {0})", MultiTestSettings.MinComboTests)));
        comboPage.Controls.Add(checkGrid);
        comboPage.Controls.Add(comboFields);
        comboPage.Controls.Add(comboEstimate);

        // ── Footer ──
        var resetBtn = Pill(L.T("Defaults"), 96);
        var okBtn = Pill(L.T("OK"), 96, primary: true);
        var cancelBtn = Pill(L.T("Cancel"), 96);
        cancelBtn.DialogResult = DialogResult.Cancel;
        cancelBtn.Margin = new Padding(8, 0, 0, 0);
        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 14, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var rightButtons = Flow(FlowDirection.LeftToRight);
        rightButtons.Controls.Add(okBtn);
        rightButtons.Controls.Add(cancelBtn);
        footer.Controls.Add(resetBtn, 0, 0);
        footer.Controls.Add(rightButtons, 1, 0);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 14, 16, 14),
        };
        root.Controls.Add(tabRow);
        root.Controls.Add(seqPage);
        root.Controls.Add(comboPage);
        root.Controls.Add(Hint(L.T("With Each CCD, the total time is multiplied by the CCD count")));
        root.Controls.Add(footer);
        dlg.Controls.Add(root);
        dlg.CancelButton = cancelBtn;

        void UpdateEstimates()
        {
            long seq = 0;
            foreach (var r in rows)
            {
                if (PositiveInt(r.Rounds) is not int rd || PositiveInt(r.Seconds) is not int sc) { seq = -1; break; }
                seq += (long)rd * sc;
            }
            seqEstimate.Text = seq >= 0 ? L.F("About {0} min", Minutes(seq)) : L.T("Enter valid rounds and seconds per round");

            int picked = comboChecks.Values.Count(c => c.Checked);
            if (picked < MultiTestSettings.MinComboTests)
                comboEstimate.Text = L.F("Pick at least {0}", MultiTestSettings.MinComboTests);
            else if (PositiveInt(comboRoundsBox.Text) is int cr && PositiveInt(comboSecondsBox.Text) is int cs)
            {
                string min = Minutes((long)picked * cr * cs);
                comboEstimate.Text = L.F("About {0} min ({1} tests × {2} s × {3} rounds)", min, picked, cs, cr);
            }
            else
                comboEstimate.Text = L.T("Enter valid rounds and seconds per round");
            okBtn.Enabled = picked >= MultiTestSettings.MinComboTests;
        }

        void RebuildSteps()
        {
            stepList.SuspendLayout();
            var old = stepList.Controls.Cast<Control>().ToArray();
            stepList.Controls.Clear();
            foreach (var c in old) c.Dispose();
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0)
                {
                    var arrow = Hint("↓");
                    arrow.Margin = new Padding(52, 0, 0, 0);
                    stepList.Controls.Add(arrow);
                }
                stepList.Controls.Add(BuildStepRow(i));
            }
            stepList.ResumeLayout();
            addBtn.Enabled = rows.Count < Math.Min(MultiTestSettings.MaxSeqSteps, names.Length);
            UpdateEstimates();
        }

        // Structural edits rebuild the list after the triggering control's event has returned.
        void Rebuild() => dlg.BeginInvoke(RebuildSteps);

        Control BuildStepRow(int i)
        {
            var row = Flow(FlowDirection.LeftToRight);
            row.Margin = new Padding(0, 4, 0, 4);
            var index = Hint($"{i + 1}");
            index.Width = 20;
            index.AutoSize = false;
            index.Margin = new Padding(0, 6, 4, 0);
            row.Controls.Add(index);

            var pick = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Width = 84,
                BackColor = Theme.SurfaceAlt,
                ForeColor = Theme.TextHi,
                Font = new Font(Theme.FontFamily, 10F),
                Margin = new Padding(0, 2, 12, 0),
            };
            DarkCombo.Apply(pick, center: true);
            foreach (var n in names)
                if (n == rows[i].Test || rows.All(r => r.Test != n)) pick.Items.Add(n);
            pick.SelectedItem = rows[i].Test;
            pick.SelectedIndexChanged += (_, _) =>
            {
                if (pick.SelectedItem is string t && t != rows[i].Test) { rows[i].Test = t; Rebuild(); }
            };
            row.Controls.Add(pick);

            var roundsBox = new TextBox { Text = rows[i].Rounds };
            var secondsBox = new TextBox { Text = rows[i].Seconds };
            AddFieldPair(row, L.T("Rounds"), roundsBox, 50);
            AddFieldPair(row, L.T("Sec/round"), secondsBox, 56);
            roundsBox.TextChanged += (_, _) => { rows[i].Rounds = roundsBox.Text; UpdateEstimates(); };
            secondsBox.TextChanged += (_, _) => { rows[i].Seconds = secondsBox.Text; UpdateEstimates(); };

            var up = IconButton("↑");
            var down = IconButton("↓");
            var remove = IconButton("✕");
            up.Enabled = i > 0;
            down.Enabled = i < rows.Count - 1;
            remove.Enabled = rows.Count > MultiTestSettings.MinSeqSteps;
            _tips.SetToolTip(up, L.T("Move up"));
            _tips.SetToolTip(down, L.T("Move down"));
            _tips.SetToolTip(remove, L.T("Remove step"));
            up.Click += (_, _) => { (rows[i - 1], rows[i]) = (rows[i], rows[i - 1]); Rebuild(); };
            down.Click += (_, _) => { (rows[i + 1], rows[i]) = (rows[i], rows[i + 1]); Rebuild(); };
            remove.Click += (_, _) => { rows.RemoveAt(i); Rebuild(); };
            row.Controls.Add(up);
            row.Controls.Add(down);
            row.Controls.Add(remove);
            return row;
        }

        void Fill(MultiTestSettings s)
        {
            rows.Clear();
            rows.AddRange(s.Sequence.Select(x => new StepRow { Test = x.Test, Rounds = x.Rounds.ToString(), Seconds = x.Seconds.ToString() }));
            foreach (var (n, cb) in comboChecks) cb.Checked = s.ComboTests.Contains(n);
            comboSecondsBox.Text = s.ComboSeconds.ToString();
            comboRoundsBox.Text = s.ComboRounds.ToString();
            RebuildSteps();
        }

        void ShowPage(bool seq)
        {
            seqPage.Visible = seq;
            comboPage.Visible = !seq;
            foreach (var (tab, sel) in new[] { (seqTab, seq), (comboTab, !seq) })
            {
                tab.Normal = sel ? Theme.Accent : Theme.SurfaceAlt;
                tab.Hover = sel ? Theme.AccentHover : Theme.SurfaceHover;
                tab.Outline = Color.Empty;
                tab.ForeColor = sel ? Theme.AccentText : Theme.TextMid;
                tab.Invalidate();
            }
        }

        seqTab.Click += (_, _) => ShowPage(true);
        comboTab.Click += (_, _) => ShowPage(false);
        addBtn.Click += (_, _) =>
        {
            string next = names.First(n => rows.All(r => r.Test != n));
            rows.Add(new StepRow { Test = next, Rounds = Config.DefaultBkt.ToString(), Seconds = Config.DefaultDuration.ToString() });
            RebuildSteps();
        };
        foreach (var cb in comboChecks.Values) cb.CheckedChanged += (_, _) => UpdateEstimates();
        comboSecondsBox.TextChanged += (_, _) => UpdateEstimates();
        comboRoundsBox.TextChanged += (_, _) => UpdateEstimates();
        resetBtn.Click += (_, _) => Fill(MultiTestSettings.Default());

        MultiTestSettings? result = null;
        okBtn.Click += (_, _) =>
        {
            var plan = new MultiTestSettings { BackoffStep = _multi.BackoffStep, RiskNoticeDismissed = _multi.RiskNoticeDismissed };
            for (int i = 0; i < rows.Count; i++)
            {
                if (PositiveInt(rows[i].Rounds) is not int rd || PositiveInt(rows[i].Seconds) is not int sc)
                {
                    ThemedDialog.Show(dlg, L.F("Rounds and seconds per round of step {0} must be positive integers.", i + 1), L.T("Invalid value"));
                    return;
                }
                plan.Sequence.Add(new SeqStep { Test = rows[i].Test, Rounds = rd, Seconds = sc });
            }
            if (PositiveInt(comboRoundsBox.Text) is not int cr || PositiveInt(comboSecondsBox.Text) is not int cs)
            {
                ThemedDialog.Show(dlg, L.T("Combined test rounds and seconds per round must be positive integers."), L.T("Invalid value"));
                return;
            }
            plan.ComboTests = names.Where(n => comboChecks[n].Checked).ToList();
            plan.ComboRounds = cr;
            plan.ComboSeconds = cs;
            result = plan;
            dlg.DialogResult = DialogResult.OK;
        };

        Fill(_multi);
        ShowPage(_selectedMode != "COMBO");
        if (dlg.ShowDialog(this) != DialogResult.OK || result == null) return;

        _multi = result;
        RefreshMultiTips();
        if (!_multi.Save()) SetStatus(L.T("Failed to save the multi-test settings; they still apply until the program closes"), Theme.Danger);
    }
}
