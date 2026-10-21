namespace RyzenPboStudio;

/// <summary>Logon autostart: boot-apply of saved CO/CS and auto-resume of an interrupted test.</summary>
internal sealed partial class MainForm
{
    private readonly bool _autostartLaunch;   // launched by the scheduled task with --autostart
    private CheckBox _bootApplyCheck = null!;
    private bool _bootApplyReverting;

    /// <summary>Boot-apply checkbox at the right end of the param bar.</summary>
    private Control BuildBootApplyCheck()
    {
        _bootApplyCheck = new CheckBox
        {
            Text = "开机自动应用 CO/CS",
            AutoSize = true,
            Checked = AutoStart.BootApplyEnabled,
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            Cursor = Cursors.Hand,
            Margin = new Padding(20, 14, 0, 0),
        };
        _tips.SetToolTip(_bootApplyCheck,
            "勾选后每次登录 Windows，本程序会自动以管理员身份启动，\n" +
            "并写入已保存的 CO（Curve Optimizer 的 Save）与 CS（Curve Shaper 的 Save / 最后一次 Apply）。\n" +
            "只写每核负压与 Curve Shaper，不改 FMax / PPT / EDC / TDC。取消勾选即不再生效。\n" +
            "测试中途死机重启后也会借此自动启动并恢复测试；完全无人值守需在 Windows 中开启自动登录。");
        _bootApplyCheck.CheckedChanged += (_, _) => OnBootApplyToggled();
        return _bootApplyCheck;
    }

    private void OnBootApplyToggled()
    {
        if (_bootApplyReverting) return;
        bool on = _bootApplyCheck.Checked;
        if (on)
        {
            var profile = CoProfile.Load();
            int slots = RyzenSmu.SlotCount;
            string? problem = profile.Offsets.Count == 0
                ? "还没有已保存的 CO 配置。\n\n请先在 Curve Optimizer 中调好负压并点「Save」保存，再勾选此项。"
                : profile.Offsets.Count != slots
                    ? $"已保存的 CO 配置有 {profile.Offsets.Count} 个核心，与当前 CPU 的 {slots} 个不符。\n\n请在 Curve Optimizer 中重新点「Save」保存后再勾选此项。"
                    : null;
            if (problem != null)
            {
                MessageBox.Show(this, problem, "开机自动应用 CO/CS", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RevertBootApplyCheck(false);
                return;
            }
        }

        if (!AutoStart.SetBootApply(on))
        {
            SetStatus("保存开机自动应用设置失败", Theme.Accent);
            RevertBootApplyCheck(!on);
            return;
        }
        Log.Write(on
            ? "已开启开机自动应用 CO/CS：每次登录 Windows 后自动启动并写入已保存的 CO/CS"
            : "已关闭开机自动应用 CO/CS");
        SetStatus(on ? "已开启开机自动应用 CO/CS" : "已关闭开机自动应用 CO/CS", Theme.Success);
        SyncAutoStartInBackground();
    }

    private void RevertBootApplyCheck(bool value)
    {
        _bootApplyReverting = true;
        _bootApplyCheck.Checked = value;
        _bootApplyReverting = false;
    }

    /// <summary>Runs once the form is shown and editors are initialised: handles --autostart and syncs the task.</summary>
    private void OnAutostartShown()
    {
        if (_autostartLaunch)
        {
            Log.Write("由开机自启任务启动");
            // An interrupted test takes priority over boot-apply
            if (Workspace.WasInterrupted())
                OfferAutoResume();
            else if (AutoStart.BootApplyEnabled)
                BootApplySavedProfile();
        }
        // Also re-registers the task if the app folder was moved
        SyncAutoStartInBackground();
    }

    /// <summary>Writes saved per-core CO offsets and CS margins only; FMax / PPT / EDC / TDC are left alone.</summary>
    private void BootApplySavedProfile()
    {
        // Flag still present: the system went down shortly after the last boot-apply
        if (AutoStart.BootApplyPending)
        {
            AutoStart.ClearBootApplyPending();
            Log.Write("上次开机自动应用 CO/CS 后不久系统异常重启，本次未应用已保存的 CO/CS", "WARN");
            SetStatus("已跳过开机自动应用（上次应用后系统异常重启）", Theme.Accent);
            MessageBox.Show(this,
                "上次开机自动应用 CO/CS 后不久系统异常重启，为避免反复死机，本次没有应用已保存的 CO/CS。\n\n" +
                "「开机自动应用 CO/CS」仍保持勾选，下次开机会照常应用。\n" +
                "如果这组设置不稳定，请调整后重新保存，或取消勾选。",
                "未自动应用 CO/CS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var profile = CoProfile.Load();
        if (profile.Offsets.Count == 0)
        {
            Log.Write("开机自动应用：未找到已保存的 CO 配置（co_profile.json），已跳过", "WARN");
            SetStatus("开机自动应用已跳过：没有已保存的 CO 配置", Theme.Accent);
            return;
        }
        int slots = RyzenSmu.SlotCount;
        if (profile.Offsets.Count != slots)
        {
            Log.Write($"开机自动应用：已保存的 CO 配置有 {profile.Offsets.Count} 个核心，当前 CPU 为 {slots} 个，已跳过 CO/CS，请重新保存", "WARN");
            SetStatus("开机自动应用已跳过：CO 配置与当前 CPU 核心数不符", Theme.Accent);
            return;
        }

        bool csSupported = RyzenSmu.IsCurveShaperSupported();
        int[,]? csGrid = csSupported && _csState.HasMargins ? _csState.ToGrid() : null;

        if (!AutoStart.MarkBootApplyPending())
        {
            Log.Write("无法写入开机应用保护标记，为安全起见本次未自动应用 CO/CS", "WARN");
            SetStatus("开机自动应用已跳过：无法写入保护标记", Theme.Accent);
            return;
        }

        bool okCo = Tuning.Apply(profile.Offsets, "BOOT", null, "boot-apply");
        Log.Write($"开机自动应用 CO: [{string.Join(", ", profile.Offsets)}]" + (okCo ? "" : "（写入异常，详见上方）"));
        bool okCs = true;
        if (csGrid != null)
        {
            okCs = RyzenSmu.SetCurveShaper(csGrid);
            Log.Write("开机自动应用 Curve Shaper" + (okCs ? "" : "（写入异常，详见上方）"));
        }
        else if (csSupported)
        {
            Log.Write("未找到已保存的 Curve Shaper 配置，仅应用 CO");
        }

        OnCoRefresh();
        if (csGrid != null) OnCsRefresh();

        string what = csGrid != null ? "CO/CS" : "CO";
        SetStatus(okCo && okCs ? $"已开机自动应用 {what}" : "开机自动应用部分失败，详见日志",
            okCo && okCs ? Theme.Success : Theme.Accent);
    }

    /// <summary>Interrupted test on an --autostart launch: after a countdown, start through StartTest so RunTest does the recovery.</summary>
    private void OfferAutoResume()
    {
        var resume = AutoStart.LoadResume();
        if (resume.Declined)
        {
            Log.Write("此前已取消自动恢复，等待手动开始测试");
            return;
        }
        if (resume.Count >= AutoStart.MaxAutoResumes)
        {
            AutoStart.DeclineResume();
            Log.Write($"测试已连续 {resume.Count} 次在自动恢复后死机/重启，停止自动恢复，需手动处理", "WARN");
            SetStatus($"已连续 {resume.Count} 次自动恢复后死机，停止自动恢复，需手动处理", Theme.Accent);
            MessageBox.Show(this,
                $"测试已连续 {resume.Count} 次在自动恢复后死机/重启，已停止自动恢复。\n\n" +
                "请检查负压设置与硬件状态后手动处理。\n" +
                "确认后点「开始测试」仍会按中断恢复。",
                "已停止自动恢复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ShowPage(Page.Testing);   // TESTING page shows status and progress
        bool takeover = Journal.ReadLastEntry()?.Reason == Journal.ManualTakeover;
        using (var dlg = new ResumeCountdownDialog(takeover, resume.Count + 1))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                AutoStart.DeclineResume();
                Log.Write("已取消自动恢复测试：中断记录保留，手动点「开始测试」仍会按中断恢复", "WARN");
                SetStatus("已取消自动恢复 · 手动开始测试时仍按中断恢复", Theme.Warn);
                return;
            }
        }

        int attempt = AutoStart.IncrementResumeCount();
        if (attempt < 0)
        {
            Log.Write("无法记录自动恢复次数，为防止无限重启循环，本次不自动开始测试", "WARN");
            SetStatus("未自动恢复：无法记录恢复次数，请手动开始测试", Theme.Accent);
            return;
        }
        Log.Write($"自动恢复测试（连续第 {attempt} 次）", "WARN");
        StartTest(null, EventArgs.Empty);
    }

    /// <summary>Called by RunTest right after MarkInProgress (test thread).</summary>
    private void OnTestStartedForAutostart()
    {
        string? err = AutoStart.OnTestStarted();
        if (err != null) Ui(() => SetStatus($"{err} · 死机后不会自动恢复测试", Theme.Accent));
    }

    /// <summary>Called from TestFinished (UI thread): a completed test resets the resume streak.</summary>
    private void OnTestFinishedForAutostart()
    {
        if (_testCompletedNormally && !_stopRequested) AutoStart.ResetResumeCount();
        SyncAutoStartInBackground();
    }

    /// <summary>Called from OnFormClosing; on a user close the in-progress flag is already cleared.</summary>
    private void OnClosingForAutostart(bool userClosing)
    {
        AutoStart.ClearBootApplyPending();
        if (userClosing) AutoStart.Sync();
    }

    private void SyncAutoStartInBackground()
    {
        Task.Run(() =>
        {
            string? err = AutoStart.Sync();
            if (err != null) Ui(() => SetStatus(err, Theme.Accent));
        });
    }
}
