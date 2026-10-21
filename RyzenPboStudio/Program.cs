namespace RyzenPboStudio;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool autostart = args.Any(a => string.Equals(a, AutoStart.Arg, StringComparison.OrdinalIgnoreCase));

        // 建立 logs\ / profiles\ 并迁移旧版遗留文件。必须早于任何状态读取，
        // 否则升级后读不到上次崩溃的恢复点。
        Workspace.EnsureLayout();

        // 启动即检测：Intel 处理器不支持
        if (SystemInfo.IsIntel())
        {
            MessageBox.Show(
                "Intel 处理器不支持！\n\n本工具仅支持 AMD Ryzen 处理器。",
                "不支持的处理器",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // manifest 已要求管理员权限，这里做一次兜底校验
        if (!SystemInfo.IsAdministrator())
        {
            MessageBox.Show(
                "请以管理员身份运行此程序！",
                "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Single instance: two processes driving the SMU would overwrite each other's CO writes.
        // Wait a few seconds so an instance still exiting after a self-update can release it.
        using var instanceMutex = new Mutex(false, @"Global\RyzenPboStudio.SingleInstance");
        bool ownsInstance;
        try
        {
            ownsInstance = instanceMutex.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            ownsInstance = true;   // previous instance died without releasing
        }
        if (!ownsInstance)
        {
            // Autostart while the user already opened the app: exit silently
            if (!autostart)
                MessageBox.Show(
                    "AMD Ryzen PBO Studio 已在运行。",
                    "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            ToolBundle.EnsureReady();
        }
        catch (Exception e)
        {
            MessageBox.Show(
                $"释放或校验测试组件失败：\n\n{e.Message}",
                "组件准备失败",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Curve Optimizer 读写经 ZenStates-Core，硬性依赖 PawnIO 驱动
        if (!RyzenSmu.IsPawnIoInstalled)
        {
            MessageBox.Show(
                "未检测到 PawnIO 驱动。\n\n" +
                "本工具通过 PawnIO 访问 CPU 的 Curve Optimizer，请先安装后再运行：\n" +
                "https://pawnio.eu",
                "缺少 PawnIO 驱动",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            Application.Run(new MainForm(autostart));
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }
}
