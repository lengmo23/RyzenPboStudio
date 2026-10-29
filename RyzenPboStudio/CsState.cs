using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>
/// Stores last applied CS margins when old BIOS versions return all-zero readback
/// despite successful writes, so refresh does not clear the editor.
/// </summary>
internal sealed class CsState
{
    /// <summary>Whether CS readback is unavailable on this BIOS.</summary>
    [JsonPropertyName("read_broken")] public bool ReadBroken { get; set; }

    /// <summary>Last applied margins flattened as tier * 3 + temperature column.</summary>
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
            // A power loss can leave an all-zero file; delete it to avoid repeated parse failures.
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

    /// <summary>Expands saved margins to a 5-by-3 grid, padding missing values with zero.</summary>
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

    /// <summary>Stores a 5-by-3 margin grid in flat order.</summary>
    public void FromGrid(int[,] g)
    {
        var list = new List<int>(15);
        for (int t = 0; t < 5; t++)
            for (int c = 0; c < 3; c++)
                list.Add(g[t, c]);
        Margins = list;
    }
}
