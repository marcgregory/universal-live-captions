param(
    [string]$Stage = "$env:TEMP\opencode\uc_pkg\Stage",          # staging root (built fresh each run)
    [string]$Version = "0.5.31",
    [switch]$SkipPublish,                                        # reuse existing Stage\UniversalCaptions
    [switch]$SkipSetup,                                          # build staging only, skip ISCC
    [switch]$SkipZip                                             # build staging + (optionally) setup, skip the portable ZIP
)
# UniversalCaptions offline installer packaging build (reproducible).
# Builds: self-contained win-x64 app publish -> trimmed -> merged/pruned Python runtime -> bundled
# model + Argos packages -> launcher -> Inno Setup .exe + portable ZIP. Evidence of sizes/closure
# is written to the staging root as manifest.txt. Dev venvs are never modified.
#
# Distribution model (v0.5.31+): both artifacts ship the same staged closure, so the runtime and
# model behavior are identical regardless of how the user installs.
#   - UniversalCaptions-Setup-{Version}.exe       (recommended, end users)
#   - UniversalCaptions-{Version}-win-x64-full.zip (portable / advanced)
#
# Both artifacts install to a FLAT root layout (UniversalCaptions.App.exe + py/ + models/ +
# argos-packages/ + launcher.cmd as siblings). The Inno Setup installer achieves this via its
# [Files] source map ({#StageDir}\UniversalCaptions\* -> {app}\, {#StageDir}\py\* -> {app}\py\).
# The portable ZIP builds from a separate zip-stage dir that mirrors this flat layout by copying
# Stage\UniversalCaptions\* (the app's publish output) to the zip-stage root, then symlinking
# py/, models/, argos-packages/, launcher.cmd, manifest.txt as siblings.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"

function Get-Mb($path) { [Math]::Round((Get-ChildItem -LiteralPath $path -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB, 1) }

if (-not $SkipPublish) {
    Write-Host "=== 1/7 publish self-contained win-x64 ==="
    if (Test-Path $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $Stage | Out-Null
    dotnet publish "$root\src\UniversalCaptions.App\UniversalCaptions.App.csproj" -c Release -r win-x64 `
        --self-contained true -o "$Stage\UniversalCaptions" | Out-Null
}

Write-Host "=== 2/7 trim publish (win-x64 only, en satellites) ==="
$app = "$Stage\UniversalCaptions"
@('linux-arm','linux-arm64','linux-x64','macos-arm64','macos-x64','win-arm64','win-x86') |
    ForEach-Object { $p = "$app\runtimes\$_"; if (Test-Path $p) { Remove-Item -LiteralPath $p -Recurse -Force } }
@('cs','de','es','fr','it','ja','ko','pl','pt-BR','ru','tr','zh-Hans','zh-Hant') |
    ForEach-Object { $p = "$app\$_"; if (Test-Path $p) { Remove-Item -LiteralPath $p -Recurse -Force } }

Write-Host "=== 3/7 build packaging Python runtime (merged + pruned copy of dev venvs) ==="
$uvPython = "$env:APPDATA\uv\python\cpython-3.11-windows-x86_64-none"
$pyStage = "$Stage\py"
robocopy $uvPython $pyStage /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
robocopy "$env:TEMP\fwv\Lib\site-packages" "$pyStage\Lib\site-packages" /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
robocopy "$env:TEMP\argosv\Lib\site-packages" "$pyStage\Lib\site-packages" /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
$sp = "$pyStage\Lib\site-packages"

# Prune (verified runtime closure; torch stays — required by stanza SBD on the en->tl path):
# spacy tree, compiler/training-only torch trees, dev tooling, deep __pycache__.
$drop = @('spacy','spacy_legacy','spacy_loggers','thinc','blis','cymem','preshed','murmurhash','srsly',
          'catalogue','wasabi','weasel','functorch','pkg_resources','pip','setuptools','pygments','rich')
foreach ($name in $drop) {
    Get-ChildItem $sp -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $name -or $_.Name -like "$name-*" -or $_.Name -like "spacy_*" } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}
foreach ($p in @("$sp\torch\include","$sp\torch\share","$sp\torch\ao\pruning\_experimental",
                 "$sp\torch\_inductor\kernel\vendored_templates","$sp\onnxruntime\tools",
                 "$sp\torch-2.13.0.dist-info\licenses")) {
    if (Test-Path $p) { cmd /c rd /s /q "\\?\$p" 2>&1 | Out-Null }
}
Copy-Item -LiteralPath "$sp\..\..\torch-2.13.0.dist-info\licenses\LICENSE" -Destination "$sp\torch\LICENSE.txt" -Force -ErrorAction SilentlyContinue
Get-ChildItem $pyStage -Directory -Recurse -Filter "__pycache__" -ErrorAction SilentlyContinue |
    ForEach-Object { cmd /c rd /s /q "\\?\$($_.FullName)" 2>&1 | Out-Null }

Write-Host "=== 4/7 stage model + argos packages ==="
New-Item -ItemType Directory -Force -Path "$Stage\models", "$Stage\argos-packages" | Out-Null
$hfSnapshot = Get-ChildItem "$env:USERPROFILE\.cache\huggingface\hub\models--Systran--faster-whisper-small\snapshots" -Directory | Select-Object -First 1
New-Item -ItemType Directory -Force -Path "$Stage\models\faster-whisper-small" | Out-Null
Copy-Item -Path "$($hfSnapshot.FullName)\*" -Destination "$Stage\models\faster-whisper-small" -Recurse -Force
Copy-Item "$root\artifacts\models\ggml-base.bin" "$Stage\models\ggml-base.bin" -Force
Copy-Item "$env:USERPROFILE\.local\share\argos-translate\packages\translate-en_tl-1_9" "$Stage\argos-packages" -Recurse -Force
Copy-Item "$PSScriptRoot\launcher.cmd" "$Stage\launcher.cmd" -Force

Write-Host "=== 5/7 write manifest ==="
$manifest = @"
UniversalCaptions offline bundle manifest (v$Version, $(Get-Date -Format 'yyyy-MM-dd HH:mm'))
App (self-contained win-x64, trimmed): $(Get-Mb $app) MB
Python runtime (pruned):               $(Get-Mb $pyStage) MB
  torch (required by stanza SBD):      $(Get-Mb "$sp\torch") MB
faster-whisper small model (offline):  $(Get-Mb "$Stage\models\faster-whisper-small") MB
ggml-base fallback model:              $([Math]::Round((Get-Item "$Stage\models\ggml-base.bin").Length/1MB,1)) MB
Argos packages (en->tl closure):       $(Get-Mb "$Stage\argos-packages") MB
Stage total:                           $(Get-Mb $Stage) MB
Dependency closure notes:
- spacy tree, pip/setuptools/pygments/rich, torch include/share, torch compiler/training extras, onnxruntime tools, deep third-party licenses omitted.
- torch + torchgen + sympy + mpmath + networkx required by torch import / stanza SBD (restored per smoke test).
- torch LICENSE.txt preserved; deep third-party license sub-tree omitted (0.3 MB of source-vendored texts).
- ARGOS_PACKAGES_DIR -> bundled en->tl package; HF_HUB_OFFLINE=1 -> no silent downloads.
"@
$manifest | Set-Content -LiteralPath "$Stage\manifest.txt" -Encoding UTF8

if ($SkipSetup -and $SkipZip) { Write-Host "=== staging done (skip setup + zip). manifest -> $Stage\manifest.txt ==="; return }

Write-Host "=== 6/7 write portable ZIP (flat root layout) ==="
if ($SkipZip) {
    Write-Host "    (skipped)"
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipOut = "$PSScriptRoot\output\UniversalCaptions-$Version-win-x64-full.zip"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $zipOut) | Out-Null
    if (Test-Path $zipOut) { Remove-Item -LiteralPath $zipOut -Force }

    # Build a flat zip-stage dir that mirrors the installer's {app}\ layout. The Stage dir keeps
    # its UniversalCaptions/ subdir for the ISS source map, but the portable ZIP must place
    # UniversalCaptions.App.exe at the same level as py/, so launcher.cmd (%~dp0\App.exe) and
    # InstallPathResolver (AppContext.BaseDirectory\py\python.exe) both resolve correctly.
    $zipStage = "$env:TEMP\opencode\uc_pkg\ZipStage-$Version"
    if (Test-Path $zipStage) { Remove-Item -LiteralPath $zipStage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $zipStage | Out-Null

    # Copy the app's publish output UP to the zip-stage root (was Stage\UniversalCaptions\*).
    robocopy "$Stage\UniversalCaptions" $zipStage /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null

    # Copy runtime/model/argos/launcher siblings so the zip-stage root has them as flat siblings.
    robocopy "$Stage\py" "$zipStage\py" /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    robocopy "$Stage\models" "$zipStage\models" /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    robocopy "$Stage\argos-packages" "$zipStage\argos-packages" /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    Copy-Item "$Stage\launcher.cmd" "$zipStage\launcher.cmd" -Force
    Copy-Item "$Stage\manifest.txt" "$zipStage\manifest.txt" -Force

    # Layout invariant: App.exe + py/python.exe + launcher.cmd at the zip-stage root, no
    # UniversalCaptions\ subdir left over.
    $appExeAtRoot = Test-Path (Join-Path $zipStage 'UniversalCaptions.App.exe')
    $pyAtRoot     = Test-Path (Join-Path $zipStage 'py\python.exe')
    $launcherAtRoot = Test-Path (Join-Path $zipStage 'launcher.cmd')
    $nestedSubdir = Test-Path (Join-Path $zipStage 'UniversalCaptions\UniversalCaptions.App.exe')
    if (-not ($appExeAtRoot -and $pyAtRoot -and $launcherAtRoot -and -not $nestedSubdir)) {
        throw "Zip-stage layout invalid: appExe=$appExeAtRoot py=$pyAtRoot launcher=$launcherAtRoot nested=$nestedSubdir"
    }
    Write-Host "    zip-stage flat layout OK: App.exe + py/ + launcher.cmd at root"

    [System.IO.Compression.ZipFile]::CreateFromDirectory($zipStage, $zipOut, `
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # Cleanup the staging scratch (the artifact is what we ship, not the temp dir).
    Remove-Item -LiteralPath $zipStage -Recurse -Force

    $zipMb = [Math]::Round((Get-Item $zipOut).Length / 1MB, 1)
    Write-Host "    -> $zipOut ($zipMb MB)"
}

Write-Host "=== 7/7 compile Inno Setup ==="
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found at $iscc — install Inno Setup (winget install JRSoftware.InnoSetup --scope user)" }
& $iscc "/DStageDir=$Stage" "/DAppVersion=$Version" "$PSScriptRoot\UniversalCaptions.iss"
