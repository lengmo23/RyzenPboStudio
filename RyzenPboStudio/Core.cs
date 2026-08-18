using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>用户可调参数（对应原 Python 文件顶部的常量）。</summary>
internal static class Config
{
    public const int DefaultDuration = 120; // 每轮测试时间（秒）
    public const int DefaultVt3 = 20;       // VT3 测试轮数
    public const int DefaultBkt = 10;       // BKT 测试轮数
    public const int DefaultSvt = 10;       // SVT 测试轮数
    public const int StepOnError = 2;       // 每次报错后负压增加的步长
}

/// <summary>日志：仅输出到界面（事件）。不再自动落盘，需用户在界面点「保存日志」手动导出。</summary>
internal static class Log
{
    public static event Action<string>? OnLine;

    public static void Write(string msg, string level = "INFO")
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}";
        OnLine?.Invoke(line);
    }
}

/// <summary>持久化的测试状态（JSON 字段名与原 Python 版保持一致，可互读）。</summary>
internal sealed class TestState
{
    [JsonPropertyName("offsets")] public List<int> Offsets { get; set; } = new();
    [JsonPropertyName("test_round")] public int TestRound { get; set; }
    [JsonPropertyName("test_mode")] public string TestMode { get; set; } = "VT3";
    [JsonPropertyName("seq_phase")] public string? SeqPhase { get; set; }
    [JsonPropertyName("duration_seconds")] public int? DurationSeconds { get; set; }
    [JsonPropertyName("iterations_map")] public Dictionary<string, int>? IterationsMap { get; set; }
    // 测试范围：ALL=全部核心，CCD=单个 CCD，EACH=逐 CCD 依次，CUSTOM=自定义核心。
    // EACH 时 ScopeCcd 记录断电前跑到第几个 CCD，恢复后从那个 CCD 继续。
    [JsonPropertyName("test_scope")] public string? TestScope { get; set; }
    [JsonPropertyName("scope_ccd")] public int? ScopeCcd { get; set; }
    [JsonPropertyName("scope_cores")] public List<int>? ScopeCores { get; set; }
    [JsonPropertyName("timestamp")] public double Timestamp { get; set; }
}

/// <summary>统一的子进程调用：捕获 stdout+stderr，带超时。</summary>
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
        p.WaitForExit(); // 确保异步读取回调全部冲刷

        return (p.HasExited ? p.ExitCode : -1, sb.ToString());
    }
}

/// <summary>工作目录、路径、状态文件、关机标记等。</summary>
internal static class Workspace
{
    /// <summary>EXE 所在目录（外部工具基于这里查找）。运行时产物不再散落在此，见 LogsDir / ProfilesDir。</summary>
    public static readonly string BaseDir =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>日志目录：y-cruncher 输出、手动导出的运行日志，均按日期时间命名。</summary>
    public static string LogsDir => Path.Combine(BaseDir, "logs");

    /// <summary>配置与状态目录：负压历史、恢复状态、CO/CS 配置、校准数据。</summary>
    public static string ProfilesDir => Path.Combine(BaseDir, "profiles");

    public static string StateFile => Path.Combine(ProfilesDir, "undervolt_state.json");
    public static string InProgressFlag => Path.Combine(ProfilesDir, "test_in_progress");
    public static string FinalOffsets => Path.Combine(ProfilesDir, "final_offsets.txt");

    /// <summary>新建一条带时间戳的日志路径：logs\{prefix}_yyyyMMdd_HHmmss.{ext}。</summary>
    public static string NewLogPath(string prefix, string ext) =>
        Path.Combine(LogsDir, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");

    /// <summary>旧版本把运行时文件直接写在 EXE 同目录。</summary>
    private static readonly string[] LegacyProfileFiles =
    {
        "undervolt_state.json", "test_in_progress", "final_offsets.txt",
        "applied_offsets.ndjson", "cs_state.json", "co_profile.json", "tel_calib.json",
    };

    private static readonly string[] LegacyLogFiles = { "y-cruncher.log", "undervolt_log.txt" };

    /// <summary>
    /// 建立 logs\ 与 profiles\ 目录，并把旧版本遗留在 EXE 同目录的运行时文件迁移进来。
    /// 必须在读取脏标记与负压历史之前调用：否则从旧版本升级后会读不到上次崩溃的恢复点，
    /// 断电恢复会静默失效。
    /// </summary>
    public static void EnsureLayout()
    {
        try { Directory.CreateDirectory(LogsDir); } catch { /* 写入时会再报错 */ }
        try { Directory.CreateDirectory(ProfilesDir); } catch { /* 同上 */ }

        foreach (string name in LegacyProfileFiles)
            MoveLegacy(Path.Combine(BaseDir, name), Path.Combine(ProfilesDir, name));
        foreach (string name in LegacyLogFiles)
            MoveLegacy(Path.Combine(BaseDir, name), Path.Combine(LogsDir, name));
    }

    /// <summary>迁移单个旧文件。目标已存在说明新位置的数据更新，保留新的、不覆盖。</summary>
    private static void MoveLegacy(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to))
                File.Move(from, to);
        }
        catch
        {
            // 迁移失败不阻断启动；旧文件留在原处，最多是这一次读不到历史
        }
    }

    public static void ClearState()
    {
        try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { }
    }

    /// <summary>标记「测试进行中」（脏标记）。正常结束/退出时清除；下次启动若仍在 = 上次异常中断。</summary>
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
            DurableIO.WriteAllText(StateFile, json);   // 强制落盘 + 原子替换
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

    /// <summary>查询事件日志最近是否有内核电源事件(41)/Bug Check(1001)，仅用于丰富恢复日志，不作为判定依据。</summary>
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
            // 查询失败按无事件处理
        }
        return false;
    }
}

/// <summary>带强制落盘的写入：先写临时文件并 Flush 到物理磁盘，再原子替换；追加同样落盘。</summary>
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
            fs.Flush(flushToDisk: true);   // 真正写到盘，不只是系统缓存
        }
        File.Move(tmp, path, overwrite: true); // 同卷原子替换，断电不会留下半截文件
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

/// <summary>负压应用日志的一条记录。</summary>
internal sealed class AppliedEntry
{
    [JsonPropertyName("ts")] public string Ts { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("offsets")] public List<int> Offsets { get; set; } = new();
}

/// <summary>
/// 负压应用历史日志（NDJSON，每行一组）。每次下发负压前先在这里强制落盘，
/// 因此即便随后立刻死机断电，崩溃前的负压也已在磁盘上。追加式天然抗崩溃：
/// 断电最多损坏最后一行，读取时自动跳过。
/// </summary>
internal static class Journal
{
    public static string FilePath => Path.Combine(Workspace.ProfilesDir, "applied_offsets.ndjson");

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
            Log.Write($"写入负压日志失败: {e.Message}", "WARN");
        }
    }

    /// <summary>读取最后一条有效记录的负压（跳过断电导致损坏的尾行）。</summary>
    public static List<int>? ReadLastOffsets()
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
                    if (entry?.Offsets is { Count: > 0 }) return entry.Offsets;
                }
                catch
                {
                    // 损坏行，继续往前找
                }
            }
        }
        catch
        {
            // 读失败按无记录处理
        }
        return null;
    }
}

/// <summary>下发负压的唯一入口：先落盘日志，再写入 CPU（persist-before-apply）。</summary>
internal static class Tuning
{
    public static bool Apply(IReadOnlyList<int> offsets, string mode, string? phase, string reason)
    {
        Journal.Record(offsets, mode, phase, reason); // ① 先强制落盘
        return RyzenSmu.SetOffsets(offsets);          // ② 再下发到 CPU
    }
}
