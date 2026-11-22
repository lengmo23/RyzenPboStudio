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

/// <summary>Extracts inpoutx64.dll into a protected ProgramData directory and verifies SHA-256 before use.</summary>
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
                throw new TimeoutException(L.T("Timed out waiting to extract the test components"));

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
            ?? throw new InvalidDataException(L.T("The embedded tool bundle has no manifest"));
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<ToolBundleManifest>(stream)
            ?? throw new InvalidDataException(L.T("The embedded tool bundle manifest is invalid"));
    }

    private static void ValidateManifest(ToolBundleManifest manifest)
    {
        if (manifest.FormatVersion != 1
            || string.IsNullOrWhiteSpace(manifest.BundleVersion)
            || manifest.Files.Count == 0)
        {
            throw new InvalidDataException(L.T("Unsupported tool bundle manifest format"));
        }

        if (manifest.BundleVersion.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new InvalidDataException(L.T("The tool bundle version contains invalid characters"));

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)
                || file.Size < 0
                || file.Sha256.Length != 64
                || !paths.Add(file.Path))
            {
                throw new InvalidDataException(L.T("The tool bundle file list is invalid"));
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
                    ?? throw new InvalidDataException(L.F("The tool bundle is missing a file: {0}", file.Path));
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
                throw new InvalidDataException(L.T("The extracted test components failed the integrity check"));

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
                // Preserve the primary error instead of reporting temporary cleanup failure.
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
            ?? throw new FileNotFoundException(L.T("Test component resource not found in the program"));
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
            throw new InvalidDataException(L.T("The tool bundle contains a path outside its folder"));
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
