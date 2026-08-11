using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace RyzenPboStudio;

/// <summary>读取 HWiNFO 共享内存(Global\HWiNFO_SENS_SM2)中每核 "Core N Clock" 频率，使监控 FREQ 与 HWiNFO 完全一致。
/// 需 HWiNFO 运行并开启「共享内存支持」；不可用（未运行/未开/无权限）时返回 null，调用方回退到自算频率。
/// 读数元素布局采用 HWiNFO SDK 文档偏移：szLabelOrig[128]@12、szUnit[16]@268、Value(double)@284。</summary>
internal static class HwInfoReader
{
    private const string SmName = "Global\\HWiNFO_SENS_SM2";
    private const int LabelOffset = 12;
    private const int ValueOffset = 284;   // 12 + szLabelOrig[128] + szLabelUser[128] + szUnit[16]
    private static readonly Regex CoreClockRx = new(@"^Core (\d+) Clock\b", RegexOptions.Compiled);

    /// <summary>返回 物理核索引 → 当前 Core Clock(MHz)；HWiNFO 不可用时返回 null。</summary>
    public static Dictionary<int, double>? ReadCoreClocks()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SmName, MemoryMappedFileRights.Read);
            using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            uint offReading = acc.ReadUInt32(32);
            uint szReading  = acc.ReadUInt32(36);
            uint numReading = acc.ReadUInt32(40);
            if (szReading < ValueOffset + 8 || numReading == 0 || numReading > 100000) return null;

            var result = new Dictionary<int, double>();
            var lbl = new byte[128];
            for (uint i = 0; i < numReading; i++)
            {
                long b = offReading + (long)i * szReading;
                acc.ReadArray(b + LabelOffset, lbl, 0, 128);
                int z = Array.IndexOf(lbl, (byte)0);
                if (z <= 0) continue;
                var m = CoreClockRx.Match(Encoding.ASCII.GetString(lbl, 0, z));
                if (!m.Success) continue;   // 跳过 "Core N T0 Effective Clock" 等
                int core = int.Parse(m.Groups[1].Value);
                double val = acc.ReadDouble(b + ValueOffset);
                if (val > 0 && val < 10000) result[core] = val;
            }
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;   // OpenExisting 抛出即视为 HWiNFO 不可用
        }
    }

    /// <summary>读取 HWiNFO 的 CPU 实测电压（SVI3 TFN 遥测）。Zen5 label 为 "CPU VDDCR_VDD Voltage (SVI3 TFN)"，
    /// Zen4 为 "CPU Core Voltage (SVI3 TFN)"。HWiNFO 不可用时返回 null，调用方显示 "--"。</summary>
    public static double? ReadCpuTelemetryVoltage()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SmName, MemoryMappedFileRights.Read);
            using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            uint offReading = acc.ReadUInt32(32);
            uint szReading  = acc.ReadUInt32(36);
            uint numReading = acc.ReadUInt32(40);
            if (szReading < ValueOffset + 8 || numReading == 0 || numReading > 100000) return null;

            var lbl = new byte[128];
            for (uint i = 0; i < numReading; i++)
            {
                long b = offReading + (long)i * szReading;
                acc.ReadArray(b + LabelOffset, lbl, 0, 128);
                int z = Array.IndexOf(lbl, (byte)0);
                if (z <= 0) continue;
                string s = Encoding.ASCII.GetString(lbl, 0, z);
                // Zen5 叫 "CPU VDDCR_VDD Voltage (SVI3 TFN)"，Zen4 叫 "CPU Core Voltage (SVI3 TFN)"
                if (!s.StartsWith("CPU VDDCR_VDD Voltage", StringComparison.Ordinal) &&
                    !s.StartsWith("CPU Core Voltage", StringComparison.Ordinal)) continue;
                double val = acc.ReadDouble(b + ValueOffset);
                if (val > 0 && val < 3) return val;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
