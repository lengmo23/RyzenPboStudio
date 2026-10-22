using System.Diagnostics;

namespace RyzenPboStudio;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool autostart = args.Any(a => string.Equals(a, AutoStart.Arg, StringComparison.OrdinalIgnoreCase));

        // Migrate runtime files before reading state so upgrades preserve crash recovery.
        Workspace.EnsureLayout();

        // Reject unsupported Intel processors at startup.
        if (SystemInfo.IsIntel())
        {
            ThemedDialog.Show(null,
                "Intel 处理器不支持！\n\n本工具仅支持 AMD Ryzen 处理器。",
                "不支持的处理器", warning: true);
            return;
        }

        // Manifest requests elevation; verify it before hardware access.
        if (!SystemInfo.IsAdministrator())
        {
            ThemedDialog.Show(null,
                "请以管理员身份运行此程序！",
                "错误", warning: true);
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
                ThemedDialog.Show(null,
                    "AMD Ryzen PBO Studio 已在运行。",
                    "提示");
            return;
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception e)
        {
            Log.Write($"设置进程优先级失败: {e.Message}", "WARN");
        }

        try
        {
            ToolBundle.EnsureReady();
        }
        catch (Exception e)
        {
            ThemedDialog.Show(null,
                $"释放或校验测试组件失败：\n\n{e.Message}",
                "组件准备失败", warning: true);
            return;
        }

        // Curve Optimizer access requires PawnIO through ZenStates-Core.
        if (!RyzenSmu.IsPawnIoInstalled)
        {
            ThemedDialog.Show(null,
                "未检测到 PawnIO 驱动。\n\n" +
                "本工具通过 PawnIO 访问 CPU 的 Curve Optimizer，请先安装后再运行：\n" +
                "https://pawnio.eu",
                "缺少 PawnIO 驱动", warning: true);
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
