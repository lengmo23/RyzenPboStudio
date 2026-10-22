using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace RyzenPboStudio;

/// <summary>Reads per-core clock values from HWiNFO shared memory.
/// Returns null when shared memory is unavailable so the monitor can use its fallback.
/// Sensor offsets follow the HWiNFO SDK layout.</summary>
internal static class HwInfoReader
{
    private const string SmName = "Global\\HWiNFO_SENS_SM2";
    private const int LabelOffset = 12;
    private const int ValueOffset = 284;   // 12 + szLabelOrig[128] + szLabelUser[128] + szUnit[16]
    private static readonly Regex CoreClockRx = new(@"^Core (\d+) Clock\b", RegexOptions.Compiled);

    /// <summary>Maps physical cores to Core Clock in MHz, or returns null.</summary>
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
                if (!m.Success) continue; // Ignore per-thread effective-clock labels.
                int core = int.Parse(m.Groups[1].Value);
                double val = acc.ReadDouble(b + ValueOffset);
                if (val > 0 && val < 10000) result[core] = val;
            }
            return result.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads SVI3 measured CPU voltage using Zen4/Zen5 sensor labels;
    /// returns null when HWiNFO is unavailable.</summary>
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

    /// <summary>Reads HWiNFO Bus Clock when the CPU PLL cannot see an external clock;
    /// returns null when unavailable.</summary>
    public static double? ReadBusClock()
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
                if (!Encoding.ASCII.GetString(lbl, 0, z).StartsWith("Bus Clock", StringComparison.Ordinal)) continue;
                double val = acc.ReadDouble(b + ValueOffset);
                if (val is > 50 and < 200) return val;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
