using System.Buffers.Binary;
using System.Management;
using System.Text.Json;

namespace RyzenPboStudio;

/// <summary>
/// BIOS AMD_ACPI (AOD) WMI interface (root\wmi) for PBO / CO / Curve Shaper BIOS persistence.
/// Set commands stage values in the BIOS and take effect after a reboot.
/// </summary>
internal static class AmdAcpi
{
    private static ManagementBaseObject? Query(ManagementObject mo, string method, uint id)
    {
        try
        {
            var inParams = mo.GetMethodParameters(method);
            inParams["ID"] = id;
            return mo.InvokeMethod(method, inParams, null)?["pack"] as ManagementBaseObject;
        }
        catch { return null; }
    }

    /// <summary>AMD_ACPI.RunCommand with Inbuf = function ID (LE32) + value (LE32); throws on WMI failure.</summary>
    private static ManagementBaseObject RunCommand(ManagementObject mo, uint id, uint value)
    {
        var inParams = mo.GetMethodParameters("RunCommand");
        inParams["Inbuf"] = InputBytes(id, value);
        return mo.InvokeMethod("RunCommand", inParams, null);
    }

    private static byte[] InputBytes(uint id, uint value)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, id);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), value);
        return buf;
    }

    private static readonly object TraceGate = new();

    /// <summary>Appends the raw RunCommand request and return to logs\acpi-command-trace.jsonl (rotated at 2 MiB).
    /// The return is not interpreted; a logging failure never affects the write.</summary>
    private static void Trace(string inst, uint id, uint value, ManagementBaseObject? output)
    {
        try
        {
            string line = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.Now,
                Instance = inst,
                Method = "RunCommand",
                CommandId = $"0x{id:X8}",
                RequestedRaw = value,
                InputHex = Convert.ToHexString(InputBytes(id, value)),
                Output = Plain(output),
            });
            lock (TraceGate)
            {
                Directory.CreateDirectory(Workspace.LogsDir);
                string path = Path.Combine(Workspace.LogsDir, "acpi-command-trace.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, Path.Combine(Workspace.LogsDir, "acpi-command-trace.previous.jsonl"), overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { }
    }

    /// <summary>WMI return values as JSON-friendly data: byte arrays as hex, embedded objects as dictionaries.</summary>
    private static object? Plain(object? value) => value switch
    {
        null => null,
        byte[] bytes => Convert.ToHexString(bytes),
        ManagementBaseObject pack => pack.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => Plain(p.Value)),
        string or bool or uint or int or ushort or short or byte or sbyte or long or ulong => value,
        Array array => array.Cast<object?>().Select(Plain).ToArray(),
        _ => value.ToString(),
    };

    // AMD_ACPI AOD command IDs for PBO / CO / CS BIOS persistence.
    private const uint CmdPpt = 0x00050001;
    private const uint CmdTdc = 0x00050002;
    private const uint CmdEdc = 0x00050003;
    private const uint CmdScalar = 0x00050004;
    private const uint CmdCo = 0x0005000A;
    private const uint CmdBoost = 0x00020036;
    private const uint CmdCs = 0x00020059;

    internal sealed record PboBiosInput(
        IReadOnlyList<int> CoOffsets,
        uint FMaxMHz,
        bool FMaxWritable,
        uint PptW,
        uint TdcA,
        uint EdcA,
        uint Scalar,
        bool ScalarWritable,
        int[,]? CsGrid,
        bool CsSupported);

    internal sealed record PboBiosCommand(uint CommandId, uint Payload, string Label, string DisplayValue, bool IsPositiveCo = false);

    internal sealed record PboBiosPlan(
        int FirmwareVersion,
        bool IsGraniteRidge,
        List<PboBiosCommand> Commands,
        List<(string Label, string Value)> Limits,
        List<(int Slot, int Value)> CoEntries,
        int[,]? CsGrid,
        List<string> SummaryLines,
        List<string> SkippedReasons,
        bool HasPositiveCo);

    internal sealed record PboBiosWriteResult(List<PboBiosCommand> Written, PboBiosCommand? Failed, string? Error)
    {
        public bool Complete => Error == null && Failed == null;
    }

    private sealed class AodMutexGuard : IDisposable
    {
        private readonly Mutex _rmMutex = new(false, @"Global\AMDRyzenMasterMutex");
        private readonly Mutex _aodMutex = new(false, "AOD_WMI_HANDLER_MUTEX");
        private bool _ownsRm;
        private bool _ownsAod;

        public AodMutexGuard()
        {
            try
            {
                _ownsRm = Acquire(_rmMutex);
                if (!_ownsRm) throw new InvalidOperationException(L.T("AMD Ryzen Master interface is busy; nothing was written."));
                _ownsAod = Acquire(_aodMutex);
                if (!_ownsAod) throw new InvalidOperationException(L.T("AMD BIOS firmware interface is busy; nothing was written."));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static bool Acquire(Mutex m)
        {
            try { return m.WaitOne(5000); }
            catch (AbandonedMutexException) { return true; }
        }

        public void Dispose()
        {
            if (_ownsAod) { _ownsAod = false; _aodMutex.ReleaseMutex(); }
            _aodMutex.Dispose();
            if (_ownsRm) { _ownsRm = false; _rmMutex.ReleaseMutex(); }
            _rmMutex.Dispose();
        }
    }

    private static ManagementObject OpenAod(out string instanceName)
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AMD_ACPI");
        using var results = searcher.Get();
        string? aodPath = null;
        string? inst = null;
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                if (obj.Path.Path.IndexOf("AOD", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (aodPath != null)
                    throw new NotSupportedException(L.T("Multiple AOD firmware instances were found; target is ambiguous."));
                aodPath = obj.Path.Path;
                inst = obj["InstanceName"]?.ToString() ?? aodPath;
            }
        }
        if (aodPath == null)
            throw new NotSupportedException(L.T("This motherboard does not expose the AMD_ACPI AOD interface."));
        var mo = new ManagementObject(aodPath);
        mo.Get();
        instanceName = inst!;
        return mo;
    }

    private static int QueryVersion(ManagementObject mo)
    {
        using var res = mo.InvokeMethod("QueryVersion", null, null);
        return Convert.ToInt32(res?["Version"] ?? throw new InvalidOperationException(L.T("Cannot read BIOS AOD protocol version.")));
    }

    private static HashSet<uint> ReadCommandIds(ManagementObject mo)
    {
        var ids = new HashSet<uint>();
        void Read(string method, bool optional)
        {
            try
            {
                using var res = mo.InvokeMethod(method, null, null);
                using var pack = res?["pack"] as ManagementBaseObject;
                if (pack?["ID"] is not uint[] arr) throw new InvalidOperationException(L.T("Firmware command list is incomplete."));
                int len = Convert.ToInt32(pack["Length"]);
                if (len < 0 || len > arr.Length) throw new InvalidOperationException(L.T("Firmware command list is incomplete."));
                for (int i = 0; i < len; i++) ids.Add(arr[i]);
            }
            catch (ManagementException e) when (optional && e.ErrorCode is ManagementStatus.NotFound or ManagementStatus.MethodNotImplemented or ManagementStatus.InvalidMethod)
            {
            }
        }
        Read("GetObjectID", optional: false);
        Read("GetObjectID2", optional: true);
        return ids;
    }

    private static (long Min, long Max, long Step) QuerySignedOrUnsignedRange(ManagementObject mo, uint id, bool signed)
    {
        using var pack = Query(mo, "GetMinMaxSteppingValues", id)
            ?? throw new NotSupportedException(L.F("Cannot query BIOS range for command 0x{0:X8}.", id));
        uint rawMin = Convert.ToUInt32(pack["Min"]);
        uint rawMax = Convert.ToUInt32(pack["Max"]);
        long min = signed ? (int)rawMin : rawMin;
        long max = signed ? (int)rawMax : rawMax;
        long step = Convert.ToUInt32(pack["Stepping"]);
        if (min > max || step <= 0)
            throw new NotSupportedException(L.F("BIOS reported an invalid range for command 0x{0:X8}.", id));
        return (min, max, step);
    }

    private static bool InRange(long v, long min, long max, long step) =>
        step > 0 && min <= max && v >= min && v <= max && (v - min) % step == 0;

    /// <summary>Probes AMD_ACPI AOD capabilities and builds a validated list of BIOS PBO/CO/CS commands.</summary>
    public static PboBiosPlan PreparePboWrite(PboBiosInput input)
    {
        bool isGraniteRidge = RyzenSmu.IsGraniteRidge();
        int boostBase = 0;
        bool hasBoostBaseline = isGraniteRidge && RyzenSmu.TryReadBoostBaseline(out boostBase);

        using var guard = new AodMutexGuard();
        lock (RyzenSmu.IoLock)
        {
            using var mo = OpenAod(out _);
            int version = QueryVersion(mo);
            if (version is not (5 or 6 or 7))
                throw new NotSupportedException(L.F("Unsupported BIOS AOD protocol version {0}.", version));

            var ids = ReadCommandIds(mo);
            var commands = new List<PboBiosCommand>();
            var limits = new List<(string Label, string Value)>();
            var coEntries = new List<(int Slot, int Value)>();
            int[,]? csGrid = null;
            var summary = new List<string>();
            var skipped = new List<string>();

            // PPT / TDC / EDC (mW / mA) — protocol v6/v7.
            void AddLimit(uint cmdId, string label, uint uiVal, string unit)
            {
                if (version < 6 || !ids.Contains(cmdId))
                {
                    skipped.Add(L.F("{0} (not exposed by BIOS)", label));
                    return;
                }
                var (min, max, step) = QuerySignedOrUnsignedRange(mo, cmdId, signed: false);
                long raw = (long)uiVal * 1000;
                if (!InRange(raw, min, max, step))
                    throw new InvalidOperationException(
                        L.F("{0} ({1} {2}) is outside BIOS range {3}..{4} {2}.", label, uiVal, unit, min / 1000.0, max / 1000.0));
                commands.Add(new PboBiosCommand(cmdId, (uint)raw, label, $"{uiVal} {unit}"));
                limits.Add((label, $"{uiVal} {unit}"));
                summary.Add($"{label}: {uiVal} {unit}");
            }

            AddLimit(CmdPpt, "PPT", input.PptW, "W");
            AddLimit(CmdTdc, "TDC", input.TdcA, "A");
            AddLimit(CmdEdc, "EDC", input.EdcA, "A");

            // Scalar (1X..10X -> 100..1000) — protocol v6/v7.
            if (!input.ScalarWritable || version < 6 || !ids.Contains(CmdScalar))
            {
                skipped.Add(L.T("Scalar (not exposed by BIOS)"));
            }
            else
            {
                long raw = (long)input.Scalar * 100;
                bool validated = false;
                using (var dv = Query(mo, "Getdvalues", CmdScalar))
                {
                    if (dv != null && Convert.ToInt32(dv["Length"]) is > 0 and var len &&
                        Convert.ToUInt32(dv["ID"]) == CmdScalar && dv["DValuesBuffer"] is uint[] buf)
                    {
                        var allowed = buf.Take(Math.Min(len, buf.Length)).Where(v => v is >= 100 and <= 1000 && v % 100 == 0).Select(v => (long)v).ToHashSet();
                        if (allowed.Count > 0)
                        {
                            if (!allowed.Contains(raw))
                                throw new InvalidOperationException(L.F("Scalar {0}X is not in the BIOS option list.", input.Scalar));
                            validated = true;
                        }
                    }
                }
                if (!validated)
                {
                    var (min, max, step) = QuerySignedOrUnsignedRange(mo, CmdScalar, signed: false);
                    if (!InRange(raw, min, max, step))
                        throw new InvalidOperationException(L.F("Scalar {0}X is outside BIOS range.", input.Scalar));
                }
                commands.Add(new PboBiosCommand(CmdScalar, (uint)raw, "Scalar", $"{input.Scalar}X"));
                limits.Add(("Scalar", $"{input.Scalar}X"));
                summary.Add($"Scalar: {input.Scalar}X");
            }

            // FMax / Boost Override (signed MHz offset, 25 MHz step) — Granite Ridge only.
            if (!isGraniteRidge)
            {
                skipped.Add(L.T("FMax (Zen 5 only)"));
            }
            else if (!input.FMaxWritable || version < 6 || !ids.Contains(CmdBoost))
            {
                skipped.Add(L.T("FMax (not exposed by BIOS)"));
            }
            else if (!hasBoostBaseline)
            {
                skipped.Add(L.T("FMax (cannot read stock boost fuse)"));
            }
            else
            {
                long offset = (long)input.FMaxMHz - boostBase;
                var (min, max, step) = QuerySignedOrUnsignedRange(mo, CmdBoost, signed: true);
                if (offset % 25 != 0 || !InRange(offset, min, max, step))
                    throw new InvalidOperationException(
                        L.F("FMax {0} MHz (offset {1:+#;-#;0} MHz) is outside BIOS range {2}..{3} MHz (step 25).",
                            input.FMaxMHz, offset, boostBase + min, boostBase + max));
                string sign = offset >= 0 ? "+" : "";
                commands.Add(new PboBiosCommand(CmdBoost, unchecked((uint)(int)offset), "FMax", $"{input.FMaxMHz} MHz ({sign}{offset} MHz)"));
                limits.Add(("FMax", $"{input.FMaxMHz} MHz ({sign}{offset} MHz)"));
                summary.Add($"FMax: {input.FMaxMHz} MHz ({sign}{offset} MHz)");
            }

            // Per-core Curve Optimizer — protocol v5/v6/v7.
            bool hasPositiveCo = false;
            if (!ids.Contains(CmdCo))
            {
                skipped.Add(L.T("Curve Optimizer (not exposed by BIOS)"));
            }
            else if (!RyzenSmu.TryGetBiosCoreTargets(out var coreTargets, out string? mapErr))
            {
                throw new NotSupportedException(mapErr ?? L.T("Cannot map physical cores for BIOS CO write."));
            }
            else
            {
                if (coreTargets.Any(t => t.Slot >= input.CoOffsets.Count))
                    throw new NotSupportedException(L.T("Core count exceeds the CO editor slots; CO was not written."));
                var (min, max, step) = QuerySignedOrUnsignedRange(mo, CmdCo, signed: false);
                if (max > 255)
                    throw new NotSupportedException(L.T("Unverified BIOS CO magnitude encoding."));
                long effMax = Math.Min(max, 50L);
                if (min > effMax)
                    throw new NotSupportedException(L.T("BIOS CO range does not overlap the supported range."));
                effMax -= (effMax - min) % step;

                var coParts = new List<string>();
                foreach (var (slot, targetIdx) in coreTargets)
                {
                    int v = input.CoOffsets[slot];
                    if (v is < -50 or > 30)
                        throw new InvalidOperationException(L.F("Core {0} CO ({1}) must be between -50 and +30.", slot, v));
                    if (v > 0 && version == 5)
                        throw new NotSupportedException(L.T("BIOS AOD protocol v5 does not support positive CO values."));
                    long mag = Math.Abs((long)v);
                    if (!InRange(mag, min, effMax, step))
                        throw new InvalidOperationException(L.F("Core {0} CO ({1}) is outside BIOS magnitude range {2}..{3} (step {4}).", slot, v, min, effMax, step));
                    // High byte: 0x20 = per-core positive, 0x21 = per-core negative/zero; bits [23:16] = compact core index.
                    uint payload = (v > 0 ? 0x20000000u : 0x21000000u) | ((uint)targetIdx << 16) | (uint)mag;
                    if (v > 0) hasPositiveCo = true;
                    string valText = v > 0 ? $"+{v}" : v.ToString();
                    commands.Add(new PboBiosCommand(CmdCo, payload, $"CO Core {slot} (C{targetIdx})", valText, IsPositiveCo: v > 0));
                    coEntries.Add((slot, v));
                    coParts.Add($"C{slot}:{valText}");
                }
                summary.Add($"CO ({coreTargets.Count}): {string.Join(" ", coParts)}");
            }

            // Curve Shaper — protocol v6/v7, only when both CPU and BIOS expose it.
            if (input.CsSupported && input.CsGrid != null)
            {
                if (version < 6 || !ids.Contains(CmdCs))
                {
                    skipped.Add(L.T("Curve Shaper (not exposed by BIOS)"));
                }
                else
                {
                    var (min, max, step) = QuerySignedOrUnsignedRange(mo, CmdCs, signed: true);
                    long rawMin = min;
                    if (min < -50) min += (-50 - min + step - 1) / step * step;
                    max = Math.Min(max, 30L);
                    if (min > max)
                        throw new NotSupportedException(L.T("BIOS Curve Shaper range does not overlap -50..+30."));
                    max -= (max - rawMin) % step;

                    string[] bands = { "MIN", "LOW", "MED", "HIGH", "MAX" };
                    var csParts = new List<string>();
                    for (int t = 0; t < 5; t++)
                    {
                        int low = input.CsGrid[t, 0], med = input.CsGrid[t, 1], high = input.CsGrid[t, 2];
                        if (!InRange(low, min, max, step) || !InRange(med, min, max, step) || !InRange(high, min, max, step))
                            throw new InvalidOperationException(
                                L.F("Curve Shaper {0} ({1}/{2}/{3}) is outside BIOS range {4}..{5} (step {6}).", bands[t], low, med, high, min, max, step));
                        uint payload = 0x80u | (uint)t
                            | ((uint)unchecked((byte)(sbyte)low) << 8)
                            | ((uint)unchecked((byte)(sbyte)med) << 16)
                            | ((uint)unchecked((byte)(sbyte)high) << 24);
                        string valText = (low == med && med == high) ? low.ToString() : $"{low}/{med}/{high}";
                        commands.Add(new PboBiosCommand(CmdCs, payload, $"CS {bands[t]}", valText));
                        csParts.Add($"{bands[t]}:{valText}");
                    }
                    csGrid = (int[,])input.CsGrid.Clone();
                    summary.Add($"CS: {string.Join(" ", csParts)}");
                }
            }

            if (commands.Count == 0)
                throw new NotSupportedException(L.T("This BIOS exposes no writable PBO / CO / CS commands."));

            return new PboBiosPlan(version, isGraniteRidge, commands, limits, coEntries, csGrid, summary, skipped, hasPositiveCo);
        }
    }

    /// <summary>Sends a prepared PBO/CO/CS BIOS plan sequentially; stops at the first firmware error without rolling back.</summary>
    public static PboBiosWriteResult SendPboWrite(PboBiosPlan plan)
    {
        var written = new List<PboBiosCommand>();
        using var guard = new AodMutexGuard();
        lock (RyzenSmu.IoLock)
        {
            using var mo = OpenAod(out string inst);
            int version = QueryVersion(mo);
            var ids = ReadCommandIds(mo);
            if (version != plan.FirmwareVersion || plan.Commands.Any(c => !ids.Contains(c.CommandId)))
                return new PboBiosWriteResult(written, null, L.T("BIOS firmware capabilities changed; nothing was written."));

            foreach (var cmd in plan.Commands)
            {
                try
                {
                    using var output = RunCommand(mo, cmd.CommandId, cmd.Payload);
                    Trace(inst, cmd.CommandId, cmd.Payload, output);
                    CheckPboReply(version, output);
                    written.Add(cmd);
                }
                catch (Exception e)
                {
                    return new PboBiosWriteResult(written, cmd, e.Message);
                }
            }
        }
        return new PboBiosWriteResult(written, null, null);
    }

    private static void CheckPboReply(int version, ManagementBaseObject? output)
    {
        using var outbuf = output?["Outbuf"] as ManagementBaseObject;
        if (outbuf?["Result"] is not byte[] reply || reply.Length < (version == 5 ? 203 : 16))
            throw new InvalidOperationException(L.T("Firmware reply is missing or incomplete; result is unknown."));
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(2, 4));
        if (status != 0)
            throw new InvalidOperationException(L.F("Firmware returned status 0x{0:X8}.", status));
    }
}
