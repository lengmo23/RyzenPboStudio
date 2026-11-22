namespace RyzenPboStudio;

/// <summary>PBO Tuning page BIOS persistence through the motherboard's AMD_ACPI (AOD) WMI interface.</summary>
internal sealed partial class MainForm
{
    private Card _pboBiosCard = null!;
    private PillButton _pboBiosBtn = null!;
    private bool _pboBiosWriting;
    private bool? _pboBiosLaptop;

    /// <summary>Builds the bottom-right card aligned with the PBO parameter bar.</summary>
    private Control BuildPboBiosBar()
    {
        _pboBiosLaptop ??= SystemInfo.IsLaptop();
        _pboBiosCard = NewCard();
        _pboBiosCard.Margin = new Padding(0);
        _pboBiosCard.Padding = new Padding(14, 9, 14, 9);

        _pboBiosBtn = Primary(L.T("Write to BIOS"));
        _pboBiosBtn.Dock = DockStyle.Fill;
        _pboBiosBtn.Radius = 7;
        _pboBiosBtn.BackColor = Theme.Surface;
        _pboBiosBtn.Margin = new Padding(0);
        _pboBiosBtn.Click += (_, _) => _ = OnPboBiosWriteAsync();

        _pboBiosCard.Controls.Add(_pboBiosBtn);
        RefreshPboBiosButton();
        return _pboBiosCard;
    }

    /// <summary>Permanently disables BIOS writes on laptops (untested); on desktops, unlocks only in Expert mode.
    /// Uses SoftDisabled so the HWND stays enabled and ToolTip hover text does not flicker.</summary>
    private void RefreshPboBiosButton()
    {
        if (_pboBiosBtn == null) return;
        bool laptop = _pboBiosLaptop ??= SystemInfo.IsLaptop();
        bool unlocked = !laptop && AppSettings.Current.ExpertMode;
        _pboBiosBtn.SoftDisabled = !unlocked || _pboBiosWriting;
        string tip = laptop
            ? L.T("Disabled on laptops (untested)")
            : unlocked
                ? L.T("Write current PBO / CO / CS settings to the BIOS")
                : L.T("Write current PBO / CO / CS settings to the BIOS (requires Expert mode in Settings)");
        _tips.SetToolTip(_pboBiosBtn, tip);
    }

    private async Task OnPboBiosWriteAsync()
    {
        if ((_pboBiosLaptop ??= SystemInfo.IsLaptop()) || !AppSettings.Current.ExpertMode || !_editorsReady
            || _pboBiosWriting || _coApplying || _pboApplying || _csApplying || _bootApplying || (_memOcPage?.IsWriting ?? false))
            return;

        if (_testTask is { IsCompleted: false } || _measureTask is { IsCompleted: false })
        {
            ThemedDialog.Show(this, L.T("Cannot write to BIOS while a test or measurement is running."), L.T("Write to BIOS"));
            return;
        }

        // Commit any typed-but-unblurred NumericUpDown input before snapshotting values.
        ValidateChildren();

        bool csSupported = _csApplyBtn.Enabled;
        var input = new AmdAcpi.PboBiosInput(
            CoOffsets: _coCells.Take(_coSlotCount).Select(c => (int)c.Value).ToList(),
            FMaxMHz: (uint)_fmaxBox.Value,
            FMaxWritable: _fmaxWritable,
            PptW: (uint)_pptBox.Value,
            TdcA: (uint)_tdcBox.Value,
            EdcA: (uint)_edcBox.Value,
            Scalar: SelectedScalar(),
            ScalarWritable: _scalarWritable,
            CsGrid: csSupported ? CsGridFromCells() : null,
            CsSupported: csSupported);

        _pboBiosBtn.SoftDisabled = true;
        SetStatus(L.T("Checking BIOS capabilities..."), Theme.Warn);
        AmdAcpi.PboBiosPlan plan;
        try
        {
            plan = await Task.Run(() => AmdAcpi.PreparePboWrite(input));
        }
        catch (Exception e)
        {
            if (IsDisposed) return;
            Log.Write(L.F("PBO BIOS write unavailable: {0}", e.Message), "WARN");
            SetStatus(L.T("PBO BIOS write unavailable"), Theme.Warn);
            ThemedDialog.Show(this, e.Message, L.T("Write to BIOS"));
            RefreshPboBiosButton();
            return;
        }
        if (IsDisposed) return;

        if (!ConfirmPboBiosWrite(plan))
        {
            SetStatus(L.T("Ready"), Theme.Success);
            RefreshPboBiosButton();
            return;
        }

        _pboBiosWriting = true;
        SetStatus(L.T("Writing PBO settings to BIOS..."), Theme.Warn);
        try
        {
            var result = await Task.Run(() => AmdAcpi.SendPboWrite(plan));
            if (IsDisposed) return;

            if (result.Complete)
            {
                Log.Write(L.F("Wrote PBO settings to BIOS ({0} commands): {1}", result.Written.Count, string.Join(" | ", plan.SummaryLines)));
                SetStatus(L.T("PBO settings written to BIOS (effective after a restart)"), Theme.Success);
                string done = L.F("All {0} commands were accepted by the BIOS firmware.\n\nChanges take effect after a restart.", result.Written.Count);
                if (plan.HasPositiveCo)
                    done += "\n\n" + L.T("Contains positive CO values: firmware acceptance does not guarantee BIOS sign retention after restart.");
                ThemedDialog.Show(this, done, L.T("Write to BIOS"));
            }
            else
            {
                string target = result.Failed != null ? $"{result.Failed.Label} ({result.Failed.DisplayValue})" : L.T("firmware check");
                string msg = L.F("BIOS write stopped at {0}: {1}\n\nAccepted: {2}/{3}. Remaining commands were not sent; earlier writes were not rolled back.",
                    target, result.Error ?? L.T("unknown error"), result.Written.Count, plan.Commands.Count);
                Log.Write(msg.Replace("\n\n", " "), "ERROR");
                SetStatus(L.T("PBO BIOS write failed"), Theme.Danger);
                ThemedDialog.Show(this, msg, L.T("Write to BIOS"));
            }
        }
        catch (Exception e)
        {
            if (IsDisposed) return;
            Log.Write(L.F("PBO BIOS write failed: {0}", e.Message), "ERROR");
            SetStatus(L.T("PBO BIOS write failed"), Theme.Danger);
            ThemedDialog.Show(this, L.F("PBO BIOS write failed: {0}", e.Message), L.T("Write to BIOS"));
        }
        finally
        {
            _pboBiosWriting = false;
            if (!IsDisposed) RefreshPboBiosButton();
        }
    }

    /// <summary>Structured, scrollable confirmation dialog showing PBO limits, CO, and CS before writing to BIOS.</summary>
    private bool ConfirmPboBiosWrite(AmdAcpi.PboBiosPlan plan)
    {
        const int width = 460;
        const int maxScrollHeight = 340;
        using var dlg = NewDialog(L.T("Write to BIOS"));
        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(22, 18, 22, 16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));

        var warnings = new List<string>();
        if (!plan.IsGraniteRidge)
            warnings.Add(L.T("Non-Zen 5 CPU detected: BIOS PBO / CO writing is experimental on this platform."));
        if (plan.HasPositiveCo)
            warnings.Add(L.T("Contains positive CO values: firmware acceptance does not guarantee BIOS sign retention after restart."));
        if (warnings.Count > 0)
        {
            root.Controls.Add(new Label
            {
                Text = string.Join("\n", warnings),
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                ForeColor = Theme.Danger,
                BackColor = Theme.Surface,
                Font = new Font(Theme.FontFamily, 9.5F, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 10),
            });
        }

        root.Controls.Add(new Label
        {
            Text = L.T("Confirm BIOS Write"),
            AutoSize = true,
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 12F, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 2),
        });
        root.Controls.Add(new Label
        {
            Text = L.F("AMD_ACPI v{0} · {1} commands · effective after a restart", plan.FirmwareVersion, plan.Commands.Count),
            AutoSize = true,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 8.75F),
            Margin = new Padding(0, 0, 0, 10),
        });

        int innerWidth = width - SystemInformation.VerticalScrollBarWidth - 28;
        var list = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.WellBg,
            Margin = new Padding(0),
            Padding = new Padding(2),
        };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, innerWidth));

        void AddSectionTitle(string title, bool first)
        {
            list.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = Theme.AccentHover,
                BackColor = Theme.WellBg,
                Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold),
                Margin = new Padding(0, first ? 0 : 12, 0, 6),
            });
        }

        static string Signed(int v) => v > 0 ? $"+{v}" : v.ToString();

        bool hasSection = false;
        if (plan.Limits.Count > 0)
        {
            AddSectionTitle(L.T("PBO Parameters"), first: !hasSection);
            hasSection = true;
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Theme.WellBg,
                Margin = new Padding(0),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, innerWidth - 110));
            for (int r = 0; r < plan.Limits.Count; r++)
            {
                grid.Controls.Add(new Label
                {
                    Text = plan.Limits[r].Label,
                    AutoSize = true,
                    ForeColor = Theme.TextLo,
                    BackColor = Theme.WellBg,
                    Font = new Font(Theme.FontFamily, 9F),
                    Margin = new Padding(0, 2, 8, 2),
                }, 0, r);
                grid.Controls.Add(new Label
                {
                    Text = plan.Limits[r].Value,
                    AutoSize = true,
                    ForeColor = Theme.TextHi,
                    BackColor = Theme.WellBg,
                    Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold),
                    Margin = new Padding(0, 2, 0, 2),
                }, 1, r);
            }
            list.Controls.Add(grid);
        }

        if (plan.CoEntries.Count > 0)
        {
            AddSectionTitle(L.F("Curve Optimizer ({0} cores)", plan.CoEntries.Count), first: !hasSection);
            hasSection = true;
            int rows = (plan.CoEntries.Count + 1) / 2;
            int pairWidth = innerWidth / 2;
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                BackColor = Theme.WellBg,
                Margin = new Padding(0),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, pairWidth - 72));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, pairWidth - 72));
            for (int i = 0; i < plan.CoEntries.Count; i++)
            {
                int colGroup = i / rows;
                int row = i % rows;
                var (slot, val) = plan.CoEntries[i];
                grid.Controls.Add(new Label
                {
                    Text = $"Core {slot}",
                    AutoSize = true,
                    ForeColor = Theme.TextLo,
                    BackColor = Theme.WellBg,
                    Font = new Font(Theme.FontFamily, 9F),
                    Margin = new Padding(0, 2, 6, 2),
                }, colGroup * 2, row);
                grid.Controls.Add(new Label
                {
                    Text = Signed(val),
                    AutoSize = true,
                    ForeColor = val > 0 ? Theme.Warn : Theme.TextHi,
                    BackColor = Theme.WellBg,
                    Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold),
                    Margin = new Padding(0, 2, 12, 2),
                }, colGroup * 2 + 1, row);
            }
            list.Controls.Add(grid);
        }

        if (plan.CsGrid != null)
        {
            AddSectionTitle("Curve Shaper", first: !hasSection);
            hasSection = true;
            string[] cols = { "LOW", "MED", "HIGH" };
            string[] bands = { "MIN", "LOW", "MED", "HIGH", "MAX" };
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                BackColor = Theme.WellBg,
                Margin = new Padding(0),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            for (int c = 0; c < 3; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            for (int c = 0; c < 3; c++)
            {
                grid.Controls.Add(new Label
                {
                    Text = cols[c],
                    AutoSize = false,
                    Size = new Size(60, 18),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Theme.TextLo,
                    BackColor = Theme.WellBg,
                    Font = new Font(Theme.FontFamily, 8.25F, FontStyle.Bold),
                    Margin = new Padding(0, 0, 0, 3),
                }, c + 1, 0);
            }
            for (int t = 0; t < 5; t++)
            {
                grid.Controls.Add(new Label
                {
                    Text = bands[t],
                    AutoSize = false,
                    Size = new Size(54, 20),
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = Theme.TextLo,
                    BackColor = Theme.WellBg,
                    Font = new Font(Theme.FontFamily, 8.75F, FontStyle.Bold),
                    Margin = new Padding(0, 2, 6, 2),
                }, 0, t + 1);
                for (int c = 0; c < 3; c++)
                {
                    int v = plan.CsGrid[t, c];
                    grid.Controls.Add(new Label
                    {
                        Text = Signed(v),
                        AutoSize = false,
                        Size = new Size(60, 20),
                        TextAlign = ContentAlignment.MiddleCenter,
                        ForeColor = v > 0 ? Theme.Warn : Theme.TextHi,
                        BackColor = Theme.WellBg,
                        Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold),
                        Margin = new Padding(0, 2, 0, 2),
                    }, c + 1, t + 1);
                }
            }
            list.Controls.Add(grid);
        }

        if (plan.SkippedReasons.Count > 0)
        {
            list.Controls.Add(new Label
            {
                Text = L.T("Skipped: ") + string.Join(L.T(", "), plan.SkippedReasons),
                AutoSize = true,
                MaximumSize = new Size(innerWidth, 0),
                ForeColor = Theme.TextLo,
                BackColor = Theme.WellBg,
                Font = new Font(Theme.FontFamily, 8.5F),
                Margin = new Padding(0, 12, 0, 2),
            });
        }

        var scroller = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            BackColor = Theme.WellBg,
            Margin = new Padding(0),
        };
        scroller.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(scroller.Handle);
        scroller.Controls.Add(list);

        int prefH = list.GetPreferredSize(Size.Empty).Height;
        var well = new Card
        {
            Fill = Theme.WellBg,
            Stroke = Theme.BorderSubtle,
            Radius = 8,
            BackColor = Theme.Surface,
            Size = new Size(width, Math.Clamp(prefH + 22, 90, maxScrollHeight)),
            Padding = new Padding(12, 10, 6, 10),
            Margin = new Padding(0),
        };
        well.Controls.Add(scroller);
        root.Controls.Add(well);

        root.Controls.Add(new Label
        {
            Text = L.T("Wrong settings may prevent booting and need a CMOS clear to recover."),
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            ForeColor = Theme.TextMid,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 9F),
            Margin = new Padding(0, 10, 0, 0),
        });

        var ok = Pill(L.T("Write"), 96, primary: true);
        var cancel = Pill(L.T("Cancel"), 96);
        ok.DialogResult = DialogResult.Yes;
        cancel.DialogResult = DialogResult.No;
        cancel.Margin = new Padding(8, 0, 0, 0);
        var buttons = Flow(FlowDirection.LeftToRight);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, 14, 0, 0);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons);

        dlg.Controls.Add(root);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.Yes;
    }
}
