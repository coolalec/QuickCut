# Downloads the FFmpeg shared build pinned in ffmpeg.json (gyan.dev, via GyanD/codexffmpeg releases)
# into ..\ffmpeg-<version>-full_build-shared and prints that folder. Skips the download if it is already there.
#   powershell -ExecutionPolicy Bypass -File get-ffmpeg.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$pin = Get-Content (Join-Path $PSScriptRoot 'ffmpeg.json') -Raw | ConvertFrom-Json
$name = "ffmpeg-$($pin.version)-full_build-shared"
$dest = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\$name"))

if (-not (Test-Path (Join-Path $dest 'bin\ffmpeg.exe'))) {
    $url = "https://github.com/GyanD/codexffmpeg/releases/download/$($pin.version)/$name.zip"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "quickcut-$name"
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $zip = Join-Path $tmp "$name.zip"
        Write-Host "Downloading $url"
        Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($hash -ne $pin.sha256) { throw "SHA256 mismatch for $name.zip: got $hash, expected $($pin.sha256)" }

        Expand-Archive $zip (Join-Path $tmp 'x')
        $src = Join-Path $tmp "x\$name"
        if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
        New-Item -ItemType Directory (Join-Path $dest 'bin') | Out-Null
        # Only what QuickCut ships: ffmpeg.exe, ffprobe.exe and their DLLs. No ffplay, headers, import libs or docs.
        Get-ChildItem (Join-Path $src 'bin') -File | Where-Object { $_.Name -ne 'ffplay.exe' } |
            Copy-Item -Destination (Join-Path $dest 'bin')
        foreach ($f in 'LICENSE', 'README.txt') {
            if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) $dest }
        }
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$dest
