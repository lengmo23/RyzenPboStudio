using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>手动 Curve Optimizer 编辑器的可保存配置：每核负压 + FMax。</summary>
internal sealed class CoProfile
{
    [JsonPropertyName("offsets")] public List<int> Offsets { get; set; } = new();
    [JsonPropertyName("fmax")] public uint FMax { get; set; }

    private static string FilePath => Path.Combine(Workspace.ProfilesDir, "co_profile.json");

    public static CoProfile Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CoProfile>(File.ReadAllText(FilePath)) ?? new CoProfile();
        }
        catch (Exception e)
        {
            Log.Write($"读取 CO 配置失败: {e.Message}", "WARN");
        }
        return new CoProfile();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Write($"保存 CO 配置失败: {e.Message}", "WARN");
        }
    }
}
