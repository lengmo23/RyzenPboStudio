using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace RyzenPboStudio;

/// <summary>Uses Windows Kernel-Processor-Power Event 55 as a CPPC ranking fallback.
/// Zen3 lacks CPPC MSRs, but ACPI _CPC values are logged at boot.
/// MaximumPerformancePercent uses the same scale as Highest * 100 / Nominal.</summary>
internal static class CppcReader
{
    private const string Provider = "Microsoft-Windows-Kernel-Processor-Power";
    private const int ScanLimit = 512; // Enough records to cover the latest boot batch.
    private const int TimeoutMs = 2000; // Avoid delaying startup on large event logs.
    // Only collect records from the latest boot batch.
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMinutes(5);

    /// <summary>Returns MaximumPerformancePercent per physical core, or null.
    /// Event records are per logical processor; use the first sibling.</summary>
    public static uint[]? Read(int cores, int tpc)
    {
        if (cores <= 0 || tpc <= 0) return null;
        try
        {
            var task = Task.Run(() => ReadCore(cores, tpc));
            return task.Wait(TimeoutMs) ? task.Result : null;
        }
        catch (Exception e)
        {
            Log.Write($"读取 CPPC 事件日志失败: {e.Message}", "WARN");
            return null;
        }
    }

    private static uint[]? ReadCore(int cores, int tpc)
    {
        var query = new EventLogQuery("System", PathType.LogName,
            $"*[System[Provider[@Name='{Provider}'] and (EventID=55)]]")
        {
            ReverseDirection = true,
        };

        var perf = new uint[cores];
        int filled = 0, scanned = 0;
        DateTime? batchNewest = null;

        using var reader = new EventLogReader(query);
        for (EventRecord? rec = reader.ReadEvent();
             rec != null && filled < cores && scanned < ScanLimit;
             rec = reader.ReadEvent())
        {
            using (rec)
            {
                scanned++;
                if (rec.TimeCreated is not { } ts) continue;
                batchNewest ??= ts;
                if (batchNewest - ts > BatchWindow) break;
                if (!TryParse(rec, out int lp, out uint pct)) continue;
                if (lp % tpc != 0) continue; // Use the first thread of each physical core.
                int core = lp / tpc;
                if (core < 0 || core >= cores || perf[core] != 0) continue;
                perf[core] = pct;
                filled++;
            }
        }

        return filled > 0 ? perf : null;
    }

    /// <summary>Reads event fields by name to tolerate Windows field-order changes.</summary>
    private static bool TryParse(EventRecord rec, out int lp, out uint pct)
    {
        lp = -1; pct = 0;
        var root = XDocument.Parse(rec.ToXml()).Root;
        if (root == null) return false;
        XNamespace ns = root.GetDefaultNamespace();
        var data = root.Element(ns + "EventData")?.Elements(ns + "Data");
        if (data == null) return false;

        foreach (var d in data)
        {
            switch (d.Attribute("Name")?.Value)
            {
                case "Number": int.TryParse(d.Value, out lp); break;
                case "MaximumPerformancePercent": uint.TryParse(d.Value, out pct); break;
            }
        }
        return lp >= 0 && pct > 0;
    }
}
