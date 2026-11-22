using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RyzenPboStudio;

/// <summary>Runs y-cruncher stress tests, maps error cores, and adjusts CO in automatic mode.</summary>
internal static partial class YCruncher
{
    /// <summary>Finds the external y-cruncher under tools/y-cruncher or supported fallback paths.
    /// The open-source repository does not include its binaries.</summary>
    public static string FindExe()
    {
        string appDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(appDir, "tools", "y-cruncher", "y-cruncher.exe"),
            Path.Combine(appDir, "tools", "y-cruncher v0.8.7.9547b", "y-cruncher.exe"),
            Path.Combine(appDir, "tools", "y-cruncher.exe"),
            Path.Combine(appDir, "y-cruncher", "y-cruncher.exe"),
            Path.Combine(appDir, "y-cruncher v0.8.7.9547b", "y-cruncher.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        string exe = Path.Combine(appDir, "tools", "y-cruncher", "y-cruncher.exe");
        throw new FileNotFoundException(
            L.F("y-cruncher not found, so the stress test cannot run.\n\n" +
                "y-cruncher is not bundled with this program. Download it from:\n" +
                "  https://www.numberworld.org/y-cruncher/\n\n" +
                "Extract it into the tools folder next to the program so that this file exists:\n" +
                "  {0}\n\n" +
                "Then click Start again.", exe));
    }

    /// <summary>
    /// Writes a core-limited stress config with all fields required by y-cruncher v0.8.7.
    /// Scales memory by selected logical cores to keep memory per thread constant.
    /// </summary>
    private static string WriteStressConfig(
        IReadOnlyList<string> algorithms, int durationSeconds, int iterations, IReadOnlyList<int> logicalCores)
    {
        long totalRam = 0;
        try { totalRam = (long)SystemInfo.TotalPhysicalBytes(); } catch { }
        if (totalRam <= 0) totalRam = 8L * 1024 * 1024 * 1024;

        int allLogical = Math.Max(1, Environment.ProcessorCount);
        // Match y-cruncher’s default memory-use ratio.
        long mem = (long)(totalRam * 0.72 * logicalCores.Count / allLogical);
        long minMem = 256L * 1024 * 1024 * logicalCores.Count; // At least 256 MB per selected thread.
        if (mem < minMem) mem = minMem;

        // stdout iteration counts control rounds; allow enough time to avoid premature self-termination.
        long secondsTotal = (long)durationSeconds * Math.Max(1, iterations) * algorithms.Count + 3600;

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("    Action : \"StressTest\"");
        sb.AppendLine("    StressTest : {");
        sb.AppendLine("        AllocateLocally : false");
        sb.AppendLine($"        TotalMemory : {mem}");
        sb.AppendLine($"        SecondsPerTest : {durationSeconds}");
        sb.AppendLine($"        SecondsTotal : {secondsTotal}");
        sb.AppendLine("        StopOnError : true");
        sb.AppendLine($"        LogicalCores : [{string.Join(' ', logicalCores)}]");
        sb.AppendLine($"        Tests : [{string.Join(' ', algorithms.Select(a => $"\"{a}\""))}]");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        Directory.CreateDirectory(Workspace.ProfilesDir);
        string path = Path.Combine(Workspace.ProfilesDir, "yc-stress.cfg");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        return path;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr hJob, int infoClass, out JobBasicAccounting info, int size, IntPtr retLen);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JobExtendedLimit info, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimit
    {
        // JOBOBJECT_BASIC_LIMIT_INFORMATION
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
        // IO_COUNTERS
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    // The job contains only y-cruncher processes launched here, including architecture children.
    // Closing the job kills them without affecting a user-launched y-cruncher.
    // KILL_ON_JOB_CLOSE also handles forced application termination.
    private static readonly object JobLock = new();
    private static IntPtr _job;

    // A terminated job rejects new processes (ERROR_ACCESS_DENIED) for a while,
    // so Kill() closes it and the next launch gets a fresh one.
    private static IntPtr GetJob()
    {
        lock (JobLock)
        {
            if (_job == IntPtr.Zero) _job = CreateJob();
            return _job;
        }
    }

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return job;
        var limit = new JobExtendedLimit { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limit, Marshal.SizeOf<JobExtendedLimit>()))
            Log.Write(L.F("Failed to tie y-cruncher to the program's lifetime (error {0})", Marshal.GetLastWin32Error()), "WARN");
        return job;
    }

    /// <summary>Stops this application’s y-cruncher and its child processes.</summary>
    public static void Kill()
    {
        IntPtr job;
        lock (JobLock) { job = _job; _job = IntPtr.Zero; }
        if (job == IntPtr.Zero) return;

        TerminateJobObject(job, 1);

        // Wait for asynchronous termination and memory release before the next stage.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000
               && QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                                            Marshal.SizeOf<JobBasicAccounting>(), IntPtr.Zero)
               && info.ActiveProcesses > 0)
            Thread.Sleep(50);

        CloseHandle(job);
    }

    /// <summary>
    /// Runs the requested algorithms for the requested iterations.
    /// Automatic errors back off the mapped physical core and restart the round;
    /// manual mode reports and stops without changing CO. Returns pass status and offsets.
    /// logicalCores restricts the stress config; scopeCores restricts crash backoff
    /// to participating SMU slots.
    /// </summary>
    public static (bool ok, List<int> offsets) RunStressTest(
        IReadOnlyList<string> algorithms, int iterations, List<int> offsets,
        int durationSeconds, string mode, CancellationToken token,
        bool autoAdjust = true, Action<List<int>>? onManualError = null,
        IReadOnlyList<int>? logicalCores = null, IReadOnlyList<int>? scopeCores = null,
        string? scopeLabel = null)
    {
        string yc = FindExe();
        string ycDir = Path.GetDirectoryName(yc)!;
        var current = new List<int>(offsets);
        string algoLabel = string.Join("+", algorithms); // Used in durable history and logs.

        while (true)
        {
            if (token.IsCancellationRequested)
                return (false, current);

            string scope = scopeLabel ?? L.T("all cores");
            Log.Write(L.F("=== Starting {0} rounds of {1} · {2} s per round · Scope {3} ===", iterations, algoLabel, durationSeconds, scope));
            List<string> args;
            if (logicalCores is { Count: > 0 })
            {
                // A core-limited stress run requires a config file, not command-line arguments.
                // stdout iteration counts control rounds; allow enough runtime.
                string cfgPath = WriteStressConfig(algorithms, durationSeconds, iterations, logicalCores);
                args = new List<string> { "skip-warnings", "config", cfgPath };
            }
            else
            {
                args = new List<string> { "skip-warnings", "stress", $"-D:{durationSeconds}" };
                args.AddRange(algorithms);
            }
            Log.Write(L.T("Run: ") + $"{yc} {string.Join(' ', args)}");

            var ycLog = new StringBuilder();
            bool testStarted = false;
            bool testFinished = false;
            bool crashed = false;
            var failed = new SortedSet<int>();
            int currentIter = 0;
            int returncode = -1;

            try
            {
                using var proc = new Process();
                proc.StartInfo.FileName = yc;
                foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
                proc.StartInfo.WorkingDirectory = ycDir;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                proc.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                var lines = new BlockingCollection<string>();
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Add(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lines.Add(e.Data); };

                proc.Start();
                IntPtr job = GetJob();
                if (job == IntPtr.Zero || !AssignProcessToJobObject(job, proc.Handle))
                    Log.Write(L.F("Failed to add y-cruncher to the job object (error {0}); its child processes may survive the test", Marshal.GetLastWin32Error()), "WARN");
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                while (true)
                {
                    if (token.IsCancellationRequested)
                    {
                        TryKill(proc);
                        break;
                    }

                    if (!lines.TryTake(out var line, 100))
                    {
                        if (proc.HasExited && lines.Count == 0) break;
                        continue;
                    }
                    if (string.IsNullOrEmpty(line)) continue;

                    ycLog.AppendLine(line);

                    if (!testStarted && line.Contains("Iteration:"))
                    {
                        testStarted = true;
                        Log.Write(L.T("=== Stress test started ==="));
                    }

                    if (line.Contains("Test Finished") || line.Contains("Test finished"))
                        testFinished = true;

                    // Treat a process crash without a named core as whole-run instability.
                    if (line.Contains("has crashed", StringComparison.OrdinalIgnoreCase))
                    {
                        crashed = true;
                        Log.Write($"[yc] {line}");
                        TryKill(proc);
                        break;
                    }

                    // Detect a reported error core as output arrives.
                    var mErr = Regex.Match(line, @"logical\s+core\s+(\d+)", RegexOptions.IgnoreCase);
                    if (mErr.Success)
                    {
                        failed.Add(int.Parse(mErr.Groups[1].Value));
                        Log.Write($"[yc] {line}");
                        TryKill(proc);
                        break;
                    }

                    // Stop after the requested iteration count.
                    var mIter = Regex.Match(line, @"Iteration:\s*(\d+)", RegexOptions.IgnoreCase);
                    if (mIter.Success)
                    {
                        currentIter = int.Parse(mIter.Groups[1].Value);
                        if (currentIter >= iterations)
                        {
                            Log.Write(L.F("Reached {0} rounds, stopping the test", iterations));
                            testFinished = true;
                            TryKill(proc);
                            break;
                        }
                    }

                    if (testStarted)
                        Log.Write($"[yc] {line}");
                }

                proc.WaitForExit();
                returncode = proc.HasExited ? proc.ExitCode : -1;

                try { File.WriteAllText(Workspace.NewLogPath("y-cruncher", "log"), ycLog.ToString(), Encoding.UTF8); }
                catch (Exception e) { Log.Write(L.F("Failed to save the y-cruncher log: {0}", e.Message), "WARN"); }
            }
            catch (Exception e)
            {
                Log.Write(L.F("Failed to run y-cruncher: {0}", e.Message), "ERROR");
                return (false, current);
            }

            if (failed.Count > 0)
            {
                if (!BackoffFailedCores(failed, current, mode, algoLabel, autoAdjust, onManualError))
                    return (false, current);

                Log.Write(L.F("CO adjusted, restarting {0} rounds in 3 s...", iterations));
                if (token.WaitHandle.WaitOne(3000))
                    return (false, current); // Cancelled while waiting.
                continue;
            }

            if (crashed)
            {
                if (!BackoffAfterCrash(current, scopeCores, mode, algoLabel, autoAdjust, onManualError))
                    return (false, current);

                Log.Write(L.F("CO adjusted, restarting {0} rounds in 3 s...", iterations));
                if (token.WaitHandle.WaitOne(3000))
                    return (false, current);
                continue;
            }

            if (testFinished)
            {
                Log.Write(L.F("✓ All {0} rounds passed", iterations));
                return (true, current);
            }

            if (returncode != 0)
            {
                if (token.IsCancellationRequested)
                {
                    Log.Write(L.T("Test stopped by the user"));
                    return (false, current);
                }
                Log.Write(L.F("y-cruncher exited abnormally (code={0}) without reporting a failing core", returncode), "WARN");
                string full = ycLog.ToString();
                string tail = full.Length > 500 ? full[^500..] : full;
                Log.Write($"yc_log: {tail}");
                Log.Write(L.F("Restarting {0} rounds in 3 s...", iterations));
                if (token.WaitHandle.WaitOne(3000))
                    return (false, current);
                continue;
            }

            Log.Write(L.T("Test not completed"));
            return (false, current);
        }
    }

    /// <summary>Error on specific logical cores: in auto mode back off their physical cores by StepOnError and apply,
    /// returning true so the caller reruns. Returns false (caller stops) in manual mode or if applying fails.</summary>
    private static bool BackoffFailedCores(
        SortedSet<int> failedLogical, List<int> current, string mode, string phase,
        bool autoAdjust, Action<List<int>>? onManualError)
    {
        var crashedLogical = failedLogical.ToList();
        // Map logical core to OS core and then SMU slot before indexing CO offsets.
        var crashedSlots = crashedLogical
            .Select(CoreTopology.PhysicalOf)
            .Select(RyzenSmu.OsCoreToSlot)
            .Distinct().OrderBy(x => x).ToList();

        Log.Write(L.F("Failing logical cores: [{0}]", string.Join(", ", crashedLogical)), "WARN");
        Log.Write(L.F("Physical cores: [{0}]", string.Join(", ", crashedSlots)), "WARN");

        if (!autoAdjust)
        {
            // Manual mode reports an error without changing offsets.
            Log.Write(L.T("Manual mode: failing core detected, CO not changed, test stopped"), "WARN");
            onManualError?.Invoke(crashedSlots);
            return false;
        }

        RefreshBaselineFromCpu(current);

        // A core already at the CO ceiling has no backoff left, so its error is likely not CO-related
        var atMax = crashedSlots.Where(s => s >= 0 && s < current.Count && current[s] >= Config.CoMax).ToList();
        if (atMax.Count > 0)
        {
            Log.Write(L.F("CO of failing cores [{0}] is already at the +{1} limit (no room to back off); the cause is most likely not CO, stopping this stage", string.Join(", ", atMax), Config.CoMax), "ERROR");
            return false;
        }

        var before = new List<int>(current);
        foreach (int slot in crashedSlots)
            if (slot >= 0 && slot < current.Count) current[slot] = Math.Min(Config.CoMax, current[slot] + Config.StepOnError);
        Log.WriteCo(L.F("Failing cores CO +{0}:", Config.StepOnError), current, before);

        // Persist offsets before applying them so crash recovery can recover the write.
        if (!Tuning.Apply(current, mode, phase, "backoff"))
        {
            Log.Write(L.T("Failed to apply CO"), "ERROR");
            return false;
        }
        return true;
    }

    /// <summary>
    /// A y-cruncher crash without a named core backs off every participating core below the CO ceiling.
    /// Stop if another crash occurs with every participating core already at the ceiling.
    /// Manual mode only notifies and returns false.
    /// </summary>
    private static bool BackoffAfterCrash(
        List<int> current, IReadOnlyList<int>? scopeCores, string mode, string phase,
        bool autoAdjust, Action<List<int>>? onManualError)
    {
        if (!autoAdjust)
        {
            // A process crash in manual mode also leaves CO unchanged.
            Log.Write(L.T("Manual mode: y-cruncher crashed (treated as CO instability), CO not changed, test stopped"), "WARN");
            onManualError?.Invoke(new List<int>());
            return false;
        }

        RefreshBaselineFromCpu(current);
        var before = new List<int>(current);
        bool anyRaised = false;
        for (int i = 0; i < current.Count; i++)
        {
            // Back off only cores included in the selected test scope.
            if (RyzenSmu.IsSlotDisabled(i)) continue;
            if (scopeCores is { Count: > 0 } && !scopeCores.Contains(i)) continue;
            if (current[i] < Config.CoMax)
            {
                current[i] = Math.Min(Config.CoMax, current[i] + Config.StepOnError);
                anyRaised = true;
            }
        }

        if (!anyRaised)
        {
            Log.Write(scopeCores is { Count: > 0 }
                ? L.F("y-cruncher crashed, but every core in the test scope is already at the +{0} CO limit (no room to back off); the cause is most likely not CO, stopping this stage", Config.CoMax)
                : L.F("y-cruncher crashed, but every core is already at the +{0} CO limit (no room to back off); the cause is most likely not CO, stopping this stage", Config.CoMax), "ERROR");
            return false;
        }

        Log.WriteCo(L.F("y-cruncher crashed, affected cores CO +{0}:", Config.StepOnError), current, before, "WARN");
        if (!Tuning.Apply(current, mode, phase, "yc-crash-backoff"))
        {
            Log.Write(L.T("Failed to apply CO"), "ERROR");
            return false;
        }
        return true;
    }

    /// <summary>Refreshes offsets from CPU before backoff to include manual changes during the test.
    /// Rejects an all-zero readback against nonzero cached offsets, unless all zeros were applied manually,
    /// and leaves fused-off slots alone.</summary>
    public static void RefreshBaselineFromCpu(List<int> current, bool log = true)
    {
        var live = RyzenSmu.ReadOffsets(current.Count);
        if (live.Count != current.Count) return;
        // Reject an anomalous all-zero readback unless the last CO write was a manual all-zero apply.
        if (live.All(v => v == 0) && current.Any(v => v != 0) && !LastWriteWasManualZero()) return;

        bool changed = false;
        for (int i = 0; i < current.Count; i++)
        {
            if (RyzenSmu.IsSlotDisabled(i)) continue;
            if (current[i] != live[i]) { current[i] = live[i]; changed = true; }
        }
        if (changed && log)
            Log.WriteCo(L.T("Current CPU CO:"), current, level: "WARN");
    }

    private static bool LastWriteWasManualZero() =>
        Journal.ReadLastEntry() is { Reason: Journal.ManualInTest or Journal.ManualTakeover } last
        && last.Offsets.All(v => v == 0);

    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { }
    }
}
