using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RyzenPboStudio;

/// <summary>GitHub Release fields used by this application.</summary>
internal sealed class GhRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public List<GhAsset> Assets { get; set; } = new();
}

internal sealed class GhAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
}

/// <summary>Available update metadata.</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size);

/// <summary>
/// Checks GitHub Releases and applies updates.
///
/// Download and extraction finish while the application can report failures.
/// File replacement waits until exit because Windows locks running binaries.
/// Exclude logs and profiles from replacement to preserve recovery history.
/// </summary>
internal static class Updater
{
    private const string Owner = "lengmo23";
    private const string Repo = "RyzenPboStudio";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>Project homepage.</summary>
    public const string HomePage = $"https://github.com/{Owner}/{Repo}";

    /// <summary>Release page opened from update details.</summary>
    public const string ReleasesPage = $"{HomePage}/releases";

    /// <summary>Manual download URL shown when all sources fail.</summary>
    public const string PanUrl = "https://wwbnf.lanzoum.com/b01eupfd7c";

    /// <summary>Extraction code for the manual download.</summary>
    public const string PanCode = "csdf";

    /// <summary>Response-header timeout for one source.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Idle-transfer timeout; slow but active downloads continue.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Try GitHub directly before mirror sources.</summary>
    private static readonly (string Prefix, string Name)[] DownloadSources =
    {
        ("", "GitHub"),
        ("https://ghfast.top/", "ghfast.top"),
        ("https://gh-proxy.com/", "gh-proxy.com"),
        ("https://ghproxy.net/", "ghproxy.net"),
    };

    public static Version CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>Returns the latest update, or null when absent or unavailable.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API requires User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RyzenPboStudio/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        GhRelease? release = await http.GetFromJsonAsync<GhRelease>(LatestReleaseApi, token);
        if (release == null || release.Draft || release.Prerelease) return null;

        if (!TryParseTag(release.TagName, out Version latest)) return null;
        if (latest <= CurrentVersion) return null;

        // Prefer the smaller update archive; use full only when update is absent.
        var zips = release.Assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.DownloadUrl.Length > 0)
            .ToList();
        GhAsset? zip = zips.FirstOrDefault(a => a.Name.Contains("update", StringComparison.OrdinalIgnoreCase))
                       ?? zips.FirstOrDefault(a => a.Name.Contains("full", StringComparison.OrdinalIgnoreCase))
                       ?? zips.FirstOrDefault();
        if (zip == null) return null;

        return new UpdateInfo(latest, release.TagName, PlainText(release.Body), zip.DownloadUrl, zip.Size);
    }

    /// <summary>Converts Release Markdown to plain text for the update dialog.</summary>
    private static string PlainText(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("```")) continue;
            if (line.Length > 0 && line.All(c => c == '-' || c == '='))
                continue;
            line = line.TrimStart('#', '>').Trim();
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
                line = "· " + line[2..].Trim();
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
            lines.Add(line);
        }

        // Collapse repeated blank lines.
        var sb = new StringBuilder();
        bool blank = false;
        foreach (string line in lines)
        {
            if (line.Length == 0) { blank = true; continue; }
            if (blank && sb.Length > 0) sb.Append('\n');
            sb.Append(line).Append('\n');
            blank = false;
        }
        return sb.ToString().Trim();
    }

    // Remember skipped updates

    private static string SkipFile => Path.Combine(Workspace.ProfilesDir, "update_skip.txt");

    /// <summary>Records a declined version so startup does not prompt for it again.</summary>
    public static void SkipVersion(Version version)
    {
        try { File.WriteAllText(SkipFile, version.ToString()); } catch { /* A failed save only repeats the prompt next time. */ }
    }

    /// <summary>Checks skipped versions only during silent startup checks.</summary>
    public static bool IsSkipped(Version version)
    {
        try
        {
            return File.Exists(SkipFile)
                && Version.TryParse(File.ReadAllText(SkipFile).Trim(), out Version? skipped)
                && skipped == version;
        }
        catch { return false; }
    }

    /// <summary>Clears an obsolete skipped-version record when already current.</summary>
    public static void ClearSkip()
    {
        try { if (File.Exists(SkipFile)) File.Delete(SkipFile); } catch { }
    }

    /// <summary>Parses tags with or without a leading v.</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out Version? parsed) && (version = parsed) != null;
    }

    /// <summary>Downloads an archive through GitHub or mirrors and reports percent
    /// progress, active source, and connection stage.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress,
        Action<string>? onSource = null, Action<string>? onStage = null, CancellationToken token = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RyzenPboStudioUpdate");
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, $"update-{info.Tag}.zip");

        var errors = new List<string>();
        foreach ((string prefix, string name) in DownloadSources)
        {
            token.ThrowIfCancellationRequested();
            onSource?.Invoke(name);
            onStage?.Invoke($"正在连接 {name}…");
            try
            {
                await FetchAsync(prefix.Length == 0 ? info.DownloadUrl : prefix + info.DownloadUrl,
                    zipPath, info.Size, progress, onStage, name, token);
                Log.Write($"更新包下载完成（{name}）");
                return zipPath;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw; // Only user cancellation escapes; source timeouts trigger failover.
            }
            catch (Exception e)
            {
                Log.Write($"从 {name} 下载更新包失败：{e.Message}", "WARN");
                errors.Add($"{name}：{e.Message}");
                onStage?.Invoke($"{name} {ShortReason(e)}，正在换源…");
            }
        }

        throw new IOException("所有下载源均失败。\n" + string.Join("\n", errors));
    }

    /// <summary>Checks downloaded size against GitHub metadata to reject mirror error pages.</summary>
    private static async Task FetchAsync(string url, string zipPath, long expectedSize,
        IProgress<int>? progress, Action<string>? onStage, string sourceName, CancellationToken token)
    {
        // Use the system proxy for GitHub.
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
            UseDefaultCredentials = true,
        };
        // Separate connection and idle-transfer timeouts avoid a total download deadline.
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RyzenPboStudio/{CurrentVersion}");

        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        headCts.CancelAfter(ConnectTimeout);
        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"{ConnectTimeout.TotalSeconds:0} 秒无响应");
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? expectedSize;
            onStage?.Invoke($"正在从 {sourceName} 下载…");

            using Stream src = await resp.Content.ReadAsStreamAsync(token);
            using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long done = 0;
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            idleCts.CancelAfter(IdleTimeout);
            while (true)
            {
                int read;
                try
                {
                    read = await src.ReadAsync(buffer, idleCts.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException($"传输停滞 {IdleTimeout.TotalSeconds:0} 秒");
                }
                if (read <= 0) break;
                idleCts.CancelAfter(IdleTimeout); // Reset idle timeout after each chunk.
                await dst.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        long actual = new FileInfo(zipPath).Length;
        if (expectedSize > 0 && actual != expectedSize)
            throw new IOException($"文件大小不符，应为 {expectedSize} 字节、实际 {actual} 字节");
    }

    /// <summary>Shortens download errors for the status bar.</summary>
    private static string ShortReason(Exception e) => e switch
    {
        TimeoutException => e.Message,
        HttpRequestException { StatusCode: { } code } => $"返回 {(int)code}",
        HttpRequestException => "连接失败",
        IOException => "内容不完整",
        _ => "失败",
    };

    /// <summary>
    /// Extracts the update and finds its root by locating the main executable.
    /// Rejects packages without the executable before replacing installed files.
    /// </summary>
    public static string ExtractAndVerify(string zipPath)
    {
        string extractDir = Path.Combine(Path.GetDirectoryName(zipPath)!, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        ZipFile.ExtractToDirectory(zipPath, extractDir);

        string exeName = Path.GetFileName(Environment.ProcessPath ?? "AMD Ryzen PBO Studio.exe");
        string root = extractDir;
        if (!File.Exists(Path.Combine(root, exeName)))
        {
            // Release archives usually place the executable one directory below the archive root.
            string[] subDirs = Directory.GetDirectories(root);
            string? match = subDirs.FirstOrDefault(d => File.Exists(Path.Combine(d, exeName)));
            if (match == null)
                throw new InvalidDataException($"更新包结构不符：其中找不到 {exeName}。");
            root = match;
        }
        return root;
    }

    /// <summary>
    /// Writes a replacement script and launches it after download; caller then exits.
    /// The script preserves logs and profiles and restarts the application.
    /// </summary>
    public static void LaunchReplacerAndExit(string newVersionRoot)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, "AMD Ryzen PBO Studio.exe");
        string workDir = Path.GetDirectoryName(newVersionRoot)!;
        string script = Path.Combine(workDir, "apply-update.cmd");

        // /E includes empty directories; /XD excludes runtime data; /R and /W limit retries.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine($"echo 正在等待 AMD Ryzen PBO Studio 退出...");
        sb.AppendLine($":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {Environment.ProcessId}\" 2>nul | find \"{Environment.ProcessId}\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine("echo 正在覆盖安装目录...");
        sb.AppendLine($"robocopy \"{newVersionRoot}\" \"{installDir}\" /E /XD logs profiles /R:5 /W:2 >nul");
        sb.AppendLine("if errorlevel 8 (");
        sb.AppendLine("  echo 更新失败，原有版本未被破坏。按任意键退出。");
        sb.AppendLine("  pause >nul");
        sb.AppendLine("  exit /b 1");
        sb.AppendLine(")");
        sb.AppendLine($"start \"\" \"{exePath}\"");
        sb.AppendLine($"cd /d \"%TEMP%\"");
        sb.AppendLine($"rd /s /q \"{workDir}\" 2>nul");

        File.WriteAllText(script, sb.ToString(), Encoding.UTF8);

        Process.Start(new ProcessStartInfo
        {
            FileName = script,
            WorkingDirectory = workDir,
            UseShellExecute = true,
            CreateNoWindow = false, // Keep the console visible if replacement fails.
        });
    }
}
