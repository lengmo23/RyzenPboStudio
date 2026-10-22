using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>Calibrates the PM Table index for measured SVI3 TEL voltage.
/// Matches HWiNFO SVI3 samples against table values until one stable index remains.
/// Recalibrates after a PM Table version change; normal reads then need no HWiNFO.</summary>
internal sealed class TelVoltCalib
{
    private const double Tol = 0.0035; // Half of SVI3’s 5 mV quantization step plus margin.
    private const int LockAfterStable = 8; // Consecutive windows with one stable candidate.
    private const int ForceAfter = 15; // Force the lowest-error candidate after a prolonged tie.

    /// <summary>Locked PM Table float index, or -1 before calibration.</summary>
    public int Index { get; private set; } = -1;

    private readonly uint tableVersion;
    private HashSet<int>? candidates;
    private readonly Dictionary<int, double> errSum = new();
    private int stableWins, totalWins;

    private static string FilePath => Path.Combine(Workspace.ProfilesDir, "tel_calib.json");

    private sealed class Persist
    {
        [JsonPropertyName("tel_idx")] public int TelIdx { get; set; } = -1;
        [JsonPropertyName("table_version")] public uint TableVersion { get; set; }
    }

    public TelVoltCalib(uint tableVersion)
    {
        this.tableVersion = tableVersion;
        try
        {
            if (File.Exists(FilePath))
            {
                var p = JsonSerializer.Deserialize<Persist>(File.ReadAllText(FilePath));
                if (p != null && p.TelIdx >= 0 && p.TableVersion == tableVersion)
                    Index = p.TelIdx;
            }
        }
        catch (JsonException e)
        {
            Log.Write($"TEL 校准数据已损坏，丢弃: {e.Message}", "WARN");
            try { File.Delete(FilePath); } catch { }
        }
        catch (Exception e)
        {
            Log.Write($"读取 TEL 校准失败: {e.Message}", "WARN");
        }
    }

    /// <summary>Feeds a PM Table snapshot and HWiNFO SVI3 voltage while unlocked.</summary>
    public void Feed(float[] pt, double telHw)
    {
        var cur = new List<int>();
        for (int j = 0; j < pt.Length; j++)
        {
            double v = pt[j];
            if (v > 0.3 && v < 2.0 && Math.Abs(v - telHw) <= Tol) cur.Add(j);
        }
        if (cur.Count == 0) return; // Skip one unmatched window without resetting candidates.

        totalWins++;
        foreach (int j in cur)
            errSum[j] = (errSum.TryGetValue(j, out var s) ? s : 0) + Math.Abs(pt[j] - telHw);

        if (candidates == null) candidates = new HashSet<int>(cur);
        else candidates.IntersectWith(cur);

        if (candidates.Count == 0)
        {
            // Restart candidate search if the intersection becomes empty.
            candidates = new HashSet<int>(cur);
            stableWins = 0; totalWins = 1;
            errSum.Clear();
            foreach (int j in cur) errSum[j] = Math.Abs(pt[j] - telHw);
            return;
        }
        if (candidates.Count == 1)
        {
            if (++stableWins >= LockAfterStable) LockIndex(candidates.First());
            return;
        }
        stableWins = 0;
        if (totalWins >= ForceAfter)
            LockIndex(candidates.OrderBy(j => errSum.TryGetValue(j, out var s) ? s : double.MaxValue).First());
    }

    private void LockIndex(int idx)
    {
        Index = idx;
        try
        {
            DurableIO.WriteAllText(FilePath, JsonSerializer.Serialize(new Persist { TelIdx = idx, TableVersion = tableVersion }));
        }
        catch (Exception e)
        {
            Log.Write($"保存 TEL 校准失败: {e.Message}", "WARN");
        }
        Log.Write($"TEL 电压已本地校准: PM Table 偏移 0x{idx * 4:X}（此后不依赖 HWiNFO）");
    }
}
