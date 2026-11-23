using System.Diagnostics;

namespace RyzenPboStudio;

/// <summary>Settings dialog opened from the gear in the side rail.</summary>
internal sealed partial class MainForm
{
    private bool _skipExitConfirm;   // Restart and update close the window without the exit confirmation.

    private void ShowSettingsDialog()
    {
        var settings = AppSettings.Current;
        using var dialog = new Form
        {
            Text = L.T("Settings"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Bg,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 10F),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        dialog.HandleCreated += (_, _) => DarkTitleBar.Apply(dialog.Handle);
        using var tips = new ToolTip { AutoPopDelay = 12000, InitialDelay = 350, ReshowDelay = 120 };

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Bg,
            Padding = new Padding(18, 10, 18, 14),
        };
        // Added first so labels inherit the dialog font before they are measured for the column widths.
        dialog.Controls.Add(root);
        var grids = new List<TableLayoutPanel>();
        var cards = new List<(Card Card, TableLayoutPanel Grid)>();

        // One titled card per group; each row is a caption on the left and its control on the right.
        TableLayoutPanel Section(string title)
        {
            root.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                ForeColor = Theme.TextLo,
                BackColor = Theme.Bg,
                Font = new Font(Theme.FontFamily, 9F),
                Margin = new Padding(2, 8, 0, 4),
            });
            var grid = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Theme.Surface,
                Location = new Point(14, 6),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            var card = new Card { Margin = new Padding(0) };
            card.Controls.Add(grid);
            root.Controls.Add(card);
            grids.Add(grid);
            cards.Add((card, grid));
            return grid;
        }
        void Row(TableLayoutPanel grid, string caption, Control control)
        {
            int row = grid.RowCount++;
            grid.Controls.Add(new Label
            {
                Text = caption,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = Theme.TextHi,
                BackColor = Theme.Surface,
                Margin = new Padding(0, 9, 12, 9),
            }, 0, row);
            control.Anchor = AnchorStyles.Right;
            grid.Controls.Add(control, 1, row);
        }
        AccentToggle Toggle(bool on, Action<bool> changed)
        {
            var t = new AccentToggle { Checked = on, Size = new Size(48, 30), BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 2) };
            t.Click += (_, _) =>
            {
                t.Checked = !t.Checked;
                changed(t.Checked);
            };
            return t;
        }
        PillButton Small(string text)
        {
            var b = Ghost(text);
            b.Font = new Font(Theme.FontFamily, 9F);
            b.Size = new Size(Math.Max(76, TextRenderer.MeasureText(text, b.Font).Width + 24), 28);
            b.BackColor = Theme.Surface;
            b.Margin = new Padding(8, 3, 0, 3);
            return b;
        }
        FlowLayoutPanel Flow(params Control[] items)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0) };
            f.Controls.AddRange(items);
            return f;
        }

        // General
        var general = Section(L.T("General"));
        string startLanguage = settings.Language;
        var codes = new List<string> { L.Auto };
        var names = new List<string> { L.T("System default") };
        foreach (var (code, name) in L.Available())
        {
            codes.Add(code);
            names.Add(name);
        }
        var language = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextHi,
            Width = 160,
            Margin = new Padding(0, 6, 0, 6),
        };
        DarkCombo.Apply(language);
        language.Items.AddRange(names.Cast<object>().ToArray());
        language.SelectedIndex = Math.Max(0, codes.IndexOf(settings.Language));
        Row(general, L.T("Language"), language);

        // Language takes effect after a restart; the hint row appears once the choice differs from the running one.
        var restart = Small(L.T("Restart now"));
        bool restartNow = false;
        restart.Click += (_, _) =>
        {
            restartNow = true;
            dialog.Close();
        };
        var restartHint = new Label { Text = L.T("Takes effect after a restart"), AutoSize = true, ForeColor = Theme.Warn, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
        var restartRow = Flow(restartHint, restart);
        restartRow.Anchor = AnchorStyles.Right;
        restartRow.Visible = false;
        general.Controls.Add(restartRow, 1, general.RowCount++);

        Row(general, L.T("Confirm before exit"), Toggle(settings.ConfirmExit, on =>
        {
            settings.ConfirmExit = on;
            settings.Save();
        }));

        // Updates
        var updates = Section(L.T("Updates"));
        Row(updates, L.T("Check for updates at startup"), Toggle(settings.AutoCheckUpdate, on =>
        {
            settings.AutoCheckUpdate = on;
            settings.Save();
        }));

        // Monitor
        var monitor = Section(L.T("Monitor"));
        var interval = new NumericUpDown
        {
            Minimum = AppSettings.MinMonitorIntervalMs,
            Maximum = AppSettings.MaxMonitorIntervalMs,
            Increment = 100,
            Value = settings.MonitorIntervalMs,
            Width = 90,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextHi,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Center,
            Margin = new Padding(0, 5, 6, 5),
        };
        DarkSpin.Apply(interval);
        void SaveInterval()
        {
            int ms = (int)interval.Value;
            if (ms == settings.MonitorIntervalMs) return;
            settings.MonitorIntervalMs = ms;
            settings.Save();
        }
        interval.ValueChanged += (_, _) => SaveInterval();
        // Value commits typed text, clamped to the range.
        dialog.FormClosing += (_, _) => SaveInterval();
        Row(monitor, L.T("Monitor refresh interval"), Flow(interval,
            new Label { Text = "ms", AutoSize = true, ForeColor = Theme.TextLo, BackColor = Theme.Surface, Margin = new Padding(0, 9, 0, 0) }));

        // Data
        var data = Section(L.T("Data"));
        PillButton OpenDir(string text, string dir)
        {
            var b = Small(text);
            b.Click += (_, _) =>
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
                catch (Exception e) { ThemedDialog.Show(dialog, L.F("Cannot open the folder: {0}", e.Message), L.T("Settings")); }
            };
            return b;
        }
        Row(data, L.T("Data folders"), Flow(OpenDir("profiles", Workspace.ProfilesDir), OpenDir("logs", Workspace.LogsDir)));

        var expert = new AccentToggle { Checked = settings.ExpertMode, Size = new Size(48, 30), BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 2) };
        var expertHint = new Label
        {
            Text = L.T("Write to BIOS unlocked; laptops may need battery/CMOS removal if unbootable."),
            AutoSize = true,
            ForeColor = Theme.Warn,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 8.75F),
            Margin = new Padding(0, 0, 0, 8),
            Visible = settings.ExpertMode,
        };
        tips.SetToolTip(expert, L.T("Suppress routine prompts and unlock Write to BIOS (improper BIOS settings may prevent booting; laptops may need battery removal to recover)"));
        Row(general, L.T("Expert mode"), expert);

        // Footer
        var close = Primary(L.T("Done"));
        close.Size = new Size(96, 34);
        close.Anchor = AnchorStyles.Right;
        close.Margin = new Padding(0);
        close.DialogResult = DialogResult.OK;
        var footer = new TableLayoutPanel { Height = close.Height, ColumnCount = 2, BackColor = Theme.Bg, Margin = new Padding(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label
        {
            Text = "AMD Ryzen PBO Studio " + HeaderBar.DisplayVersion,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Theme.TextLo,
            BackColor = Theme.Bg,
            Font = new Font(Theme.FontFamily, 9F),
        }, 0, 0);
        footer.Controls.Add(close, 1, 0);
        root.Controls.Add(footer);

        // Columns widen to fit the text, so longer translations and high DPI are not clipped; every card gets the same width.
        // Hidden labels are not part of their row's preferred size yet, so those rows are measured by parts.
        static int W(Control c) => (c is Button ? c.Width : c.PreferredSize.Width) + c.Margin.Horizontal;
        int captionW = 200, controlW = Math.Max(200, W(restartHint) + W(restart));
        foreach (var g in grids)
            foreach (Control c in g.Controls)
            {
                if (g.GetColumn(c) == 0) captionW = Math.Max(captionW, W(c));
                else controlW = Math.Max(controlW, W(c));
            }
        foreach (var g in grids)
        {
            g.ColumnStyles[0].Width = captionW;
            g.ColumnStyles[1].Width = controlW;
        }
        void SizeCards()
        {
            foreach (var (card, grid) in cards)
            {
                var pref = grid.GetPreferredSize(Size.Empty);
                card.Size = new Size(pref.Width + grid.Left * 2, pref.Height + grid.Top * 2);
            }
            footer.Width = cards[0].Card.Width;
        }
        expertHint.MaximumSize = new Size(captionW + controlW, 0);
        general.Controls.Add(expertHint, 0, general.RowCount++);
        general.SetColumnSpan(expertHint, 2);
        SizeCards();

        expert.Click += (_, _) =>
        {
            bool next = !expert.Checked;
            if (next && !ThemedDialog.Ask(dialog,
                L.T("Enabling Expert mode suppresses routine test/recovery prompts and unlocks Write to BIOS.\n\n• Desktop: if the system fails to boot, clear CMOS via the motherboard jumper or CMOS battery.\n• Laptop: most laptops have no external reset button; recovering from a black screen may require disassembling the chassis to disconnect the main battery and CMOS battery. Only proceed if you can recover from a failed boot."),
                L.T("Enable Expert Mode"), L.T("Enable"), L.T("Cancel"),
                L.T("High-risk operation: writing settings to the BIOS may prevent the system from booting.")))
                return;

            expert.Checked = next;
            settings.ExpertMode = next;
            settings.Save();
            expertHint.Visible = next;
            SizeCards();
            RefreshPboBiosButton();
            _memOcPage?.RefreshCommitButton();
            Log.Write(next ? L.T("Expert mode on") : L.T("Expert mode off"));
        };

        language.SelectedIndexChanged += (_, _) =>
        {
            settings.Language = codes[language.SelectedIndex];
            settings.Save();
            restartRow.Visible = settings.Language != startLanguage;
            SizeCards();
        };

        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
        if (restartNow) RestartApp();
    }

    /// <summary>Asked on a user close when "Confirm before exit" is on and no running task already asked.</summary>
    private bool ConfirmExit() =>
        _skipExitConfirm || !AppSettings.Current.ConfirmExit
        || ThemedDialog.Ask(this, L.T("Exit AMD Ryzen PBO Studio?"), L.T("Confirm"), L.T("Exit"), L.T("Cancel"));

    /// <summary>Closes through the normal exit path (which asks while a test is running) and starts a new instance.
    /// The new instance waits for this one's single-instance mutex.</summary>
    private void RestartApp(string? arg = null)
    {
        _skipExitConfirm = true;
        Close();
        if (!IsDisposed || Environment.ProcessPath is not { } exe)
        {
            _skipExitConfirm = false;
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (!string.IsNullOrEmpty(arg)) psi.ArgumentList.Add(arg);
            Process.Start(psi);
        }
        catch (Exception e) { Log.Write(L.F("Restart failed: {0}", e.Message), "ERROR"); }
    }
}
