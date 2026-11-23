namespace RyzenPboStudio;

/// <summary>Logon autostart: boot-apply of saved CO/CS and applied PBO limits, and auto-resume of an interrupted test.</summary>
internal sealed partial class MainForm
{
    private readonly bool _autostartLaunch;   // launched by the scheduled task with --autostart
    private bool _bootApplyOn;
    private bool _bootApplying;               // Boot-apply is writing; manual CO / CS / PBO Apply is blocked.

    private static string BootApplyTip =>
        L.T("After sign-in, writes the saved CO/CS and the FMax, PPT, EDC and TDC last applied on the PBO page\n" +
            "Only PBO values that differ from the current BIOS values are written");

    /// <summary>Binds the boot-apply toggle hosted in the left navigation rail.</summary>
    private void InitBootApplyToggle()
    {
        _bootApplyOn = AutoStart.BootApplyEnabled;
        _sideRail.BootApplyToggle.Checked = _bootApplyOn;
        _tips.SetToolTip(_sideRail.BootApplyToggle, BootApplyTip);
        _sideRail.BootApplyToggle.Click += (_, _) => OnBootApplyToggled(!_bootApplyOn);
    }

    private void OnBootApplyToggled(bool on)
    {
        if (on)
        {
            if (!AppSettings.Current.ExpertMode && !ConfirmBootApplyRisk(this)) return;

            var profile = CoProfile.Load();
            int slots = RyzenSmu.SlotCount;
            string? problem = profile.Offsets.Count == 0
                ? L.T("There is no saved CO profile yet.\n\nTune CO in Curve Optimizer and click Save first, then turn this on.")
                : profile.Offsets.Count != slots
                    ? L.F("The saved CO profile has {0} cores, but this CPU has {1}.\n\nClick Save in Curve Optimizer again, then turn this on.", profile.Offsets.Count, slots)
                    : null;
            if (problem != null)
            {
                ThemedDialog.Show(this, problem, L.T("Apply PBO at startup"));
                return;
            }
        }

        if (!AutoStart.SetBootApply(on))
        {
            SetStatus(L.T("Failed to save the startup apply setting"), Theme.Danger);
            return;
        }
        _bootApplyOn = on;
        _sideRail.BootApplyToggle.Checked = _bootApplyOn;
        Log.Write(on
            ? L.T("Apply PBO at startup on: the program starts after each Windows sign-in and writes the saved CO/CS and applied PBO values")
            : L.T("Apply PBO at startup off"));
        SetStatus(on ? L.T("Apply PBO at startup on") : L.T("Apply PBO at startup off"), Theme.Success);
        SyncAutoStartInBackground();
    }

    private bool ConfirmBootApplyRisk(IWin32Window owner)
    {
        using var dialog = new Form
        {
            Text = L.T("Apply PBO at startup"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(480, 150),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 10F),
        };
        dialog.HandleCreated += (_, _) => DarkTitleBar.Apply(dialog.Handle);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 18, 20, 16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(new Label
        {
            Text = L.T("Make sure the saved CO/CS and applied PBO values are correct and tested; otherwise the CPU may be undervolted at startup and freeze or crash."),
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        var acknowledge = Primary(L.T("I understand"));
        acknowledge.Size = new Size(112, 34);
        acknowledge.BackColor = Theme.Surface;
        acknowledge.Margin = new Padding(0);
        acknowledge.DialogResult = DialogResult.OK;
        var cancel = Ghost(L.T("Cancel"));
        cancel.Size = new Size(84, 34);
        cancel.BackColor = Theme.Surface;
        cancel.Margin = new Padding(0, 0, 10, 0);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(acknowledge);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>Runs once the form is shown and editors are initialised: offers to resume an interrupted test on any
    /// launch, boot-applies on --autostart, and syncs the task.</summary>
    private async Task OnAutostartShownAsync()
    {
        if (_autostartLaunch) Log.Write(L.T("Started by the startup task"));
        // An interrupted test takes priority over boot-apply
        if (Workspace.WasInterrupted())
            OfferResume();
        else if (_autostartLaunch && AutoStart.BootApplyEnabled)
            await BootApplySavedProfile();
        // Also re-registers the task if the app folder was moved
        SyncAutoStartInBackground();
    }

    /// <summary>Writes saved per-core CO offsets and CS margins, then the FMax / PPT / EDC / TDC of the last PBO Apply
    /// where they differ from the BIOS values the CPU booted with.</summary>
    private async Task BootApplySavedProfile()
    {
        bool disabled = false;
        try
        {
            // Flag still present: the system went down shortly after the last boot-apply
            if (AutoStart.BootApplyPending)
            {
                AutoStart.ClearBootApplyPending();
                Log.Write(L.T("The system restarted unexpectedly soon after the last startup apply; CO/CS and PBO values were not applied this time"), "WARN");
                SetStatus(L.T("Startup apply skipped (unexpected restart after the last apply)"), Theme.Danger);
                if (!AppSettings.Current.ExpertMode)
                    ThemedDialog.Show(this,
                        L.T("The system restarted unexpectedly soon after the last startup apply. To avoid repeated crashes, CO/CS and PBO values were not applied this time.\n\n" +
                            "Apply PBO at startup is still on and will apply as usual at the next startup.\n" +
                            "If these settings are unstable, adjust and save them again, or turn off Apply PBO at startup."),
                        L.T("PBO not applied"));
                return;
            }

            var profile = CoProfile.Load();
            if (profile.Offsets.Count == 0)
            {
                Log.Write(L.T("Startup apply: no saved CO profile (co_profile.json), skipped"), "WARN");
                SetStatus(L.T("Startup apply skipped: no saved CO profile"), Theme.Danger);
                return;
            }
            int slots = RyzenSmu.SlotCount;
            if (profile.Offsets.Count != slots)
            {
                Log.Write(L.F("Startup apply: the saved CO profile has {0} cores but this CPU has {1}; CO/CS skipped, save it again", profile.Offsets.Count, slots), "WARN");
                SetStatus(L.T("Startup apply skipped: CO profile does not match the CPU core count"), Theme.Danger);
                return;
            }

            bool csSupported = RyzenSmu.IsCurveShaperSupported();
            int[,]? csGrid = csSupported && _csState.HasMargins ? _csState.ToGrid() : null;

            if (!AutoStart.MarkBootApplyPending())
            {
                Log.Write(L.T("Cannot write the startup apply guard flag; to be safe, CO/CS and PBO values were not applied"), "WARN");
                SetStatus(L.T("Startup apply skipped: cannot write the guard flag"), Theme.Danger);
                return;
            }

            var pbo = PboJournal.ReadLast(PboJournal.ManualApply);
            // Off the UI thread; tests and manual Apply stay disabled until the boot values are written and read back.
            _bootApplying = true;
            SetControlsEnabled(false);
            disabled = true;
            SetStatus(L.T("Applying at startup..."), Theme.Warn);
            var (okCo, okCs, okPbo) = await Task.Run(() =>
            {
                bool co = Tuning.Apply(profile.Offsets, "BOOT", null, "boot-apply");
                Log.WriteCo(co ? L.T("Startup apply CO:") : L.T("Startup apply CO write failed:"), profile.Offsets, level: co ? "INFO" : "ERROR");
                bool cs = true;
                if (csGrid != null)
                {
                    cs = RyzenSmu.SetCurveShaper(csGrid);
                    Log.Write(cs ? L.T("Startup apply Curve Shaper") : L.T("Startup apply Curve Shaper (write error, see above)"));
                }
                else if (csSupported)
                {
                    Log.Write(L.T("No saved Curve Shaper profile, CS skipped"));
                }

                bool p = true;
                if (pbo != null)
                    p = RestorePbo(pbo, L.T("Startup apply"));
                else
                    Log.Write(L.T("Startup apply: nothing applied on the PBO page yet, PBO skipped"));
                return (co, cs, p);
            });
            if (IsDisposed) return;
            SetControlsEnabled(true);
            disabled = false;

            await OnCoRefresh();
            if (csGrid != null && !IsDisposed) await OnCsRefresh();
            if (IsDisposed) return;

            string what = (csGrid != null ? "CO/CS" : "CO") + (pbo != null ? "/PBO" : "");
            bool ok = okCo && okCs && okPbo;
            SetStatus(ok ? L.F("Applied {0} at startup", what) : L.T("Startup apply partly failed, see the log"), ok ? Theme.Success : Theme.Danger);
        }
        catch (Exception e)
        {
            Log.Write(L.F("{0} error: {1}", L.T("Startup apply"), e.Message), "ERROR");
            if (!IsDisposed) SetStatus(L.T("Startup apply partly failed, see the log"), Theme.Danger);
        }
        finally
        {
            _bootApplying = false;
            if (disabled && !IsDisposed) SetControlsEnabled(true);
        }
    }

    /// <summary>
    /// Asks whether to continue an interrupted test and, if so, starts through StartTest so RunTest does the recovery.
    /// Only an --autostart launch that was not declined counts down and continues on its own; a manual launch or a
    /// previously declined resume waits for the user.
    /// </summary>
    private void OfferResume()
    {
        var resume = AutoStart.LoadResume();
        bool countdown = _autostartLaunch && !resume.Declined;
        if (countdown && resume.Count >= AutoStart.MaxAutoResumes)
        {
            AutoStart.DeclineResume();
            Log.Write(L.F("The test crashed or restarted after auto-resume {0} times in a row; auto-resume stopped, manual action needed", resume.Count), "WARN");
            SetStatus(L.F("Crashed after {0} auto-resumes in a row; auto-resume stopped", resume.Count), Theme.Danger);
            if (!AppSettings.Current.ExpertMode)
                ThemedDialog.Show(this,
                    L.F("The test crashed or restarted after auto-resume {0} times in a row, so auto-resume has stopped.\n\n" +
                        "Check the CO settings and hardware, then continue manually.\n" +
                        "Clicking Start still resumes the interrupted test.", resume.Count),
                    L.T("Auto-resume stopped"));
            return;
        }

        ShowPage(Page.Testing);   // TESTING page shows status and progress
        bool takeover = Journal.ReadLastEntry()?.Reason == Journal.ManualTakeover;
        var (test, scope) = DescribeInterruptedTest();
        if (!countdown || !AppSettings.Current.ExpertMode)
        {
            using (var dlg = new ResumePromptDialog(test, scope, takeover, countdown ? resume.Count + 1 : null))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                {
                    AutoStart.DeclineResume();
                    Log.Write(L.T("Interrupted test not continued: the record is kept and Start still resumes it"), "WARN");
                    SetStatus(L.T("Not continued · Start resumes the interrupted test"), Theme.Warn);
                    return;
                }
            }
        }

        if (!countdown)
        {
            // The user chose to continue, like pressing Start: the auto-resume streak starts over.
            AutoStart.ResetResumeCount();
            Log.Write(L.T("Continuing the interrupted test"), "WARN");
            StartTest(null, EventArgs.Empty);
            return;
        }

        int attempt = AutoStart.IncrementResumeCount();
        if (attempt < 0)
        {
            Log.Write(L.T("Cannot record the auto-resume count; to prevent a restart loop, the test is not started automatically"), "WARN");
            SetStatus(L.T("Not resumed: cannot record the resume count, start the test manually"), Theme.Danger);
            return;
        }
        Log.Write(L.F("Auto-resuming the test (attempt {0} in a row)", attempt), "WARN");
        StartTest(null, EventArgs.Empty);
    }

    /// <summary>Test mode with its resume stage, and the scope, of the interrupted run for the resume prompt.</summary>
    private (string test, string scope) DescribeInterruptedTest()
    {
        var st = Workspace.LoadState();
        if (st == null) return (L.T("Last unfinished test"), L.T("Scope as last set"));
        string test = st.TestMode switch
        {
            "SEQ" => string.IsNullOrEmpty(st.SeqPhase) ? L.T("Sequential test")
                                                       : L.F("Sequential test · continue from {0}", st.SeqPhase),
            "COMBO" => L.T("Combined test"),
            _ => L.F("Single test · {0}", st.TestMode),
        };
        string scope = st.TestScope switch
        {
            "CUSTOM" when st.ScopeCores is { Count: > 0 } cores => L.T("Custom cores ") + $"[{string.Join(", ", cores)}]",
            "EACH" => L.F("Each CCD, continue from CCD{0}", st.ScopeCcd ?? 0),
            _ => ScopeLabel(st.TestScope ?? "ALL", st.ScopeCcd ?? 0),
        };
        return (test, L.F("Scope: {0}", scope));
    }

    /// <summary>Called by RunTest right after MarkInProgress (test thread).</summary>
    private void OnTestStartedForAutostart()
    {
        string? err = AutoStart.OnTestStarted();
        if (err != null) Ui(() => SetStatus(L.F("{0} · the test will not auto-resume after a crash", err), Theme.Danger));
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
            if (err != null) Ui(() => SetStatus(err, Theme.Danger));
        });
    }
}
