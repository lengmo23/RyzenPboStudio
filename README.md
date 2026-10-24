# AMD Ryzen PBO Studio

English | [简体中文](README_zh_cn.md)

A PBO tuning and Curve Optimizer undervolt testing tool for AMD Ryzen 5000 / 7000 / 9000 series processors.

![Main page](pbo.png)

![Testing page](testing.png)

> The program's interface is in Chinese.

## Features

### Live monitoring

The title bar shows the CPU, motherboard and BIOS, memory, graphics card and OS. Below it, a per-core
monitor stays at the top of the window, split by CCD:

- Per-core CPPC ranking (gold / silver core marks), CO value, effective frequency, frequency and VID
- Average load and hotspot temperature of each CCD
- BCLK (shown as BCLK/BCLK2 with asynchronous BCLK), TEL/VID, Vdroop, THM, TDC, EDC, PPT and Fmax

Per-core frequency is taken from the first available source:

1. The per-core frequency reported by the SMU in the PM Table, scaled by BCLK
2. The HW P-state snapshot MSR `0xC0010293`, when the PM Table does not provide it

When the PM Table provides per-core frequency, effective frequency and load are read from it too, without
core-pinned counter reads, so idle cores are not woken up.

### Manual tuning

- **Curve Optimizer**: per-core offsets, with save and load
- **Curve Shaper**: 5 frequency points × 3 temperature points
- **PBO limits**: FMax, PPT, EDC, TDC and PBO Scalar (1X–10X) applied in one click; items the CPU does
  not support are disabled
- **Apply at boot**: when checked, the program starts at every Windows logon and applies the saved CO /
  Curve Shaper (FMax, PPT, EDC, TDC and Scalar are left alone). Turning it on asks you to confirm the
  settings have passed a stress test; if the system restarts abnormally shortly after applying, the next
  boot skips applying to avoid a crash loop

### Single Core Boost and voltage alignment

Both need AVX2 / FMA and are on the **TESTING** page:

- **Single Core Boost**: runs a single-core load on each core in turn and measures its frequency,
  effective frequency, VID and per-core voltage under the current CO. It only measures and changes nothing
- **Voltage alignment**: sets every core's CO to 0 and measures each core, then in each CCD takes the
  highest-CPPC core needing the least voltage at the same frequency as the reference and adds negative
  offset to cores that need more voltage. It then shifts each CCD as a whole to find the frequency
  ceiling, fine-tunes cores that fall behind and rechecks all cores. The result only fills the CO
  editor and is not saved; it must pass a stress test before you use it

### Automatic undervolt testing

y-cruncher is used as the stress engine. Three test types are available:

| Type       | Description                                                  |
| ---------- | ------------------------------------------------------------ |
| Single     | One of VT3, BKT, SVT, BBP, SFTv4, SNT, FFTv4, N63 or YC018   |
| Sequential | VT3 → BKT → SVT one after another, 20 / 10 / 10 rounds by default |
| Combined   | VT3, BKT and SVT together, 10 rounds by default              |

The duration of each round is adjustable (120 seconds by default), and so is the round count of a single test.

YC018 is a memory-intensive Pi benchmark whose size follows installed memory: 10b at 48 GB or more, 5b at
32 GB or more, 2.5b below that. It always uses all cores; the round count is the number of benchmark
runs and the round duration does not apply.

Test scope:

| Scope      | Description                                        |
| ---------- | -------------------------------------------------- |
| All cores  | Stress all cores together                          |
| Custom     | Pick the cores to stress                           |
| Single CCD | Stress one CCD only (multi-CCD CPUs only)          |
| Each CCD   | Run the full test on each CCD in turn (multi-CCD CPUs only) |

When only one CCD is stressed, it gets the whole power budget and runs at higher clocks than under an
all-core load, so the result does not carry over directly to all-core use.

Offset adjustment:

- **Auto**: when y-cruncher reports an error, the failing core's offset is backed off by one step (+2)
  and the whole round is rerun until it passes
- **Manual**: on an error, the test only shows a notice and stops, and no setting is changed

### Crash recovery

Every offset set is written to disk before it is applied to the CPU. If the system freezes or loses
power during a test, reopen the program and click Start: it reads the offsets in use before the crash,
backs every core off by one step, and continues from where it stopped.

If you applied CO values by hand after the crash, the test continues with your values and does not back off.

Starting a test registers a logon task. If the system crashes mid-test, the program starts again after
you log on to Windows and resumes the test after a 30-second countdown, which you can cancel. After 10
consecutive crashes following an automatic resume it stops resuming and leaves the rest to you. For fully
unattended runs, enable automatic logon in Windows.

### Online update

New versions on GitHub are checked at startup, or manually from the About dialog behind the i button in
the title bar. If GitHub is unreachable, several download mirrors are tried in turn. Updates never touch
`logs\` or `profiles\`.

## Requirements

| Item      | Requirement                                                                |
| --------- | -------------------------------------------------------------------------- |
| CPU       | AMD Ryzen 5000 / 7000 / 9000 series                                        |
| OS        | Windows 10 / 11 x64                                                        |
| Runtime   | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Driver    | [PawnIO](https://pawnio.eu), required to start                             |
| Privilege | Administrator                                                              |

Feature support by generation:

| Feature           | 5000 series              | 7000 series | 9000 series |
| ----------------- | ------------------------ | ----------- | ----------- |
| Curve Optimizer   | Yes                      | Yes         | Yes         |
| Curve Shaper      | No                       | No          | Yes         |
| PPT / EDC / TDC   | Yes                      | Yes         | Yes         |
| FMax              | No                       | Yes         | Yes         |
| CPPC core ranking | Needs CPPC on in BIOS    | Yes         | Yes         |

## Usage

1. Download `-full.zip` (includes y-cruncher) from [Releases](https://github.com/lengmo23/RyzenPboStudio/releases)
   and extract it anywhere. `-update.zip` is for the in-app updater and does not include y-cruncher,
   so do not use it for a first install
2. Run `AMD Ryzen PBO Studio.exe` as administrator
3. On the **AMD PBO** page, check that the monitor shows valid readings (per-core frequency and CO values)
4. On the **TESTING** page, choose the test type and scope, then click Start
5. When every test passes, the run log lists the passed CO and Curve Shaper values, and the final offsets
   are written to `profiles\final_offsets.txt`

The program creates two folders next to the EXE:

- `logs\`: a live log for each app launch and y-cruncher logs for each run
- `profiles\`: offset history, recovery state, CO / Curve Shaper profiles and calibration data

## Warning

PBO and undervolting are overclocking. Too large an offset can cause calculation errors, freezes, blue
screens or reboots, and you may lose unsaved work. Running in an unstable state for a long time can
damage hardware and may affect your warranty.

This program is distributed under the GNU General Public License v3.0 (GPL-3.0) **WITHOUT ANY WARRANTY**,
without even the implied warranty of merchantability or fitness for a particular purpose. The author
accepts no liability for any data loss, hardware damage, or other direct or indirect damages arising
from its use; you use it entirely at your own risk. Save and close all work in progress before running
a stress test.

## Building from source

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o .\bin
```

The output goes to `bin\`, and the whole folder can be distributed. If `y-cruncher v0.8.7.9547b\`
exists in the repository root, the build copies it to `bin\tools\y-cruncher\`; otherwise this step is skipped.

## Author

[@lengmo23](https://github.com/lengmo23)

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## License

This project is released under the [GNU General Public License v3.0](LICENSE). It statically links
ZenStates-Core, which is GPL-3.0, so the project as a whole must also be GPL-3.0.

## Third-party components

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) (GPL-3.0): SMU access layer; all CO,
  Curve Shaper and PBO reads and writes go through it
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool) (GPL-3.0): reference for SMU commands and PM Table offsets
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli) (GPL-3.0): source of `inpoutx64.dll`
- [y-cruncher](https://www.numberworld.org/y-cruncher/): stress engine, copyright Alexander J. Yee

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the full notices.
