using System.Diagnostics;

namespace RyzenPboStudio;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool autostart = args.Any(a => string.Equals(a, AutoStart.Arg, StringComparison.OrdinalIgnoreCase));
        bool eggUnlocked = args.Any(a => string.Equals(a, "--egg-unlocked", StringComparison.OrdinalIgnoreCase));

        // Migrate runtime files before reading state so upgrades preserve crash recovery.
        Workspace.EnsureLayout();
        AppSettings.Load();

        // Reject unsupported Intel processors at startup.
        if (SystemInfo.IsIntel())
        {
            ThemedDialog.Show(null,
                L.T("Intel processors are not supported.\n\nThis tool supports AMD Ryzen processors only."),
                L.T("Unsupported processor"));
            return;
        }

        // Manifest requests elevation; verify it before hardware access.
        if (!SystemInfo.IsAdministrator())
        {
            ThemedDialog.Show(null,
                L.T("Please run this program as administrator."),
                L.T("Error"));
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
                    L.T("AMD Ryzen PBO Studio is already running."),
                    L.T("Notice"));
            return;
        }

        try
        {
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch (Exception e)
        {
            Log.Write(L.F("Failed to set the process priority: {0}", e.Message), "WARN");
        }

        try
        {
            ToolBundle.EnsureReady();
        }
        catch (Exception e)
        {
            ThemedDialog.Show(null,
                L.F("Failed to extract or verify the test components:\n\n{0}", e.Message),
                L.T("Component setup failed"));
            return;
        }

        // Curve Optimizer access requires PawnIO through ZenStates-Core.
        if (!RyzenSmu.IsPawnIoInstalled)
        {
            ThemedDialog.Show(null,
                L.T("PawnIO driver not found.\n\n" +
                    "This tool accesses the CPU Curve Optimizer through PawnIO. Install it, then run this program again:\n" +
                    "https://pawnio.eu"),
                L.T("PawnIO driver missing"));
            return;
        }

        try
        {
            Application.Run(new MainForm(autostart, eggUnlocked));
        }
        finally
        {
            instanceMutex.ReleaseMutex();
        }
    }
}
