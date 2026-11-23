<p align="center">
  <a href="README.md">English</a> ·
  <a href="README_zh_cn.md">简体中文</a> ·
  <strong>繁體中文</strong> ·
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
  面向 AMD Ryzen 5000 / 7000 / 9000 系列處理器的 PBO 調校、Curve Optimizer（CO）自動測試與記憶體超頻工具。
</p>

![PBO 調參](pbotuning.png)

## 功能

### 介面與即時監控

左側欄用於在 **PBO 調參**、**穩定性測試**、**記憶體超頻** 和 **執行日誌** 四個頁面間切換，下方顯示 **系統概況**（處理器、記憶體與即時 FCLK/UCLK、主機板、BIOS 與 AGESA、顯示卡驅動及系統）、**開機自動套用PBO** 開關。

右側頂部是每核監控面板，顯示：

- PPT、TDC、EDC、THM 的目前值與上限
- TEL/VID、Vdroop、Fmax 與 BCLK（非同步外頻時顯示為 `BCLK/BCLK2`）
- 依 CCD 分組的核心狀態：各 CCD 平均使用率與熱點溫度（`HOT`），以及每核 CPPC 排名（金 / 銀核標記）、頻率、有效頻率（`EFF`）、CO 值與 Core VID

### PBO 調參

- **Curve Optimizer（CO）**：依實體核心設定偏移值，支援整欄 `+/-` 調整，以及套用、重新整理、儲存與載入設定
- **Curve Shaper（CS）**：5 個頻率段（`MIN` / `LOW` / `MED` / `HIGH` / `MAX`）× 3 個溫度段（`LOW` / `MED` / `HIGH`），僅 Ryzen 9000 系列（Zen 5）支援
- **PBO 參數與 Scalar**：同時寫入 FMax、PPT、EDC、TDC 與 PBO Scalar（1X–10X）
- **開機自動套用 PBO**：開啟後每次登入 Windows 會自動寫入已儲存的 CO / CS 和已套用儲存的 FMax、PPT、EDC、TDC。啟用前請先通過壓力測試；若套用後系統異常重啟，下次開機將自動略過

### 穩定性測試

![穩定性測試](testing.png)

#### 單核測量與電壓對齊

兩項功能在 **穩定性測試** 頁右側，要求 CPU 支援 AVX2 / FMA：

- **單核測量**：逐核測出目前 CO 下的頻率、有效頻率、VID 與 Core VID，不修改任何參數
- **電壓對齊**：以每個 CCD 中體質最好的核心為基準，調整其餘核心的 CO，使各核心在同頻下的電壓趨於一致，並測出各 CCD 的單核頻率上限。結果只填入 CO 編輯器，不會自動儲存，**使用前必須通過壓力測試**

#### 壓力測試

壓力測試使用 **y-cruncher**，支援三種方式：

| 方式     | 說明                                                             |
| -------- | ---------------------------------------------------------------- |
| 單項測試 | 從 VT3、BKT、SVT、BBP、SFTv4、SNT、FFTv4、N63、YC018 中任選一項  |
| 順序測試 | 依順序逐項執行，預設 `VT3 → BKT → SVT`，各 20 / 10 / 10 輪       |
| 組合測試 | 每輪內輪換執行所選項目，預設 `VT3`、`BKT`、`SVT`，共 10 輪       |

各方式的項目、輪數與每輪時長都可自訂。`YC018` 是記憶體密集型 Pi 基準測試，規模依記憶體容量自動選擇，固定使用全部核心。

測試範圍：

| 範圍     | 說明                                           |
| -------- | ---------------------------------------------- |
| 全部核心 | 所有核心同時參與壓測                           |
| 自訂     | 手動勾選參與壓測的實體核心                     |
| 單一 CCD | 只壓測指定的 CCD（僅多 CCD 處理器）            |
| 逐 CCD   | 依序對每個 CCD 完整測試一遍（僅多 CCD 處理器） |

單 CCD 結果僅供參考，最終穩定性請以全核測試為準。

CO 調整方式：

- **自動模式**：y-cruncher 報錯時，把出錯的邏輯核心對應回實體核心，回退該核 CO（預設 `+2`，可在 `+1～+5` 間設定）並重跑整輪，直到通過
- **手動模式**：報錯時僅提示並停止測試，不更動任何參數

### 記憶體超頻

> 記憶體超頻功能由 [Cat-girl-memoryOC](https://v.douyin.com/yMpmAidKqdY/)（`@纯爱出演小猪`）支援實現。

![記憶體超頻](dramoc.png)

- **記憶體 SPD 資訊**：目前記憶體彙總、各插槽記憶體條型號與容量、顆粒廠商（ICs），以及從 SPD 讀出的 EXPO 檔位
- **記憶體寫入與設定檔**：**寫入 BIOS**、**復原改動**、**匯入設定**、**匯出設定**，下方顯示已載入的可調參數數量與目前狀態
- **電壓與其他**：`SOC`、`VDDP`、`VDDG CCD / IOD`、`VDD MISC`、`CPU VDDIO`、`DRAM VDD / VDDQ / VPP`、平均 `DIMM Temp`（SPD 溫度感測器）與 `DIMM Power`（PMIC，懸停可看單條讀數）、`FCLK OC Mode`，以及唯讀項 `BGS`、`BGS Alt`、`GDM`、`Cmd2T`。SoC 側電壓取自 PM Table，記憶體電壓取自 PMIC，每 2 秒重新整理一次
- **記憶體時序控制**：`Memory Frequency`、`FCLK`、`UCLK DIV1 MODE`、`PowerDown Enable`、`Refresh Mode`，記憶體控制器的全部時序
- **訊號控制**：`ProcODT`、驅動強度、`Rtt`、`Nitro`、`Robust Training`、`Interleave` 等選項

#### EXPO 填入與設定匯入 / 匯出

- **EXPO**：點擊 SPD 中的 EXPO 檔位，會自動填入對應的 EXPO 預設，再次點擊復原。需點擊 **寫入 BIOS** 才會下發。
- **匯出設定**：把目前目標儲存為 `.json` 檔案，預設存放在 `dram\` 目錄（依平台、頻率、CL 與顆粒命名，如 `DESKTOP-DDR5-6400-CL28-Hynix.json`）。每筆記錄包含 `AMD_ACPI` 函式名，以及 `"auto": true` 或原始 BIOS `value`（`label` 與 `display` 僅供閱讀）：

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

- **匯入設定**：支援本程式匯出的 `RyzenPboStudio.MemoryConfig` 檔案，以及 **Cat-girl memoryOC** 的記憶體設定（獨立記憶體設定或組合設定中的記憶體部分）。`Auto` 項會恢復為目前 BIOS 設定，主機板 BIOS 不支援的項目會在狀態列提示。點擊 **復原改動** 可將所有目標恢復為目前 BIOS 值

#### 寫入 BIOS

- 參數透過主機板 `AMD_ACPI` WMI 介面寫入。軟體無法下發 Auto 值
- **寫入 BIOS** 預設鎖定，需先在 **設定** 中開啟 **專家模式**
- `CPU VDDIO` 上限為 `1.500 V`，寫入前必須指定目標值，不能保持 Auto
- 確認視窗會標出明顯異常的目標（觸及 BIOS 上下限，或達到目前值的 2 倍及以上 / 一半及以下）。寫入前會校驗韌體函式 ID 與關鍵項目前值，自動將目前 BIOS 值備份到 `profiles\memory_backup_<時間>.json`（可匯入還原）；指令逐項傳送，出錯即停止，完成後逐項回讀，所有原始指令與返回記錄在 `logs\acpi-command-trace.jsonl`
- 寫入後需 **重啟並完成記憶體訓練** 才會生效。**錯誤的參數可能導致黑屏無法開機**：桌機需清除 CMOS，筆電通常沒有外部重置鍵，可能需要拆機斷開主電池與 CMOS 電池，程式無法自動恢復

### 執行日誌與當機復原

![執行日誌](logging.png)

- **執行日誌**：完整日誌在 **執行日誌** 頁檢視（**PBO 調參** 頁右側也有同步的小視窗），記錄 y-cruncher 輪次與測速、邏輯核到實體核的報錯對應、CO 回退和 BIOS 寫入記錄；每次啟動都會儲存到 `logs\`
- **當機復原**：測試中途當機或斷電重啟後，程式隨開機自動啟動，恢復 PBO 限制與 Curve Shaper，將 CO 回退一檔（若中斷後已手動改過 CO 則依手動值繼續）並從中斷階段續跑，期間可隨時取消。無人值守過夜測試請在 Windows 中開啟自動登入

### 設定與語言

點擊左側欄底部的 **齒輪圖示** 開啟 **設定**：

- **一般**：語言、退出前確認，以及 **專家模式**（略過常規測試 / 復原 / 開機套用彈窗，並解鎖 **寫入 BIOS**）
- **更新**：啟動時自動檢查更新
- **監控**：重新整理間隔（`100～10000 ms`，預設 `500 ms`）
- **資料**：快捷開啟 `profiles` 和 `logs` 目錄

程式內建英文，`lang\` 目錄自帶 9 種語言翻譯（簡體中文 `zh-Hans`、繁體中文 `zh-Hant`、日語 `ja`、韓語 `ko`、德語 `de`、西班牙語 `es`、法語 `fr`、巴西葡萄牙語 `pt-BR`、俄語 `ru`），預設跟隨 Windows 顯示語言，找不到時用英文。每個 `<語言代碼>.ini` 依 `英文原文=譯文` 編寫（需保留 `{0}` 等佔位符）；新增語言只需複製一份檔案、依語言代碼改名並修改 `[meta]` 下的 `name=`。切換語言後重啟生效。

## 運行要求

| 項目     | 要求                                                                                                              |
| -------- | ----------------------------------------------------------------------------------------------------------------- |
| 處理器   | AMD Ryzen 5000 / 7000 / 9000 系列                                                                                 |
| 作業系統 | Windows 10 / 11 x64                                                                                               |
| 執行庫   | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（下載 `-net8contained.zip` 時免安裝） |
| 驅動     | [PawnIO](https://pawnio.eu)（必需，未安裝時程式無法啟動）                                                         |
| 權限     | 系統管理員                                                                                                        |

各代處理器的功能支援情況：

| 功能            | 5000 系列（Zen 3）    | 7000 系列（Zen 4） | 9000 系列（Zen 5） |
| --------------- | --------------------- | ------------------ | ------------------ |
| Curve Optimizer | 支援                  | 支援               | 支援               |
| Curve Shaper    | 不支援                | 不支援             | 支援               |
| PPT / EDC / TDC | 支援                  | 支援               | 支援               |
| 修改 FMax       | 不支援                | 支援               | 支援               |
| CPPC 核心排名   | 需在 BIOS 中開啟 CPPC | 支援               | 支援               |

## 使用方法

1. 從 [Releases](https://github.com/lengmo23/RyzenPboStudio/releases) 下載對應壓縮檔並解壓到任意目錄：
   - **`-lite.zip`**：含 y-cruncher，需系統已安裝 .NET 8 Desktop Runtime
   - **`-net8contained.zip`**：內建 .NET 8 執行時期與 y-cruncher，未安裝 .NET 8 可直接執行
   - `-update.zip` 與 `-net8patch-rt*.zip` 僅供程式內自動更新使用，不含 y-cruncher，請勿用於首次安裝
2. 安裝 [PawnIO](https://pawnio.eu)，然後以系統管理員身分執行 `AMD Ryzen PBO Studio.exe`
3. 在 **PBO 調參** 頁確認每核頻率、CO 值和功耗讀數正常
4. 切換到 **穩定性測試** 頁，選好測試方式、範圍和 CO 調整方式，點擊「開始測試」
5. 全部通過後，**執行日誌** 會列出通過的 CO 與 CS 數值，最終 CO 同時寫入 `profiles\final_offsets.txt`
6. 如需調記憶體，切換到 **記憶體超頻** 頁編輯或匯入目標（也可先填 EXPO），在 **設定** 中開啟 **專家模式** 後點「寫入 BIOS」，重啟完成記憶體訓練即可生效

程式會在同目錄使用三個資料夾：

- `logs\`：每次啟動的執行日誌、y-cruncher 日誌，以及 `AMD_ACPI` 探測與寫入記錄
- `profiles\`：CO 歷程、復原狀態、CO / CS 設定、校準資料、設定與 BIOS 記憶體備份
- `dram\`：記憶體超頻設定的預設匯入 / 匯出目錄（隨包附帶預設時也放在這裡）

## 風險提示

PBO 和記憶體超頻都屬於超頻操作，會影響系統穩定性。設定不當可能導致運算錯誤、當機、藍畫面、重啟、未儲存資料遺失，或因記憶體參數不當導致黑屏無法開機（桌機需清除 CMOS，筆電可能需要拆機斷開主電池與 CMOS 電池）。長期在不穩定狀態下執行可能損傷硬體，也可能影響保固。**測試前請儲存資料，測試期間請勿操作電腦，寫入 BIOS 前請核對好所有目標值。**

本程式依據 GNU 通用公共授權條款第 3 版（GPL-3.0）發布，**不附帶任何形式的擔保**，包括但不限於對適售性或特定用途適用性的默示擔保。因使用本程式導致的任何資料遺失、硬體損壞或其他直接、間接損失，作者概不負責，相關風險由使用者自行承擔。

## 從原始碼建置

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\memoc
```

建置產物輸出到 `memoc\`，整個目錄可直接分發（非單檔）。若儲存庫根目錄存在 `y-cruncher v0.8.7.9547b\` 或 `dram-presets\`，建置時會自動複製到發布目錄的 `tools\y-cruncher\` 與 `dram\`；不存在時自動略過。

## 作者與致謝

- **開發者**：[@lengmo23](https://github.com/lengmo23)（[@冷漠OC](https://v.douyin.com/6VGnQBTxwIQ/)）
- **聯合開發者（記憶體超頻）**：[@纯爱出演小猪](https://v.douyin.com/yMpmAidKqdY/)（Cat-girl-memoryOC）
- **特別鳴謝**：`@古腾古腾咕咚咚`（指導與建議）、`@阿肝`（功能建議與測試）、`@Yee`（核心測試）

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## 請作者喝杯咖啡

AMD Ryzen PBO Studio 是我利用業餘時間獨立開發的開源工具，每一個功能都經過日夜打磨與反覆迭代。如果它幫你節省了反覆調參的時間，或讓你的電腦獲得了更好的效能，歡迎請我喝杯咖啡！你的每一份支持，都是我持續維護和更新的動力，也能讓更多玩家用上更好的調參工具。

贊助完全自願，不影響任何功能的使用。程式左下角的咖啡杯按鈕中也提供相同的收款碼。

<p align="center">
  <img src="Alipay.png" alt="支付寶" height="400">
  <img src="Wechat.png" alt="微信支付" height="400">
</p>

## 授權

本專案依據 [GNU General Public License v3.0](LICENSE) 發布。由於專案靜態連結了同為 GPL-3.0 的 ZenStates-Core，整個專案也必須以 GPL-3.0 發布。

## 第三方元件

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core)（GPL-3.0）：Ryzen SMU 存取函式庫，提供 CO、CS、PM Table 與 PBO 參數的讀寫介面
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool)（GPL-3.0）：SMU 指令定義與 PM Table 佈局的參考實作
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli)（GPL-3.0）：提供 `inpoutx64.dll` 硬體存取元件
- [y-cruncher](https://www.numberworld.org/y-cruncher/)：壓力測試引擎，版權歸 Alexander J. Yee 所有
- [PawnIO](https://pawnio.eu)：必需的核心驅動元件，由使用者單獨安裝

完整聲明見 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
