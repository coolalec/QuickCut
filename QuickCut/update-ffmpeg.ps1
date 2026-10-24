# Moves ffmpeg.json to the newest gyan.dev release build (tags like 9.0.2; git builds are ignored).
#   pwsh update-ffmpeg.ps1 [-DryRun]
# Prints the decision; when it updates, writes old= / new= to $env:GITHUB_OUTPUT for the workflow.
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$pinPath = Join-Path $PSScriptRoot 'ffmpeg.json'
$pin = Get-Content $pinPath -Raw | ConvertFrom-Json

$headers = @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'QuickCut-update-ffmpeg' }
if ($env:GH_TOKEN) { $headers.Authorization = "Bearer $env:GH_TOKEN" }
$releases = Invoke-RestMethod 'https://api.github.com/repos/GyanD/codexffmpeg/releases?per_page=100' -Headers $headers -UseBasicParsing

$latest = $releases |
    Where-Object { -not $_.draft -and -not $_.prerelease -and $_.tag_name -match '^\d+(\.\d+){1,2}$' } |
    Sort-Object { [version]$_.tag_name } -Descending |
    Select-Object -First 1
if (-not $latest) { throw 'No release builds found in GyanD/codexffmpeg' }

$new = $latest.tag_name
if ([version]$new -le [version]$pin.version) {
    "FFmpeg $($pin.version) is current (latest release build: $new)"
    return
}

$assetName = "ffmpeg-$new-full_build-shared.zip"
$asset = $latest.assets | Where-Object name -eq $assetName
if (-not $asset) { throw "Release $new has no $assetName" }
if ($asset.digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "Release $new has no SHA256 digest for $assetName" }
$sha = $Matches[1]

if ($DryRun) {
    "Would update FFmpeg $($pin.version) -> $new (sha256 $sha)"
    return
}

# Download once so a digest that does not match the file fails here rather than in every build.
$zip = Join-Path ([IO.Path]::GetTempPath()) $assetName
Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
try {
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $sha) { throw "SHA256 mismatch for ${assetName}: got $hash, expected $sha" }
}
finally { Remove-Item $zip -Force -ErrorAction SilentlyContinue }

$json = "{`n  `"version`": `"$new`",`n  `"sha256`": `"$sha`"`n}`n"
[IO.File]::WriteAllText($pinPath, $json)
"Updated FFmpeg $($pin.version) -> $new"

if ($env:GITHUB_OUTPUT) {
    "old=$($pin.version)" | Add-Content $env:GITHUB_OUTPUT
    "new=$new" | Add-Content $env:GITHUB_OUTPUT
}
