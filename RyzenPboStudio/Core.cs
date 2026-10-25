using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>User-adjustable tuning settings.</summary>
internal static class Config
{
    public const int DefaultDuration = 120;
    public const int DefaultVt3 = 20;
    public const int DefaultBkt = 10;
    public const int DefaultSvt = 10;
    public const int DefaultStepOnError = 2;
    public const int MaxStepOnError = 5;
    // Auto-mode CO backoff per reported error; loaded from profiles\setting.json.
    public static int StepOnError { get; set; } = DefaultStepOnError;
    public const int CoMax = 30;      // highest CO the SMU accepts; backoff stops here.
}

/// <summary>Writes each session's log to a separate file and streams lines to the UI.</summary>
internal static class Log
{
    private static readonly object FileLock = new();
    private static readonly string RunFile = Path.Combine(Workspace.LogsDir,
        $"run_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}.log");
    private static StreamWriter? _writer;
    private static bool _writeFailureReported;

    public static event Action<string>? OnLine;

    public static void Write(string msg, string level = "INFO")
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";
        lock (FileLock)
        {
            try
            {
                if (_writer == null)
                {
                    Directory.CreateDirectory(Workspace.LogsDir);
                    _writer = new StreamWriter(
                        new FileStream(RunFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false)) { AutoFlush = true };
                }
                _writer.WriteLine(line);
            }
            catch (Exception e)
            {
                if (!_writeFailureReported)
                {
                    _writeFailureReported = true;
                    OnLine?.Invoke($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [WARN] 保存运行日志失败: {e.Message}");
                }
            }
            OnLine?.Invoke(line);
        }
    }

    /// <summary>
    /// Per-core CO as a title line plus one aligned line per core; fused-off slots are skipped. With
    /// <paramref name="before"/> only the changed cores are listed as "Core n  a → b".
    /// </summary>
    public static void WriteCo(string title, IReadOnlyList<int> co, IReadOnlyList<int>? before = null, string level = "INFO")
    {
        var slots = Enumerable.Range(0, co.Count)
            .Where(i => !RyzenSmu.IsSlotDisabled(i) && (before == null || i >= before.Count || before[i] != co[i]))
            .ToList();
        if (before != null && slots.Count == 0)
        {
            Write($"{title}无变化", level);
            return;
        }
        Write(title, level);
        foreach (int i in slots)
            Write(before != null && i < before.Count ? $"Core {i,-3} {before[i],3} → {co[i],3}" : $"Core {i,-3} {co[i],3}", level);
    }
}

/// <summary>Persisted test state; JSON property names must remain compatible across versions.</summary>
internal sealed class TestState
{
    [JsonPropertyName("offsets")] public List<int> Offsets { get; set; } = new();
    [JsonPropertyName("test_round")] public int TestRound { get; set; }
    [JsonPropertyName("test_mode")] public string TestMode { get; set; } = "VT3";
    [JsonPropertyName("seq_phase")] public string? SeqPhase { get; set; }
    [JsonPropertyName("duration_seconds")] public int? DurationSeconds { get; set; }
    [JsonPropertyName("iterations_map")] public Dictionary<string, int>? IterationsMap { get; set; }
    // Sequential / combined plan of the interrupted run; older state files only have IterationsMap.
    [JsonPropertyName("multi_test")] public MultiTestSettings? MultiTest { get; set; }
    // Scope values: ALL, one CCD, each CCD in turn, or custom cores.
    // EACH stores the current CCD so crash recovery can resume there.
    [JsonPropertyName("test_scope")] public string? TestScope { get; set; }
    [JsonPropertyName("scope_ccd")] public int? ScopeCcd { get; set; }
    [JsonPropertyName("scope_cores")] public List<int>? ScopeCores { get; set; }
    [JsonPropertyName("timestamp")] public double Timestamp { get; set; }
}

/// <summary>Runs a child process with captured output and a timeout.</summary>
internal static class ProcUtil
{
    public static (int code, string output) Capture(
        string exe, IEnumerable<string> args, string? workingDir, int timeoutMs)
    {
        using var p = new Process();
        p.StartInfo.FileName = exe;
        foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
        if (workingDir != null) p.StartInfo.WorkingDirectory = workingDir;
        p.StartInfo.UseShellExecute = false;
        p.StartInfo.CreateNoWindow = true;
        p.StartInfo.RedirectStandardOutput = true;
        p.StartInfo.RedirectStandardError = true;
        p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        p.StartInfo.StandardErrorEncoding = Encoding.UTF8;

        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
        p.WaitForExit();

        return (p.HasExited ? p.ExitCode : -1, sb.ToString());
    }
}

/// <summary>Application paths and runtime state files.</summary>
internal static class Workspace
{
    /// <summary>Executable directory used to locate external tools.</summary>
    public static readonly string BaseDir =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Directory for session logs and y-cruncher output.</summary>
    public static string LogsDir => Path.Combine(BaseDir, "logs");

    /// <summary>Directory for CO history, recovery state, CS settings, and calibration.</summary>
    public static string ProfilesDir => Path.Combine(BaseDir, "profiles");

    public static string StateFile => Path.Combine(ProfilesDir, "undervolt_state.json");
    public static string InProgressFlag => Path.Combine(ProfilesDir, "test_in_progress");
    public static string FinalOffsets => Path.Combine(ProfilesDir, "final_offsets.txt");

    /// <summary>Creates a timestamped path under logs.</summary>
    public static string NewLogPath(string prefix, string ext) =>
        Path.Combine(LogsDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");

    /// <summary>Legacy runtime files were stored beside the executable.</summary>
    private static readonly string[] LegacyProfileFiles =
    {
        "undervolt_state.json", "test_in_progress", "final_offsets.txt",
        "applied_offsets.ndjson", "cs_state.json", "co_profile.json", "tel_calib.json",
    };

    private static readonly string[] LegacyLogFiles = { "y-cruncher.log", "undervolt_log.txt" };

    /// <summary>
    /// Creates runtime directories and migrates files from the old executable directory.
    /// Call before reading the crash marker or CO history so upgrades preserve recovery.
    /// </summary>
    public static void EnsureLayout()
    {
        try { Directory.CreateDirectory(LogsDir); } catch { /* Report directory errors when writing. */ }
        try { Directory.CreateDirectory(ProfilesDir); } catch { /* Report directory errors when writing. */ }

        foreach (string name in LegacyProfileFiles)
            MoveLegacy(Path.Combine(BaseDir, name), Path.Combine(ProfilesDir, name));
        foreach (string name in LegacyLogFiles)
            MoveLegacy(Path.Combine(BaseDir, name), Path.Combine(LogsDir, name));
    }

    /// <summary>Migrates a legacy file without replacing newer data at the destination.</summary>
    private static void MoveLegacy(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to))
                File.Move(from, to);
        }
        catch
        {
            // Keep the legacy file if migration fails; startup can continue.
        }
    }

    public static void ClearState()
    {
        try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { }
    }

    /// <summary>Marks a test active; an uncleared marker indicates an interrupted run.</summary>
    public static void MarkInProgress(string info)
    {
        try { DurableIO.WriteAllText(InProgressFlag, info); } catch { }
    }

    public static void ClearInProgress()
    {
        try { if (File.Exists(InProgressFlag)) File.Delete(InProgressFlag); } catch { }
    }

    public static bool WasInterrupted() => File.Exists(InProgressFlag);

    public static void SaveState(TestState state)
    {
        try
        {
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            DurableIO.WriteAllText(StateFile, json); // Flush and replace atomically.
        }
        catch (Exception e)
        {
            Log.Write($"保存状态失败: {e.Message}", "WARN");
        }
    }

    public static TestState? LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                return JsonSerializer.Deserialize<TestState>(
                    File.ReadAllText(StateFile),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch (Exception e)
        {
            Log.Write($"读取状态失败: {e.Message}", "WARN");
        }
        return null;
    }

    /// <summary>Reads recent power or bug-check events for recovery logs, not crash detection.</summary>
    public static bool RecentKernelPowerEvent()
    {
        try
        {
            var (_, output) = ProcUtil.Capture(
                "wevtutil",
                new[]
                {
                    "qe", "System",
                    "/q:*[System[(EventID=41 or EventID=1001)]]",
                    "/c:1", "/f:text",
                },
                null, 15000);

            if (output.Contains("Event ID: 41") || output.Contains("Event ID: 1001"))
                return true;
        }
        catch
        {
            // Treat event-log query failure as no event.
        }
        return false;
    }
}

/// <summary>Flushes writes to disk before atomic replacement or append.</summary>
internal static class DurableIO
{
    public static void WriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                                       4096, FileOptions.WriteThrough))
        using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            w.Write(text);
            w.Flush();
            fs.Flush(flushToDisk: true); // Flush through the OS cache to disk.
        }
        File.Move(tmp, path, overwrite: true); // Same-volume replacement prevents a partial state file.
    }

    public static void AppendLine(string path, string line)
    {
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                      4096, FileOptions.WriteThrough);
        byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }
}

/// <summary>One persisted CO application record.</summary>
internal sealed class AppliedEntry
{
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("offsets")] public List<int> Offsets { get; set; } = new();
}

/// <summary>
/// Append and flush each CO group before writing the CPU.
/// A crash can damage only the final NDJSON line, which readers skip.
/// </summary>
internal static class Journal
{
    public static string FilePath => Path.Combine(Workspace.ProfilesDir, "applied_offsets.ndjson");

    /// <summary>Manual CO applied while idle takes ownership and skips recovery backoff.</summary>
    public const string ManualTakeover = "manual-takeover";

    /// <summary>Manual CO applied during a test still receives crash backoff.</summary>
    public const string ManualInTest = "manual-in-test";

    public static void Record(IReadOnlyList<int> offsets, string mode, string? phase, string reason)
    {
        try
        {
            var entry = new AppliedEntry
            {
                Ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Mode = mode,
                Phase = phase,
                Reason = reason,
                Offsets = new List<int>(offsets),
            };
            DurableIO.AppendLine(FilePath, JsonSerializer.Serialize(entry));
        }
        catch (Exception e)
        {
            Log.Write($"写入 CO 日志失败: {e.Message}", "WARN");
        }
    }

    /// <summary>Reads the last valid record, skipping a damaged tail line.</summary>
    public static AppliedEntry? ReadLastEntry()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string[] lines = File.ReadAllLines(FilePath);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<AppliedEntry>(line);
                    if (entry?.Offsets is { Count: > 0 }) return entry;
                }
                catch
                {
                    // Skip a damaged record and continue backward.
                }
            }
        }
        catch
        {
            // Treat a read failure as no history.
        }
        return null;
    }

    public static List<int>? ReadLastOffsets() => ReadLastEntry()?.Offsets;
}

/// <summary>Persists CO history before applying offsets to the CPU.</summary>
internal static class Tuning
{
    public static bool Apply(IReadOnlyList<int> offsets, string mode, string? phase, string reason)
    {
        Journal.Record(offsets, mode, phase, reason); // Persist offsets first.
        bool ok = RyzenSmu.SetOffsets(offsets); // Then apply and verify each core.
        if (!ok)
        {
            // A failed write may leave some cores changed; preserve the observed state for crash recovery.
            var observed = RyzenSmu.ReadOffsets(offsets.Count);
            Journal.Record(observed, mode, phase, "write-verification-failed");
            Log.WriteCo("CO 写入失败，当前读数（读取失败记为 0）：", observed, level: "WARN");
        }
        return ok;
    }
}
