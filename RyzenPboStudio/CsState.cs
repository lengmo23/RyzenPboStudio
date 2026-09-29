using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>
/// Curve Shaper 备用模式状态。旧 BIOS 无 CS 读取接口（读回全 0，但写入已生效），
/// 此时保存用户最后应用的 CS margin，刷新时回填，避免编辑器被读回的 0 重置。
/// </summary>
internal sealed class CsState
{
    /// <summary>该机 CS 读取接口是否失效（读回 raw args 全 0）。</summary>
    [JsonPropertyName("read_broken")] public bool ReadBroken { get; set; }

    /// <summary>最后一次成功应用的 CS margin，按 [tier*3 + col] 扁平存储（col: 0=低温 1=中温 2=高温）。</summary>
    [JsonPropertyName("margins")] public List<int> Margins { get; set; } = new();

    private static string FilePath => Path.Combine(Workspace.ProfilesDir, "cs_state.json");

    public bool HasMargins => Margins.Count >= 15;

    public static CsState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CsState>(File.ReadAllText(FilePath)) ?? new CsState();
        }
        catch (JsonException e)
        {
            // 断电常把文件留成一串 0x00，内容已无从恢复，删掉以免每次启动重复报错
            Log.Write($"CS 备用状态已损坏，丢弃: {e.Message}", "WARN");
            try { File.Delete(FilePath); } catch { }
        }
        catch (Exception e)
        {
            Log.Write($"读取 CS 备用状态失败: {e.Message}", "WARN");
        }
        return new CsState();
    }

    public void Save()
    {
        try
        {
            DurableIO.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Write($"保存 CS 备用状态失败: {e.Message}", "WARN");
        }
    }

    /// <summary>把扁平 margin 还原成 [5,3] grid（不足 15 个时余下为 0）。</summary>
    public int[,] ToGrid()
    {
        var g = new int[5, 3];
        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
            {
                int idx = t * 3 + c;
                if (idx < Margins.Count) g[t, c] = Margins[idx];
            }
        return g;
    }

    /// <summary>用 [5,3] grid 覆盖保存的 margin。</summary>
    public void FromGrid(int[,] g)
    {
        var list = new List<int>(15);
        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
                list.Add(g[t, c]);
        Margins = list;
    }
}
