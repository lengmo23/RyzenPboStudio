# RyzenPboStudio 开发说明

主程序（.NET 8 WinForms）的内部实现说明。使用说明见仓库根目录的 `README.md`（英文）与 `README_zh_cn.md`（中文）。

## 目录与文件

| 文件              | 作用                                                                |
| ----------------- | ------------------------------------------------------------------- |
| `Program.cs`      | 启动检查：Intel、管理员权限、组件释放、PawnIO                      |
| `MainForm.cs`     | 主窗口、CO / Curve Shaper / PBO 编辑、测试编排、崩溃恢复            |
| `MonitorView.cs`  | 顶部每核监控                                                        |
| `RyzenSmu.cs`     | 经 ZenStates-Core 读写 CO、PBO 限制、Curve Shaper；PM Table 布局探测 |
| `YCruncher.cs`    | 启动 y-cruncher、解析报错核心、自动回退负压                         |
| `Core.cs`         | 配置常量、日志、状态文件、负压历史、落盘写入                        |
| `Updater.cs`      | 从 GitHub Release 检查和安装更新                                    |
| `ToolBundle.cs`   | 释放并校验内嵌的 `inpoutx64.dll`                                    |
| `SystemInfo.cs`   | CPU / 主板 / 显卡 / 内存信息，逻辑核与物理核、CCD 的映射            |
| `CppcReader.cs`   | 5000 系从内核电源事件 55 读 CPPC                                    |

## 硬件访问

- 所有硬件读写（SMU、MSR、SMN）都经 PawnIO 驱动，由 ZenStates-Core 调用。
- `inpoutx64.dll` 也是必需的：ZenStates-Core 的 `Cpu` 构造时会加载它，`GetBclk()` 等 MMIO 读取也靠它。
- 监控线程和负压读写共用一个 `Cpu` 实例，所有访问都要持有 `RyzenSmu.IoLock`，避免 SMU 邮箱并发出错。
- 负压表按**槽位**索引（含熔丝屏蔽核），y-cruncher 报的是逻辑核。换算顺序为
  逻辑核 → OS 物理核（`CoreTopology.PhysicalOf`）→ 槽位（`RyzenSmu.OsCoreToSlot`）。
- PM Table 偏移逐型号不同，不写死，由 `RyzenSmu.ProbePtLayout` 在运行时探测。

## 内嵌组件

`inpoutx64.dll` 打包成内嵌资源，首次运行释放到 `%ProgramData%\RyzenPboStudio\Tools\<版本>`，
每次启动逐文件校验 SHA-256。该目录只允许管理员写入；不放在 EXE 旁边，是为了避免 DLL 劫持。

包版本号是 `Scripts\build-tool-bundle.ps1` 里的 `$bundleVersion`，也是释放目录名。
**包内容有变化时必须同时改这个版本号**，否则会继续用旧缓存。

## y-cruncher

y-cruncher 不内嵌，发布时由 csproj 的 `CopyYCruncherToPublish` 目标从仓库根目录拷到
`bin\tools\y-cruncher\`，本地没有时自动跳过。运行时由 `YCruncher.FindExe()` 查找。

- 全部核心：用命令行 `stress` 启动。
- 限定核心：命令行不支持指定核心，改为生成配置文件，用 `LogicalCores` 指定逻辑核。
- 本程序启动的 y-cruncher 放在一个 Job Object 里（设置了 `KILL_ON_JOB_CLOSE`）。
  清理时只结束这个 Job 里的进程，不影响用户自己打开的 y-cruncher；本程序退出或被强制结束时，
  它们也会一起结束。

## 死机恢复

CO 负压断电即丢，重启后 CPU 回到 BIOS 设定，所以死机前的负压只能从程序自己写的记录里找回。
原则是**先落盘，再下发**：

1. 所有状态文件都经 `DurableIO` 写入（`WriteThrough` + `Flush(true)` + 原子替换）。
2. 每次写负压到 CPU 之前，先追加一行到 `applied_offsets.ndjson`。断电最多损坏最后一行，读取时跳过。
3. 测试开始时写脏标记 `test_in_progress`，正常结束或用户关闭时删除。下次开始测试时若它还在，
   就取 `applied_offsets.ndjson` 最后一条有效记录，所有核心加一个步长（默认 +2）后从中断的阶段继续。
4. 中断后用户手动应用过 CO（记录原因为 `manual-takeover`），则按用户的值继续，不再回退。
5. 测试开始时从 CPU 读取 FMax 与 PM Table 中的 PPT / EDC / TDC，追加一行到 `applied_pbo.ndjson`；之后每次点 PBO 栏的
   Apply（包括测试中途）再追加一行。续跑时取最后一条记录，逐项与当前值比较，
   只写回被 BIOS 改掉的项；一致的项不写（写 FMax 会扰乱电源计划状态和部分主板的核心频率限制），
   当前读不到的项也不写。

`Workspace.EnsureLayout()` 在 `Program.Main` 最开始创建 `logs\`、`profiles\`，并把旧版本放在
EXE 同目录的文件迁移进来。它必须早于任何状态读取，否则升级后会读不到恢复点。

`profiles\` 下的文件：

- `applied_offsets.ndjson`：负压历史
- `test_state.json`：恢复用的测试配置
- `applied_pbo.ndjson`：FMax / PPT / EDC / TDC 历史
- `test_in_progress`：脏标记
- `final_offsets.txt`：测试全部通过后的最终负压
- `co_profile.json`、`cs_state.json`：CO / Curve Shaper 配置
- `setting.json`：顺序 / 组合测试计划与自动模式 CO 步长
- `update_skip.txt`：用户选择跳过的版本

## 在线更新

`Updater` 读 `releases/latest`，优先下载 `-update.zip`，没有则用 `-full.zip`。下载按
GitHub 直连 → 几个加速镜像的顺序尝试，下载后用 API 给出的 SHA-256 校验（Release 没有 digest 时只核对大小），不符就换下一个源。解压校验通过后写一个
`apply-update.cmd`，等程序退出后先把安装目录备份一份，再用 `robocopy` 覆盖（排除 `logs`、`profiles`），
然后重新启动。覆盖中途失败时用备份还原并启动旧版本；所有下载源都失败时提示加入 QQ 群获取最新版本。

Release 正文会转成纯文字显示在更新提示里，只写用户能看懂的更新内容。
