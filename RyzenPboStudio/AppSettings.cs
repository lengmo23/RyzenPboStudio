using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>profiles\settings.json: options from the settings dialog. Boot-apply stays in autostart.json.</summary>
internal sealed class AppSettings
{
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
    [JsonPropertyName("auto_check_update")] public bool AutoCheckUpdate { get; set; } = true;
    [JsonPropertyName("monitor_interval_ms")] public int MonitorIntervalMs { get; set; } = 500;
    [JsonPropertyName("confirm_exit")] public bool ConfirmExit { get; set; }
    /// <summary>Test, recovery and boot-apply prompts are logged instead of shown, and their questions take a fixed answer.</summary>
    [JsonPropertyName("expert_mode")] public bool ExpertMode { get; set; }

    /// <summary>Range of the monitor sampling window entered in the settings dialog.</summary>
    public const int MinMonitorIntervalMs = 100, MaxMonitorIntervalMs = 10000;

    public static AppSettings Current { get; private set; } = new();

    private static string FilePath => Path.Combine(Workspace.ProfilesDir, "settings.json");

    /// <summary>Loads the settings and applies the language; called once at startup before any UI text is built.</summary>
    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Current = new(); }
        Current.MonitorIntervalMs = Math.Clamp(Current.MonitorIntervalMs, MinMonitorIntervalMs, MaxMonitorIntervalMs);
        L.Init(Current.Language);
    }

    public bool Save()
    {
        try
        {
            DurableIO.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception e)
        {
            Log.Write(L.F("Failed to save settings.json: {0}", e.Message), "WARN");
            return false;
        }
    }
}
