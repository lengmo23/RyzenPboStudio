using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RyzenPboStudio;

/// <summary>profiles\autostart.json: boot-apply switch.</summary>
internal sealed class AutoStartSettings
{
    [JsonPropertyName("boot_apply")] public bool BootApply { get; set; }
}

/// <summary>profiles\auto_resume.json: consecutive auto-resume count and whether the user cancelled auto-resume.</summary>
internal sealed class AutoResumeState
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("declined")] public bool Declined { get; set; }
}

/// <summary>
/// Logon autostart via a scheduled task, plus its state files. The task exists iff boot-apply is on,
/// or a test is in progress / interrupted and auto-resume was not cancelled.
/// </summary>
internal static class AutoStart
{
    public const string Arg = "--autostart";
    public const string TaskName = "AMD Ryzen PBO Studio Autostart";
    public const int MaxAutoResumes = 10;
    public const int ResumeCountdownSeconds = 30;

    /// <summary>Uptime after boot-apply before the boot-loop flag is cleared.</summary>
    private static readonly TimeSpan BootApplyStableTime = TimeSpan.FromMinutes(3);

    private static string SettingsFile => Path.Combine(Workspace.ProfilesDir, "autostart.json");
    private static string ResumeFile => Path.Combine(Workspace.ProfilesDir, "auto_resume.json");
    private static string BootApplyPendingFlag => Path.Combine(Workspace.ProfilesDir, "boot_apply_pending");

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    private static readonly object SyncLock = new();
    private static System.Threading.Timer? _stableTimer;

    // ── Settings and resume count ───────────────────────────────────────────

    public static bool BootApplyEnabled => Load<AutoStartSettings>(SettingsFile)?.BootApply == true;

    public static bool SetBootApply(bool enabled) =>
        Save(SettingsFile, new AutoStartSettings { BootApply = enabled });

    public static AutoResumeState LoadResume() => Load<AutoResumeState>(ResumeFile) ?? new AutoResumeState();

    public static void ResetResumeCount()
    {
        var s = LoadResume();
        if (s.Count == 0) return;
        s.Count = 0;
        Save(ResumeFile, s);
    }

    /// <summary>Increments and persists the resume count; returns -1 if it could not be persisted (the loop cap would not hold).</summary>
    public static int IncrementResumeCount()
    {
        var s = LoadResume();
        s.Count++;
        return Save(ResumeFile, s) ? s.Count : -1;
    }

    public static void DeclineResume()
    {
        var s = LoadResume();
        if (s.Declined) return;
        s.Declined = true;
        Save(ResumeFile, s);
    }

    /// <summary>A test started: re-enable auto-resume for it and make sure the task exists before load begins.</summary>
    public static string? OnTestStarted()
    {
        var s = LoadResume();
        if (s.Declined)
        {
            s.Declined = false;
            Save(ResumeFile, s);
        }
        return Sync();
    }

    // ── Boot-apply loop guard ───────────────────────────────────────────────

    /// <summary>The last boot-apply was not followed by 3 minutes of uptime or a clean exit.</summary>
    public static bool BootApplyPending => File.Exists(BootApplyPendingFlag);

    /// <summary>Durably sets the flag before applying and clears it after 3 minutes. Returns false if it could not be written.</summary>
    public static bool MarkBootApplyPending()
    {
        try
        {
            DurableIO.WriteAllText(BootApplyPendingFlag, DateTime.Now.ToString("O"));
        }
        catch (Exception e)
        {
            Log.Write($"写入开机应用保护标记失败: {e.Message}", "WARN");
            return false;
        }
        _stableTimer?.Dispose();
        _stableTimer = new System.Threading.Timer(_ => ClearBootApplyPending(), null,
            BootApplyStableTime, Timeout.InfiniteTimeSpan);
        return true;
    }

    public static void ClearBootApplyPending()
    {
        try { if (File.Exists(BootApplyPendingFlag)) File.Delete(BootApplyPendingFlag); } catch { }
    }

    // ── Scheduled task ──────────────────────────────────────────────────────

    /// <summary>Creates or deletes the task to match current state. Returns null on success, else a short status text (details are logged).</summary>
    public static string? Sync()
    {
        lock (SyncLock)
        {
            try
            {
                bool want = BootApplyEnabled || (Workspace.WasInterrupted() && !LoadResume().Declined);
                bool exists = TaskExists();
                if (want)
                    return exists && TaskMatchesCurrentExe() ? null : Register();
                return exists ? Unregister() : null;
            }
            catch (Exception e)
            {
                Log.Write($"同步开机自启任务失败: {e.Message}", "WARN");
                return "开机自启任务同步失败，详见日志";
            }
        }
    }

    private static bool TaskExists() =>
        ProcUtil.Capture(Schtasks, new[] { "/Query", "/TN", TaskName }, null, 15000).code == 0;

    /// <summary>
    /// Whether the registered task points at this EXE. An unreadable task file counts as a match,
    /// since this instance may have been started by that very task and should not re-register it.
    /// </summary>
    private static bool TaskMatchesCurrentExe()
    {
        try
        {
            string file = Path.Combine(Environment.SystemDirectory, "Tasks", TaskName);
            string xml = File.ReadAllText(file);
            string exe = Environment.ProcessPath ?? "";
            return xml.Contains($"<Command>{SecurityElement.Escape(exe)}</Command>", StringComparison.OrdinalIgnoreCase)
                && xml.Contains($"<Arguments>{Arg}</Arguments>", StringComparison.Ordinal);
        }
        catch
        {
            return true;
        }
    }

    private static string? Register()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取程序路径");
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法获取当前用户 SID");

        // Admin-only directory, so a non-elevated process cannot swap the XML before schtasks reads it
        string dir = Path.GetDirectoryName(ToolBundle.RootDirectory)!;
        string xmlPath = Path.Combine(dir, $"autostart-{Guid.NewGuid():N}.xml");
        int code;
        string output;
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exe, sid), Encoding.Unicode);
            (code, output) = ProcUtil.Capture(Schtasks,
                new[] { "/Create", "/XML", xmlPath, "/TN", TaskName, "/F" }, null, 15000);
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }

        if (code != 0)
        {
            Log.Write($"注册开机自启任务失败（schtasks 返回 {code}）: {output.Trim()}", "WARN");
            return "开机自启任务注册失败，详见日志";
        }
        return null;
    }

    private static string? Unregister()
    {
        var (code, output) = ProcUtil.Capture(Schtasks, new[] { "/Delete", "/TN", TaskName, "/F" }, null, 15000);
        if (code != 0)
        {
            Log.Write($"移除开机自启任务失败（schtasks 返回 {code}）: {output.Trim()}", "WARN");
            return "开机自启任务移除失败，详见日志";
        }
        return null;
    }

    /// <summary>
    /// Logon trigger delayed 30 s (PawnIO driver and desktop ready), elevated in the interactive session.
    /// PT0S disables the default 72 h kill; Priority 4 is normal (the default 7 is below normal).
    /// </summary>
    private static string BuildTaskXml(string exe, string sid)
    {
        string user = SecurityElement.Escape(sid);
        string cmd = SecurityElement.Escape(exe);
        string workDir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? Workspace.BaseDir);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>AMD Ryzen PBO Studio</Author>
                <Description>登录后自动启动 AMD Ryzen PBO Studio：应用已保存的 CO/CS，或恢复异常中断的测试。</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT30S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{cmd}</Command>
                  <Arguments>{Arg}</Arguments>
                  <WorkingDirectory>{workDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    // ── JSON I/O ────────────────────────────────────────────────────────────

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Log.Write($"读取 {Path.GetFileName(path)} 失败，按默认值处理: {e.Message}", "WARN");
        }
        return null;
    }

    private static bool Save<T>(string path, T value)
    {
        try
        {
            DurableIO.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"保存 {Path.GetFileName(path)} 失败: {e.Message}", "WARN");
            return false;
        }
    }
}

/// <summary>
/// Asks whether to continue an interrupted test. With a countdown (logon autostart) it continues on its own when the
/// countdown ends; without one it waits for the user. Continue returns OK; Not now or closing returns Cancel.
/// </summary>
internal sealed class ResumePromptDialog : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly PillButton _continueBtn;
    private int _remaining;

    /// <param name="test">Test mode and the stage to resume, shown prominently.</param>
    /// <param name="scope">Cores the test covers.</param>
    /// <param name="manualTakeover">Offsets were taken over manually; recovery keeps them without +StepOnError.</param>
    /// <param name="attempt">1-based consecutive auto-resume number; null for no countdown.</param>
    public ResumePromptDialog(string test, string scope, bool manualTakeover, int? attempt)
    {
        const int width = 440;
        _remaining = attempt != null ? AutoStart.ResumeCountdownSeconds : 0;

        Text = "检测到中断的测试";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;   // at logon the main window often ends up behind other apps
        BackColor = Theme.Surface;
        ForeColor = Theme.TextHi;
        Font = new Font(Theme.FontFamily, 9.5F);

        Label Line(string text, float size, Color color, bool bold = false, int bottom = 0, Color? back = null, int max = width) => new()
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(max, 0),
            ForeColor = color,
            BackColor = back ?? Theme.Surface,
            Font = new Font(Theme.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
            Margin = new Padding(0, 0, 0, bottom),
        };

        // The interrupted test sits in an outlined card so it stands out from the question and the notes.
        const int pad = 16;
        int inner = width - 2 * pad;
        var info = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.SurfaceAlt,
            Location = new Point(pad, pad - 2),
            Margin = new Padding(0),
        };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, inner));
        info.Controls.Add(Line("中断的测试", 8.5F, Theme.TextLo, bottom: 4, back: Theme.SurfaceAlt, max: inner));
        info.Controls.Add(Line(test, 14F, Theme.TextHi, bold: true, bottom: 2, back: Theme.SurfaceAlt, max: inner));
        info.Controls.Add(Line(scope, 10F, Theme.TextHi, bottom: 10, back: Theme.SurfaceAlt, max: inner));
        info.Controls.Add(new Panel { Size = new Size(inner, 1), BackColor = Theme.Border, Margin = new Padding(0, 0, 0, 8) });
        info.Controls.Add(Line(manualTakeover ? "CO 处理：沿用手动应用的 CO 继续"
                                              : $"CO 处理：所有核心回退 +{Config.StepOnError} 后继续",
            9.5F, Theme.TextHi, back: Theme.SurfaceAlt, max: inner));
        var card = new Card
        {
            Fill = Theme.SurfaceAlt,
            Stroke = Theme.Accent,
            Radius = 8,
            Width = width,
            Margin = new Padding(0, 0, 0, 12),
        };
        card.Controls.Add(info);
        card.Height = info.GetPreferredSize(new Size(inner, 0)).Height + 2 * pad - 4;

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 18, 20, 16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        layout.Controls.Add(Line("检测到中断的测试任务，是否继续？", 12F, Theme.TextHi, bold: true, bottom: 12));
        layout.Controls.Add(card);
        if (attempt != null)
            layout.Controls.Add(Line($"连续第 {attempt}/{AutoStart.MaxAutoResumes} 次自动恢复，倒计时结束将自动继续。\n" +
                                     "完全无人值守需在 Windows 中开启自动登录。", 8.5F, Theme.TextLo, bottom: 4));

        _continueBtn = NewButton("继续测试", 132, primary: true);
        _continueBtn.DialogResult = DialogResult.OK;
        var laterBtn = NewButton("暂不继续", 96, primary: false);
        laterBtn.DialogResult = DialogResult.Cancel;
        var btnRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 10, 0, 0),
        };
        btnRow.Controls.Add(_continueBtn);
        btnRow.Controls.Add(laterBtn);
        layout.Controls.Add(btnRow);

        Controls.Add(layout);
        AcceptButton = _continueBtn;
        CancelButton = laterBtn;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        UpdateCountdown();
        _timer.Tick += (_, _) =>
        {
            if (--_remaining <= 0)
            {
                _timer.Stop();
                DialogResult = DialogResult.OK;
                return;
            }
            UpdateCountdown();
        };
    }

    private void UpdateCountdown() =>
        _continueBtn.Text = _remaining > 0 ? $"继续测试（{_remaining}）" : "继续测试";

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (_remaining > 0) _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        DarkTitleBar.Apply(Handle);
    }

    /// <summary>Same pill buttons as the main window: primary = accent red, otherwise grey.</summary>
    private static PillButton NewButton(string text, int width, bool primary) => new()
    {
        Text = text,
        Size = new Size(width, 34),
        Normal = primary ? Theme.Accent : Theme.SurfaceAlt,
        Hover = primary ? Theme.AccentHover : Theme.Border,
        ForeColor = primary ? Theme.AccentText : Theme.TextHi,
        BackColor = Theme.Surface,
        Font = new Font(Theme.FontFamily, 10F, primary ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(8, 0, 0, 0),
    };
}
