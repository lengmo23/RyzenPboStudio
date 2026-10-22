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

        throw new FileNotFoundException(
            "未找到 y-cruncher，无法执行压力测试。\n\n" +
            "本软件不附带 y-cruncher，请自行下载：\n" +
            "  https://www.numberworld.org/y-cruncher/\n\n" +
            "解压后放到程序目录下的 tools 文件夹，使结构为：\n" +
            $"  {Path.Combine(appDir, "tools", "y-cruncher", "y-cruncher.exe")}\n\n" +
            "放好后重新点击开始测试。");
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
    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    private static IntPtr CreateJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return job;
        var limit = new JobExtendedLimit { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limit, Marshal.SizeOf<JobExtendedLimit>()))
            Log.Write($"设置 y-cruncher 进程作业随程序退出失败 (错误 {Marshal.GetLastWin32Error()})", "WARN");
        return job;
    }

    /// <summary>Stops this application’s y-cruncher and its child processes.</summary>
    public static void Kill()
    {
        IntPtr job = Job.Value;
        if (job == IntPtr.Zero) return;

        TerminateJobObject(job, 1);

        // Wait for asynchronous termination and memory release before the next stage.
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000
               && QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                                            Marshal.SizeOf<JobBasicAccounting>(), IntPtr.Zero)
               && info.ActiveProcesses > 0)
            Thread.Sleep(50);

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

            Log.Write($"=== 开始 {iterations} 轮 {algoLabel} 测试 · 每轮 {durationSeconds} 秒 · 范围 {scopeLabel ?? "全部核心"} ===");
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
            Log.Write($"运行: {yc} {string.Join(' ', args)}");

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
                if (Job.Value == IntPtr.Zero || !AssignProcessToJobObject(Job.Value, proc.Handle))
                    Log.Write($"y-cruncher 加入进程作业失败 (错误 {Marshal.GetLastWin32Error()})，结束测试时可能无法清理其子进程", "WARN");
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
                        Log.Write("=== 压力测试开始 ===");
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
                            Log.Write($"达到目标 {iterations} 轮，终止测试");
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
                catch (Exception e) { Log.Write($"保存 y-cruncher 日志失败: {e.Message}", "WARN"); }
            }
            catch (Exception e)
            {
                Log.Write($"运行 y-cruncher 失败: {e.Message}", "ERROR");
                return (false, current);
            }

            if (failed.Count > 0)
            {
                if (!BackoffFailedCores(failed, current, mode, algoLabel, autoAdjust, onManualError))
                    return (false, current);

                Log.Write($"负压已调整，3 秒后重新开始 {iterations} 轮测试...");
                if (token.WaitHandle.WaitOne(3000))
                    return (false, current); // Cancelled while waiting.
                continue;
            }

            if (crashed)
            {
                if (!BackoffAfterCrash(current, scopeCores, mode, algoLabel, autoAdjust, onManualError))
                    return (false, current);

                Log.Write($"负压已调整，3 秒后重新开始 {iterations} 轮测试...");
                if (token.WaitHandle.WaitOne(3000))
                    return (false, current);
                continue;
            }

            if (testFinished)
            {
                Log.Write($"✓ {iterations} 轮测试全部通过");
                return (true, current);
            }

            if (returncode != 0)
            {
                if (token.IsCancellationRequested)
                {
                    Log.Write("用户手动终止测试");
                    return (false, current);
                }
                Log.Write($"y-cruncher 异常退出 (code={returncode})，未检测到报错核心", "WARN");
                string full = ycLog.ToString();
                string tail = full.Length > 500 ? full[^500..] : full;
                Log.Write($"yc_log: {tail}");
                Log.Write($"3 秒后重新开始 {iterations} 轮测试...");
                if (token.WaitHandle.WaitOne(3000))
                    return (false, current);
                continue;
            }

            Log.Write("测试未完成");
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

        Log.Write($"报错逻辑核心: [{string.Join(", ", crashedLogical)}]", "WARN");
        Log.Write($"对应物理核心: [{string.Join(", ", crashedSlots)}]", "WARN");

        if (!autoAdjust)
        {
            // Manual mode reports an error without changing offsets.
            Log.Write("手动模式：检测到报错核心，未调整负压，测试停止", "WARN");
            onManualError?.Invoke(crashedSlots);
            return false;
        }

        RefreshBaselineFromCpu(current);

        var before = new List<int>(current);
        foreach (int slot in crashedSlots)
            if (slot >= 0 && slot < current.Count) current[slot] += Config.StepOnError;
        Log.WriteCo($"报错核心负压 +{Config.StepOnError}：", current, before);

        // Persist offsets before applying them so crash recovery can recover the write.
        if (!Tuning.Apply(current, mode, phase, "backoff"))
        {
            Log.Write("应用负压失败", "ERROR");
            return false;
        }
        return true;
    }

    /// <summary>
    /// A y-cruncher crash without a named core backs off all participating negative-CO cores.
    /// Clamp at zero; stop if another crash occurs with no offsets left to back off.
    /// Manual mode only notifies and returns false.
    /// </summary>
    private static bool BackoffAfterCrash(
        List<int> current, IReadOnlyList<int>? scopeCores, string mode, string phase,
        bool autoAdjust, Action<List<int>>? onManualError)
    {
        if (!autoAdjust)
        {
            // A process crash in manual mode also leaves CO unchanged.
            Log.Write("手动模式：y-cruncher 崩溃（视为负压不稳定），未调整负压，测试停止", "WARN");
            onManualError?.Invoke(new List<int>());
            return false;
        }

        RefreshBaselineFromCpu(current);
        var before = new List<int>(current);
        bool anyReduced = false;
        for (int i = 0; i < current.Count; i++)
        {
            // Back off only cores included in the selected test scope.
            if (scopeCores is { Count: > 0 } && !scopeCores.Contains(i)) continue;
            if (current[i] < 0)
            {
                current[i] = Math.Min(0, current[i] + Config.StepOnError);
                anyReduced = true;
            }
        }

        if (!anyReduced)
        {
            Log.Write(scopeCores is { Count: > 0 }
                ? "y-cruncher 崩溃，但本次测试范围内的核心负压已全为 0（无可回退空间），大概率非负压不稳定导致，停止本阶段测试"
                : "y-cruncher 崩溃，但所有核心负压已为 0（无可回退空间），大概率非负压不稳定导致，停止本阶段测试", "ERROR");
            return false;
        }

        Log.WriteCo($"y-cruncher 崩溃，相关核心负压 +{Config.StepOnError}：", current, before, "WARN");
        if (!Tuning.Apply(current, mode, phase, "yc-crash-backoff"))
        {
            Log.Write("应用负压失败", "ERROR");
            return false;
        }
        return true;
    }

    /// <summary>Refreshes offsets from CPU before backoff to include manual changes during the test.
    /// Rejects an all-zero readback against nonzero cached offsets and leaves fused-off slots alone.</summary>
    private static void RefreshBaselineFromCpu(List<int> current)
    {
        var live = RyzenSmu.ReadOffsets(current.Count);
        if (live.Count != current.Count) return;
        if (live.All(v => v == 0) && current.Any(v => v != 0)) return; // Reject an anomalous all-zero readback.

        bool changed = false;
        for (int i = 0; i < current.Count; i++)
        {
            if (RyzenSmu.IsSlotDisabled(i)) continue;
            if (current[i] != live[i]) { current[i] = live[i]; changed = true; }
        }
        if (changed)
            Log.WriteCo("CPU 当前负压：", current, level: "WARN");
    }

    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { }
    }
}
