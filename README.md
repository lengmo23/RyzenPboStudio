<p align="center">
  <strong>English</strong> ·
  <a href="README_zh_cn.md">简体中文</a> ·
  <a href="README_zh_tw.md">繁體中文</a> ·
  <a href="README_ja.md">日本語</a> ·
  <a href="README_ko.md">한국어</a> ·
  <a href="README_de.md">Deutsch</a> ·
  <a href="README_es.md">Español</a> ·
  <a href="README_fr.md">Français</a> ·
  <a href="README_pt_br.md">Português (Brasil)</a> ·
  <a href="README_ru.md">Русский</a>
</p>

<h1 align="center">AMD Ryzen PBO Studio</h1>

<p align="center">
  A PBO tuning, automated Curve Optimizer (CO) testing, and memory overclocking tool for AMD Ryzen 5000 / 7000 / 9000 series processors.
</p>

![PBO Tuning](pbotuning.png)

## Features

### Interface and live monitoring

The left sidebar switches between **PBO Tuning**, **Stability Test**, **Memory OC**, and **Run Log**, with the **System Overview** card (CPU, memory and live FCLK/UCLK, motherboard, BIOS & AGESA, GPU & driver, OS) and the **Apply PBO at startup** switch below.

At the top of the right panel is a per-core monitor showing:

- PPT, TDC, EDC, and THM current readings and limits
- TEL/VID, Vdroop, Fmax, and BCLK (shown as `BCLK/BCLK2` under asynchronous clocking)
- Per-CCD core telemetry: average utilization and hotspot temperature (`HOT`), plus each core's CPPC rank (gold / silver markers), frequency, effective frequency (`EFF`), CO value, and Core VID

### PBO Tuning

- **Curve Optimizer (CO)**: per-core offsets with column `+/-` stepping, plus Apply, Refresh, Save, and Load
- **Curve Shaper (CS)**: 5 frequency bands (`MIN` / `LOW` / `MED` / `HIGH` / `MAX`) × 3 temperature bands (`LOW` / `MED` / `HIGH`), supported on Ryzen 9000 series (Zen 5)
- **PBO limits & Scalar**: apply FMax, PPT, EDC, TDC, and PBO Scalar (1X–10X) together
- **Apply PBO at startup**: applies saved CO / CS profiles and the saved applied FMax, PPT, EDC, and TDC on each Windows logon. Pass a stress test before enabling it; if the system restarts abnormally after boot application, the next startup skips it automatically

### Stability Test

![Stability Test](testing.png)

#### Single Core Boost and Voltage Alignment

Both are on the right side of **Stability Test** and require AVX2 / FMA:

- **Single Core Boost**: measures each core's frequency, effective frequency, VID, and Core VID under the current CO without changing any parameters
- **Voltage Alignment**: uses the best core in each CCD as the reference, adjusts the other cores' CO so they run at similar voltage at the same frequency, and finds each CCD's single-core frequency ceiling. Results are only filled into the CO editor and not saved automatically; **stress test them before daily use**

#### Stress testing

Stress testing runs **y-cruncher** in three modes:

| Mode       | Description                                                                              |
| ---------- | ---------------------------------------------------------------------------------------- |
| Single     | Runs one of VT3, BKT, SVT, BBP, SFTv4, SNT, FFTv4, N63, or YC018                         |
| Sequential | Runs selected tests in order; defaults to `VT3 → BKT → SVT` for 20 / 10 / 10 rounds      |
| Combined   | Rotates selected tests within each round; defaults to `VT3`, `BKT`, and `SVT`, 10 rounds |

The tests, round counts, and duration per round are customizable. `YC018` is a memory-heavy Pi benchmark sized automatically from installed RAM and always uses all cores.

Test scope:

| Scope      | Description                                                  |
| ---------- | ------------------------------------------------------------ |
| All cores  | Stresses all cores together                                  |
| Custom     | Stresses only the selected physical cores                    |
| Single CCD | Stresses only the selected CCD (multi-CCD CPUs only)         |
| Each CCD   | Runs the full test on each CCD in turn (multi-CCD CPUs only) |

Single-CCD results are for reference only; verify final stability with an all-core test.

CO adjustment:

- **Auto**: when y-cruncher reports an error, maps the failing logical core back to its physical core, backs off its CO (default `+2`, configurable from `+1` to `+5`), and reruns the round until it passes
- **Manual**: displays a notice and stops the test on error without changing any parameters

### Memory OC

> Memory OC is supported by [Cat-girl-memoryOC](https://v.douyin.com/yMpmAidKqdY/) (`@纯爱出演小猪`).

![Memory OC](dramoc.png)

- **Memory SPD Info**: active memory summary, per-slot module model and capacity, DRAM ICs, and EXPO profiles read from SPD
- **Memory Write & Profiles**: **Write to BIOS**, **Revert**, **Import**, and **Export**, with the count of loaded adjustable parameters and current status below
- **Misc & Voltages**: `SOC`, `VDDP`, `VDDG CCD / IOD`, `VDD MISC`, `CPU VDDIO`, `DRAM VDD / VDDQ / VPP`, average `DIMM Temp` (SPD hub sensor) and `DIMM Power` (PMIC; hover for per-DIMM readings), `FCLK OC Mode`, and read-only items (`BGS`, `BGS Alt`, `GDM`, `Cmd2T`). SoC-side voltages read from the PM table and DRAM voltages from module PMICs, refreshing every 2 seconds
- **DRAM Timing Control**: `Memory Frequency`, `FCLK`, `UCLK DIV1 MODE`, `PowerDown Enable`, `Refresh Mode`, and full memory controller timings
- **Signal Control**: `ProcODT`, drive strengths, `Rtt`, `Nitro`, `Robust Training`, `Interleave`, and related options

#### EXPO fill and config import / export

- **EXPO**: clicking an EXPO profile in SPD fills in the corresponding EXPO preset; clicking it again restores the previous targets. Nothing is written until **Write to BIOS** is clicked
- **Export**: saves targets as a `.json` file in `dram\` (named by platform, speed, CL, and IC vendor, e.g. `DESKTOP-DDR5-6400-CL28-Hynix.json`). Each entry holds the `AMD_ACPI` function name and either `"auto": true` or the raw BIOS `value` (`label` and `display` are for reading only):

```json
{
  "format": "RyzenPboStudio.MemoryConfig",
  "version": 1,
  "source": "targets",
  "memoryType": "DDR5",
  "settings": [
    { "function": "Set Mem Clock", "label": "Memory Frequency", "auto": false, "value": 3200, "display": "DDR5-6400" },
    { "function": "Set VDDIO", "label": "DRAM VDD (V)", "auto": false, "value": 1400, "display": "1.400" },
    { "function": "Set TrrdL", "label": "TrrdL", "auto": true, "display": "Auto" }
  ]
}
```

- **Import**: supports `RyzenPboStudio.MemoryConfig` files and **Cat-girl memoryOC** memory configs (standalone memory config or the memory section of a combined profile). `Auto` entries reset those targets to the current BIOS setting; items unsupported by the motherboard BIOS are listed in the status line. Click **Revert** to reset all targets back to the current BIOS values

#### Writing to BIOS

- Settings are written through the motherboard's `AMD_ACPI` WMI interface. The software cannot write `Auto` values
- **Write to BIOS** is locked by default; enable **Expert mode** in **Settings** first
- `CPU VDDIO` is capped at `1.500 V` and must have an explicit target before writing; it cannot stay on Auto
- The confirmation dialog flags clearly abnormal targets (at BIOS min/max, or ≥2× / ≤0.5× the current value). Before writing, the program checks firmware function IDs and key current values, backs up current BIOS settings to `profiles\memory_backup_<time>.json` (re-importable via **Import**), sends commands one by one (stopping on error), reads each item back, and logs raw commands and replies to `logs\acpi-command-trace.jsonl`
- Changes **take effect after a reboot and memory training**. **Wrong settings can cause a black screen and prevent booting**: desktops need a CMOS clear, while laptops usually have no external reset button and may require opening the chassis to disconnect the main battery and CMOS battery. The program cannot recover automatically

### Run Log and crash recovery

![Run Log](logging.png)

- **Run Log**: view the full log on **Run Log** (also mirrored in the compact panel on **PBO Tuning**), recording y-cruncher iterations and speeds, logical-to-physical core error mapping, CO backoff steps, and BIOS write records; each launch is saved under `logs\`
- **Crash recovery**: if the system freezes or loses power mid-test, the program starts automatically after reboot, restores PBO limits and Curve Shaper, backs off CO by one step (or keeps manual CO if you edited it after the crash), and resumes from the interrupted stage. Recovery can be canceled at any time. Enable Windows automatic logon for unattended overnight tests

### Settings and languages

Click the **gear icon** at the bottom of the left sidebar to open **Settings**:

- **General**: language, confirm before exit, and **Expert mode** (skips routine test, recovery, and boot-apply prompts and unlocks **Write to BIOS**)
- **Updates**: check for updates at startup
- **Monitor**: refresh interval (`100–10000 ms`, default `500 ms`)
- **Data**: shortcuts to open `profiles` and `logs`

English is built in, and `lang\` includes 9 translations (`zh-Hans`, `zh-Hant`, `ja`, `ko`, `de`, `es`, `fr`, `pt-BR`, `ru`). By default the UI follows the Windows display language and falls back to English. Each `<culture>.ini` uses `English text=translation` (keeping placeholders like `{0}`); to add a language, copy a file, rename it to the culture code, and update `name=` under `[meta]`. Language changes apply after a restart.

## Requirements

| Item      | Requirement                                                                                      |
| --------- | ------------------------------------------------------------------------------------------------ |
| CPU       | AMD Ryzen 5000 / 7000 / 9000 series                                                              |
| OS        | Windows 10 / 11 x64                                                                              |
| Runtime   | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (not needed for `-net8contained.zip`) |
| Driver    | [PawnIO](https://pawnio.eu) (required; the program will not start without it)                    |
| Privilege | Administrator                                                                                    |

Feature support by generation:

| Feature           | 5000 series (Zen 3)           | 7000 series (Zen 4) | 9000 series (Zen 5) |
| ----------------- | ----------------------------- | ------------------- | ------------------- |
| Curve Optimizer   | Yes                           | Yes                 | Yes                 |
| Curve Shaper      | No                            | No                  | Yes                 |
| PPT / EDC / TDC   | Yes                           | Yes                 | Yes                 |
| FMax              | No                            | Yes                 | Yes                 |
| CPPC core ranking | Requires CPPC enabled in BIOS | Yes                 | Yes                 |

## Usage

1. Download a zip from [Releases](https://github.com/lengmo23/RyzenPboStudio/releases) and extract it to any folder:
   - **`-lite.zip`**: includes y-cruncher; requires .NET 8 Desktop Runtime installed
   - **`-net8contained.zip`**: bundles .NET 8 Runtime and y-cruncher; runs directly without installing .NET 8
   - `-update.zip` and `-net8patch-rt*.zip` are only for in-app updates and do not include y-cruncher
2. Install [PawnIO](https://pawnio.eu), then run `AMD Ryzen PBO Studio.exe` as administrator
3. On **PBO Tuning**, confirm that per-core frequencies, CO values, and power readings look normal
4. Switch to **Stability Test**, choose the mode, scope, and CO adjustment mode, then click **Start**
5. Once all tests pass, **Run Log** lists the passing CO and CS values, and the final CO is also written to `profiles\final_offsets.txt`
6. To tune memory, switch to **Memory OC**, edit or import targets (or fill an EXPO preset), enable **Expert mode** in **Settings**, click **Write to BIOS**, and reboot for memory training

The program uses three folders in the same directory:

- `logs\`: per-launch run logs, y-cruncher logs, and `AMD_ACPI` probe/write records
- `profiles\`: CO history, recovery state, CO / CS configs, calibration data, settings, and BIOS memory backups
- `dram\`: default folder for memory OC config import / export (bundled presets also live here if included)

## Warning

Both PBO and memory overclocking affect system stability. Improper settings can cause calculation errors, freezes, BSODs, reboots, loss of unsaved data, or a black screen that prevents booting (requiring a CMOS clear on desktops, or disconnecting the main and CMOS batteries on laptops). Running in an unstable state for extended periods can damage hardware and may affect your warranty. **Save your work before testing, do not use the PC during a test, and check all target values before writing to BIOS.**

This program is distributed under the GNU General Public License v3.0 (GPL-3.0) **WITHOUT ANY WARRANTY**, without even the implied warranty of merchantability or fitness for a particular purpose. The author accepts no liability for any data loss, hardware damage, or other direct or indirect damages arising from its use; you use it entirely at your own risk.

## Building from source

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\memoc
```

The build output is written to `memoc\` and the entire folder can be distributed as is (not a single-file bundle). If `y-cruncher v0.8.7.9547b\` or `dram-presets\` exist in the repository root, they are copied to `tools\y-cruncher\` and `dram\` inside the publish folder; otherwise those steps are skipped.

## Author and credits

- **Developer**: [@lengmo23](https://github.com/lengmo23) ([@冷漠OC](https://v.douyin.com/6VGnQBTxwIQ/))
- **Co-developer (Memory OC)**: [@纯爱出演小猪](https://v.douyin.com/yMpmAidKqdY/) (Cat-girl-memoryOC)
- **Special Thanks**: `@古腾古腾咕咚咚` (guidance and suggestions), `@阿肝` (feature ideas and testing), `@Yee` (core testing)

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## Buy Me a Coffee

AMD Ryzen PBO Studio is an open-source tool I develop independently in my spare time, and every feature has gone through countless rounds of refinement. If it has saved you time on tuning or helped you get more performance out of your PC, you are welcome to buy me a coffee! Every contribution helps me keep maintaining and improving the project, and helps more enthusiasts get a better tuning tool.

Sponsorship is entirely voluntary and does not affect any feature. The same payment codes are also available from the coffee-cup button at the bottom left of the program.

<p align="center">
  <img src="Alipay.png" alt="Alipay" height="400">
  <img src="Wechat.png" alt="WeChat Pay" height="400">
</p>

## License

This project is released under the [GNU General Public License v3.0](LICENSE). Because it statically links ZenStates-Core, which is also licensed under GPL-3.0, the project as a whole must be distributed under GPL-3.0.

## Third-party components

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) (GPL-3.0): Ryzen SMU access library providing read and write interfaces for CO, CS, PM Table, and PBO parameters
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool) (GPL-3.0): reference implementation for SMU command definitions and PM Table layouts
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli) (GPL-3.0): provides the `inpoutx64.dll` hardware access component
- [y-cruncher](https://www.numberworld.org/y-cruncher/): stress test engine, copyright Alexander J. Yee
- [PawnIO](https://pawnio.eu): required kernel driver installed separately by the user

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for full license notices.
