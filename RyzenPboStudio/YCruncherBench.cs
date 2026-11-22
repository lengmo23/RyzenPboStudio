using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RyzenPboStudio;

/// <summary>YC018: y-cruncher Pi benchmark (standard, memory-intensive, multi-threaded on all cores), size picked by installed RAM.</summary>
internal static partial class YCruncher
{
    public const string BenchMode = "YC018";

    // Definite computation-error markers: fail and kill at once. Other abnormal ends are still caught by
    // "no normal end / no spot-check pass", so warnings ("ECC Recovered Errors", low-memory paging notices) stay out.
    private static readonly Regex BenchErrorPattern = new(
        @"\bmismatch\b|redundancy\s+check|spot\s+check\s+failed|base\s+conversion\s+failed|" +
        @"multiplication\s+failed|data\s+corruption|maximum\s+coefficient\s+size|exponent\s+has\s+exceeded|" +
        @"fatal\s+error|critical\s+error|assertion\s+failure|error\s+encountered|unknown\s+error|" +
        @"digit\s+output\s+failed|memory\s+allocation\s+failed|allocation\s+error\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "Spot Check: Good through N": result digits match the built-in reference
    private static readonly Regex BenchSpotGood = new(@"good\s+through", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Summary block printed on a normal finish
    private static readonly Regex BenchEnd = new(@"total\s+computation\s+time|validation\s+file\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private enum BenchOutcome { Pass, CoreError, Crash, Failed, Cancelled }

    /// <summary>
    /// Bench size by installed RAM (SMBIOS, so 32 / 48 GB kits report whole GiB); thresholds keep a 2 GiB margin.
    /// Memory needed: 10b ~44.7 GiB, 5b ~22.3 GiB, 2.5b ~11.8 GiB.
    /// </summary>
    private static string PickBenchSize()
    {
        double gib = SystemInfo.TotalPhysicalBytes() / (1024d * 1024 * 1024);
        if (gib >= 46)
        {
            Log.Write(L.F("Installed memory {0:F1} GiB (≥ 48 GB), using Pi Benchmark 10b (needs about 44.7 GiB)", gib));
            return "10b";
        }
        if (gib >= 30)
        {
            Log.Write(L.F("Installed memory {0:F1} GiB (≥ 32 GB), using Pi Benchmark 5b (needs about 22.3 GiB)", gib));
            return "5b";
        }
        Log.Write(gib > 0
            ? L.F("Installed memory {0:F1} GiB is under 32 GB, falling back to Pi Benchmark 2.5b (needs about 11.8 GiB)", gib)
            : L.T("Cannot read the installed memory size, falling back to Pi Benchmark 2.5b (needs about 11.8 GiB)"), "WARN");
        return "2.5b";
    }

    /// <summary>
    /// Runs the Pi benchmark `rounds` times on all cores. Errors follow RunStressTest: a reported logical core is
    /// backed off by StepOnError and all rounds restart (manual mode: notify and stop); a whole-process crash uses
    /// the same crash backoff. Failures not attributable to a core change no CO and stop. Returns (passed, offsets).
    /// </summary>
    public static (bool ok, List<int> offsets) RunPiBench(
        int rounds, List<int> offsets, string mode, CancellationToken token,
        bool autoAdjust = true, Action<List<int>>? onManualError = null)
    {
        string yc = FindExe();
        string ycDir = Path.GetDirectoryName(yc)!;
        var current = new List<int>(offsets);

        Log.Write(L.T("YC018 is a fixed-size benchmark: it always uses all cores, Rounds is the number of runs, and Seconds per round is ignored"));
        string size = PickBenchSize();

        while (true)
        {
            if (token.IsCancellationRequested)
                return (false, current);

            Log.Write(L.F("=== Starting {0} rounds of YC018 · Pi Benchmark {1} · Scope all cores ===", rounds, size));

            var outcome = BenchOutcome.Pass;
            var failed = new SortedSet<int>();
            string reason = "";
            for (int round = 1; round <= rounds && outcome == BenchOutcome.Pass; round++)
            {
                Log.Write(L.F(">>> YC018 round {0}/{1}", round, rounds));
                (outcome, failed, reason) = RunBenchOnce(yc, ycDir, size, token);
                if (outcome == BenchOutcome.Pass)
                    Log.Write(L.F("YC018 round {0}/{1} passed", round, rounds));
            }

            switch (outcome)
            {
                case BenchOutcome.Pass:
                    Log.Write(L.F("✓ All {0} rounds of YC018 passed", rounds));
                    return (true, current);

                case BenchOutcome.Cancelled:
                    Log.Write(L.T("Test stopped by the user"));
                    return (false, current);

                case BenchOutcome.CoreError:
                    if (!BackoffFailedCores(failed, current, mode, BenchMode, autoAdjust, onManualError))
                        return (false, current);
                    break;

                case BenchOutcome.Crash:
                    if (!BackoffAfterCrash(current, null, mode, BenchMode, autoAdjust, onManualError))
                        return (false, current);
                    break;

                default:
                    Log.Write(L.F("YC018 benchmark failed: {0}", reason), "ERROR");
                    Log.Write(L.T("This error cannot be traced to a core, so no CO was changed and the test stopped. " +
                                  "Memory, IF (FCLK) or CO may be unstable; verify memory and FCLK on their own first. " +
                                  "If the log shows a memory allocation failure, close memory-heavy programs and retry"), "ERROR");
                    return (false, current);
            }

            Log.Write(L.F("CO adjusted, restarting {0} rounds in 3 s...", rounds));
            if (token.WaitHandle.WaitOne(3000))
                return (false, current);
        }
    }

    /// <summary>Runs one bench and classifies its output; the full output goes to a y-cruncher log in logs\.</summary>
    private static (BenchOutcome outcome, SortedSet<int> failed, string reason) RunBenchOnce(
        string yc, string ycDir, string size, CancellationToken token)
    {
        try { Directory.CreateDirectory(Workspace.LogsDir); } catch { }

        // pause:-2 never waits for a key; status:none avoids rewritten status lines on a pipe;
        // -od:0 skips digit output; -o puts the validation file in logs\ instead of the y-cruncher folder
        var args = new List<string>
        {
            "skip-warnings", "pause:-2", "status:none",
            "bench", size, "-od:0", "-o", Workspace.LogsDir,
        };
        Log.Write(L.T("Run: ") + $"{yc} {string.Join(' ', args)}");

        var ycLog = new StringBuilder();
        var failed = new SortedSet<int>();
        bool crashed = false, reachedEnd = false, spotGood = false, cancelled = false;
        string? errorLine = null;
        int returncode = -1;

        // Returns true once the outcome is decided and the process should be killed
        bool Handle(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            ycLog.AppendLine(line);
            Log.Write($"[yc] {line}");

            if (line.Contains("has crashed", StringComparison.OrdinalIgnoreCase))
            {
                crashed = true;
                return true;
            }
            var mErr = Regex.Match(line, @"logical\s+core\s+(\d+)", RegexOptions.IgnoreCase);
            if (mErr.Success)
            {
                failed.Add(int.Parse(mErr.Groups[1].Value));
                return true;
            }
            if (BenchErrorPattern.IsMatch(line))
            {
                errorLine ??= line;
                return true;
            }
            if (BenchSpotGood.IsMatch(line)) spotGood = true;
            if (BenchEnd.IsMatch(line)) reachedEnd = true;
            return false;
        }

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

            bool stopped = false;
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    cancelled = true;
                    stopped = true;
                    TryKill(proc);
                    break;
                }
                if (!lines.TryTake(out var line, 100))
                {
                    if (proc.HasExited && lines.Count == 0) break;
                    continue;
                }
                if (Handle(line))
                {
                    stopped = true;
                    TryKill(proc);
                    break;
                }
            }

            proc.WaitForExit();   // parameterless overload also waits for async output to drain
            returncode = proc.HasExited ? proc.ExitCode : -1;

            // Output can still arrive after exit; the summary and spot check are the last lines
            while (!stopped && lines.TryTake(out var rest))
                if (Handle(rest)) stopped = true;

            try { File.WriteAllText(Workspace.NewLogPath("y-cruncher", "log"), ycLog.ToString(), Encoding.UTF8); }
            catch (Exception e) { Log.Write(L.F("Failed to save the y-cruncher log: {0}", e.Message), "WARN"); }
        }
        catch (Exception e)
        {
            Log.Write(L.F("Failed to run y-cruncher: {0}", e.Message), "ERROR");
            return (BenchOutcome.Failed, failed, L.F("cannot run y-cruncher ({0})", e.Message));
        }

        if (cancelled || token.IsCancellationRequested) return (BenchOutcome.Cancelled, failed, "");
        if (failed.Count > 0) return (BenchOutcome.CoreError, failed, "");
        if (crashed) return (BenchOutcome.Crash, failed, "");
        if (errorLine != null)
            return (BenchOutcome.Failed, failed, L.F("y-cruncher reported a computation error: {0}", errorLine.Trim()));

        if (reachedEnd && spotGood && returncode == 0)
            return (BenchOutcome.Pass, failed, "");

        string full = ycLog.ToString();
        string tail = full.Length > 500 ? full[^500..] : full;
        Log.Write($"yc_log: {tail}");
        string why = !reachedEnd ? L.T("y-cruncher did not finish")
                   : !spotGood ? L.T("no passing result check (Spot Check: Good through …)")
                   : L.T("unexpected y-cruncher exit code");
        return (BenchOutcome.Failed, failed, $"{why} (code={returncode})");
    }
}
