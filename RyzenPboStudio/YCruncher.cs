using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RyzenPboStudio;

/// <summary>调用 y-cruncher 跑压力测试，解析报错核心并自动调压。</summary>
internal static class YCruncher
{
    /// <summary>y-cruncher 不内嵌在程序里，随发布目录放在 tools\y-cruncher\。
    /// 开源仓库不分发它时，用户按同样结构自行放置即可；另接受几种常见的放法。</summary>
    public static string FindExe()
    {
        string appDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(appDir, "tools", "y-cruncher", "y-cruncher.exe"),   // 标准位置
            Path.Combine(appDir, "tools", "y-cruncher v0.8.7.9547b", "y-cruncher.exe"),
            Path.Combine(appDir, "tools", "y-cruncher.exe"),                 // 直接解压进 tools
            Path.Combine(appDir, "y-cruncher", "y-cruncher.exe"),            // 放在 EXE 同级
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
    /// 写一份限定逻辑核心的 stress 配置。y-cruncher 的 config 是 JSON 风格但数组元素用空格分隔、
    /// 不能用逗号；字段名与类型按 v0.8.7 的实际要求，缺一个都会直接抛 KeyNotFoundException。
    /// 内存按「选中核心数 / 全部逻辑核数」等比缩放，使每线程内存量与全核默认时保持一致。
    /// </summary>
    private static string WriteStressConfig(
        IReadOnlyList<string> algorithms, int durationSeconds, int iterations, IReadOnlyList<int> logicalCores)
    {
        long totalRam = 0;
        try { totalRam = (long)SystemInfo.TotalPhysicalBytes(); } catch { }
        if (totalRam <= 0) totalRam = 8L * 1024 * 1024 * 1024;

        int allLogical = Math.Max(1, Environment.ProcessorCount);
        // 0.72 对齐 y-cruncher 自己的默认内存取用比例
        long mem = (long)(totalRam * 0.72 * logicalCores.Count / allLogical);
        long minMem = 256L * 1024 * 1024 * logicalCores.Count;   // 每线程至少 256 MB
        if (mem < minMem) mem = minMem;

        // SecondsTotal 留足余量：轮数由 stdout 的 Iteration 计数控制，别让 y-cruncher 先自行收工
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

    // Holds only the y-cruncher processes this app started; its arch child (e.g. "24-ZN5 ~ Komari")
    // joins automatically, so Kill() never touches a y-cruncher the user launched separately.
    // The handle is never closed: the OS closes it when this app exits (even if killed), which ends the job.
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

    /// <summary>强制结束本程序启动的 y-cruncher 及其架构子进程。</summary>
    public static void Kill()
    {
        IntPtr job = Job.Value;
        if (job == IntPtr.Zero) return;

        Log.Write("正在强制结束 y-cruncher 进程...");
        TerminateJobObject(job, 1);

        // 终止是异步的，等进程真正退出（释放内存）后再开下一阶段
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000
               && QueryInformationJobObject(job, JobObjectBasicAccountingInformation, out var info,
                                            Marshal.SizeOf<JobBasicAccounting>(), IntPtr.Zero)
               && info.ActiveProcesses > 0)
            Thread.Sleep(50);

        Log.Write("y-cruncher 进程清理完成");
    }

    /// <summary>
    /// 跑 iterations 轮压测（algorithms 可为一个或多个 y-cruncher 组件，多个即「和项」一起测）：
    /// 自动模式下报错则对应物理核心 +StepOnError 后重跑整轮，直到通过或被取消；
    /// 手动模式(autoAdjust=false)下报错只提醒并停止，不改动任何负压。返回 (是否通过, 最终负压)。
    /// logicalCores 非空时只压这些逻辑核（走 config 文件，命令行的 stress 不支持指定核心），
    /// scopeCores（槽位口径）则限定 y-cruncher 整体崩溃时的负压回退范围，避免误伤未参与本次测试的核心。
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
        string algoLabel = string.Join("+", algorithms);   // 落盘/日志用

        while (true)
        {
            if (token.IsCancellationRequested)
                return (false, current);

            Log.Write($"=== 开始 {iterations} 轮 {algoLabel} 测试 · 每轮 {durationSeconds} 秒 · 范围 {scopeLabel ?? "全部核心"} ===");
            List<string> args;
            if (logicalCores is { Count: > 0 })
            {
                // 限定核心：只能用配置文件，命令行的 stress 不接受任何核心/线程相关参数。
                // 轮数仍由下面的 Iteration 计数控制，SecondsTotal 给足以免 y-cruncher 先超时收工。
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

                    // y-cruncher 自身崩溃（非指定核心报错）：视为不稳定，跳出后按整机回退处理
                    if (line.Contains("has crashed", StringComparison.OrdinalIgnoreCase))
                    {
                        crashed = true;
                        Log.Write($"[yc] {line}");
                        TryKill(proc);
                        break;
                    }

                    // 实时检测报错核心
                    var mErr = Regex.Match(line, @"logical\s+core\s+(\d+)", RegexOptions.IgnoreCase);
                    if (mErr.Success)
                    {
                        failed.Add(int.Parse(mErr.Groups[1].Value));
                        Log.Write($"[yc] {line}");
                        TryKill(proc);
                        break;
                    }

                    // 检测迭代轮数，达到目标即终止
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
                var crashedLogical = failed.ToList();
                // 逻辑核 → OS 物理核 → 槽位。负压表按槽位索引，少了后一步会写到屏蔽槽上
                var crashedSlots = crashedLogical
                    .Select(CoreTopology.PhysicalOf)
                    .Select(RyzenSmu.OsCoreToSlot)
                    .Distinct().OrderBy(x => x).ToList();

                Log.Write($"报错逻辑核心: [{string.Join(", ", crashedLogical)}]", "WARN");
                Log.Write($"对应物理核心: [{string.Join(", ", crashedSlots)}]", "WARN");

                if (!autoAdjust)
                {
                    // 手动模式：只提醒，不动负压，交由用户自行判断如何调整
                    Log.Write("手动模式：检测到报错核心，未调整负压，测试停止", "WARN");
                    onManualError?.Invoke(crashedSlots);
                    return (false, current);
                }

                RefreshBaselineFromCpu(current);

                foreach (int slot in crashedSlots)
                {
                    if (slot >= 0 && slot < current.Count)
                    {
                        int old = current[slot];
                        current[slot] = old + Config.StepOnError;
                        Log.Write($"  物理核心 {slot}: {old} → {current[slot]} (+{Config.StepOnError})");
                    }
                }

                Log.Write($"新负压: [{string.Join(", ", current)}]");

                // 先落盘再下发：万一应用后立刻死机，这组负压已在磁盘上
                if (!Tuning.Apply(current, mode, algoLabel, "backoff"))
                {
                    Log.Write("应用负压失败", "ERROR");
                    return (false, current);
                }

                Log.Write($"负压已调整，3 秒后重新开始 {iterations} 轮测试...");
                if (token.WaitHandle.WaitOne(3000))
                    return (false, current); // 等待期间被取消
                continue;
            }

            // y-cruncher 自身崩溃（未指明报错核心）：视为整机不稳定，把仍在负压的物理核心各回退一档后重试。
            // 钳到 0（不过冲成正压）；若所有核心已回到 0 仍崩溃，说明大概率非负压问题，停止本阶段避免空转。
            if (crashed)
            {
                if (!autoAdjust)
                {
                    // 手动模式：y-cruncher 崩溃同样只提醒，不动负压
                    Log.Write("手动模式：y-cruncher 崩溃（视为负压不稳定），未调整负压，测试停止", "WARN");
                    onManualError?.Invoke(new List<int>());
                    return (false, current);
                }

                RefreshBaselineFromCpu(current);
                bool anyReduced = false;
                for (int i = 0; i < current.Count; i++)
                {
                    // 限定了测试范围时只回退参与本次测试的核心，不动没测过的那些
                    if (scopeCores is { Count: > 0 } && !scopeCores.Contains(i)) continue;
                    if (current[i] < 0)
                    {
                        int old = current[i];
                        current[i] = Math.Min(0, current[i] + Config.StepOnError);
                        anyReduced = true;
                        Log.Write($"  物理核心 {i}: {old} → {current[i]} (+{Config.StepOnError})");
                    }
                }

                if (!anyReduced)
                {
                    Log.Write(scopeCores is { Count: > 0 }
                        ? "y-cruncher 崩溃，但本次测试范围内的核心负压已全为 0（无可回退空间），大概率非负压不稳定导致，停止本阶段测试"
                        : "y-cruncher 崩溃，但所有核心负压已为 0（无可回退空间），大概率非负压不稳定导致，停止本阶段测试", "ERROR");
                    return (false, current);
                }

                Log.Write($"检测到 y-cruncher 崩溃，视为不稳定：相关物理核心负压各 +{Config.StepOnError} → [{string.Join(", ", current)}]", "WARN");
                if (!Tuning.Apply(current, mode, algoLabel, "yc-crash-backoff"))
                {
                    Log.Write("应用负压失败", "ERROR");
                    return (false, current);
                }

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

    /// <summary>报错回退前，用 CPU 当前实际负压刷新基准，使自动调整跟随用户测试中途的手动调压。
    /// 整体读为全 0 而内存基准非全 0 时视为读取异常，保留内存基准，避免误把负压清零。屏蔽槽不动。</summary>
    private static void RefreshBaselineFromCpu(List<int> current)
    {
        var live = RyzenSmu.ReadOffsets(current.Count);
        if (live.Count != current.Count) return;
        if (live.All(v => v == 0) && current.Any(v => v != 0)) return;   // 读取异常保护

        bool changed = false;
        for (int i = 0; i < current.Count; i++)
        {
            if (RyzenSmu.IsSlotDisabled(i)) continue;
            if (current[i] != live[i]) { current[i] = live[i]; changed = true; }
        }
        if (changed)
            Log.Write($"CPU 当前负压: [{string.Join(", ", current)}]", "WARN");
    }

    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { }
    }
}
