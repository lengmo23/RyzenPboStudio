param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$projectRootPath = [IO.Path]::GetFullPath($ProjectRoot)
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$fixedTimestamp = [DateTimeOffset]::new(2025, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
# Bumped when the bundle contents change: it names the extraction directory under
# %ProgramData%, so a new value forces a fresh extract instead of reusing a stale cache.
# v3 dropped y-cruncher, which now ships beside the executable in tools\y-cruncher\.
$bundleVersion = 'inpoutx64-only_v3'

$sourceFiles = [Collections.Generic.List[object]]::new()

# CO read/write now uses ZenStates-Core; only inpoutx64.dll (IODriver dependency)
# and its license need to ship. Archive prefix 'ryzen-smu-cli-0.1.3' is kept so it
# matches the DLL search directory used in RyzenSmu.
foreach ($name in @('inpoutx64.dll', 'InpOut.LICENSE.txt')) {
    $src = Join-Path (Join-Path $projectRootPath 'ryzen-smu-cli-0.1.3') $name
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
        throw "Missing native tool file: $src"
    }
    $sourceFiles.Add([PSCustomObject]@{
        SourcePath = $src
        ArchivePath = "ryzen-smu-cli-0.1.3/$name"
    })
}

$noticesPath = Join-Path $projectRootPath 'THIRD-PARTY-NOTICES.txt'
if (-not (Test-Path -LiteralPath $noticesPath -PathType Leaf)) {
    throw "Missing third-party notices: $noticesPath"
}
$sourceFiles.Add([PSCustomObject]@{
    SourcePath = $noticesPath
    ArchivePath = 'THIRD-PARTY-NOTICES.txt'
})

$sortedFiles = $sourceFiles | Sort-Object ArchivePath
$manifestFiles = [Collections.Generic.List[object]]::new()
$sha256 = [Security.Cryptography.SHA256]::Create()

foreach ($file in $sortedFiles) {
    $stream = [IO.File]::OpenRead($file.SourcePath)
    try {
        $hashBytes = $sha256.ComputeHash($stream)
        $hash = [BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }

    $info = Get-Item -LiteralPath $file.SourcePath
    $manifestFiles.Add([ordered]@{
        path = $file.ArchivePath
        size = $info.Length
        sha256 = $hash
    })
}
$sha256.Dispose()

$manifest = [ordered]@{
    formatVersion = 1
    bundleVersion = $bundleVersion
    files = $manifestFiles
}
$manifestJson = $manifest | ConvertTo-Json -Depth 5 -Compress

$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

Add-Type -AssemblyName System.IO.Compression
$archiveStream = [IO.File]::Open($outputFullPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new(
        $archiveStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $false,
        [Text.Encoding]::UTF8)
    try {
        foreach ($file in $sortedFiles) {
            $entry = $archive.CreateEntry($file.ArchivePath, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTimestamp
            $input = [IO.File]::OpenRead($file.SourcePath)
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }

        $manifestEntry = $archive.CreateEntry('bundle-manifest.json', [IO.Compression.CompressionLevel]::Optimal)
        $manifestEntry.LastWriteTime = $fixedTimestamp
        $writer = [IO.StreamWriter]::new($manifestEntry.Open(), [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write($manifestJson)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $archiveStream.Dispose()
}

$outputInfo = Get-Item -LiteralPath $outputFullPath
Write-Host "Tool bundle: $($manifestFiles.Count) files, $([Math]::Round($outputInfo.Length / 1MB, 2)) MB"
