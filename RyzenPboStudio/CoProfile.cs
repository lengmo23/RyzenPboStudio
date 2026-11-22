using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>Saved per-core CO offsets for the manual editor; FMax belongs to the PBO limits, not here.</summary>
internal sealed class CoProfile
{
    [JsonPropertyName("offsets")] public List<int> Offsets { get; set; } = new();

    private static string FilePath => Path.Combine(Workspace.ProfilesDir, "co_profile.json");

    public static CoProfile Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CoProfile>(File.ReadAllText(FilePath)) ?? new CoProfile();
        }
        catch (JsonException e)
        {
            Log.Write(L.F("CO profile is corrupt and was discarded: {0}", e.Message), "WARN");
            try { File.Delete(FilePath); } catch { }
        }
        catch (Exception e)
        {
            Log.Write(L.F("Failed to read the CO profile: {0}", e.Message), "WARN");
        }
        return new CoProfile();
    }

    public void Save()
    {
        try
        {
            DurableIO.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Write(L.F("Failed to save the CO profile: {0}", e.Message), "WARN");
        }
    }
}
