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

/// <summary>profiles\setting.json: sequential and combined test plans.</summary>
internal sealed class MultiTestSettings
{
    public const int MinSeqSteps = 2;
    public const int MaxSeqSteps = 6;
    public const int MinComboTests = 2;

    [JsonPropertyName("sequence")] public List<SeqStep> Sequence { get; set; } = new();
    [JsonPropertyName("combo_tests")] public List<string> ComboTests { get; set; } = new();
    [JsonPropertyName("combo_rounds")] public int ComboRounds { get; set; }
    [JsonPropertyName("combo_seconds")] public int ComboSeconds { get; set; }

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

    /// <summary>Saved plan, or the defaults when the file is missing or invalid.</summary>
    public static MultiTestSettings Load(ICollection<string> known)
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<MultiTestSettings>(File.ReadAllText(FilePath)) is { } s
                && s.IsValid(known))
                return s;
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

/// <summary>Settings dialog for the sequential and combined tests.</summary>
internal sealed partial class MainForm
{
    private MultiTestSettings _multi = MultiTestSettings.Load(MultiTestNames);
    // Plan of the running test; on crash recovery it comes from the state file, not the current settings.
    private MultiTestSettings _runPlan = null!;
    private PillButton _multiSetBtn = null!;

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
        "依次运行：" + string.Join(" → ", _multi.Sequence.Select(s => $"{s.Test} {s.Rounds} 轮 × {s.Seconds} 秒"));

    private string ComboTip() =>
        $"{string.Join(" + ", _multi.ComboTests)} 组合运行，共 {_multi.ComboRounds} 轮，每轮内各项轮流运行 {_multi.ComboSeconds} 秒";

    private void RefreshMultiTips()
    {
        _tips.SetToolTip(_seqBtn, SeqTip());
        _tips.SetToolTip(_comboBtn, ComboTip());
    }

    private PillButton BuildMultiSettingsButton()
    {
        var b = new PillButton
        {
            Text = "☰",
            Font = new Font("Segoe UI Symbol", 11F),
            Size = new Size(30, 30),
            Radius = 8,
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.Border,
            ForeColor = Theme.TextHi,
            Margin = new Padding(0, 0, 0, 2),
        };
        _tips.SetToolTip(b, "设置顺序测试与组合测试的项目、轮数和每轮时间");
        b.Click += (_, _) => ShowMultiTestDialog();
        return b;
    }

    private sealed class StepRow
    {
        public string Test = "";
        public string Rounds = "";
        public string Seconds = "";
    }

    private static int? PositiveInt(string raw) => int.TryParse(raw.Trim(), out int v) && v > 0 ? v : null;

    private static string Minutes(long seconds) => $"{Math.Ceiling(seconds / 60.0):F0}";

    private void ShowMultiTestDialog()
    {
        string[] names = MultiTestNames;
        using var dlg = new Form
        {
            Text = "多项测试设置",
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

        PillButton Pill(string text, int width, bool primary = false) => new()
        {
            Text = text,
            Size = new Size(width, 30),
            Radius = 8,
            Font = new Font(Theme.FontFamily, 9.5F, primary ? FontStyle.Bold : FontStyle.Regular),
            BackColor = Theme.Surface,
            Normal = primary ? Theme.Accent : Theme.SurfaceAlt,
            Hover = primary ? Theme.AccentHover : Theme.Border,
            ForeColor = primary ? Theme.AccentText : Theme.TextHi,
            Margin = new Padding(0),
        };
        PillButton IconButton(string glyph) => new()
        {
            Text = glyph,
            Size = new Size(26, 26),
            Radius = 6,
            Font = new Font("Segoe UI Symbol", 9F),
            BackColor = Theme.Surface,
            Normal = Theme.SurfaceAlt,
            Hover = Theme.Border,
            ForeColor = Theme.TextHi,
            Margin = new Padding(4, 1, 0, 0),
        };
        Label Hint(string text = "") => new()
        {
            Text = text,
            AutoSize = true,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 6, 0, 0),
        };
        FlowLayoutPanel Flow(FlowDirection dir) => new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = dir,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };

        // ── Page switch ──
        var seqTab = Pill("顺序测试", 120);
        var comboTab = Pill("组合测试", 120);
        comboTab.Margin = new Padding(8, 0, 0, 0);
        var tabRow = Flow(FlowDirection.LeftToRight);
        tabRow.Margin = new Padding(0, 0, 0, 12);
        tabRow.Controls.Add(seqTab);
        tabRow.Controls.Add(comboTab);

        // ── Sequential page ──
        var rows = new List<StepRow>();
        var stepList = Flow(FlowDirection.TopDown);
        var addBtn = Pill("+ 添加步骤", 110);
        addBtn.Margin = new Padding(0, 8, 0, 0);
        var seqEstimate = Hint();
        var seqPage = Flow(FlowDirection.TopDown);
        seqPage.Controls.Add(Hint($"按顺序依次运行，同一项目只能出现一次（{MultiTestSettings.MinSeqSteps}～{MultiTestSettings.MaxSeqSteps} 步）"));
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
        AddFieldPair(comboFields, "每轮(秒)", comboSecondsBox, 66);
        AddFieldPair(comboFields, "轮数", comboRoundsBox, 60);
        var comboEstimate = Hint();
        var comboPage = Flow(FlowDirection.TopDown);
        comboPage.Controls.Add(Hint($"每一轮内，勾选的项目轮流各运行「每轮秒数」（至少选 {MultiTestSettings.MinComboTests} 项）"));
        comboPage.Controls.Add(checkGrid);
        comboPage.Controls.Add(comboFields);
        comboPage.Controls.Add(comboEstimate);

        // ── Footer ──
        var resetBtn = Pill("恢复默认", 96);
        var okBtn = Pill("确定", 96, primary: true);
        var cancelBtn = Pill("取消", 96);
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
        root.Controls.Add(Hint("逐 CCD 测试时，总时长乘以 CCD 数"));
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
            seqEstimate.Text = seq >= 0 ? $"预计时长约 {Minutes(seq)} 分钟" : "请填写有效的轮数和每轮秒数";

            int picked = comboChecks.Values.Count(c => c.Checked);
            if (picked < MultiTestSettings.MinComboTests)
                comboEstimate.Text = $"至少选择 {MultiTestSettings.MinComboTests} 项";
            else if (PositiveInt(comboRoundsBox.Text) is int cr && PositiveInt(comboSecondsBox.Text) is int cs)
                comboEstimate.Text = $"预计时长约 {Minutes((long)picked * cr * cs)} 分钟（{picked} 项 × {cs} 秒 × {cr} 轮）";
            else
                comboEstimate.Text = "请填写有效的轮数和每轮秒数";
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
            AddFieldPair(row, "轮数", roundsBox, 50);
            AddFieldPair(row, "每轮(秒)", secondsBox, 56);
            roundsBox.TextChanged += (_, _) => { rows[i].Rounds = roundsBox.Text; UpdateEstimates(); };
            secondsBox.TextChanged += (_, _) => { rows[i].Seconds = secondsBox.Text; UpdateEstimates(); };

            var up = IconButton("↑");
            var down = IconButton("↓");
            var remove = IconButton("✕");
            up.Enabled = i > 0;
            down.Enabled = i < rows.Count - 1;
            remove.Enabled = rows.Count > MultiTestSettings.MinSeqSteps;
            _tips.SetToolTip(up, "上移");
            _tips.SetToolTip(down, "下移");
            _tips.SetToolTip(remove, "删除此步");
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
                tab.Hover = sel ? Theme.AccentHover : Theme.Border;
                tab.ForeColor = sel ? Theme.AccentText : Theme.TextHi;
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
            var plan = new MultiTestSettings();
            for (int i = 0; i < rows.Count; i++)
            {
                if (PositiveInt(rows[i].Rounds) is not int rd || PositiveInt(rows[i].Seconds) is not int sc)
                {
                    ThemedDialog.Show(dlg, $"顺序测试第 {i + 1} 步的轮数和每轮秒数必须是正整数。", "参数错误");
                    return;
                }
                plan.Sequence.Add(new SeqStep { Test = rows[i].Test, Rounds = rd, Seconds = sc });
            }
            if (PositiveInt(comboRoundsBox.Text) is not int cr || PositiveInt(comboSecondsBox.Text) is not int cs)
            {
                ThemedDialog.Show(dlg, "组合测试的轮数和每轮秒数必须是正整数。", "参数错误");
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
        if (!_multi.Save()) SetStatus("多项测试设置保存失败，本次运行期间仍然生效", Theme.Accent);
    }
}
