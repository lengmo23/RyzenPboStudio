# AMD Ryzen PBO Studio

[English](README.md) | 简体中文

面向 AMD Ryzen 5000 / 7000 / 9000 系处理器的 PBO 调节与 Curve Optimizer（CO）自动测试工具。

![主界面](pbo.png)

![测试页](testing.png)

## 功能

### 实时监控

标题栏显示 CPU、主板与 BIOS、内存、显卡及操作系统信息。标题栏下方常驻每核监控面板，按 CCD 分栏显示：

- 每核 CPPC 排名（金 / 银核标记）、CO 值、有效频率、频率和 Core VID
- 各 CCD 的平均占用率与热点温度
- BCLK（异步外频时显示为 BCLK/BCLK2）、TEL/VID、Vdroop、THM、TDC、EDC、PPT 和 Fmax

### 手动调参

- **CO**：按物理核心设置偏移值，支持保存与载入配置
- **Curve Shaper（CS）**：5 个频率档 × 3 个温度档
- **PBO 限制**：一键写入 FMax、PPT、EDC、TDC 和 PBO Scalar（1X–10X），当前 CPU 不支持的项目自动禁用
- **开机自动应用**：每次登录 Windows 时自动写入已保存的 CO / CS（不修改 FMax、PPT、EDC、TDC 和 Scalar）。
  启用前请确认该组设置已通过压力测试；若应用后系统异常重启，下次开机将自动跳过应用

### Single Core Boost 与电压对齐

两项功能均位于 **TESTING** 页，要求 CPU 支持 AVX2 / FMA：

- **Single Core Boost**：逐核测量当前 CO 下的频率、有效频率、VID 与 Core VID，仅做测量，不修改任何参数
- **电压对齐**：以每个 CCD 中体质最佳的核心为基准，自动调整其余核心的 CO，使各核心在同频下的电压趋于一致，
  并探明单核频率上限。结果仅填入 CO 编辑器，不会自动保存，**使用前必须通过压力测试**

### CO 自动测试

压力测试引擎为 y-cruncher，支持三种测试方式：

| 方式 | 说明                                                                |
| ---- | ------------------------------------------------------------------- |
| 单项 | 从 VT3、BKT、SVT、BBP、SFTv4、SNT、FFTv4、N63、YC018 中任选一项     |
| 顺序 | 按设定顺序逐项运行，默认 VT3 → BKT → SVT，各 20 / 10 / 10 轮       |
| 组合 | 每一轮内依次轮换运行所选项目，默认 VT3、BKT、SVT，共 10 轮          |

各测试方式的项目、轮数与每轮时长均可自定义。YC018 为内存密集型的 Pi 基准测试，规模随内存容量自动选择，
始终使用全部核心。

测试范围：

| 范围     | 说明                                           |
| -------- | ---------------------------------------------- |
| 全部核心 | 所有核心同时参与压测                           |
| 自定义   | 手动勾选参与压测的物理核心                     |
| 单个 CCD | 仅压测指定的 CCD（仅多 CCD 处理器）            |
| 逐 CCD   | 依次对每个 CCD 完整测试一遍（仅多 CCD 处理器） |

单 CCD 测试结果仅供参考，最终稳定性请以全核测试为准。

CO 调整方式：

- **自动模式**：y-cruncher 报错时，自动回退出错核心的 CO（默认 +2，步长可自定义）并重新测试，直至通过
- **手动模式**：报错时仅提示并停止测试，不改动任何参数

### 死机恢复

测试中途死机或断电重启后，程序会随开机自动启动，回退 CO 后继续未完成的测试，期间可随时取消。
如需完全无人值守运行，请在 Windows 中开启自动登录。

## 运行要求

| 项目     | 要求                                                                       |
| -------- | -------------------------------------------------------------------------- |
| 处理器   | AMD Ryzen 5000 / 7000 / 9000 系                                            |
| 操作系统 | Windows 10 / 11 x64                                                        |
| 运行库   | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 驱动     | [PawnIO](https://pawnio.eu)（必需，未安装时程序无法启动）                  |
| 权限     | 管理员                                                                     |

各代处理器的功能支持情况：

| 功能            | 5000 系               | 7000 系 | 9000 系 |
| --------------- | --------------------- | ------- | ------- |
| CO              | 支持                  | 支持    | 支持    |
| CS              | 不支持                | 不支持  | 支持    |
| PPT / EDC / TDC | 支持                  | 支持    | 支持    |
| 修改 FMax       | 不支持                | 支持    | 支持    |
| CPPC 核心排名   | 需在 BIOS 中开启 CPPC | 支持    | 支持    |

## 使用方法

1. 从 [Releases](https://github.com/lengmo23/RyzenPboStudio/releases) 下载 `-full.zip`（含 y-cruncher），
   解压到任意目录。`-update.zip` 供程序内自动更新使用，不含 y-cruncher，请勿用于首次安装
2. 以管理员身份运行 `AMD Ryzen PBO Studio.exe`
3. 在 **AMD PBO** 页确认监控数据正常，每核频率与 CO 值均有读数
4. 切换到 **TESTING** 页，选择测试方式与范围，点击「开始测试」
5. 全部测试通过后，运行日志会列出通过测试的 CO 与 CS 数值，最终 CO 同时写入 `profiles\final_offsets.txt`

程序运行后会在其所在目录生成两个文件夹：

- `logs\`：每次启动的运行日志，以及每次 y-cruncher 运行的日志
- `profiles\`：CO 历史、恢复状态、CO / CS 配置及校准数据

## 风险提示

PBO 与 CO 调节属于超频操作，CO 与 CS 的设置都会影响系统稳定性。设置不当可能导致运算错误、死机、蓝屏或自动重启，
并可能造成未保存数据的丢失；长期在不稳定状态下运行可能损伤硬件，也可能影响产品保修。
**测试前请保存相关资料，测试期间请勿操作电脑。**

本程序依据 GNU 通用公共许可证第 3 版（GPL-3.0）发布，**不附带任何形式的担保**，包括但不限于对适销性或特定用途适用性的默示担保。因使用本程序导致的任何数据丢失、硬件损坏或其他直接、间接损失，作者概不负责，相关风险由使用者自行承担。

## 从源码构建

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o .\bin
```

构建产物输出到 `bin\`，整个目录可直接分发。若仓库根目录存在 `y-cruncher v0.8.7.9547b\`，构建时会将其复制到
`bin\tools\y-cruncher\`；不存在时跳过此步骤。

## 作者

[@lengmo23](https://github.com/lengmo23)

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## 请作者喝杯咖啡

AMD Ryzen PBO Studio 是我利用业余时间独立开发的开源工具，每一个功能都经过日夜打磨与反复迭代。
如果它帮你节省了反复调参的时间，或让你的电脑获得了更好的性能，欢迎请我喝杯咖啡！
你的每一份支持，都是我持续维护和更新的动力，也能让更多玩家用上更好的调参工具。

赞助完全自愿，不影响任何功能的使用。程序标题栏的咖啡杯按钮中也提供相同的收款码。

<p align="center">
  <img src="Alipay.png" alt="支付宝" height="400">
  <img src="Wechat.png" alt="微信支付" height="400">
</p>

## 许可

本项目依据 [GNU General Public License v3.0](LICENSE) 发布。由于项目静态链接了同为 GPL-3.0 的 ZenStates-Core，
整个项目也必须以 GPL-3.0 发布。

## 第三方组件

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core)（GPL-3.0）：Ryzen SMU 访问库，提供 CO、CS 与 PBO 参数的读写接口
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool)（GPL-3.0）：SMU 命令定义与 PM Table 布局的参考实现
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli)（GPL-3.0）：提供 `inpoutx64.dll` 硬件访问组件
- [y-cruncher](https://www.numberworld.org/y-cruncher/)：压力测试引擎，版权归 Alexander J. Yee 所有

完整声明见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
