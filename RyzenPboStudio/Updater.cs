using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>GitHub Release 返回体中本程序用得到的字段。</summary>
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

/// <summary>检测到的新版本。</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size);

/// <summary>
/// 从 GitHub Release 检查并应用更新。
///
/// 更新分两段执行：下载与解压全部在程序还活着时完成，任何失败都能当场报给用户并放弃；
/// 只有「用新文件覆盖安装目录」这一步必须等程序退出后由外部脚本做——Windows 不允许
/// 覆盖正在运行的 exe/dll。覆盖时排除 logs 与 profiles：profiles 里存着断电恢复要用的
/// 负压历史，更新绝不能把它抹掉。
/// </summary>
internal static class Updater
{
    private const string Owner = "lengmo23";
    private const string Repo = "RyzenPboStudio";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>项目主页。</summary>
    public const string HomePage = $"https://github.com/{Owner}/{Repo}";

    /// <summary>发布页地址，供「查看更新内容」等场景打开。</summary>
    public const string ReleasesPage = $"{HomePage}/releases";

    public static Version CurrentVersion =>
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, v.Build) : new Version(0, 0, 0);

    /// <summary>查询最新 Release。无更新或查询失败返回 null（由调用方决定是否提示）。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub API 强制要求 User-Agent，缺失会被 403 拒绝
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RyzenPboStudio/{CurrentVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        GhRelease? release = await http.GetFromJsonAsync<GhRelease>(LatestReleaseApi, token);
        if (release == null || release.Draft || release.Prerelease) return null;

        if (!TryParseTag(release.TagName, out Version latest)) return null;
        if (latest <= CurrentVersion) return null;

        // 发布同时提供 full（含 y-cruncher，供新用户下载）与 update（仅主程序，约 3MB）两个包。
        // 自动更新优先取 update：y-cruncher 极少变动，没必要每次更新都重下 46MB。
        // 若某次发布只传了 full，则回退到它，更新照样可用。
        var zips = release.Assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.DownloadUrl.Length > 0)
            .ToList();
        GhAsset? zip = zips.FirstOrDefault(a => a.Name.Contains("update", StringComparison.OrdinalIgnoreCase))
                       ?? zips.FirstOrDefault(a => a.Name.Contains("full", StringComparison.OrdinalIgnoreCase))
                       ?? zips.FirstOrDefault();
        if (zip == null) return null;

        string title = release.Name.Length > 0 ? release.Name : release.TagName;
        return new UpdateInfo(latest, release.TagName, $"{title}\n\n{release.Body}".Trim(), zip.DownloadUrl, zip.Size);
    }

    /// <summary>把 "v2.1.0" / "2.1.0" 解析成 Version。</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out Version? parsed) && (version = parsed) != null;
    }

    /// <summary>下载 zip 到临时目录。progress 回调传 0-100。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken token = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "RyzenPboStudioUpdate");
        Directory.CreateDirectory(dir);
        string zipPath = Path.Combine(dir, $"update-{info.Tag}.zip");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"RyzenPboStudio/{CurrentVersion}");

        using (HttpResponseMessage resp = await http.GetAsync(
                   info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;

            using Stream src = await resp.Content.ReadAsStreamAsync(token);
            using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            byte[] buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, token)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        return zipPath;
    }

    /// <summary>
    /// 解压到临时目录并定位新版本的根目录（发布包顶层是一个版本命名的文件夹）。
    /// 校验其中确有主程序，避免把损坏或结构不符的包拿去覆盖安装目录。
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
            // 顶层若是单个文件夹（发布包的常见形态），下沉一层再找
            string[] subDirs = Directory.GetDirectories(root);
            string? match = subDirs.FirstOrDefault(d => File.Exists(Path.Combine(d, exeName)));
            if (match == null)
                throw new InvalidDataException($"更新包结构不符：其中找不到 {exeName}。");
            root = match;
        }
        return root;
    }

    /// <summary>
    /// 写出并启动替换脚本，然后由调用方退出程序。脚本等待本进程结束后用新文件覆盖安装目录，
    /// 再重新启动程序。logs 与 profiles 被排除在覆盖之外——profiles 里是断电恢复所需的负压历史。
    /// </summary>
    public static void LaunchReplacerAndExit(string newVersionRoot)
    {
        string installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exePath = Environment.ProcessPath ?? Path.Combine(installDir, "AMD Ryzen PBO Studio.exe");
        string workDir = Path.GetDirectoryName(newVersionRoot)!;
        string script = Path.Combine(workDir, "apply-update.cmd");

        // /E 含空目录，/XD 排除运行时数据目录，/R /W 缩短重试以免卡住
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
            CreateNoWindow = false,   // 保留窗口，覆盖失败时用户能看到提示
        });
    }
}
