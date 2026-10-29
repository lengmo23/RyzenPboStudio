using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
    // "sha256:<hex>", computed by GitHub on upload; absent on assets uploaded before GitHub added it.
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

/// <summary>One change from the Release notes: a headline and its one-line explanation.</summary>
internal sealed record ReleaseNote(string Title, string Detail);

/// <summary>Available update metadata; Sha256 is null when the Release does not publish a digest.</summary>
internal sealed record UpdateInfo(Version Version, string Tag, IReadOnlyList<ReleaseNote> Notes, string DownloadUrl, long Size,
    string? Sha256);

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

    /// <summary>QQ group where users get the latest version when the in-app update fails.</summary>
    public const string QqGroup = "1132124359";

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

    // Asset-name marker of the self-contained package; it must never contain "update", "lite" or "full", which installs
    // without bundled .NET (including older versions) select by.
    private const string SelfContainedTag = "net8contained";

    /// <summary>True when .NET 8 is bundled next to the executable rather than installed on the system.</summary>
    private static bool IsSelfContained => File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

    /// <summary>Returns the latest update, or null when absent or unavailable. currentNotes holds the Release notes of
    /// the running version when it is the latest release, otherwise null.</summary>
    public static async Task<(UpdateInfo? update, IReadOnlyList<ReleaseNote>? currentNotes)> CheckAsync(
        CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API requires User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RyzenPboStudio/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        GhRelease? release = await http.GetFromJsonAsync<GhRelease>(LatestReleaseApi, token);
        if (release == null || release.Draft || release.Prerelease) return (null, null);

        if (!TryParseTag(release.TagName, out Version latest)) return (null, null);
        if (latest == CurrentVersion) return (null, ParseNotes(release.Body));
        if (latest < CurrentVersion) return (null, null);

        var zips = release.Assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.DownloadUrl.Length > 0)
            .ToList();
        GhAsset? zip;
        if (IsSelfContained)
        {
            // update.zip relies on an installed .NET 8, so a bundled-runtime install takes the self-contained package.
            zip = zips.FirstOrDefault(a => a.Name.Contains(SelfContainedTag, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"{release.TagName} 没有内置 .NET 8 的安装包，请到项目主页手动下载。");
        }
        else
        {
            // Prefer the smaller update archive; use lite (named full before 3.0.3) only when update is absent.
            var plain = zips.Where(a => !a.Name.Contains(SelfContainedTag, StringComparison.OrdinalIgnoreCase)).ToList();
            zip = plain.FirstOrDefault(a => a.Name.Contains("update", StringComparison.OrdinalIgnoreCase))
                  ?? plain.FirstOrDefault(a => a.Name.Contains("lite", StringComparison.OrdinalIgnoreCase)
                                            || a.Name.Contains("full", StringComparison.OrdinalIgnoreCase))
                  ?? plain.FirstOrDefault();
        }
        if (zip == null) return (null, null);

        return (new UpdateInfo(latest, release.TagName, ParseNotes(release.Body), zip.DownloadUrl, zip.Size,
            ParseSha256(zip.Digest)), null);
    }

    /// <summary>Hex SHA-256 from a GitHub asset digest such as "sha256:ab12…"; null for other or missing digests.</summary>
    private static string? ParseSha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest == null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string hex = digest[prefix.Length..].Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    // Release section that explains which archive to download by hand; meaningless inside the app.
    private const string DownloadGuideHeading = "下载哪个";

    /// <summary>Splits Release Markdown into one note per heading, dropping the download guide.
    /// Text before the first heading becomes a note without a title.</summary>
    internal static List<ReleaseNote> ParseNotes(string markdown)
    {
        var notes = new List<ReleaseNote>();
        string title = "";
        var detail = new List<string>();

        void Flush()
        {
            if (title != DownloadGuideHeading && (title.Length > 0 || detail.Count > 0))
                notes.Add(new ReleaseNote(title, string.Join("\n", detail)));
            detail.Clear();
        }

        foreach (string raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```")) continue;
            if (line.All(c => c == '-' || c == '=')) continue;
            if (line.StartsWith('#'))
            {
                Flush();
                title = CleanMarkdown(line.TrimStart('#'));
                continue;
            }
            line = line.TrimStart('>').Trim();
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
                line = "· " + line[2..].Trim();
            detail.Add(CleanMarkdown(line));
        }
        Flush();
        return notes;
    }

    private static string CleanMarkdown(string text)
    {
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        return Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1").Trim();
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
                    zipPath, info.Size, info.Sha256, progress, onStage, name, token);
                Log.Write(info.Sha256 != null ? $"更新包下载完成（{name}，SHA-256 校验通过）" : $"更新包下载完成（{name}）");
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

    /// <summary>Checks the download against GitHub metadata: SHA-256 when the Release publishes a digest, otherwise
    /// only the size. The digest comes from the GitHub API, so a mirror cannot pass off a different file.</summary>
    private static async Task FetchAsync(string url, string zipPath, long expectedSize, string? expectedSha256,
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
        if (expectedSha256 != null)
        {
            string hash;
            using (var fs = File.OpenRead(zipPath)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, token));
            if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 不符，应为 {expectedSha256.ToLowerInvariant()}、实际 {hash.ToLowerInvariant()}");
        }
    }

    /// <summary>Shortens download errors for the status bar.</summary>
    private static string ShortReason(Exception e) => e switch
    {
        TimeoutException => e.Message,
        HttpRequestException { StatusCode: { } code } => $"返回 {(int)code}",
        HttpRequestException => "连接失败",
        IOException => "内容不完整",
        InvalidDataException => "校验不符",
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
    /// The script backs up the installed files, copies the new version over them and restarts the application.
    /// robocopy replaces file by file, so a failure midway would leave a mix of old and new files; in that case the
    /// backup is copied back and the old version restarted. logs and profiles are never touched.
    /// </summary>
    public static void LaunchReplacerAndExit(string newVersionRoot)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, "AMD Ryzen PBO Studio.exe");
        string workDir = Path.GetDirectoryName(newVersionRoot)!;
        string backupDir = Path.Combine(workDir, "backup");
        string script = Path.Combine(workDir, "apply-update.cmd");
        // An update package without tools leaves y-cruncher alone, so there is no need to back it up.
        string excludeDirs = Directory.Exists(Path.Combine(newVersionRoot, "tools")) ? "logs profiles" : "logs profiles tools";

        // /E includes empty directories; /IS /IT overwrite files robocopy would treat as unchanged; /XD excludes runtime
        // data; /R and /W limit retries; exit code 8+ is a failure.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("echo 正在等待 AMD Ryzen PBO Studio 退出...");
        sb.AppendLine(":waitloop");
        sb.AppendLine($"tasklist /FI \"PID eq {Environment.ProcessId}\" 2>nul | find \"{Environment.ProcessId}\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto waitloop");
        sb.AppendLine(")");
        sb.AppendLine("echo 正在备份当前版本...");
        sb.AppendLine($"rd /s /q \"{backupDir}\" 2>nul");   // a leftover from an earlier failed update
        sb.AppendLine($"robocopy \"{installDir}\" \"{backupDir}\" /E /XD {excludeDirs} /R:2 /W:1 >nul");
        sb.AppendLine("if errorlevel 8 (");
        sb.AppendLine("  echo 备份当前版本失败，已取消更新，原有版本未做任何改动。");
        sb.AppendLine($"  start \"\" \"{exePath}\"");
        sb.AppendLine("  goto failed");
        sb.AppendLine(")");
        sb.AppendLine("echo 正在覆盖安装目录...");
        sb.AppendLine($"robocopy \"{newVersionRoot}\" \"{installDir}\" /E /IS /IT /XD logs profiles /R:5 /W:2 >nul");
        sb.AppendLine("if errorlevel 8 (");
        sb.AppendLine("  echo 覆盖安装失败，正在还原原有版本...");
        sb.AppendLine($"  robocopy \"{backupDir}\" \"{installDir}\" /E /IS /IT /XD logs profiles /R:5 /W:2 >nul");
        sb.AppendLine("  if errorlevel 8 (");
        // Quoted: a ")" in an unquoted path, e.g. "Program Files (x86)", would end the if block early.
        sb.AppendLine($"    echo 还原失败。备份保存在 \"{backupDir}\"，请手动复制回 \"{installDir}\"。");
        sb.AppendLine("    goto failed");
        sb.AppendLine("  )");
        sb.AppendLine("  echo 已还原原有版本。");
        sb.AppendLine($"  start \"\" \"{exePath}\"");
        sb.AppendLine("  goto failed");
        sb.AppendLine(")");
        sb.AppendLine($"start \"\" \"{exePath}\"");
        sb.AppendLine("cd /d \"%TEMP%\"");
        sb.AppendLine($"rd /s /q \"{workDir}\" 2>nul");
        sb.AppendLine("exit /b 0");
        sb.AppendLine(":failed");
        sb.AppendLine($"echo 如需获取最新版本，请加入 QQ 群 {QqGroup}。按任意键关闭。");
        sb.AppendLine("pause >nul");
        sb.AppendLine("exit /b 1");

        // No BOM: cmd would read it as part of the first command.
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = script,
            WorkingDirectory = workDir,
            UseShellExecute = true,
            CreateNoWindow = false, // Keep the console visible if replacement fails.
        });
    }
}
