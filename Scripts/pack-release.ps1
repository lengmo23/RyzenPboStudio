<#
.SYNOPSIS
    Package bin\ into lite, net8contained and update release archives.

.DESCRIPTION
    lite          includes y-cruncher for first-time installation; needs .NET 8 installed.
    net8contained is lite with the .NET 8 runtime bundled (self-contained), for machines without .NET 8.
                  It is published from the same source into a temporary folder, and is also the in-app
                  update package of such installs.
    update        contains only the application and dependencies for in-app updates of the lite package.
    Updater prefers assets whose names contain "update"; a self-contained install takes "net8contained".

    All archives use the same fixed top-level folder. Updater.ExtractAndVerify
    locates it by the main executable. README and notices come from the repository
    root rather than the ZenStates-Core build output.

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

# Normalize ProductVersion to three numeric parts without commit metadata.
$version = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
if ($version -match '^([0-9]+\.[0-9]+\.[0-9]+)') { $version = $Matches[1] }
elseif ($version -match '^([0-9]+\.[0-9]+)') { $version = "$($Matches[1]).0" }
else { throw "无法从 ProductVersion 解析版本号: $version" }
Write-Host "版本: $version"

# Runtime data is not part of the release.
foreach ($junk in @('logs', 'profiles')) {
    $p = Join-Path $bin $junk
    if (Test-Path -LiteralPath $p) {
        Write-Host "跳过运行时目录: $junk"
    }
}

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) "rps-pack-$(Get-Random)"
# A fixed folder name lets a manual install replace the existing directory.
$folderName = 'AMDRyzenPBOStudio'

function New-Package {
    param([string]$Kind, [bool]$IncludeTools, [string]$AppDir = $bin)

    $stage = Join-Path $stageRoot "$Kind\$folderName"
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    # Copy application files; tools always come from the publish folder and only into first-install archives.
    Get-ChildItem -LiteralPath $AppDir | Where-Object { $_.Name -notin @('logs', 'profiles', 'tools') } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $stage -Recurse -Force
    }
    if ($IncludeTools) {
        $tools = Join-Path $bin 'tools'
        if (Test-Path -LiteralPath $tools) {
            Copy-Item -LiteralPath $tools -Destination $stage -Recurse -Force
        }
        else {
            Write-Warning "bin\tools 不存在，$Kind 包将不含 y-cruncher。"
        }
    }

    # Use repository documentation instead of the dependency's build output.
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README_zh_cn.md') -Destination (Join-Path $stage 'README_zh_cn.md') -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Force

    # Updater.CheckAsync selects assets by "update", then "lite" / "full" (older releases), or "net8contained".
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
    # Self-contained build of the same source, checked against the publish folder before any archive is written.
    $net8Dir = Join-Path $stageRoot 'publish-net8'
    $proj = Join-Path $repoRoot 'RyzenPboStudio\RyzenPboStudio.csproj'
    Write-Host "发布含 .NET 8 的版本..."
    dotnet publish $proj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $net8Dir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "含 .NET 8 的版本发布失败（dotnet publish 返回 $LASTEXITCODE）。" }
    $net8Version = (Get-Item -LiteralPath (Join-Path $net8Dir $exeName)).VersionInfo.ProductVersion
    if ($net8Version -notmatch "^$([regex]::Escape($version))(\+|$)") {
        throw "含 .NET 8 的版本号 $net8Version 与 $BinDir 的 $version 不一致，请先重新发布 $BinDir。"
    }

    $lite = New-Package -Kind 'lite'   -IncludeTools $true
    $upd  = New-Package -Kind 'update' -IncludeTools $false
    # "update", "lite" and "full" must stay out of this name: installs without bundled .NET select by them.
    $net8 = New-Package -Kind 'net8contained' -IncludeTools $true -AppDir $net8Dir
}
finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}

Write-Host ""
Write-Host "输出目录: $out"
