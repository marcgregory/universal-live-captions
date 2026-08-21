param(
    [string]$Stage = "$env:TEMP\opencode\uc_pkg\Stage",          # staging root (built fresh each run)
    [string]$Version = "0.5.43",
    [switch]$SkipPublish,                                        # reuse existing Stage\UniversalCaptions
    [switch]$SkipSetup,                                          # build staging only, skip ISCC
    [switch]$SkipZip                                             # build staging + (optionally) setup, skip the portable ZIP
)
# UniversalCaptions offline installer packaging build (reproducible).
# ADR-0011 (Gemini-only pipeline): the app is a single self-contained .NET publish — no Python
# runtime, no local models, no Argos packages. Speech + translation run in the Gemini Live session;
# the only local secret is the API key in Windows Credential Manager.
#
# Distribution model: both artifacts ship the same staged closure.
#   - UniversalCaptions-Setup-{Version}.exe       (recommended, end users)
#   - UniversalCaptions-{Version}-win-x64-full.zip (portable / advanced)
#
# Both artifacts install to a FLAT root layout (UniversalCaptions.App.exe at the root). The Inno
# Setup installer achieves this via its [Files] source map ({#StageDir}\UniversalCaptions\* ->
# {app}\). The portable ZIP builds from a separate zip-stage dir that mirrors this flat layout by
# copying Stage\UniversalCaptions\* to the zip-stage root.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"

function Get-Mb($path) { [Math]::Round((Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB, 1) }

if (-not $SkipPublish) {
    Write-Host "=== 1/5 publish self-contained win-x64 ==="
    if (Test-Path $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null
    dotnet publish "$root\src\UniversalCaptions.App\UniversalCaptions.App.csproj" -c Release -r win-x64 `
        --self-contained true -o "$Stage\UniversalCaptions" | Out-Null
}

Write-Host "=== 2/5 trim publish (win-x64 only, en satellites) ==="
$app = "$Stage\UniversalCaptions"
@('linux-arm','linux-arm64','linux-x64','macos-arm64','macos-x64','win-arm64','win-x86') |
    ForEach-Object { $p = "$app\runtimes\$_"; if (Test-Path $p) { Remove-Item -LiteralPath $p -Recurse -Force } }
@('cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hans','zh-Hant') |
    ForEach-Object { $p = "$app\$_"; if (Test-Path $p) { Remove-Item -LiteralPath $p -Recurse -Force } }

Write-Host "=== 3/5 write manifest ==="
$manifest = @"
UniversalCaptions offline bundle manifest (v$Version, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))
App (self-contained win-x64):          $(Get-Mb $app) MB
Stage total:                           $(Get-Mb $Stage) MB
Closure notes:
- Gemini-only pipeline (ADR-0011): no Python runtime, no local STT models, no Argos packages.
- The Gemini API key is read from Windows Credential Manager at session start; nothing is bundled.
- No silent downloads: the app talks only to the configured Gemini websocket endpoint.
"@
$manifest | Set-Content -LiteralPath "$Stage\manifest.txt" -Encoding UTF8

if ($SkipSetup -and $SkipZip) { Write-Host "=== staging done (skip setup + zip). manifest -> $Stage\manifest.txt ==="; return }

Write-Host "=== 4/5 write portable ZIP (flat root layout) ==="
if ($SkipZip) {
    Write-Host "    (skipped)"
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipOut = "$PSScriptRoot\output\UniversalCaptions-$Version-win-x64-full.zip"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipOut) | Out-Null
    if (Test-Path $zipOut) { Remove-Item -LiteralPath $zipOut -Force }

    # Build a flat zip-stage dir that mirrors the installer's {app}\ layout. The Stage dir keeps
    # its UniversalCaptions/ subdir for the ISS source map, but the portable ZIP must place
    # UniversalCaptions.App.exe at the zip root.
    $zipStage = "$env:TEMP\opencode\uc_pkg\ZipStage-$Version"
    if (Test-Path $zipStage) { Remove-Item -LiteralPath $zipStage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $zipStage | Out-Null

    # Copy the app's publish output UP to the zip-stage root (was Stage\UniversalCaptions\*).
    robocopy "$Stage\UniversalCaptions" $zipStage /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    Copy-Item "$Stage\manifest.txt" "$zipStage\manifest.txt" -Force

    # Layout invariant: App.exe at the zip-stage root, no UniversalCaptions\ subdir left over,
    # and no legacy Python/model/argos trees anywhere in the closure.
    $appExeAtRoot = Test-Path (Join-Path $zipStage 'UniversalCaptions.App.exe')
    $nestedSubdir = Test-Path (Join-Path $zipStage 'UniversalCaptions\UniversalCaptions.App.exe')
    $legacyTrees = @('py','models','argos-packages') | Where-Object { Test-Path (Join-Path $zipStage $_) }
    if (-not ($appExeAtRoot -and -not $nestedSubdir -and -not $legacyTrees)) {
        throw "Zip-stage layout invalid: appExe=$appExeAtRoot nested=$nestedSubdir legacy=[$($legacyTrees -join ',')]"
    }
    Write-Host "    zip-stage flat layout OK: App.exe at root, no legacy trees"

    [System.IO.Compression.ZipFile]::CreateFromDirectory($zipStage, $zipOut, `
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # Cleanup the staging scratch (the artifact is what we ship, not the temp dir).
    Remove-Item -LiteralPath $zipStage -Recurse -Force

    $zipMb = [Math]::Round((Get-Item $zipOut).Length / 1MB, 1)
    Write-Host "    -> $zipOut ($zipMb MB)"
}

Write-Host "=== 5/5 compile Inno Setup ==="
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found at $iscc — install Inno Setup (winget install JRSoftware.InnoSetup --scope user)" }
& $iscc "/DStageDir=$Stage" "/DAppVersion=$Version" "$PSScriptRoot\UniversalCaptions.iss"
