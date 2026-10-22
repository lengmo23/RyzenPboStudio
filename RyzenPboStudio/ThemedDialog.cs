using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>Uses DWM dark-title attribute 20, falling back to 19 on older Windows.</summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* Dark title bar is optional. */ }
    }
}

/// <summary>Dark message and confirmation dialog matching the main window.</summary>
internal sealed class ThemedDialog : Form
{
    private ThemedDialog(string text, string title, bool warning, string acceptText, string? cancelText)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 10F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var stripe = new Panel
        {
            Width = 4,
            Dock = DockStyle.Fill,
            BackColor = warning ? Theme.Warn : Theme.Border,
            Margin = new Padding(0, 2, 12, 2),
        };
        var message = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            ForeColor = Theme.TextHi,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 0, 0, 16),
        };
        PillButton NewButton(string label, DialogResult result, bool primary) => new()
        {
            Text = label,
            Size = new Size(96, 34),
            Normal = primary ? Theme.Accent : Theme.SurfaceAlt,
            Hover = primary ? Theme.AccentHover : Theme.Border,
            ForeColor = primary ? Theme.AccentText : Theme.TextHi,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold),
            DialogResult = result,
            Margin = new Padding(0),
        };
        var ok = NewButton(acceptText, cancelText == null ? DialogResult.OK : DialogResult.Yes, true);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        PillButton? cancel = null;
        if (cancelText != null)
        {
            cancel = NewButton(cancelText, DialogResult.No, false);
            cancel.Margin = new Padding(8, 0, 0, 0);
            buttons.Controls.Add(cancel);
        }
        buttons.Controls.Add(ok);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Theme.Surface,
            Padding = new Padding(18, 16, 18, 14),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.Controls.Add(stripe, 0, 0);
        layout.Controls.Add(message, 1, 0);
        layout.Controls.Add(buttons, 1, 1);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel ?? ok;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
    }

    public static void Show(IWin32Window? owner, string text, string title, bool warning = false)
    {
        using var dlg = new ThemedDialog(text, title, warning, "确定", null);
        if (owner == null) dlg.StartPosition = FormStartPosition.CenterScreen;
        if (owner == null) dlg.ShowDialog(); else dlg.ShowDialog(owner);
    }

    public static bool Ask(IWin32Window owner, string text, string title, string acceptText, string cancelText)
    {
        using var dlg = new ThemedDialog(text, title, false, acceptText, cancelText);
        return dlg.ShowDialog(owner) == DialogResult.Yes;
    }
}
