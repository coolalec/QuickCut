# Builds a Release copy into ..\dist\QuickCut with FFmpeg next to the exe.
#   powershell -ExecutionPolicy Bypass -File publish.ps1 [-FfmpegBin <folder with ffmpeg.exe, ffprobe.exe and DLLs>] [-Version 1.2.0] [-Zip]
# Without -FfmpegBin the FFmpeg shared build pinned in ffmpeg.json is used (downloaded by get-ffmpeg.ps1 if missing).
# Without -Version the version comes from the latest release tag vX.Y.Z: on the tag 1.2.0, three commits later 1.2.0.3.
# Pre-release tags (vX.Y.Z.N, made by CI) are ignored, so they never become the base.
# -Zip also writes ..\dist\QuickCut-<version>-win64.zip with a QuickCut\ folder inside, ready to unzip and run.
param(
    [string]$FfmpegBin,
    [string]$Version,
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    # Windows PowerShell turns native stderr into errors under 'Stop'; a failed git call just yields nothing.
    $ErrorActionPreference = 'Continue'
    try { $out = & git.exe -C $PSScriptRoot @args 2>$null; if ($LASTEXITCODE -eq 0) { $out } } catch { }
}

if ($Version) {
    $base = $Version; $height = 0
}
elseif ((Invoke-Git describe --tags --match 'v[0-9]*' --exclude 'v*.*.*.*' --long) -match '^v(\d+\.\d+\.\d+)-(\d+)-g') {
    $base = $Matches[1]; $height = [int]$Matches[2]
}
else {
    # No release tag yet: the csproj version plus the commit count.
    $base = ([xml](Get-Content (Join-Path $PSScriptRoot 'QuickCut.csproj') -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Select-Object -First 1
    $height = [int](Invoke-Git rev-list --count HEAD)
}
$info = if ($height -gt 0) { "$base.$height" } else { $base }

if (-not $FfmpegBin) {
    $FfmpegBin = Join-Path (& (Join-Path $PSScriptRoot 'get-ffmpeg.ps1') | Select-Object -Last 1) 'bin'
}

$distRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist'))
$dist = Join-Path $distRoot 'QuickCut'
dotnet build (Join-Path $PSScriptRoot 'QuickCut.csproj') -c Release --nologo -v q `
    "-p:Version=$base" "-p:FileVersion=$base.$height" "-p:InformationalVersion=$info"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

# Start clean, but keep the user's settings file, which lives next to the exe.
if (Test-Path $dist) {
    Get-ChildItem $dist -Force | Where-Object { $_.Name -ne 'QuickCut.settings.xml' } | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Force (Join-Path $dist 'ffmpeg\bin') | Out-Null

Copy-Item (Join-Path $PSScriptRoot 'bin\Release\QuickCut.exe') $dist
Copy-Item (Join-Path $PSScriptRoot '..\LICENSE') (Join-Path $dist 'LICENSE.txt')
Copy-Item (Join-Path $PSScriptRoot '..\THIRD-PARTY-NOTICES.txt') $dist

# ffmpeg.exe, ffprobe.exe and the DLLs of a shared build. ffplay is not used.
Get-ChildItem $FfmpegBin -File | Where-Object { $_.Name -in 'ffmpeg.exe', 'ffprobe.exe' -or $_.Extension -eq '.dll' } |
    Copy-Item -Destination (Join-Path $dist 'ffmpeg\bin')

foreach ($f in 'LICENSE', 'README.txt') {
    $p = Join-Path $FfmpegBin "..\$f"
    if (Test-Path $p) { Copy-Item $p (Join-Path $dist "ffmpeg\$f") }
}

Get-ChildItem $dist -Recurse -File | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($dist.Length + 1) } }, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } }
'Version: {0}  Total: {1:N1} MB' -f $info, ((Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)

if ($Zip) {
    # Zip a staging copy so a local settings file never ends up in the package.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $distRoot "QuickCut-$info-win64.zip"
    $stage = Join-Path ([IO.Path]::GetTempPath()) 'quickcut-zip\QuickCut'
    Remove-Item (Split-Path $stage) -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $distRoot -Filter 'QuickCut-*-win64.zip' | Remove-Item -Force
    New-Item -ItemType Directory $stage | Out-Null
    Get-ChildItem $dist -Force | Where-Object { $_.Name -ne 'QuickCut.settings.xml' } | Copy-Item -Destination $stage -Recurse
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
    Remove-Item (Split-Path $stage) -Recurse -Force
    'Zip: {0} ({1:N1} MB)' -f $zipPath, ((Get-Item $zipPath).Length / 1MB)
}

if ($env:GITHUB_OUTPUT) {
    "version=$info" | Add-Content $env:GITHUB_OUTPUT
    "height=$height" | Add-Content $env:GITHUB_OUTPUT
    if ($Zip) { "zip=$zipPath" | Add-Content $env:GITHUB_OUTPUT }
}
