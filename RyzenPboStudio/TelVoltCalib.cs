using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>TEL（SVI3 实测电压）的 PM Table 偏移自动校准。
/// HWiNFO 可用时把其 SVI3 读数与 PM Table 全表逐项对照，交集收敛到唯一吻合项后锁定偏移并持久化
/// （带 PM Table 版本校验，BIOS 换表后自动重新校准）；此后 TEL 直接本地读 PM Table，不依赖 HWiNFO。</summary>
internal sealed class TelVoltCalib
{
    private const double Tol = 0.0035;      // SVI3 5mV 量化半步 + 裕量
    private const int LockAfterStable = 8;  // 交集稳定为单项的连续窗数
    private const int ForceAfter = 15;      // 多候选僵持时按累计误差最小强制锁定的窗数

    /// <summary>锁定的 PM Table float 索引；-1 = 未校准。</summary>
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
        catch (Exception e)
        {
            Log.Write($"读取 TEL 校准失败: {e.Message}", "WARN");
        }
    }

    /// <summary>喂入一窗数据：pt = PM Table 快照，telHw = HWiNFO 的 SVI3 电压。仅未锁定时调用。</summary>
    public void Feed(float[] pt, double telHw)
    {
        var cur = new List<int>();
        for (int j = 0; j < pt.Length; j++)
        {
            double v = pt[j];
            if (v > 0.3 && v < 2.0 && Math.Abs(v - telHw) <= Tol) cur.Add(j);
        }
        if (cur.Count == 0) return;   // 本窗无候选（瞬时波动/采样错拍），跳过不重置

        totalWins++;
        foreach (int j in cur)
            errSum[j] = (errSum.TryGetValue(j, out var s) ? s : 0) + Math.Abs(pt[j] - telHw);

        if (candidates == null) candidates = new HashSet<int>(cur);
        else candidates.IntersectWith(cur);

        if (candidates.Count == 0)
        {
            // 交集清空：此前候选全被否定，重新从本窗开始
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Persist { TelIdx = idx, TableVersion = tableVersion }));
        }
        catch (Exception e)
        {
            Log.Write($"保存 TEL 校准失败: {e.Message}", "WARN");
        }
        Log.Write($"TEL 电压已本地校准: PM Table 偏移 0x{idx * 4:X}（此后不依赖 HWiNFO）");
    }
}
