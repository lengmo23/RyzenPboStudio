using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    /// <summary>所有下载源都失败时给出的网盘地址，由用户手动下载后覆盖安装。</summary>
    public const string PanUrl = "https://wwbnf.lanzoum.com/b01eupfd7c";

    /// <summary>网盘提取码。</summary>
    public const string PanCode = "csdf";

    /// <summary>下载源，按顺序尝试：空前缀是直连 GitHub，其余是把原始 URL 拼在自己域名后转发的
    /// 社区镜像。镜像随时可能失效或限速，故逐个试、全失败才报错。</summary>
    /// <summary>单个源的响应头等待上限：这么久拿不到响应头就判它不可用，直接换下一个。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>传输过程中的空闲上限：连续这么久收不到新数据才判失败。只要还在收数据就不会超时，
    /// 因此慢速网络下的大包不会被误杀。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(20);

    private static readonly (string Prefix, string Name)[] DownloadSources =
    {
        ("", "GitHub"),
        ("https://ghfast.top/", "ghfast.top"),
        ("https://gh-proxy.com/", "gh-proxy.com"),
        ("https://ghproxy.net/", "ghproxy.net"),
    };

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

        return new UpdateInfo(latest, release.TagName, PlainText(release.Body), zip.DownloadUrl, zip.Size);
    }

    /// <summary>
    /// 把 Release 正文的 Markdown 压成纯文字。更新提示是个 MessageBox，原样显示
    /// #、**、` 这些标记只会干扰阅读，这里只保留文字内容与分行。
    /// </summary>
    private static string PlainText(string markdown)
    {
        var lines = new List<string>();
        foreach (string raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("```")) continue;                        // 代码围栏
            if (line.Length > 0 && line.All(c => c == '-' || c == '='))
                continue;                                                // 分隔线 / setext 下划线
            line = line.TrimStart('#', '>').Trim();                      // 标题、引用前缀
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
                line = "· " + line[2..].Trim();                          // 列表符号统一
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");   // [文字](链接) → 文字
            lines.Add(line);
        }

        // 连续空行折叠成一个，避免 Markdown 的空行在纯文本下堆成大片空白
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

    // ── 「暂不更新」的版本记忆 ──────────────────────────────────────────────

    private static string SkipFile => Path.Combine(Workspace.ProfilesDir, "update_skip.txt");

    /// <summary>用户在提示里点了「否」：记下这个版本，之后启动不再弹窗。</summary>
    public static void SkipVersion(Version version)
    {
        try { File.WriteAllText(SkipFile, version.ToString()); } catch { /* 记不下最多是下次再弹一次 */ }
    }

    /// <summary>该版本是否已被用户跳过。只用于启动时的静默检查，手动「检查更新」不受影响。</summary>
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

    /// <summary>已经是最新版本时清掉记录，避免旧版本号一直留在 profiles 里。</summary>
    public static void ClearSkip()
    {
        try { if (File.Exists(SkipFile)) File.Delete(SkipFile); } catch { }
    }

    /// <summary>把 "v2.1.0" / "2.1.0" 解析成 Version。</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string t = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out Version? parsed) && (version = parsed) != null;
    }

    /// <summary>下载 zip 到临时目录，直连失败则依次改用镜像。progress 回调传 0-100，
    /// onSource 在每次换源时告知源名，onStage 推送「正在连接 / 正在换源」等阶段文字，
    /// 让界面在还没有进度可报的等待期间也有反馈。</summary>
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
                throw;   // 仅用户主动取消走这里；超时由 FetchAsync 转成 TimeoutException，照常换源
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

    /// <summary>从单个 URL 下载并核对大小。大小取自 GitHub API 报告的资产尺寸，
    /// 对不上就当这一源失败——镜像失效时常返回 HTML 错误页，只看状态码分辨不出来。</summary>
    private static async Task FetchAsync(string url, string zipPath, long expectedSize,
        IProgress<int>? progress, Action<string>? onStage, string sourceName, CancellationToken token)
    {
        // 显式指明走系统代理：国内多数用户靠 clash 一类工具设置系统代理访问 GitHub。
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
            UseDefaultCredentials = true,
        };
        // 不用 HttpClient.Timeout：它限制的是整个请求的总时长，慢速网络下的大包会被误杀。
        // 改由下面两个链接 CTS 分别看住「连不上」与「连上后不再来数据」。
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
                idleCts.CancelAfter(IdleTimeout);   // 收到数据就把空闲计时推后
                await dst.WriteAsync(buffer.AsMemory(0, read), token);
                done += read;
                if (total > 0) progress?.Report((int)(done * 100 / total));
            }
        }

        long actual = new FileInfo(zipPath).Length;
        if (expectedSize > 0 && actual != expectedSize)
            throw new IOException($"文件大小不符，应为 {expectedSize} 字节、实际 {actual} 字节");
    }

    /// <summary>把下载失败的原因压成状态条放得下的短语。</summary>
    private static string ShortReason(Exception e) => e switch
    {
        TimeoutException => e.Message,
        HttpRequestException { StatusCode: { } code } => $"返回 {(int)code}",
        HttpRequestException => "连接失败",
        IOException => "内容不完整",
        _ => "失败",
    };

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
