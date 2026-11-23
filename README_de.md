<p align="center">
  <a href="README.md">English</a> ·
  <a href="README_zh_cn.md">简体中文</a> ·
  <a href="README_zh_tw.md">繁體中文</a> ·
  <a href="README_ja.md">日本語</a> ·
  <a href="README_ko.md">한국어</a> ·
  <strong>Deutsch</strong> ·
  <a href="README_es.md">Español</a> ·
  <a href="README_fr.md">Français</a> ·
  <a href="README_pt_br.md">Português (Brasil)</a> ·
  <a href="README_ru.md">Русский</a>
</p>

<h1 align="center">AMD Ryzen PBO Studio</h1>

<p align="center">
  Ein Werkzeug für PBO-Tuning, automatisierte Curve-Optimizer-(CO)-Tests und Speicherübertaktung für AMD Ryzen 5000 / 7000 / 9000 Prozessoren.
</p>

![PBO-Tuning](pbotuning.png)

## Funktionen

### Oberfläche und Live-Überwachung

Die linke Seitenleiste wechselt zwischen **PBO-Tuning**, **Stabilitätstest**, **Speicher-OC** und **Laufprotokoll**; darunter stehen die **Systemübersicht** (Prozessor, Speicher und Live-FCLK/UCLK, Mainboard, BIOS & AGESA, Grafiktreiber und System) sowie der Schalter **PBO beim Start anwenden**.

Oben rechts zeigt das Kern-Überwachungspanel:

- Aktuelle Werte und Grenzen von PPT, TDC, EDC und THM
- TEL/VID, Vdroop, Fmax und BCLK (bei asynchronem Takt als `BCLK/BCLK2`)
- Kernstatus nach CCD gruppiert: durchschnittliche Auslastung und Hotspot-Temperatur (`HOT`) je CCD sowie CPPC-Rang (Gold-/Silbermarkierung), Takt, Effektivtakt (`EFF`), CO-Wert und Core VID pro Kern

### PBO-Tuning

- **Curve Optimizer (CO)**: Offsets pro physischem Kern mit spaltenweiser `+/-`-Anpassung sowie Anwenden, Aktualisieren, Speichern und Laden
- **Curve Shaper (CS)**: 5 Frequenzstufen (`MIN` / `LOW` / `MED` / `HIGH` / `MAX`) × 3 Temperaturstufen (`LOW` / `MED` / `HIGH`), nur auf Ryzen 9000 (Zen 5) unterstützt
- **PBO-Parameter & Scalar**: FMax, PPT, EDC, TDC und PBO Scalar (1X–10X) gemeinsam schreiben
- **PBO beim Start anwenden**: schreibt bei jeder Windows-Anmeldung automatisch gespeicherte CO-/CS-Werte sowie die gespeicherten angewendeten FMax, PPT, EDC und TDC. Vor dem Aktivieren bitte einen Stresstest bestehen; nach einem unerwarteten Neustart überspringt der nächste Start die Anwendung automatisch

### Stabilitätstest

![Stabilitätstest](testing.png)

#### Einzelkern-Messung und Spannungsabgleich

Beide Funktionen liegen rechts auf der Seite **Stabilitätstest** und setzen AVX2 / FMA voraus:

- **Einzelkern-Messung**: misst Takt, Effektivtakt, VID und Core VID jedes Kerns unter dem aktuellen CO, ohne Parameter zu ändern
- **Spannungsabgleich**: nimmt den besten Kern je CCD als Referenz, passt die CO-Werte der übrigen Kerne an, sodass alle Kerne bei gleichem Takt ähnliche Spannungen benötigen, und ermittelt das Einzelkern-Taktlimit je CCD. Das Ergebnis wird nur in den CO-Editor eingetragen und nicht automatisch gespeichert – **vor der Nutzung muss ein Stresstest bestanden werden**

#### Stresstest

Der Stresstest nutzt **y-cruncher** in drei Modi:

| Modus       | Beschreibung                                                                      |
| ----------- | --------------------------------------------------------------------------------- |
| Einzeln     | Einen Test aus VT3, BKT, SVT, BBP, SFTv4, SNT, FFTv4, N63 oder YC018 wählen       |
| Sequenziell | Der Reihe nach ausführen; Standard `VT3 → BKT → SVT`, je 20 / 10 / 10 Runden      |
| Kombiniert  | Ausgewählte Tests je Runde rotieren; Standard `VT3`, `BKT`, `SVT`, 10 Runden      |

Tests, Rundenzahl und Rundendauer sind frei einstellbar. `YC018` ist ein speicherintensiver Pi-Benchmark, dessen Größe automatisch anhand des Arbeitsspeichers gewählt wird und der stets alle Kerne nutzt.

Testbereich:

| Bereich      | Beschreibung                                              |
| ------------ | --------------------------------------------------------- |
| Alle Kerne   | Alle Kerne gleichzeitig testen                            |
| Benutzerdef. | Ausgewählte physische Kerne testen                        |
| Einzel-CCD   | Nur den gewählten CCD testen (nur Multi-CCD-Prozessoren)  |
| Jeder CCD    | Jeden CCD nacheinander komplett testen (nur Multi-CCD)    |

Einzel-CCD-Ergebnisse dienen nur zur Orientierung; für die endgültige Stabilität gilt der All-Core-Test.

CO-Anpassung:

- **Automatik**: bei einem y-cruncher-Fehler wird der fehlerhafte logische Kern dem physischen Kern zugeordnet, dessen CO zurückgenommen (Standard `+2`, einstellbar `+1` bis `+5`) und die Runde bis zum Bestehen wiederholt
- **Manuell**: bei einem Fehler wird nur eine Meldung angezeigt und der Test gestoppt, ohne Parameter zu ändern

### Speicher-OC

> Speicher-OC wird unterstützt von [Cat-girl-memoryOC](https://v.douyin.com/yMpmAidKqdY/) (`@纯爱出演小猪`).

![Speicher-OC](dramoc.png)

- **Speicher-SPD-Info**: aktuelle Speicherübersicht, Modell und Kapazität pro Slot, Chiphersteller (ICs) sowie aus dem SPD ausgelesene EXPO-Profile
- **Speicher schreiben & Profile**: **Ins BIOS schreiben**, **Verwerfen**, **Importieren**, **Exportieren**; darunter stehen Anzahl der geladenen einstellbaren Parameter und aktueller Status
- **Spannungen & Sonstiges**: `SOC`, `VDDP`, `VDDG CCD / IOD`, `VDD MISC`, `CPU VDDIO`, `DRAM VDD / VDDQ / VPP`, durchschnittliche `DIMM Temp` (SPD-Sensor) und `DIMM Power` (PMIC; Tooltip zeigt Einzelwerte), `FCLK OC Mode` sowie schreibgeschützte Werte `BGS`, `BGS Alt`, `GDM`, `Cmd2T`. SoC-Spannungen stammen aus der PM-Tabelle, Speicherspannungen vom PMIC (Aktualisierung alle 2 Sekunden)
- **DRAM-Timing-Steuerung**: `Memory Frequency`, `FCLK`, `UCLK DIV1 MODE`, `PowerDown Enable`, `Refresh Mode` und alle Timings des Speichercontrollers
- **Signalsteuerung**: `ProcODT`, Treiberstärken, `Rtt`, `Nitro`, `Robust Training`, `Interleave` usw.

#### EXPO-Übernahme und Import / Export

- **EXPO**: ein Klick auf ein EXPO-Profil im SPD füllt das entsprechende EXPO-Preset aus; ein weiterer Klick stellt den vorherigen Zustand wieder her. Geschrieben wird erst nach Klick auf **Ins BIOS schreiben**.
- **Exportieren**: speichert die aktuellen Ziele als `.json` im Ordner `dram\` (benannt nach Plattform, Takt, CL und Chiphersteller, z. B. `DESKTOP-DDR5-6400-CL28-Hynix.json`). Jeder Eintrag enthält den `AMD_ACPI`-Funktionsnamen sowie `"auto": true` oder den BIOS-Rohwert `value` (`label` und `display` dienen nur zum Lesen):

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

- **Importieren**: unterstützt `RyzenPboStudio.MemoryConfig`-Dateien sowie Speicherkonfigurationen von **Cat-girl memoryOC** (eigenständige Speicherkonfiguration oder Speicherteil eines kombinierten Profils). `Auto`-Einträge stellen die aktuelle BIOS-Einstellung wieder her; nicht unterstützte Optionen erscheinen in der Statuszeile. **Verwerfen** setzt alle Ziele auf die aktuellen BIOS-Werte zurück

#### Ins BIOS schreiben

- Parameter werden über die `AMD_ACPI`-WMI-Schnittstelle des Mainboards geschrieben. Die Software kann keine Auto-Werte schreiben
- **Ins BIOS schreiben** ist standardmäßig gesperrt; aktivieren Sie zuerst den **Expertenmodus** in den **Einstellungen**
- `CPU VDDIO` ist auf `1.500 V` begrenzt und muss vor dem Schreiben einen Zielwert erhalten (darf nicht auf Auto bleiben)
- Das Bestätigungsfenster markiert auffällige Ziele (BIOS-Minimum/-Maximum oder ≥2× / ≤0,5× des aktuellen Werts). Vor dem Schreiben prüft das Programm Firmware-Funktions-IDs und aktuelle Kernwerte, sichert die aktuellen BIOS-Werte in `profiles\memory_backup_<Zeit>.json` (per Import wiederherstellbar), sendet Befehle einzeln (Abbruch bei Fehler), liest nach Abschluss alle Einträge zurück und protokolliert Rohbefehle und Antworten in `logs\acpi-command-trace.jsonl`
- Änderungen **wirken erst nach Neustart und abgeschlossenem Memory Training**. **Falsche Parameter können zu einem schwarzen Bildschirm führen**: Desktops erfordern ein CMOS-Reset; Laptops haben meist keinen externen Reset-Taster und müssen unter Umständen geöffnet werden, um Haupt- und CMOS-Akku zu trennen. Eine automatische Wiederherstellung ist nicht möglich

### Laufprotokoll und Absturzwiederherstellung

![Laufprotokoll](logging.png)

- **Laufprotokoll**: das vollständige Protokoll steht auf der Seite **Laufprotokoll** (zusätzlich synchron im kleinen Fenster auf **PBO-Tuning**) und dokumentiert y-cruncher-Runden und -Geschwindigkeiten, die Zuordnung fehlerhafter logischer Kerne zu physischen Kernen, CO-Anpassungen und BIOS-Schreibvorgänge; jeder Start wird unter `logs\` gespeichert
- **Absturzwiederherstellung**: nach Absturz oder Stromausfall während eines Tests startet das Programm beim Booten automatisch, stellt PBO-Limits und Curve Shaper wieder her, nimmt CO um einen Schritt zurück (oder behält manuell geänderte CO-Werte bei) und setzt die unterbrochene Phase fort; jederzeit abbrechbar. Für unbeaufsichtigte Tests über Nacht aktivieren Sie die automatische Windows-Anmeldung

### Einstellungen und Sprachen

Klicken Sie auf das **Zahnrad-Symbol** unten in der linken Seitenleiste, um die **Einstellungen** zu öffnen:

- **Allgemein**: Sprache, Vor dem Beenden bestätigen und **Expertenmodus** (überspringt Routine-Dialoge für Test / Wiederherstellung / Autostart und entsperrt **Ins BIOS schreiben**)
- **Updates**: beim Start nach Updates suchen
- **Monitor**: Aktualisierungsintervall (`100–10000 ms`, Standard `500 ms`)
- **Daten**: Schnellzugriff auf die Ordner `profiles` und `logs`

Englisch ist integriert; `lang\` enthält 9 Übersetzungen (`zh-Hans`, `zh-Hant`, `ja`, `ko`, `de`, `es`, `fr`, `pt-BR`, `ru`). Standardmäßig folgt die Sprache der Windows-Anzeigesprache mit Englisch als Fallback. Jede `<Sprachcode>.ini` nutzt `Englischer Text=Übersetzung` (Platzhalter wie `{0}` beibehalten); weitere Sprachen lassen sich per Kopie, Umbenennen und Anpassen von `name=` unter `[meta]` ergänzen. Ein Sprachwechsel gilt nach dem Neustart.

## Voraussetzungen

| Punkt         | Anforderung                                                                                            |
| ------------- | ------------------------------------------------------------------------------------------------------ |
| Prozessor     | AMD Ryzen 5000 / 7000 / 9000 Serie                                                                     |
| Betriebssystem | Windows 10 / 11 x64                                                                                    |
| Laufzeit      | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (bei `-net8contained.zip` nicht nötig) |
| Treiber       | [PawnIO](https://pawnio.eu) (erforderlich; ohne Treiber startet das Programm nicht)                    |
| Berechtigung  | Administrator                                                                                          |

Funktionsunterstützung nach Generation:

| Funktion          | 5000er Serie (Zen 3)            | 7000er Serie (Zen 4) | 9000er Serie (Zen 5) |
| ----------------- | ------------------------------- | -------------------- | -------------------- |
| Curve Optimizer   | Ja                              | Ja                   | Ja                   |
| Curve Shaper      | Nein                            | Nein                 | Ja                   |
| PPT / EDC / TDC   | Ja                              | Ja                   | Ja                   |
| FMax              | Nein                            | Ja                   | Ja                   |
| CPPC-Kernranking  | Erfordert CPPC aktiviert im BIOS | Ja                   | Ja                   |

## Verwendung

1. Laden Sie das passende Archiv aus [Releases](https://github.com/lengmo23/RyzenPboStudio/releases) herunter und entpacken Sie es:
   - **`-lite.zip`**: mit y-cruncher, benötigt installiertes .NET 8 Desktop Runtime
   - **`-net8contained.zip`**: mit integrierter .NET 8 Runtime und y-cruncher, ohne separates .NET 8 lauffähig
   - `-update.zip` und `-net8patch-rt*.zip` sind nur für die In-App-Aktualisierung gedacht, enthalten kein y-cruncher und sind nicht für die Erstinstallation geeignet
2. Installieren Sie [PawnIO](https://pawnio.eu) und starten Sie `AMD Ryzen PBO Studio.exe` als Administrator
3. Prüfen Sie auf **PBO-Tuning**, ob Kerntakte, CO-Werte und Leistungsanzeigen normal gelesen werden
4. Wählen Sie auf **Stabilitätstest** Modus, Bereich und CO-Anpassung und klicken Sie auf **Start**
5. Nach bestandenem Test stehen CO und CS im **Laufprotokoll**; die endgültigen CO-Werte werden zusätzlich in `profiles\final_offsets.txt` geschrieben
6. Für Speicher-OC wechseln Sie zu **Speicher-OC**, bearbeiten oder importieren Zielwerte (oder füllen EXPO ein), aktivieren den **Expertenmodus** in den **Einstellungen**, klicken auf **Ins BIOS schreiben** und starten für das Memory Training neu

Das Programm nutzt drei Ordner im selben Verzeichnis:

- `logs\`: Laufprotokolle pro Start, y-cruncher-Logs sowie `AMD_ACPI`-Probe- und Schreibprotokolle
- `profiles\`: CO-Verlauf, Wiederherstellungsstatus, CO-/CS-Profile, Kalibrierungsdaten, Einstellungen und BIOS-Speicherbackups
- `dram\`: Standardordner für Import / Export von Speicher-OC-Profilen (mitgelieferte Presets liegen ebenfalls hier)

## Risikohinweis

Sowohl PBO als auch Speicherübertaktung beeinflussen die Systemstabilität. Falsche Einstellungen können Rechenfehler, Abstürze, Bluescreens, Neustarts, Verlust ungespeicherter Daten oder einen schwarzen Bildschirm beim Start verursachen (Desktop: CMOS-Reset; Laptop: ggf. Öffnen zum Trennen von Haupt- und CMOS-Akku). Längerer instabiler Betrieb kann Hardware beschädigen oder die Garantie beeinträchtigen. **Sichern Sie Ihre Daten vor dem Test, nutzen Sie den PC währenddessen nicht und prüfen Sie alle Zielwerte vor dem Schreiben ins BIOS.**

Dieses Programm wird unter der GNU General Public License v3.0 (GPL-3.0) **OHNE JEGLICHE GEWÄHRLEISTUNG** veröffentlicht. Der Autor haftet nicht für Datenverlust, Hardwareschäden oder sonstige direkte bzw. indirekte Schäden; die Nutzung erfolgt auf eigenes Risiko.

## Aus dem Quellcode erstellen

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\memoc
```

Die Ausgabe landet in `memoc\` und kann als kompletter Ordner weitergegeben werden (keine Einzeldatei). Sind `y-cruncher v0.8.7.9547b\` oder `dram-presets\` im Repository-Stammverzeichnis vorhanden, werden sie automatisch in `tools\y-cruncher\` bzw. `dram\` kopiert; andernfalls wird dieser Schritt übersprungen.

## Autor & Dank

- **Entwickler**: [@lengmo23](https://github.com/lengmo23) ([@冷漠OC](https://v.douyin.com/6VGnQBTxwIQ/))
- **Co-Entwickler (Speicher-OC)**: [@纯爱出演小猪](https://v.douyin.com/yMpmAidKqdY/) (Cat-girl-memoryOC)
- **Besonderer Dank**: `@古腾古腾咕咚咚` (Hinweise & Vorschläge), `@阿肝` (Funktionsideen & Tests), `@Yee` (Kerntests)

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## Spendier mir einen Kaffee

AMD Ryzen PBO Studio ist ein Open-Source-Tool, das ich in meiner Freizeit eigenständig entwickle und über viele Iterationen verfeinert habe. Wenn es dir beim Tuning Zeit gespart oder mehr Leistung aus deinem PC geholt hat, freue ich mich über einen Kaffee!

Die Unterstützung ist völlig freiwillig und hat keinen Einfluss auf die Funktionen. Dieselben Zahlungscodes findest du auch über den Kaffeetassen-Button unten links im Programm.

<p align="center">
  <img src="Alipay.png" alt="Alipay" height="400">
  <img src="Wechat.png" alt="WeChat Pay" height="400">
</p>

## Lizenz

Dieses Projekt erscheint unter der [GNU General Public License v3.0](LICENSE). Da ZenStates-Core (ebenfalls GPL-3.0) statisch gelinkt wird, muss das gesamte Projekt unter GPL-3.0 veröffentlicht werden.

## Drittanbieter-Komponenten

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) (GPL-3.0): Ryzen-SMU-Zugriffsbibliothek für CO, CS, PM Table und PBO-Parameter
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool) (GPL-3.0): Referenz für SMU-Befehlsdefinitionen und PM-Table-Layouts
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli) (GPL-3.0): stellt `inpoutx64.dll` bereit
- [y-cruncher](https://www.numberworld.org/y-cruncher/): Stresstest-Engine, Copyright Alexander J. Yee
- [PawnIO](https://pawnio.eu): erforderlicher Kernel-Treiber, separat vom Benutzer zu installieren

Vollständige Hinweise siehe [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
