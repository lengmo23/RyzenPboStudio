using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RyzenPboStudio;

/// <summary>
/// Reads CPU and system details through registry and Win32 APIs without relying on removed wmic.
/// </summary>
internal static class SystemInfo
{
    /// <summary>Full CPU name, or "AMD Ryzen" on failure.</summary>
    public static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = (key?.GetValue("ProcessorNameString") as string ?? "").Trim();
            return name.Length > 0 ? name : "AMD Ryzen";
        }
        catch { return "AMD Ryzen"; }
    }

    /// <summary>Checks registry vendor fields for an Intel CPU.</summary>
    public static bool IsIntel()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var vendor = key?.GetValue("VendorIdentifier") as string ?? "";
            var name = key?.GetValue("ProcessorNameString") as string ?? "";
            var text = (vendor + " " + name).ToUpperInvariant();
            return text.Contains("INTEL") || text.Contains("GENUINEINTEL");
        }
        catch
        {
            // Do not block startup when vendor detection fails.
            return false;
        }
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Physical core count, falling back to half the logical count.</summary>
    public static int GetPhysicalCoreCount()
    {
        try
        {
            int count = CountPhysicalCores();
            if (count > 0) return count;
        }
        catch
        {
            // Use the fallback below when the native query fails.
        }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>Registry ~MHz written by HAL at boot; returns zero on failure.</summary>
    public static double GetBaseFrequencyMHz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz && mhz > 0) return mhz;
        }
        catch { }
        return 0;
    }

    /// <summary>Motherboard vendor and model from the BIOS registry key.</summary>
    public static string GetMotherboard()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var mfr = (key?.GetValue("BaseBoardManufacturer") as string ?? "").Trim();
            var prod = (key?.GetValue("BaseBoardProduct") as string ?? "").Trim();
            if (mfr.Length == 0 && prod.Length == 0)
            {
                mfr = (key?.GetValue("SystemManufacturer") as string ?? "").Trim();
                prod = (key?.GetValue("SystemProductName") as string ?? "").Trim();
            }
            var s = (mfr + " " + prod).Trim();
            return s.Length > 0 ? s : "—";
        }
        catch { return "—"; }
    }

    /// <summary>Prefers active display adapters over stale registry driver entries,
    /// then falls back to the display-class registry key.</summary>
    public static string GetGpuName()
    {
        return GetGpuNameFromDisplayDevices() ?? GetGpuNameFromRegistry() ?? "—";
    }

    /// <summary>Enumerates active display adapters, excluding removed devices.</summary>
    private static string? GetGpuNameFromDisplayDevices()
    {
        try
        {
            string? best = null;
            int bestScore = int.MinValue;
            for (uint i = 0; i < 64; i++)
            {
                var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;

                var desc = (dd.DeviceString ?? "").Trim();
                int score = ScoreAdapter(desc);
                if (score == int.MinValue) continue;
                // Prefer the entry driving the primary display when descriptions match.
                if ((dd.StateFlags & DisplayDevicePrimaryDevice) != 0) score += 2;
                else if ((dd.StateFlags & DisplayDeviceAttachedToDesktop) != 0) score += 1;

                if (score > bestScore)
                {
                    best = desc;
                    bestScore = score;
                }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>Uses display-class registry drivers only when active enumeration fails.</summary>
    private static string? GetGpuNameFromRegistry()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey == null) return null;

            string? best = null;
            int bestScore = int.MinValue;
            foreach (var sub in classKey.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue; // Adapter instance keys use four digits.
                using var dev = classKey.OpenSubKey(sub);
                var desc = (dev?.GetValue("DriverDesc") as string ?? "").Trim();
                int score = ScoreAdapter(desc);
                if (score == int.MinValue) continue;

                if (score > bestScore)
                {
                    best = desc;
                    bestScore = score;
                }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>Scores adapters to prefer discrete GPUs over integrated graphics.
    /// Excludes virtual and basic adapters with int.MinValue.</summary>
    private static int ScoreAdapter(string desc)
    {
        if (desc.Length == 0) return int.MinValue;
        var u = desc.ToUpperInvariant();
        if (u.Contains("BASIC") || u.Contains("REMOTE") || u.Contains("MIRROR") || u.Contains("VIRTUAL"))
            return int.MinValue; // Exclude basic, remote, and virtual adapters.

        int score = 0;
        if (u.Contains("GEFORCE") || u.Contains("NVIDIA") || u.Contains("RADEON RX")) score = 30;
        else if (u.Contains("INTEL(R) ARC") || u.Contains("INTEL ARC")) score = 25;
        else if (u.Contains("RADEON") || u.Contains("AMD") || u.Contains("INTEL")) score = 10;
        if (u.Contains("RADEON(TM) GRAPHICS") || u.Contains("UHD GRAPHICS") || u.Contains("IRIS")) score -= 5;
        return score;
    }

    // Active adapters from EnumDisplayDevices

    private const int DisplayDeviceAttachedToDesktop = 0x1;
    private const int DisplayDevicePrimaryDevice = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum,
        ref DisplayDevice lpDisplayDevice, uint dwFlags);

    /// <summary>BIOS version from the registry, or an empty string.</summary>
    public static string GetBiosVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (key?.GetValue("BIOSVersion") as string ?? "").Trim();
        }
        catch { return ""; }
    }

    /// <summary>SMBIOS Type 17 memory vendors, joined by "/" for mixed modules.</summary>
    public static string GetMemoryVendor()
    {
        try
        {
            var names = ZenStates.Core.SystemInfo.SMBios.MemoryDevices
                .Where(d => d.Size > 0)
                .Select(d => (d.ManufacturerName ?? "").Trim())
                .Where(n => n.Length > 0 && !n.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return string.Join(" / ", names);
        }
        catch { return ""; }
    }

    /// <summary>Reads display-driver version; converts NVIDIA’s Windows version
    /// to its public version format and otherwise returns the original value.</summary>
    public static string GetGpuDriverVersion(string gpuName)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey == null) return "";
            foreach (var sub in classKey.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;
                using var dev = classKey.OpenSubKey(sub);
                if (!string.Equals((dev?.GetValue("DriverDesc") as string ?? "").Trim(), gpuName, StringComparison.OrdinalIgnoreCase))
                    continue;
                string ver = (dev?.GetValue("DriverVersion") as string ?? "").Trim();
                var parts = ver.Split('.');
                if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && parts.Length == 4)
                {
                    string digits = parts[2] + parts[3];
                    if (digits.Length >= 5)
                    {
                        string last5 = digits[^5..];
                        return $"{last5[..3]}.{last5[3..]}";
                    }
                }
                return ver;
            }
        }
        catch { }
        return "";
    }

    /// <summary>Corrects Windows 11 registry ProductName using build 22000 or later.</summary>
    public static string GetOsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string name = (key?.GetValue("ProductName") as string ?? "").Trim();
            string display = (key?.GetValue("DisplayVersion") as string ?? "").Trim();
            if (int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out int build) && build >= 22000)
                name = name.Replace("Windows 10", "Windows 11");
            name = name.Replace("Windows ", "Win");
            return display.Length > 0 ? $"{name} ({display})" : name;
        }
        catch { return ""; }
    }

    /// <summary>Total physical memory reported by SMBIOS, or "—".</summary>
    public static string GetInstalledMemory()
    {
        try
        {
            if (!GetPhysicallyInstalledSystemMemory(out ulong totalKb) || totalKb == 0)
                return "—";
            double gib = totalKb / 1024d / 1024d;
            return $"{Math.Round(gib):F0} GB";
        }
        catch { return "—"; }
    }

    /// <summary>Total physical-memory bytes, or zero on failure.</summary>
    public static ulong TotalPhysicalBytes()
    {
        try
        {
            if (!GetPhysicallyInstalledSystemMemory(out ulong totalKb)) return 0;
            return totalKb * 1024UL;
        }
        catch { return 0; }
    }

    // Physical cores from GetLogicalProcessorInformation

    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemLogicalProcessorInformation
    {
        public UIntPtr ProcessorMask;
        public uint Relationship;
        public uint Padding; // Align the union at offset 16.
        // The union at offset 16 is CACHE_DESCRIPTOR only for RelationCache.
        // Other relationship types do not define these fields.
        public byte CacheLevel;
        public byte CacheAssociativity;
        public ushort CacheLineSize;
        public uint CacheSize;
        public uint CacheType;
        public uint Reserved1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    private static int CountPhysicalCores() => GetProcessorCoreMasks().Count;

    /// <summary>
    /// Maps logical to physical core IDs, including non-SMT topologies.
    /// Covers one processor group of up to 64 logical cores;
    /// returns null when topology is unavailable.
    /// </summary>
    public static int[]? GetLogicalToPhysicalMap()
    {
        try
        {
            var coreMasks = GetProcessorCoreMasks();
            if (coreMasks.Count == 0) return null;

            int maxLogical = -1;
            foreach (ulong mask in coreMasks)
                for (int bit = 0; bit < 64; bit++)
                    if ((mask & (1UL << bit)) != 0 && bit > maxLogical)
                        maxLogical = bit;
            if (maxLogical < 0) return null;

            var map = new int[maxLogical + 1];
            Array.Fill(map, -1);
            for (int phys = 0; phys < coreMasks.Count; phys++)
                for (int bit = 0; bit < 64; bit++)
                    if ((coreMasks[phys] & (1UL << bit)) != 0)
                        map[bit] = phys;
            return map;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Logical-processor masks in physical-core enumeration order.</summary>
    private static List<ulong> GetProcessorCoreMasks()
    {
        var masks = new List<ulong>();
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0) return masks;

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length))
                return masks;

            int size = Marshal.SizeOf<SystemLogicalProcessorInformation>();
            int n = (int)(length / size);
            for (int i = 0; i < n; i++)
            {
                var item = Marshal.PtrToStructure<SystemLogicalProcessorInformation>(buffer + i * size);
                if (item.Relationship == RelationProcessorCore)
                    masks.Add((ulong)item.ProcessorMask);
            }
            return masks;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Groups logical cores by shared L3, ordered by lowest logical ID.
    /// This handles fused-off cores without assuming a fixed cores-per-CCD count.
    /// Returns an empty list when topology is unavailable.
    /// </summary>
    public static List<ulong> GetCcdLogicalMasks()
    {
        var masks = new List<ulong>();
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        if (length == 0) return masks;

        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length))
                return masks;

            int size = Marshal.SizeOf<SystemLogicalProcessorInformation>();
            int n = (int)(length / size);
            for (int i = 0; i < n; i++)
            {
                var item = Marshal.PtrToStructure<SystemLogicalProcessorInformation>(buffer + i * size);
                if (item.Relationship == RelationCache && item.CacheLevel == 3)
                    masks.Add((ulong)item.ProcessorMask);
            }
            masks.Sort((a, b) => LowestBit(a).CompareTo(LowestBit(b)));
            return masks;
        }
        catch
        {
            return new List<ulong>();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int LowestBit(ulong mask)
    {
        for (int i = 0; i < 64; i++)
            if ((mask & (1UL << i)) != 0) return i;
        return int.MaxValue;
    }
}

/// <summary>Caches logical-to-physical mapping, falling back to c/2.</summary>
internal static class CoreTopology
{
    private static int[]? _map;
    private static bool _loaded;

    /// <summary>OS physical-core ID, distinct from the SMU slot ID.</summary>
    public static int PhysicalOf(int logical)
    {
        if (!_loaded)
        {
            _map = SystemInfo.GetLogicalToPhysicalMap();
            _loaded = true;
        }
        if (_map != null && logical >= 0 && logical < _map.Length && _map[logical] >= 0)
            return _map[logical];
        return logical / 2;
    }

    // CCD groups from shared L3
    private static List<ulong>? _ccdMasks;

    private static List<ulong> CcdMasks
    {
        get
        {
            _ccdMasks ??= SystemInfo.GetCcdLogicalMasks();
            return _ccdMasks;
        }
    }

    /// <summary>CCD count; defaults to one when L3 topology is unavailable.</summary>
    public static int CcdCount => Math.Max(1, CcdMasks.Count);

    /// <summary>Logical cores of one CCD, or an empty list when unavailable.</summary>
    public static List<int> LogicalCoresOfCcd(int ccd)
    {
        var result = new List<int>();
        if (ccd < 0 || ccd >= CcdMasks.Count) return result;
        ulong mask = CcdMasks[ccd];
        for (int bit = 0; bit < 64; bit++)
            if ((mask & (1UL << bit)) != 0) result.Add(bit);
        return result;
    }

    /// <summary>OS core IDs for one CCD; convert them to SMU slots before indexing CO.</summary>
    public static List<int> PhysicalCoresOfCcd(int ccd) =>
        LogicalCoresOfCcd(ccd).Select(PhysicalOf).Distinct().OrderBy(x => x).ToList();

    /// <summary>Logical siblings of an OS core, falling back to 2n and 2n+1.</summary>
    public static List<int> LogicalCoresOfPhysical(IEnumerable<int> physicalCores)
    {
        var want = new HashSet<int>(physicalCores);
        var result = new List<int>();
        if (!_loaded) { _map = SystemInfo.GetLogicalToPhysicalMap(); _loaded = true; }
        if (_map != null)
        {
            for (int lp = 0; lp < _map.Length; lp++)
                if (_map[lp] >= 0 && want.Contains(_map[lp])) result.Add(lp);
        }
        if (result.Count == 0)
            foreach (int ph in want.OrderBy(x => x)) { result.Add(ph * 2); result.Add(ph * 2 + 1); }
        return result;
    }
}
