# Writes Markdown release notes for a tag: the commits since the previous version, plus download facts.
#   pwsh release-notes.ps1 -Tag v1.2.0 -Zip ..\dist\QuickCut-1.2.0-win64.zip [-Out notes.md]
# A release (vX.Y.Z) lists everything since the previous release; a pre-release (vX.Y.Z.N) since the previous tag of any kind.
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Zip,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
# git writes UTF-8; without this the console code page garbles Chinese commit subjects.
try { [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false) } catch { }
$repo ='https://github.com/coolalec/QuickCut'

function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    try { $out = & git.exe -C $PSScriptRoot @args 2>$null; if ($LASTEXITCODE -eq 0) { $out } } catch { }
}

# Start from HEAD^: when releasing, the new tag already sits on HEAD.
$exclude = if ($Tag -match '^v\d+\.\d+\.\d+$') { @('--exclude', 'v*.*.*.*') } else { @() }
$prev = Invoke-Git describe --tags --abbrev=0 --match 'v[0-9]*' @exclude 'HEAD^'
$range = if ($prev) { "$prev..HEAD" } else { 'HEAD' }
$commits = @(Invoke-Git -c core.quotepath=off -c i18n.logOutputEncoding=UTF-8 log --no-merges --pretty='format:- %s (%h)' $range)

$pin = Get-Content (Join-Path $PSScriptRoot 'ffmpeg.json') -Raw | ConvertFrom-Json
$zipName = Split-Path $Zip -Leaf
$sha = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()

$lines = @('## 變更') + $(if ($commits.Count) { $commits } else { '- （沒有新的 commit）' }) + @(
    ''
    '## 下載'
    "下載 ``$zipName``，解壓後執行 ``QuickCut\QuickCut.exe``，不需安裝。"
    ''
    "- 附帶 FFmpeg $($pin.version)（gyan.dev full_build-shared，GPLv3）"
    "- SHA256：``$sha``"
    "- 驗證檔案確實由本專案的 GitHub Actions 建置：``gh attestation verify $zipName --repo coolalec/QuickCut``"
)
if ($prev) { $lines += @('', "**完整差異**：$repo/compare/$prev...$Tag") }

$text = ($lines -join "`n") + "`n"
if ($Out) { [IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false))) } else { $text }
