<#
.SYNOPSIS
    把 bin\ 打包成两个发布用的 zip。

.DESCRIPTION
    full   —— 含 y-cruncher，供新用户首次下载，解压即用。
    update —— 仅主程序及其依赖，供程序内自动更新使用；y-cruncher 极少变动，
              没必要每次更新都重下 46MB。Updater 会优先选取名字含 "update" 的资产。

    两个包顶层都是同一个版本命名的文件夹，Updater.ExtractAndVerify 靠找到主程序
    exe 来定位它。包内的 README 与第三方声明取自仓库根目录，而不是构建输出里
    ZenStates-Core 自带的那份（那是库说明，用户看了会困惑）。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Scripts\pack-release.ps1
#>
param(
    [string]$BinDir = "bin",
    [string]$OutDir = "release"
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $repoRoot $BinDir
$out = Join-Path $repoRoot $OutDir

$exeName = 'AMD Ryzen PBO Studio.exe'
$exePath = Join-Path $bin $exeName
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "找不到 $exePath，请先执行 dotnet publish 输出到 $BinDir。"
}

$version = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ($version -match '^([0-9]+\.[0-9]+\.[0-9]+)') { $version = $Matches[1] }
Write-Host "版本: $version"

# 运行时产物不属于交付内容
foreach ($junk in @('logs', 'profiles')) {
    $p = Join-Path $bin $junk
    if (Test-Path -LiteralPath $p) {
        Write-Host "跳过运行时目录: $junk"
    }
}

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) "rps-pack-$(Get-Random)"
$folderName = "AMD Ryzen PBO Studio v$version"

function New-Package {
    param([string]$Kind, [bool]$IncludeTools)

    $stage = Join-Path $stageRoot "$Kind\$folderName"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # 主程序及依赖：排除运行时目录与 tools（tools 单独按需复制）
    Get-ChildItem -LiteralPath $bin -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stage -Force
    }
    if ($IncludeTools) {
        $tools = Join-Path $bin 'tools'
        if (Test-Path -LiteralPath $tools) {
            Copy-Item -LiteralPath $tools -Destination $stage -Recurse -Force
        }
        else {
            Write-Warning "bin\tools 不存在，full 包将不含 y-cruncher。"
        }
    }

    # 用仓库根目录的 README / 第三方声明覆盖构建输出里 ZenStates-Core 自带的那份
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Force

    $zip = Join-Path $out "AMD-Ryzen-PBO-Studio-v$version-$Kind.zip"
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

    $size = (Get-Item -LiteralPath $zip).Length / 1MB
    $count = (Get-ChildItem -LiteralPath $stage -Recurse -File).Count
    Write-Host ("{0,-8} {1,7:N1} MB  {2,4} 个文件  {3}" -f $Kind, $size, $count, (Split-Path $zip -Leaf))
    return $zip
}

New-Item -ItemType Directory -Force -Path $out | Out-Null

try {
    $full = New-Package -Kind 'full'   -IncludeTools $true
    $upd  = New-Package -Kind 'update' -IncludeTools $false
}
finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}

Write-Host ""
Write-Host "输出目录: $out"
