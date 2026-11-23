namespace RyzenPboStudio;

/// <summary>Result table for Single Core Boost and voltage alignment, hosted on the TESTING page. Every public method
/// except Begin may be called from the worker thread.</summary>
internal sealed class ResultsPanel : Panel
{
    private readonly Label title;
    private readonly Label status;
    private readonly DataGridView grid;
    private readonly TextBox summary;
    private Font? _gridHdrFont;
    private Font? _gridCellFont;

    /// <summary>Top-right toolbar container hosting Single Core Boost and voltage alignment triggers.</summary>
    public FlowLayoutPanel HeaderActions { get; }

    public ResultsPanel()
    {
        BackColor = Theme.Surface;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 9F);

        title = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(0),
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = L.T("Single Core Boost / Voltage Alignment"),
        };

        status = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            Padding = new Padding(0),
            ForeColor = Theme.TextLo,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Text = L.T("Per-core results appear here after Single Core Boost or Voltage Alignment"),
        };

        var titleStack = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        titleStack.Controls.Add(status);
        titleStack.Controls.Add(title);

        HeaderActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Surface,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(8, 6, 0, 0),
        };

        var headerBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        headerBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        headerBar.Controls.Add(titleStack, 0, 0);
        headerBar.Controls.Add(HeaderActions, 1, 0);

        var headerDivider = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Theme.BorderSubtle,
        };
        var topGap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 8,
            BackColor = Theme.Surface,
        };

        grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            EnableHeadersVisualStyles = false,
            BackgroundColor = Theme.Surface,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            GridColor = Theme.SurfaceAlt,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 36,
            ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.SurfaceAlt,
                ForeColor = Theme.TextMid,
                SelectionBackColor = Theme.SurfaceAlt,
                SelectionForeColor = Theme.TextMid,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                WrapMode = DataGridViewTriState.True,
                Font = (_gridHdrFont = new Font(Theme.FontFamily, 8.75F, FontStyle.Bold)),
            },
            DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.TextHi,
                SelectionBackColor = Color.FromArgb(0x22, 0x2A, 0x38),
                SelectionForeColor = Theme.TextHi,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = (_gridCellFont = new Font(Theme.FontFamily, 9.25F)),
            },
            AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(0x1A, 0x20, 0x2B),
                ForeColor = Theme.TextHi,
                SelectionBackColor = Color.FromArgb(0x22, 0x2A, 0x38),
                SelectionForeColor = Theme.TextHi,
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Font = _gridCellFont,
            },
            RowTemplate = { Height = 26 },
        };
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(grid, true);

        summary = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.None,   // UpdateSummaryScroll turns it on only when the text overflows
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextHi,
            Font = new Font(Theme.FontFamily, 8.75F),
        };

        var summaryHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 76,
            BackColor = Theme.Surface,
            Padding = new Padding(2, 8, 2, 0),
        };
        summaryHost.Controls.Add(summary);

        var summaryDivider = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = Theme.BorderSubtle,
        };

        var bottomGap = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 8,
            BackColor = Theme.Surface,
        };

        summary.Resize += (_, _) => UpdateSummaryScroll();
        summary.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(summary.Handle);
        // The grid's scrollbars are child controls with their own windows.
        foreach (var bar in grid.Controls.OfType<ScrollBar>())
            bar.HandleCreated += (_, _) => DarkTitleBar.ApplyScrollBars(bar.Handle);

        // Dock order keeps header above and divider + summary below the filling table.
        Controls.Add(grid);
        Controls.Add(topGap);
        Controls.Add(headerDivider);
        Controls.Add(headerBar);
        Controls.Add(bottomGap);
        Controls.Add(summaryDivider);
        Controls.Add(summaryHost);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (grid == null) return;
        float k = Math.Max(0.6f, Font.Size / 9f);
        var oldHdr = _gridHdrFont;
        var oldCell = _gridCellFont;
        _gridHdrFont = new Font(Theme.FontFamily, 8.75F * k, FontStyle.Bold);
        _gridCellFont = new Font(Theme.FontFamily, 9.25F * k);
        grid.ColumnHeadersDefaultCellStyle.Font = _gridHdrFont;
        grid.DefaultCellStyle.Font = _gridCellFont;
        grid.AlternatingRowsDefaultCellStyle.Font = _gridCellFont;
        oldHdr?.Dispose();
        oldCell?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gridHdrFont?.Dispose();
            _gridCellFont?.Dispose();
        }
        base.Dispose(disposing);
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
        status.Text = L.T("Preparing…");
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
