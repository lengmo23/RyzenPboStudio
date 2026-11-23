<p align="center">
  <a href="README.md">English</a> ·
  <a href="README_zh_cn.md">简体中文</a> ·
  <a href="README_zh_tw.md">繁體中文</a> ·
  <a href="README_ja.md">日本語</a> ·
  <strong>한국어</strong> ·
  <a href="README_de.md">Deutsch</a> ·
  <a href="README_es.md">Español</a> ·
  <a href="README_fr.md">Français</a> ·
  <a href="README_pt_br.md">Português (Brasil)</a> ·
  <a href="README_ru.md">Русский</a>
</p>

<h1 align="center">AMD Ryzen PBO Studio</h1>

<p align="center">
  AMD Ryzen 5000 / 7000 / 9000 시리즈 프로세서를 위한 PBO 튜닝, Curve Optimizer(CO) 자동 테스트 및 메모리 오버클럭 도구입니다.
</p>

<p align="center">
  <a href="https://lengmo23.github.io/RyzenPboStudio/en/"><strong>사용 가이드 (영어)</strong></a>
</p>

![PBO 튜닝](pbotuning.png)

## 기능

### 화면 및 실시간 모니터링

왼쪽 사이드바에서 **PBO 튜닝**, **안정성 테스트**, **메모리 OC**, **실행 로그** 4개 페이지를 전환할 수 있으며, 아래에는 **시스템 개요**(프로세서, 메모리 및 실시간 FCLK/UCLK, 메인보드, BIOS 및 AGESA, 그래픽 드라이버 및 OS)와 **시작 시 PBO 자동 적용** 스위치가 표시됩니다.

오른쪽 상단에는 코어별 모니터링 패널이 있으며 다음을 표시합니다:

- PPT, TDC, EDC, THM의 현재 값과 상한
- TEL/VID, Vdroop, Fmax, BCLK(비동기 클럭 시 `BCLK/BCLK2`)
- CCD별 코어 상태: 각 CCD 평균 사용률과 핫스팟 온도(`HOT`), 코어별 CPPC 순위(골드 / 실버 표시), 클럭, 유효 클럭(`EFF`), CO 값, Core VID

### PBO 튜닝

- **Curve Optimizer(CO)**: 물리 코어별 오프셋 설정, 열 단위 `+/-` 조정, 적용·새로고침·저장·불러오기
- **Curve Shaper(CS)**: 5개 주파수 구간(`MIN` / `LOW` / `MED` / `HIGH` / `MAX`) × 3개 온도 구간(`LOW` / `MED` / `HIGH`), Ryzen 9000 시리즈(Zen 5)만 지원
- **PBO 파라미터 및 Scalar**: FMax, PPT, EDC, TDC, PBO Scalar(1X–10X)를 함께 기록
- **시작 시 PBO 자동 적용**: 켜 두면 Windows 로그인 시 저장된 CO / CS와 저장·적용된 FMax, PPT, EDC, TDC를 자동으로 기록합니다. 활성화 전에 스트레스 테스트를 통과하세요. 적용 후 비정상 재부팅이 발생하면 다음 부팅 시 자동으로 건너뜁니다

### 안정성 테스트

![안정성 테스트](testing.png)

#### 싱글 코어 측정 및 전압 정렬

두 기능은 **안정성 테스트** 페이지 오른쪽에 있으며 AVX2 / FMA 지원 CPU가 필요합니다:

- **싱글 코어 측정**: 현재 CO 상태에서 코어별 클럭, 유효 클럭, VID, Core VID를 측정합니다. 파라미터는 변경하지 않습니다
- **전압 정렬**: 각 CCD에서 수율이 가장 좋은 코어를 기준으로 나머지 코어의 CO를 조정해 동일 클럭에서 요구 전압을 맞추고, CCD별 싱글 코어 최대 클럭을 측정합니다. 결과는 CO 편집기에만 입력되며 자동 저장되지 않으므로 **사용 전 반드시 스트레스 테스트를 통과해야 합니다**

#### 스트레스 테스트

스트레스 테스트는 **y-cruncher**를 사용하며 세 가지 방식을 지원합니다:

| 방식 | 설명                                                                         |
| ---- | ---------------------------------------------------------------------------- |
| 단일 | VT3, BKT, SVT, BBP, SFTv4, SNT, FFTv4, N63, YC018 중 하나 선택               |
| 순차 | 순서대로 실행. 기본 `VT3 → BKT → SVT`, 각 20 / 10 / 10 라운드                |
| 조합 | 매 라운드마다 선택 항목을 번갈아 실행. 기본 `VT3`, `BKT`, `SVT`, 총 10 라운드 |

각 방식의 항목, 라운드 수, 라운드당 시간은 모두 사용자 지정 가능합니다. `YC018`은 메모리 집중형 Pi 벤치마크로 메모리 용량에 따라 규모가 자동 선택되며 항상 전체 코어를 사용합니다.

테스트 범위:

| 범위        | 설명                                                   |
| ----------- | ------------------------------------------------------ |
| 전체 코어   | 모든 코어를 동시에 테스트                              |
| 사용자 지정 | 선택한 물리 코어만 테스트                              |
| 단일 CCD    | 지정한 CCD만 테스트(멀티 CCD CPU 전용)                 |
| CCD별 순차  | 각 CCD를 차례로 전체 테스트(멀티 CCD CPU 전용)         |

단일 CCD 결과는 참고용이며 최종 안정성은 전체 코어 테스트로 확인하세요.

CO 조정 방식:

- **자동 모드**: y-cruncher 오류 발생 시 해당 논리 코어를 물리 코어로 매핑해 CO를 완화(기본 `+2`, `+1~+5` 설정 가능)하고 통과할 때까지 라운드를 재실행합니다
- **수동 모드**: 오류 시 알림만 표시하고 중지하며 파라미터는 바꾸지 않습니다

### 메모리 OC

> 메모리 OC 기능은 [Cat-girl-memoryOC](https://v.douyin.com/yMpmAidKqdY/)(`@纯爱出演小猪`)의 지원으로 구현되었습니다.

![메모리 OC](dramoc.png)

- **메모리 SPD 정보**: 현재 메모리 요약, 슬롯별 모듈 모델 및 용량, 칩 제조사(ICs), SPD에서 읽은 EXPO 프로필
- **메모리 쓰기 및 프로필**: **BIOS에 쓰기**, **변경 취소**, **가져오기**, **내보내기**, 아래에 불러온 조절 가능 항목 수와 현재 상태 표시
- **전압 및 기타**: `SOC`, `VDDP`, `VDDG CCD / IOD`, `VDD MISC`, `CPU VDDIO`, `DRAM VDD / VDDQ / VPP`, 평균 `DIMM Temp`(SPD 온도 센서) 및 `DIMM Power`(PMIC, 마우스 오버 시 슬롯별 수치 표시), `FCLK OC Mode`, 읽기 전용 항목 `BGS`, `BGS Alt`, `GDM`, `Cmd2T`. SoC 전압은 PM Table, 메모리 전압은 PMIC에서 2초마다 갱신
- **DRAM 타이밍 제어**: `Memory Frequency`, `FCLK`, `UCLK DIV1 MODE`, `PowerDown Enable`, `Refresh Mode` 및 메모리 컨트롤러의 전체 타이밍
- **신호 제어**: `ProcODT`, 드라이브 강도, `Rtt`, `Nitro`, `Robust Training`, `Interleave` 등

#### EXPO 입력 및 설정 가져오기 / 내보내기

- **EXPO**: SPD의 EXPO 프로필을 클릭하면 해당 EXPO 프리셋이 자동 입력되며, 다시 클릭하면 원래대로 복원됩니다. **BIOS에 쓰기**를 눌러야 실제로 적용됩니다.
- **내보내기**: 현재 목표값을 `.json`으로 `dram\` 폴더에 저장합니다(플랫폼, 속도, CL, 칩 제조사로 자동 명명, 예: `DESKTOP-DDR5-6400-CL28-Hynix.json`). 각 항목에는 `AMD_ACPI` 함수명과 `"auto": true` 또는 원시 BIOS `value`가 들어 있습니다(`label`과 `display`는 읽기 전용 참고용):

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

- **가져오기**: 본 프로그램의 `RyzenPboStudio.MemoryConfig` 파일과 **Cat-girl memoryOC** 메모리 설정(단독 메모리 설정 또는 통합 프로필의 메모리 부분)을 지원합니다. `Auto` 항목은 현재 BIOS 설정으로 되돌아가며, 메인보드 BIOS에서 지원하지 않는 항목은 상태 표시줄에 표시됩니다. **변경 취소**를 누르면 모든 목표값이 현재 BIOS 값으로 복원됩니다

#### BIOS에 쓰기

- 파라미터는 메인보드 `AMD_ACPI` WMI 인터페이스를 통해 기록됩니다. 소프트웨어에서는 Auto 값을 기록할 수 없습니다
- **BIOS에 쓰기**는 기본 잠금 상태이며, 먼저 **설정**에서 **전문가 모드**를 켜야 합니다
- `CPU VDDIO` 상한은 `1.500 V`이며, 쓰기 전 반드시 목표값을 지정해야 합니다(Auto 불가)
- 확인 창에 명백히 비정상적인 목표값(BIOS 상·하한 도달 또는 현재 값의 2배 이상 / 절반 이하)이 표시됩니다. 쓰기 전 펌웨어 함수 ID와 주요 항목의 현재 값을 검증하고, 현재 BIOS 값을 `profiles\memory_backup_<시각>.json`(가져오기로 복원 가능)에 자동 백업합니다. 명령은 항목별로 전송되며 오류 시 즉시 중단되고 완료 후 리드백하며, 모든 원시 명령과 응답은 `logs\acpi-command-trace.jsonl`에 기록됩니다
- 기록 후 **재부팅 및 메모리 트레이닝 완료** 뒤에 적용됩니다. **잘못된 값은 블랙 스크린으로 부팅 불가를 유발할 수 있습니다**: 데스크톱은 CMOS 초기화가 필요하며, 노트북은 보통 외부 리셋 버튼이 없어 분해 후 메인 배터리와 CMOS 배터리를 분리해야 할 수 있습니다. 프로그램에서는 자동 복구할 수 없습니다

### 실행 로그 및 크래시 복구

![실행 로그](logging.png)

- **실행 로그**: 전체 로그는 **실행 로그** 페이지에서 볼 수 있으며(**PBO 튜닝** 페이지 오른쪽에도 동기화된 작은 창 제공), y-cruncher 라운드와 속도, 논리 코어→물리 코어 오류 매핑, CO 완화, BIOS 쓰기 기록을 남깁니다. 실행할 때마다 `logs\`에 저장됩니다
- **크래시 복구**: 테스트 중 프리징이나 전원 차단으로 재부팅되면 부팅 시 자동 실행되어 PBO 제한과 Curve Shaper를 복원하고, CO를 한 단계 완화(중단 후 수동으로 CO를 수정했다면 그 값 유지)한 뒤 중단 단계부터 이어서 실행합니다. 언제든 취소할 수 있습니다. 무인 야간 테스트는 Windows 자동 로그인을 켜 두세요

### 설정 및 언어

왼쪽 사이드바 하단의 **톱니바퀴 아이콘**을 눌러 **설정**을 엽니다:

- **일반**: 언어, 종료 전 확인, **전문가 모드**(일반 테스트 / 복구 / 시작 적용 팝업 생략 및 **BIOS에 쓰기** 잠금 해제)
- **업데이트**: 시작 시 자동 업데이트 확인
- **모니터**: 갱신 간격(`100~10000 ms`, 기본 `500 ms`)
- **데이터**: `profiles`와 `logs` 폴더 바로 열기

영어는 내장되어 있으며 `lang\` 폴더에 9개 언어 번역이 포함되어 있습니다(중국어 간체 `zh-Hans`, 중국어 번체 `zh-Hant`, 일본어 `ja`, 한국어 `ko`, 독일어 `de`, 스페인어 `es`, 프랑스어 `fr`, 브라질 포르투갈어 `pt-BR`, 러시아어 `ru`). 기본값은 Windows 표시 언어를 따르며 없으면 영어를 사용합니다. 각 `<언어코드>.ini`는 `영어 원문=번역문` 형식(`{0}` 등 자리표시자 유지)이며, 언어를 추가하려면 파일을 복사해 언어 코드로 이름을 바꾸고 `[meta]`의 `name=`을 수정하면 됩니다. 언어 변경은 재시작 후 적용됩니다.

## 실행 요구 사항

| 항목      | 요구 사항                                                                                         |
| --------- | ------------------------------------------------------------------------------------------------- |
| 프로세서  | AMD Ryzen 5000 / 7000 / 9000 시리즈                                                               |
| 운영체제  | Windows 10 / 11 x64                                                                               |
| 런타임    | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)(`-net8contained.zip` 다운로드 시 불필요) |
| 드라이버  | [PawnIO](https://pawnio.eu)(필수, 미설치 시 실행 불가)                                            |
| 권한      | 관리자                                                                                            |

세대별 기능 지원:

| 기능            | 5000 시리즈(Zen 3)        | 7000 시리즈(Zen 4) | 9000 시리즈(Zen 5) |
| --------------- | ------------------------- | ------------------ | ------------------ |
| Curve Optimizer | 지원                      | 지원               | 지원               |
| Curve Shaper    | 미지원                    | 미지원             | 지원               |
| PPT / EDC / TDC | 지원                      | 지원               | 지원               |
| FMax 변경       | 미지원                    | 지원               | 지원               |
| CPPC 코어 순위  | BIOS에서 CPPC 활성화 필요 | 지원               | 지원               |

## 사용 방법

1. [Releases](https://github.com/lengmo23/RyzenPboStudio/releases)에서 압축 파일을 받아 원하는 폴더에 압축 해제:
   - **`-lite.zip`**: y-cruncher 포함, .NET 8 Desktop Runtime 설치 필요
   - **`-net8contained.zip`**: .NET 8 런타임과 y-cruncher 내장, .NET 8 미설치 환경에서 바로 실행 가능
   - `-update.zip`과 `-net8patch-rt*.zip`은 앱 내 자동 업데이트 전용이며 y-cruncher가 없으므로 최초 설치에 사용하지 마세요
2. [PawnIO](https://pawnio.eu)를 설치한 뒤 `AMD Ryzen PBO Studio.exe`를 관리자 권한으로 실행
3. **PBO 튜닝** 페이지에서 코어별 클럭, CO 값, 전력 수치가 정상인지 확인
4. **안정성 테스트** 페이지에서 테스트 방식, 범위, CO 조정 방식을 고른 뒤 「시작」 클릭
5. 모두 통과하면 **실행 로그**에 통과한 CO와 CS 값이 출력되며 최종 CO는 `profiles\final_offsets.txt`에도 저장됩니다
6. 메모리 튜닝은 **메모리 OC** 페이지에서 목표값을 편집하거나 가져온 뒤(EXPO 먼저 입력 가능), **설정**에서 **전문가 모드**를 켜고 「BIOS에 쓰기」를 누른 다음 재부팅해 메모리 트레이닝을 마치면 적용됩니다

프로그램은 같은 폴더에 세 개의 디렉터리를 사용합니다:

- `logs\`: 실행마다의 실행 로그, y-cruncher 로그, `AMD_ACPI` 탐색 및 쓰기 기록
- `profiles\`: CO 이력, 복구 상태, CO / CS 설정, 보정 데이터, 설정, BIOS 메모리 백업
- `dram\`: 메모리 OC 설정의 기본 가져오기 / 내보내기 폴더(동봉 프리셋이 있으면 이곳에 위치)

## 위험 안내

PBO와 메모리 오버클럭은 모두 시스템 안정성에 영향을 줍니다. 설정이 잘못되면 연산 오류, 프리징, 블루스크린, 재부팅, 미저장 데이터 손실 또는 메모리 파라미터 문제로 블랙 스크린 부팅 불가(데스크톱은 CMOS 초기화, 노트북은 분해 후 메인 배터리와 CMOS 배터리 분리 필요)가 발생할 수 있습니다. 불안정한 상태로 오래 사용하면 하드웨어 손상이나 보증 영향이 생길 수 있습니다. **테스트 전 작업을 저장하고, 테스트 중에는 PC를 조작하지 않으며, BIOS에 쓰기 전에 모든 목표값을 꼭 확인하세요.**

본 프로그램은 GNU General Public License v3.0(GPL-3.0)에 따라 **어떠한 보증도 없이** 배포됩니다. 사용으로 인한 데이터 손실, 하드웨어 손상 및 기타 직·간접적 손해에 대해 제작자는 책임을 지지 않으며 모든 위험은 사용자 본인이 부담합니다.

## 소스에서 빌드

```powershell
dotnet publish .\RyzenPboStudio\RyzenPboStudio.csproj -c Release -r win-x64 `
  --self-contained false `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\memoc
```

빌드 결과물은 `memoc\`에 출력되며 폴더 전체를 그대로 배포할 수 있습니다(단일 파일 아님). 저장소 루트에 `y-cruncher v0.8.7.9547b\` 또는 `dram-presets\`가 있으면 빌드 시 출력 폴더의 `tools\y-cruncher\`와 `dram\`으로 자동 복사됩니다.

## 제작자 및 감사

- **개발자**: [@lengmo23](https://github.com/lengmo23)([@冷漠OC](https://v.douyin.com/6VGnQBTxwIQ/))
- **공동 개발자(메모리 OC)**: [@纯爱出演小猪](https://v.douyin.com/yMpmAidKqdY/)(Cat-girl-memoryOC)
- **특별 감사**: `@古腾古腾咕咚咚`(조언 및 제안), `@阿肝`(기능 제안 및 테스트), `@Yee`(핵심 테스트)

Copyright © 2026 [@lengmo23](https://github.com/lengmo23)

## 커피 한 잔 사주기

AMD Ryzen PBO Studio는 여가 시간에 개인적으로 개발하는 오픈소스 도구로, 모든 기능을 수없이 다듬어 왔습니다. 튜닝 시간을 아끼거나 PC 성능을 끌어올리는 데 도움이 되었다면 커피 한 잔으로 응원해 주세요!

후원은 완전히 자발적이며 어떤 기능에도 영향을 주지 않습니다. 같은 결제 QR 코드는 프로그램 왼쪽 하단의 커피잔 버튼에서도 볼 수 있습니다.

<p align="center">
  <img src="Alipay.png" alt="Alipay" height="400">
  <img src="Wechat.png" alt="WeChat Pay" height="400">
</p>

## 라이선스

본 프로젝트는 [GNU General Public License v3.0](LICENSE)으로 배포됩니다. 같은 GPL-3.0인 ZenStates-Core를 정적 링크하므로 프로젝트 전체도 GPL-3.0으로 배포해야 합니다.

## 서드파티 구성 요소

- [ZenStates-Core](https://github.com/irusanov/ZenStates-Core)(GPL-3.0): CO, CS, PM Table, PBO 파라미터 읽기/쓰기를 제공하는 Ryzen SMU 접근 라이브러리
- [SMUDebugTool](https://github.com/irusanov/SMUDebugTool)(GPL-3.0): SMU 명령 정의 및 PM Table 레이아웃 참조 구현
- [ryzen-smu-cli](https://github.com/rawhide-kobayashi/ryzen-smu-cli)(GPL-3.0): `inpoutx64.dll` 하드웨어 접근 구성 요소 제공
- [y-cruncher](https://www.numberworld.org/y-cruncher/): 스트레스 테스트 엔진, Copyright Alexander J. Yee
- [PawnIO](https://pawnio.eu): 사용자가 별도로 설치하는 필수 커널 드라이버

전체 고지는 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)를 참조하세요.
