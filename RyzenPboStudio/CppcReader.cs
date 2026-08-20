using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace RyzenPboStudio;

/// <summary>CPPC 每核性能排名的备用来源：Windows 内核电源事件 55。
/// Zen3（Vermeer）的 CPUID Fn8000_0008_EBX[27] 为 0，MSR CPPC_CAP1/CAP2/REQ 在硬件上不存在——
/// 实测 5800X3D 上三个 MSR 读取均返回成功但值全为 0，写 CPPC_ENABLE 也变不出来。
/// Windows 走 ACPI _CPC 拿同一份数据，开机枚举处理器时逐逻辑核记进事件 55，
/// 其 MaximumPerformancePercent 与 MSR 路径的 Highest×100/Nominal 同刻度
/// （9950X 上 16 个核逐核相等，故两条路径的读数可以直接互换）。</summary>
internal static class CppcReader
{
    private const string Provider = "Microsoft-Windows-Kernel-Processor-Power";
    private const int ScanLimit = 512;      // 每次开机只记 logicalCores 条，扫这么多足够翻到最近一轮
    private const int TimeoutMs = 2000;     // 日志过大时查询会拖慢启动，超时就放弃
    // 日志里混着更早的批次（启动早期 CPPC 尚未生效时记下的那些）。开机枚举整批在几秒内写完，
    // 故只收与最新一条同批的记录。
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMinutes(5);

    /// <summary>按物理核返回 MaximumPerformancePercent；读不到返回 null。
    /// tpc 为每核线程数，事件按逻辑处理器记录，物理核取其首个线程那条。</summary>
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
            ReverseDirection = true,   // 从最新往回读，每个逻辑核首次出现的就是本次开机那条
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
                batchNewest ??= ts;                     // 倒序读，第一条即最新
                if (batchNewest - ts > BatchWindow) break;   // 已翻到上一批，不再往回
                if (!TryParse(rec, out int lp, out uint pct)) continue;
                if (lp % tpc != 0) continue;            // 只取每个物理核的首线程
                int core = lp / tpc;
                if (core < 0 || core >= cores || perf[core] != 0) continue;
                perf[core] = pct;
                filled++;
            }
        }

        return filled > 0 ? perf : null;
    }

    /// <summary>按字段名取值而非按位置，避免 Windows 版本间字段顺序变化导致读错列。</summary>
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
