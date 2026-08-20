using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

internal sealed class ToolBundleManifest
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("bundleVersion")] public string BundleVersion { get; set; } = "";
    [JsonPropertyName("files")] public List<ToolBundleFile> Files { get; set; } = new();
}

internal sealed class ToolBundleFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>
/// 把内嵌的第三方工具包释放到受保护的公共缓存目录，并在使用前校验每个文件。
/// 外部工具必须落盘后才能作为独立进程运行；分发物本身仍然只有一个 EXE。
/// </summary>
internal static class ToolBundle
{
    private const string ResourceName = "RyzenPboStudio.Resources.tool-bundle.zip";
    private const string ManifestEntryName = "bundle-manifest.json";
    private static readonly object Sync = new();
    private static string? _rootDirectory;

    public static string RootDirectory
    {
        get
        {
            lock (Sync)
                return _rootDirectory ??= EnsureReadyCore();
        }
    }

    public static void EnsureReady() => _ = RootDirectory;

    private static string EnsureReadyCore()
    {
        ToolBundleManifest manifest = ReadManifest();
        ValidateManifest(manifest);

        string cacheBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RyzenPboStudio",
            "Tools");

        Directory.CreateDirectory(cacheBase);
        HardenCacheDirectory(cacheBase);

        string target = GetSafeChildPath(cacheBase, manifest.BundleVersion);
        string mutexVersion = string.Concat(manifest.BundleVersion.Select(
            c => char.IsLetterOrDigit(c) ? c : '_'));
        using var mutex = new Mutex(false, $"Global\\RyzenPboStudio.ToolBundle.{mutexVersion}");
        bool ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromMinutes(3));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
                throw new TimeoutException("等待测试组件释放超时");

            if (!ValidateExtractedFiles(target, manifest))
                ExtractBundle(cacheBase, target, manifest);

            return target;
        }
        finally
        {
            if (ownsMutex)
                mutex.ReleaseMutex();
        }
    }

    private static ToolBundleManifest ReadManifest()
    {
        using var archive = OpenArchive();
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("内嵌工具包缺少清单");
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<ToolBundleManifest>(stream)
            ?? throw new InvalidDataException("内嵌工具包清单无效");
    }

    private static void ValidateManifest(ToolBundleManifest manifest)
    {
        if (manifest.FormatVersion != 1
            || string.IsNullOrWhiteSpace(manifest.BundleVersion)
            || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("内嵌工具包清单格式不受支持");
        }

        if (manifest.BundleVersion.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new InvalidDataException("内嵌工具包版本号包含非法字符");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)
                || file.Size < 0
                || file.Sha256.Length != 64
                || !paths.Add(file.Path))
            {
                throw new InvalidDataException("内嵌工具包文件清单无效");
            }
        }
    }

    private static void ExtractBundle(
        string cacheBase,
        string target,
        ToolBundleManifest manifest)
    {
        string temporary = GetSafeChildPath(
            cacheBase,
            $"{manifest.BundleVersion}.extracting-{Environment.ProcessId}-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(temporary);
            using var archive = OpenArchive();
            foreach (var file in manifest.Files)
            {
                var entry = archive.GetEntry(file.Path)
                    ?? throw new InvalidDataException($"工具包缺少文件: {file.Path}");
                string destination = GetSafeChildPath(temporary, file.Path);
                string? parent = Path.GetDirectoryName(destination);
                if (parent != null)
                    Directory.CreateDirectory(parent);

                using var input = entry.Open();
                using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.WriteThrough);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            if (!ValidateExtractedFiles(temporary, manifest))
                throw new InvalidDataException("释放后的测试组件未通过完整性校验");

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(temporary, target);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporary))
                    Directory.Delete(temporary, recursive: true);
            }
            catch
            {
                // 主错误优先，临时目录残留不额外报错
            }
        }
    }

    private static bool ValidateExtractedFiles(
        string root,
        ToolBundleManifest manifest)
    {
        if (!Directory.Exists(root))
            return false;

        foreach (var file in manifest.Files)
        {
            string path;
            try { path = GetSafeChildPath(root, file.Path); }
            catch { return false; }

            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size)
                return false;

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static ZipArchive OpenArchive()
    {
        Stream stream = typeof(ToolBundle).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("程序内未找到测试组件资源");
        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
    }

    private static string GetSafeChildPath(string root, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string child = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!child.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("工具包包含越界路径");
        return child;
    }

    private static void HardenCacheDirectory(string path)
    {
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));

        new DirectoryInfo(path).SetAccessControl(security);
    }
}
