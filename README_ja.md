<p align="center">
  <a href="README.md">English</a> ·
  <a href="README_zh_cn.md">简体中文</a> ·
  <a href="README_zh_tw.md">繁體中文</a> ·
  <strong>日本語</strong> ·
  <a href="README_ko.md">한국어</a> ·
  <a href="README_de.md">Deutsch</a> ·
  <a href="README_es.md">Español</a> ·
  <a href="README_fr.md">Français</a> ·
  <a href="README_pt_br.md">Português (Brasil)</a> ·
  <a href="README_ru.md">Русский</a>
</p>

<h1 align="center">AMD Ryzen PBO Studio</h1>

<p align="center">
  AMD Ryzen 5000 / 7000 / 9000 シリーズ向けの PBO 調整・Curve Optimizer（CO）自動テスト・メモリオーバークロックツール。
</p>

![PBO 調整](pbotuning.png)

## 機能

### 画面とリアルタイム監視

左サイドバーで **PBO 調整**、**安定性テスト**、**メモリ OC**、**実行ログ** の 4 ページを切り替えられ、下部には **システム概要**（CPU、メモリとリアルタイム FCLK/UCLK、マザーボード、BIOS・AGESA、GPU ドライバおよび OS）と **起動時に PBO を自動適用** スイッチが表示されます。

右側上部はコア別モニタで、以下を表示します：

- PPT、TDC、EDC、THM の現在値と上限
- TEL/VID、Vdroop、Fmax、BCLK（非同期クロック時は `BCLK/BCLK2`）
- CCD ごとのコア状態：各 CCD の平均使用率とホットスポット温度（`HOT`）、各コアの CPPC ランク（ゴールド / シルバーマーク）、周波数、実効周波数（`EFF`）、CO 値、Core VID

### PBO 調整

- **Curve Optimizer（CO）**：物理コアごとのオフセット設定、列単位の `+/-` 調整、適用・更新・保存・読込
- **Curve Shaper（CS）**：5 周波数帯（`MIN` / `LOW` / `MED` / `HIGH` / `MAX`）× 3 温度帯（`LOW` / `MED` / `HIGH`）、Ryzen 9000 シリーズ（Zen 5）のみ対応
- **PBO パラメータと Scalar**：FMax、PPT、EDC、TDC、PBO Scalar（1X–10X）をまとめて適用
- **起動時に PBO を自動適用**：有効にすると Windows ログイン時に保存済みの CO / CS と保存・適用した FMax、PPT、EDC、TDC を自動書き込みします。有効化する前にストレスチェックを通過させてください。適用後に異常再起動が発生した場合、次回起動時は自動でスキップします

### 安定性テスト

![安定性テスト](testing.png)

#### 単コア測定と電圧アライメント

どちらも **安定性テスト** ページの右側にあり、AVX2 / FMA 対応 CPU が必要です：

- **単コア測定**：現在の CO のまま各コアの周波数、実効周波数、VID、Core VID を計測します。設定は変更しません
- **電圧アライメント**：各 CCD で最も優秀なコアを基準に他コアの CO を調整し、同じ周波数で要求電圧が揃うようにしたうえで各 CCD の単コア上限周波数を測定します。結果は CO エディタに入力されるだけで自動保存されません。**使用前に必ずストレステストを通過させてください**

#### ストレステスト

ストレステストには **y-cruncher** を使用し、3 つの方式に対応します：

| 方式 | 説明                                                                          |
| ---- | ----------------------------------------------------------------------------- |
| 単一 | VT3、BKT、SVT、BBP、SFTv4、SNT、FFTv4、N63、YC018 から 1 つを選択             |
| 順次 | 順番に実行。既定は `VT3 → BKT → SVT` を各 20 / 10 / 10 ラウンド               |
| 複合 | ラウンドごとに選択項目をローテーション。既定は `VT3`、`BKT`、`SVT` 計 10 ラウンド |

各方式の項目、ラウンド数、1 ラウンドの時間は自由に設定できます。`YC018` はメモリ負荷の高い Pi ベンチマークで、規模は搭載メモリ量から自動選択され、常に全コアを使用します。

テスト範囲：

| 範囲     | 説明                                                   |
| -------- | ------------------------------------------------------ |
| 全コア   | すべてのコアを同時にテスト                             |
| カスタム | 選択した物理コアのみテスト                             |
| 単一 CCD | 指定した CCD のみテスト（マルチ CCD CPU のみ）         |
| CCD ごと | 各 CCD を順に最後までテスト（マルチ CCD CPU のみ）     |

単一 CCD の結果は参考値です。最終的な安定性は全コアテストで確認してください。

CO 調整方式：

- **自動モード**：y-cruncher がエラーを出すと、該当する論理コアを物理コアへ対応付けて CO を緩和（既定 `+2`、`+1～+5` で設定可能）し、通過するまでラウンドを再実行します
- **手動モード**：エラー時は通知して停止するだけで、パラメータは変更しません

### メモリ OC

> メモリ OC 機能は [Cat-girl-memoryOC](https://v.douyin.com/yMpmAidKqdY/)（`@纯爱出演小猪`）の協力により実現しています。

![メモリ OC](dramoc.png)

- **メモリ SPD 情報**：現在のメモリ構成、各スロットの型番と容量、DRAM チップメーカー（ICs）、SPD から読み取った EXPO プロファイル
- **メモリ書き込みとプロファイル**：**BIOS に書き込み**、**元に戻す**、**インポート**、**エクスポート**。下部に読み込み済みの調整可能項目数と状態を表示
- **電圧・その他**：`SOC`、`VDDP`、`VDDG CCD / IOD`、`VDD MISC`、`CPU VDDIO`、`DRAM VDD / VDDQ / VPP`、平均 `DIMM Temp`（SPD 温度センサー）と `DIMM Power`（PMIC、ホバーで各 DIMM の値を確認可能）、`FCLK OC Mode`、および読み取り専用項目 `BGS`、`BGS Alt`、`GDM`、`Cmd2T`。SoC 側電圧は PM Table、メモリ電圧は PMIC から 2 秒ごとに更新
- **DRAM タイミング制御**：`Memory Frequency`、`FCLK`、`UCLK DIV1 MODE`、`PowerDown Enable`、`Refresh Mode`、およびメモリコントローラの全タイミング
- **信号制御**：`ProcODT`、ドライブ強度、`Rtt`、`Nitro`、`Robust Training`、`Interleave` など

#### EXPO 入力と設定のインポート / エクスポート

- **EXPO**：SPD の EXPO プロファイルをクリックすると対応する EXPO プリセットが自動で入力され、再クリックで元に戻ります。実際に適用するには **BIOS に書き込み** を押してください。
- **エクスポート**：現在の目標値を `.json` として `dram\` フォルダに保存します（プラットフォーム、速度、CL、チップメーカーで自動命名、例：`DESKTOP-DDR5-6400-CL28-Hynix.json`）。各項目は `AMD_ACPI` 関数名と `"auto": true` または生の BIOS `value` を保持します（`label` と `display` は閲覧用）：

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

- **インポート**：本ツール出力の `RyzenPboStudio.MemoryConfig` および **Cat-girl memoryOC** のメモリ設定（単体設定または複合プロファイルのメモリ部分）に対応します。`Auto` 項目は現在の BIOS 設定へ戻り、非対応項目はステータス欄に表示されます。**元に戻す** で全項目を現在の BIOS 値に戻せます

#### BIOS に書き込み

- パラメータはマザーボードの `AMD_ACPI` WMI インターフェース経由で書き込みます。ソフトウェア側から Auto 値を書き込むことはできません
- **BIOS に書き込み** は既定でロックされており、先に **設定** で **エキスパートモード** を有効にする必要があります
- `CPU VDDIO` の上限は `1.500 V` です。書き込み前に目標値を指定する必要があり、Auto のままにはできません
- 確認ウィンドウには明らかに異常な目標値（BIOS の上限 / 下限に達する値、または現在値の 2 倍以上 / 半分以下）が表示されます。書き込み前にファームウェア関数 ID と主要項目の現在値を確認し、現在の BIOS 値を `profiles\memory_backup_<時刻>.json`（インポート復元可能）へ自動バックアップします。コマンドは 1 件ずつ送信し、エラーがあれば即座に停止、完了後にリードバックを行い、生のコマンドと応答は `logs\acpi-command-trace.jsonl` に記録されます
- 書き込み後は **再起動とメモリトレーニングの完了** で反映されます。**誤った設定は黒画面・起動不能の原因になります**：デスクトップは CMOS クリアが必要で、ノート PC は外部リセットボタンがないことが多く、分解してメインバッテリーと CMOS 電池を外す必要が生じる場合があります。本ソフトでは自動復旧できません

### 実行ログとクラッシュ復旧

![実行ログ](logging.png)

- **実行ログ**：完全なログは **実行ログ** ページで確認でき（**PBO 調整** ページ右側の小ウィンドウにも同期）、y-cruncher のラウンドと速度、論理コアから物理コアへのエラー対応、CO 緩和、BIOS 書き込み記録を残します。起動ごとに `logs\` へ保存されます
- **クラッシュ復旧**：テスト中のフリーズや電源断からの再起動後、自動で起動して PBO 制限と Curve Shaper を復元し、CO を 1 段階緩和（中断後に手動で CO を変更していた場合はその値を維持）して中断段階から再開します。いつでもキャンセル可能です。無人の夜間テストには Windows の自動サインインを有効にしてください

### 設定と言語

左サイドバー下部の **歯車アイコン** から **設定** を開きます：

- **全般**：言語、終了前の確認、**エキスパートモード**（定型のテスト / 復旧 / 起動適用ダイアログを省略し、**BIOS に書き込み** のロックを解除）
- **更新**：起動時にアップデートを自動確認
- **モニタ**：更新間隔（`100～10000 ms`、既定 `500 ms`）
- **データ**：`profiles` と `logs` フォルダを開くショートカット

英語を内蔵し、`lang\` フォルダに 9 言語の翻訳を同梱しています（簡体字中国語 `zh-Hans`、繁体字中国語 `zh-Hant`、日本語 `ja`、韓国語 `ko`、ドイツ語 `de`、スペイン語 `es`、フランス語 `fr`、ブラジルポルトガル語 `pt-BR`、ロシア語 `ru`）。既定では Windows の表示言語に従い、該当がない場合は英語になります。各 `<言語コード>.ini` は `英語原文=訳文` 形式（`{0}` などのプレースホルダを保持）です。言語を追加する場合はファイルをコピーして言語コードにリネームし、`[meta]` の `name=` を書き換えてください。切り替えは再起動後に反映されます。

## 動作要件

| 項目     | 要件                                                                                              |
| -------- | ------------------------------------------------------------------------------------------------- |
| CPU      | AMD Ryzen 5000 / 7000 / 9000 シリーズ                                                             |
| OS       | Windows 10 / 11 x64                                                                               |
| ランタイム | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（`-net8contained.zip` は不要） |
| ドライバ | [PawnIO](https://pawnio.eu)（必須。未インストールでは起動不可）                                   |
| 権限     | 管理者                                                                                            |

世代別の対応機能：

| 機能            | 5000 シリーズ（Zen 3）      | 7000 シリーズ（Zen 4） | 9000 シリーズ（Zen 5） |
| --------------- | --------------------------- | ---------------------- | ---------------------- |
| Curve Optimizer | 対応                        | 対応                   | 対応                   |
| Curve Shaper    | 非対応                      | 非対応                 | 対応                   |
| PPT / EDC / TDC | 対応                        | 対応                   | 対応                   |
| FMax 変更       | 非対応                      | 対応                   | 対応                   |
| CPPC コアランク | BIOS で CPPC の有効化が必要 | 対応                   | 対応                   |

## 使い方

1. [Releases](https://github.com/lengmo23/RyzenPboStudio/releases) からアーカイブをダウンロードして任意のフォルダに展開：
   - **`-lite.zip`**：y-cruncher 同梱（.NET 8 Desktop Runtime の事前インストールが必要）
   - **`-net8contained.zip`**：.NET 8 ランタイムと y-cruncher を同梱（.NET 8 未導入でもそのまま実行可能）
   - `-update.zip` と `-net8patch-rt*.zip` はアプリ内自動更新用で y-cruncher を含まないため初回導入には使わないでください
2. [PawnIO](https://pawnio.eu) をインストールし、`AMD Ryzen PBO Studio.exe` を管理者として実行
3. **PBO 調整** ページで各コアの周波数、CO 値、電力表示が正常であることを確認
4. **安定性テスト** ページでテスト方式・範囲・CO 調整方式を選び「開始」をクリック
5. すべて通過すると、**実行ログ** に合格した CO と CS が表示され、最終 CO は `profiles\final_offsets.txt` にも書き込まれます
6. メモリ調整は **メモリ OC** ページで目標を編集またはインポート（EXPO 入力も可）し、**設定** で **エキスパートモード** を有効にして「BIOS に書き込み」を押した後、再起動してメモリトレーニングを完了させてください

実行ファイルと同じディレクトリに 3 つのフォルダを使用します：

- `logs\`：起動ごとの実行ログ、y-cruncher ログ、`AMD_ACPI` の調査および書き込み記録
- `profiles\`：CO 履歴、復旧状態、CO / CS 設定、キャリブレーションデータ、設定、BIOS メモリバックアップ
- `dram\`：メモリ OC 設定の既定のインポート / エクスポート先（同梱プリセットがある場合もここ）

## リスクと免責事項

PBO およびメモリオーバークロックはシステム安定性に影響します。誤った設定は計算エラー、フリーズ、ブルースクリーン、再起動、未保存データの損失、または黒画面による起動不能（デスクトップは CMOS クリア、ノート PC は分解してメインバッテリーと CMOS 電池を外す必要あり）を招く可能性があります。不安定な状態で長時間使用するとハードウェアの損傷や保証への影響が生じる恐れがあります。**テスト前に作業データを保存し、テスト中は PC を操作せず、BIOS 書き込み前にはすべての目標値を確認してください。**

本プログラムは GNU General Public License v3.0（GPL-3.0）のもと **無保証** で配布されます。本プログラムの使用により生じたいかなるデータ損失、ハードウェア損傷、その他直接・間接の損害についても作者は責任を負いません。ご利用は自己責任でお願いします。

## ソースからのビルド

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\memoc
```

ビルド成果物は `memoc\` に出力され、フォルダごと配布できます（単一ファイルではありません）。リポジトリルートに `y-cruncher v0.8.7.9547b\` または `dram-presets\` がある場合、出力先の `tools\y-cruncher\` と `dram\` へ自動コピーされます。

## 作者・謝辞

- **開発者**：[@lengmo23](https://github.com/lengmo23)（[@冷漠OC](https://v.douyin.com/6VGnQBTxwIQ/)）
- **共同開発者（メモリ OC）**：[@纯爱出演小猪](https://v.douyin.com/yMpmAidKqdY/)（Cat-girl-memoryOC）
- **スペシャルサンクス**：`@古腾古腾咕咚咚`（指導・提案）、`@阿肝`（機能提案・テスト）、`@Yee`（コアテスト）

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## コーヒーを奢る

AMD Ryzen PBO Studio は余暇を使って個人で開発しているオープンソースツールで、どの機能も試行錯誤を重ねて磨いてきました。調整の手間を減らせたり、PC の性能を引き出す役に立てたなら、コーヒー 1 杯ご支援いただけると嬉しいです。

支援は完全に任意で、機能制限はありません。同じ QR コードはアプリ左下のコーヒーカップボタンからも表示できます。

<p align="center">
  <img src="Alipay.png" alt="Alipay" height="400">
  <img src="Wechat.png" alt="WeChat Pay" height="400">
</p>

## ライセンス

本プロジェクトは [GNU General Public License v3.0](LICENSE) で公開されています。同じく GPL-3.0 の ZenStates-Core を静的リンクしているため、プロジェクト全体も GPL-3.0 で配布されます。

## サードパーティコンポーネント

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core)（GPL-3.0）：CO、CS、PM Table、PBO パラメータの読み書きを提供する Ryzen SMU アクセスライブラリ
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool)（GPL-3.0）：SMU コマンド定義と PM Table レイアウトの参照実装
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli)（GPL-3.0）：`inpoutx64.dll` ハードウェアアクセスコンポーネントの提供元
- [y-cruncher](https://www.numberworld.org/y-cruncher/)：ストレステストエンジン、Copyright Alexander J. Yee
- [PawnIO](https://pawnio.eu)：ユーザーが別途インストールする必須カーネルドライバ

完全な表記は [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) を参照してください。
