# RyzenPboStudio — 实现说明

主程序（.NET 8 WinForms）的内部设计笔记。面向使用者的说明见仓库根目录的 `README.md`。

## 为什么是 C#

项目本来就强制依赖 .NET 8，用 C# 写 GUI 不新增任何运行时依赖。发布为一个文件夹
（EXE + 随行依赖），第三方工具以内嵌资源随程序集分发，用户无需手工摆放工具目录。

## 实现要点

1. **不使用 `wmic`** —— `wmic` 已在新版 Windows 11 中移除。改为：
   - CPU 厂商/型号 → 读注册表 `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0`
   - 物理核心数 → `GetLogicalProcessorInformation`（Win32 API）
   - （事件日志查询仍用 `wevtutil`，它没有被弃用）
2. **内嵌工具包只放 `inpoutx64.dll`** —— 首次运行释放到受保护的
   `%ProgramData%\RyzenPboStudio\Tools\<版本>`；后续启动逐文件校验 SHA-256 后直接复用。
   程序以管理员权限创建该缓存并限制普通用户写入，资源损坏时从 EXE 原子重建。
   之所以不把它放到 EXE 旁边，是因为那是用户可写目录，会引入 DLL 劫持风险。
   包版本号（`build-tool-bundle.ps1` 的 `$bundleVersion`）即释放目录名，
   **包内容变化时必须同步改它**，否则会复用旧缓存。
3. **y-cruncher 随发布目录分发，不内嵌** —— 它有 46MB 且是以独立进程运行的外部工具，
   内嵌只会让程序集膨胀并在 `%ProgramData%` 里多存一份。发布时由 csproj 的
   `CopyYCruncherToPublish` 目标拷到 `tools\y-cruncher\`；开源仓库不含它，
   该目标自动跳过，运行时由 `YCruncher.FindExe()` 在 `tools\` 下查找。
4. **`inpoutx64.dll` 是硬依赖，不可移除** —— `Cpu` 的字段初始化器 `readonly IODriver io = new IODriver()`
   在每次 `new Cpu()` 时无条件执行，而 `IODriver` 构造函数无条件 `LoadDll("inpoutx64.dll")` 且失败即抛异常。
   它同时支撑 `AMD_MMIO`，也就是 `GetBclk()`。PawnIO 负责 MSR/SMU，InpOut 负责 MMIO/端口 IO，两条通道都在用。

## 抗死机的负压持久化

CPU 负压欠压过头会直接冻屏/死机，用户只能强制断电重启。而 Curve Optimizer 负压是
SMU 实时写入的、**断电即丢**，重启后 CPU 回到 BIOS 默认——所以「崩溃前的负压」唯一来源
是程序自己写在磁盘上的记录。为此做了三件事（核心原则：**先落盘，再下发**）：

1. **强制落盘**：所有状态写入走 `DurableIO`（`FileOptions.WriteThrough` + `Flush(true)` +
   原子替换），数据真正写到物理磁盘，而非停在系统缓存里被断电清掉。
2. **应用历史日志** `applied_offsets.ndjson`（NDJSON，每行一组负压）：每次下发负压到 CPU
   **之前**先在此追加落盘。追加式天然抗崩溃——断电最多损坏最后一行，读取时自动跳过取前一条。
3. **脏标记** `test_in_progress`：测试开始时落盘，正常结束/退出时删除。下次启动若它还在，
   即判定上次异常中断 → 读 `applied_offsets.ndjson` 最后一条 = 崩溃前负压 → 所有核心 +2
   回退一档后继续。（算法只朝更安全方向走，单调收敛。）

运行时产物集中在 EXE 同目录下的两个子文件夹，不再散落在根目录：

- `profiles\`：`applied_offsets.ndjson`（负压历史）、`undervolt_state.json`（恢复用运行配置）、
  `test_in_progress`（脏标记）、`final_offsets.txt`、`co_profile.json`、`cs_state.json`、`tel_calib.json`
- `logs\`：`y-cruncher_<yyyyMMdd_HHmmss>.log`（每轮压测一份）、手动导出的运行日志

`Workspace.EnsureLayout()` 在 `Program.Main` 最开始创建这两个目录，并把旧版本遗留在根目录的
同名文件迁移进去。**该调用必须早于任何状态读取**——否则从旧版本升级后读不到上次崩溃的
恢复点，断电恢复会静默失效。迁移时若目标已存在则保留新的，不覆盖。

## 界面结构

窗口分三段：顶部常驻 `MonitorView`（每核监控），中部在 **AMD PBO** 与 **TESTING** 两页间切换，
底部为导航条。

- **AMD PBO 页**：左列 CO 编辑 | Curve Shaper 编辑 + PBO 参数条，右列运行日志
- **TESTING 页**：左列测试设置，右列运行日志，底部状态条（右端显示版本与构建日期）

两页各持有一个日志 TextBox（一个控件无法同时挂在两页），由 `OnLogTimer` 镜像写入，内容始终一致。

## 其他

- 进程清理用 `Process.Kill(entireProcessTree:true)`，对 Zen4 / Zen5 的 worker 子进程同样有效。
- 崩溃检测使用脏标记而非事件日志；事件日志仅用于在日志里补充「疑似死机/断电」的说明。
- 压测判定逻辑：报错核心正则匹配、逻辑→物理核心映射、每错 +2 重跑整轮。
