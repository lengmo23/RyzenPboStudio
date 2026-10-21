using System.Runtime.InteropServices;

namespace RyzenPboStudio;

/// <summary>暗色标题栏（Win10 2004+ 用属性 20，更早版本回退 19）。失败时标题栏保持系统色。</summary>
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
        catch { /* 非关键 */ }
    }
}

/// <summary>与主界面同风格的提示框：暗色卡片底、左侧色条（警告为黄色）、红色「确定」按钮。</summary>
internal sealed class ThemedDialog : Form
{
    private ThemedDialog(string text, string title, bool warning)
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
        var ok = new PillButton
        {
            Text = "确定",
            Size = new Size(96, 34),
            Normal = Theme.Accent,
            Hover = Theme.AccentHover,
            ForeColor = Theme.AccentText,
            BackColor = Theme.Surface,
            Font = new Font(Theme.FontFamily, 10F, FontStyle.Bold),
            Anchor = AnchorStyles.Right,
            DialogResult = DialogResult.OK,
            Margin = new Padding(0),
        };

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
        layout.Controls.Add(ok, 1, 1);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = ok;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
    }

    public static void Show(IWin32Window owner, string text, string title, bool warning = false)
    {
        using var dlg = new ThemedDialog(text, title, warning);
        dlg.ShowDialog(owner);
    }
}
