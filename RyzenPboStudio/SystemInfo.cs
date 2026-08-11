using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RyzenPboStudio;

/// <summary>
/// CPU / 权限相关查询。原 Python 版用 wmic 取 CPU 名称和核心数，
/// wmic 已在新版 Windows 11 中移除，这里改用注册表 + Win32 API，去掉该隐患。
/// </summary>
internal static class SystemInfo
{
    /// <summary>处理器完整名称。失败返回 "AMD Ryzen"。</summary>
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

    /// <summary>是否为 Intel 处理器（读注册表 VendorIdentifier / ProcessorNameString）。</summary>
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
            // 检测失败时不阻断（与原版一致）
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

    /// <summary>物理核心数。失败时回退为 逻辑核心数 / 2（与原版一致）。</summary>
    public static int GetPhysicalCoreCount()
    {
        try
        {
            int count = CountPhysicalCores();
            if (count > 0) return count;
        }
        catch
        {
            // 忽略，走下面的回退
        }
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>处理器标称/基准频率(MHz)，取自注册表 ~MHz（启动时由 HAL 写入，≈ P0 基频 = MPERF/TSC 计数频率）。失败返回 0。</summary>
    public static double GetBaseFrequencyMHz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz && mhz > 0) return mhz;
        }
        catch { /* 注册表不可用时回退 0 */ }
        return 0;
    }

    /// <summary>主板厂商 + 型号（注册表 BIOS 键，不依赖 WMI/wmic）。失败返回 "—"。</summary>
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

    /// <summary>主显卡型号（注册表显示类驱动键，不依赖 WMI/wmic）。优先返回独显，跳过虚拟/基础适配器。失败返回 "—"。</summary>
    public static string GetGpuName()
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey == null) return "—";

            string? best = null;
            int bestScore = int.MinValue;
            foreach (var sub in classKey.GetSubKeyNames())
            {
                if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;   // 仅 0000/0001… 适配器实例键
                using var dev = classKey.OpenSubKey(sub);
                var desc = (dev?.GetValue("DriverDesc") as string ?? "").Trim();
                if (desc.Length == 0) continue;
                var u = desc.ToUpperInvariant();
                if (u.Contains("BASIC") || u.Contains("REMOTE") || u.Contains("MIRROR") || u.Contains("VIRTUAL"))
                    continue;   // 跳过 Microsoft Basic Display / 远程桌面 / 虚拟显卡

                // 多显卡机器优先独显，避免先枚举到 Ryzen 核显后把它当成主显卡。
                int score = 0;
                if (u.Contains("GEFORCE") || u.Contains("NVIDIA") || u.Contains("RADEON RX")) score = 30;
                else if (u.Contains("INTEL(R) ARC") || u.Contains("INTEL ARC")) score = 25;
                else if (u.Contains("RADEON") || u.Contains("AMD") || u.Contains("INTEL")) score = 10;
                if (u.Contains("RADEON(TM) GRAPHICS") || u.Contains("UHD GRAPHICS") || u.Contains("IRIS")) score -= 5;

                if (score > bestScore)
                {
                    best = desc;
                    bestScore = score;
                }
            }
            return best ?? "—";
        }
        catch { return "—"; }
    }

    /// <summary>SMBIOS 报告的物理内存总容量。失败返回 "—"。</summary>
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

    // ── GetLogicalProcessorInformation 取物理核心数 ──────────────────────────

    private const int RelationProcessorCore = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemLogicalProcessorInformation
    {
        public UIntPtr ProcessorMask;
        public uint Relationship;
        public uint Padding;       // 对齐到 8 字节，使后面的联合体落在偏移 16
        public ulong Reserved0;
        public ulong Reserved1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    private static int CountPhysicalCores() => GetProcessorCoreMasks().Count;

    /// <summary>
    /// 逻辑核 → 物理核 映射（下标=逻辑核号，值=物理核序号）。
    /// 取代写死的 c/2：关闭 SMT、或线程数/核不为 2 时也正确。拿不到拓扑返回 null。
    /// 注：基于 GetLogicalProcessorInformation，仅覆盖单个处理器组（≤64 逻辑核），
    /// 对桌面 Ryzen 足够；超过 64 逻辑核的平台会回退到调用方的兜底逻辑。
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

    /// <summary>各物理核的逻辑处理器掩码，按枚举顺序（即物理核序号）。</summary>
    private static List<ulong> GetProcessorCoreMasks()
    {
        var masks = new List<ulong>();
        uint length = 0;
        GetLogicalProcessorInformation(IntPtr.Zero, ref length); // 先取所需长度
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
}

/// <summary>逻辑核→物理核映射的缓存包装，拿不到拓扑时回退到旧的 c/2 假设。</summary>
internal static class CoreTopology
{
    private static int[]? _map;
    private static bool _loaded;

    public static int PhysicalOf(int logical)
    {
        if (!_loaded)
        {
            _map = SystemInfo.GetLogicalToPhysicalMap();
            _loaded = true;
        }
        if (_map != null && logical >= 0 && logical < _map.Length && _map[logical] >= 0)
            return _map[logical];
        return logical / 2; // 回退：每核 2 线程的旧假设
    }
}
