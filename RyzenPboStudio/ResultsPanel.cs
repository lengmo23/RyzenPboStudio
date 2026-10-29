namespace RyzenPboStudio;

/// <summary>Result table for Single Core Boost and voltage alignment, hosted on the TESTING page. Every public method
/// except Begin may be called from the worker thread.</summary>
internal sealed class ResultsPanel : Panel
{
    private readonly Label title;
    private readonly Label status;
    private readonly DataGridView grid;
    private readonly TextBox summary;

    public ResultsPanel()
    {
        BackColor = Theme.Surface;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 9F);

        title = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(2, 2, 0, 0),
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 10.5F, FontStyle.Bold),
            Text = "Single Core Boost / 电压对齐",
        };

        status = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Padding = new Padding(2, 4, 0, 0),
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Text = "点击下方「Single Core Boost」或「电压对齐」后，这里显示每核结果",
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
            BackgroundColor = Theme.Surface,   // match the card so an empty table shows no darker block
            BorderStyle = BorderStyle.None,
            GridColor = MonTheme.Grid,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 40,   // two lines, so long headers wrap in the narrow column
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = MonTheme.Grid,
                ForeColor = MonTheme.Fg,
                SelectionBackColor = MonTheme.Grid,
                SelectionForeColor = MonTheme.Fg,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                WrapMode = DataGridViewTriState.True,
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
            Height = 64,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.None,   // UpdateSummaryScroll turns it on only when the text overflows
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 9F),
        };

        summary.Resize += (_, _) => UpdateSummaryScroll();
        summary.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(summary.Handle);
        // The grid's scrollbars are child controls with their own windows.
        foreach (var bar in grid.Controls.OfType<ScrollBar>())
            bar.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(bar.Handle);

        // Dock order keeps the title and status above the summary and filling table.
        Controls.Add(grid);
        Controls.Add(summary);
        Controls.Add(status);
        Controls.Add(title);
    }

    /// <summary>Shows the summary's scrollbar only when its text is taller than the box; an always-on bar shows an
    /// empty track while idle.</summary>
    private void UpdateSummaryScroll()
    {
        int width = Math.Max(1, summary.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        int height = summary.Text.Length == 0 ? 0
            : TextRenderer.MeasureText(summary.Text, summary.Font, new Size(width, int.MaxValue),
                                       TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        var want = height > summary.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
        if (summary.ScrollBars != want) summary.ScrollBars = want;
    }

    /// <summary>Clears the previous result for a new run (UI thread).</summary>
    public void Begin(string caption)
    {
        if (!IsHandleCreated) CreateHandle();
        title.Text = caption;
        status.Text = "准备中…";
        status.ForeColor = Theme.Warn;
        grid.Rows.Clear();
        grid.Columns.Clear();
        summary.Clear();
        UpdateSummaryScroll();
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

    /// <summary>Replaces the columns and clears all rows. wideLast doubles the last column for free text.</summary>
    public void SetTable(IReadOnlyList<string> columns, bool wideLast = false) => Post(() =>
    {
        grid.Rows.Clear();
        grid.Columns.Clear();
        for (int i = 0; i < columns.Count; i++)
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = columns[i],
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FillWeight = wideLast && i == columns.Count - 1 ? 200 : 100,
            });
    });

    public void AddRow(IReadOnlyList<string> cells, Color? fore = null) => Post(() =>
    {
        int i = grid.Rows.Add(cells.Cast<object>().ToArray());
        if (fore is { } c) grid.Rows[i].DefaultCellStyle.ForeColor = c;
        try { grid.FirstDisplayedScrollingRowIndex = i; }
        catch (InvalidOperationException) { }   // no room to display rows
    });

    public void SetSummary(string text) => Post(() =>
    {
        summary.Text = text.Replace("\n", "\r\n");
        UpdateSummaryScroll();
    });

    public void Finish(string finalStatus, bool ok) => Post(() =>
    {
        status.Text = finalStatus;
        status.ForeColor = ok ? Theme.Success : Theme.Warn;
    });
}
