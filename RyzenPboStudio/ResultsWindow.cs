namespace RyzenPboStudio;

/// <summary>Non-modal table window for Single Core Boost and voltage alignment. Every public method may be called
/// from the worker thread; calls after the window is closed are dropped. Closing or pressing 取消 while the work
/// is still running invokes the cancel callback.</summary>
internal sealed class ResultsWindow : Form
{
    private readonly Label status;
    private readonly DataGridView grid;
    private readonly TextBox summary;
    private readonly Button actionBtn;
    private readonly Action onCancel;
    private bool finished;

    public ResultsWindow(string title, Action onCancel)
    {
        this.onCancel = onCancel;
        Text = title;
        BackColor = Theme.Bg;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 9F);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1080, 560);
        MinimumSize = new Size(640, 360);
        ShowInTaskbar = false;

        status = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(10, 8, 10, 0),
            ForeColor = Theme.Warn,
            BackColor = Theme.Surface,
            Text = "准备中…",
        };

        grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            BackgroundColor = MonTheme.Panel,
            BorderStyle = BorderStyle.None,
            GridColor = MonTheme.Grid,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 28,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = MonTheme.Grid,
                ForeColor = MonTheme.Fg,
                SelectionBackColor = MonTheme.Grid,
                SelectionForeColor = MonTheme.Fg,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font(Theme.FontFamily, 9F, FontStyle.Bold),
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = MonTheme.Panel,
                ForeColor = MonTheme.Fg,
                SelectionBackColor = MonTheme.Grid,
                SelectionForeColor = MonTheme.Fg,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = new Font(Theme.MonoFamily, 9.5F),
            },
            RowTemplate = { Height = 24 },
        };

        summary = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 110,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9F),
        };

        actionBtn = new Button
        {
            Text = "取消",
            Width = 96,
            Height = 30,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.TextHi,
        };
        actionBtn.FlatAppearance.BorderColor = Theme.Border;
        actionBtn.Click += (_, _) =>
        {
            if (finished) Close();
            else RequestCancel();
        };

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 42, BackColor = Theme.Bg };
        bar.Controls.Add(actionBtn);
        bar.Resize += (_, _) => actionBtn.Location = new Point(bar.ClientSize.Width - actionBtn.Width - 10, 6);

        Controls.Add(grid);
        Controls.Add(summary);
        Controls.Add(bar);
        Controls.Add(status);
    }

    private void RequestCancel()
    {
        if (finished) return;
        actionBtn.Enabled = false;
        status.Text = "正在取消…";
        onCancel();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RequestCancel();
        base.OnFormClosing(e);
    }

    private void Post(Action a)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void SetStatus(string text) => Post(() => status.Text = text);

    /// <summary>Replaces the columns and clears all rows.</summary>
    public void SetTable(IReadOnlyList<string> columns) => Post(() =>
    {
        grid.Rows.Clear();
        grid.Columns.Clear();
        foreach (string c in columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c, SortMode = DataGridViewColumnSortMode.NotSortable });
    });

    public void AddRow(IReadOnlyList<string> cells, Color? fore = null) => Post(() =>
    {
        int i = grid.Rows.Add(cells.Cast<object>().ToArray());
        if (fore is { } c) grid.Rows[i].DefaultCellStyle.ForeColor = c;
        try { grid.FirstDisplayedScrollingRowIndex = i; }
        catch (InvalidOperationException) { }   // no room to display rows, e.g. while minimised
    });

    public void SetSummary(string text) => Post(() => summary.Text = text.Replace("\n", "\r\n"));

    /// <summary>Marks the work as done: the cancel button becomes 关闭 and closing no longer cancels.</summary>
    public void Finish(string finalStatus, bool ok) => Post(() =>
    {
        finished = true;
        status.Text = finalStatus;
        status.ForeColor = ok ? Theme.Success : Theme.Warn;
        actionBtn.Text = "关闭";
        actionBtn.Enabled = true;
    });
}
