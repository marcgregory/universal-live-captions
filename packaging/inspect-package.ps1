# v0.5.43 artifact inspection — runs after build-package.ps1 completes.
# Verifies: both artifacts exist, sizes, SHA-256, ZIP closure structure,
# no source/.git/credentials/raw WAV/intermediate junk; flat-layout invariants
# on the ZIP (UniversalCaptions.App.exe at root); and the ADR-0011 closure rule
# (no legacy Python/model/argos trees). Fails the build if any invariant is violated.

$ErrorActionPreference = 'Stop'
$version = '0.5.43'
$outDir = Join-Path $PSScriptRoot 'output'
$stage = "$env:TEMP\opencode\uc_pkg\Stage"
$scratch = "$env:TEMP\opencode\uc_pkg\Inspect-$version"

$setup = Join-Path $outDir "UniversalCaptions-Setup-$version.exe"
$zip   = Join-Path $outDir "UniversalCaptions-$version-win-x64-full.zip"

$failed = @()

# 1. Both artifacts exist
Write-Host "=== Artifact existence ==="
if (-not (Test-Path $setup)) { throw "Missing Setup.exe: $setup" }
if (-not (Test-Path $zip))   { throw "Missing ZIP: $zip" }
$setupSize = (Get-Item $setup).Length
$zipSize   = (Get-Item $zip).Length
Write-Host ("Setup.exe : {0} ({1} MB)" -f $setup, [Math]::Round($setupSize/1MB,1))
Write-Host ("ZIP       : {0} ({1} MB)" -f $zip,   [Math]::Round($zipSize/1MB,1))

# 2. Sizes + SHA-256
Write-Host ""
Write-Host "=== SHA-256 ==="
$setupSha = (Get-FileHash $setup -Algorithm SHA256).Hash
$zipSha   = (Get-FileHash $zip   -Algorithm SHA256).Hash
Write-Host "Setup.exe : $setupSha"
Write-Host "ZIP       : $zipSha"

# 3. Setup.exe is a valid PE (installer dropped by ISCC)
Write-Host ""
Write-Host "=== Setup.exe PE header ==="
$setupBytes = [System.IO.File]::ReadAllBytes($setup)
$dos = [System.Text.Encoding]::ASCII.GetString($setupBytes, 0, 2)
if ($dos -ne 'MZ') { throw "Setup.exe is not a valid PE (no MZ header)" }
Write-Host ("Setup.exe MZ header OK (first 2 bytes {0},{1})" -f $setupBytes[0], $setupBytes[1])

# 4. ZIP opens + structure check
Write-Host ""
Write-Host "=== ZIP closure ==="
if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $scratch)

# ZIP layout invariants: App.exe + manifest.txt at root; no nested UniversalCaptions\ subdir
# carrying App.exe; and NO legacy Python/model/argos trees (ADR-0011 Gemini-only closure).
$appExeAtRoot = Test-Path "$scratch\UniversalCaptions.App.exe"
if ($appExeAtRoot) {
    Write-Host "  [OK] ZIP root contains: UniversalCaptions.App.exe"
} else {
    $msg = "ZIP root missing: UniversalCaptions.App.exe"
    Write-Host ("  [FAIL] {0}" -f $msg)
    $failed += $msg
}
if (-not (Test-Path "$scratch\manifest.txt")) {
    $msg = "ZIP root missing: manifest.txt"
    Write-Host ("  [FAIL] {0}" -f $msg)
    $failed += $msg
}

$nestedAppExe = "$scratch\UniversalCaptions\UniversalCaptions.App.exe"
if (Test-Path $nestedAppExe) {
    $msg = "ZIP must NOT nest App.exe under UniversalCaptions\ subdir (layout regression)"
    Write-Host ("  [FAIL] {0}" -f $msg)
    $failed += $msg
} else {
    Write-Host "  [OK] ZIP does not nest App.exe under UniversalCaptions\ subdir"
}

foreach ($legacy in @('py','models','argos-packages')) {
    if (Test-Path "$scratch\$legacy") {
        $msg = "ZIP contains legacy '$legacy' tree (ADR-0011 violation: Gemini-only closure)"
        Write-Host ("  [FAIL] {0}" -f $msg)
        $failed += $msg
    } else {
        Write-Host "  [OK] no legacy '$legacy' tree"
    }
}

# 5. No junk (source/.git/credentials/raw media/intermediate).
Write-Host ""
Write-Host "=== Junk scan ==="
$junkPatterns = @(
    '.git', '.vs', '\bin\', '\obj\', 'node_modules',
    '*.user', '*.suo', '*.sln.docstates', '*.wav', '*.mp3', '*.mp4',
    '*.m4a', '*.csv', '*.log', '*.ps1', '*.sh', '*.cmd', '*.bat',
    '*.pfx', '*.key', '*.env', '*.env.local', '*.env.prod',
    'credentials*', '*.cred', 'appsettings*.json'
)
$zipBytes = (Get-ChildItem $scratch -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
$zipFiles = Get-ChildItem $scratch -Recurse -File -ErrorAction SilentlyContinue
Write-Host ("ZIP total uncompressed size: {0} MB" -f [Math]::Round($zipBytes/1MB,1))
Write-Host ("ZIP file count: {0}" -f $zipFiles.Count)

$junk = @()
foreach ($pat in $junkPatterns) {
    $matched = $zipFiles | Where-Object { $_.FullName -like "*\$pat" -or $_.FullName -like "*\$pat\*" }
    if ($matched) { $junk += $matched }
}

if ($junk.Count -gt 0) {
    Write-Host ("JUNK FOUND ({0} files):" -f $junk.Count)
    $junk | Select-Object -First 20 | ForEach-Object { Write-Host ("  {0}" -f $_.FullName.Substring($scratch.Length)) }
    if ($junk.Count -gt 20) { Write-Host ("  ...and {0} more" -f ($junk.Count - 20)) }
    $failed += "Junk scan: $($junk.Count) junk files in ZIP"
} else {
    Write-Host "  No junk detected."
}

# 6. Stage layout (informational).
Write-Host ""
Write-Host "=== Setup staging closure (informational) ==="
if (Test-Path $stage) {
    $stageTop = Get-ChildItem $stage -Force
    Write-Host "Stage top-level entries:"
    $stageTop | ForEach-Object {
        $name = $_.Name
        $size = if ($_.PSIsContainer) { '<dir>' } else { $_.Length }
        Write-Host ("  {0,-40} {1}" -f $name, $size)
    }
    $stageNames = $stageTop | Select-Object -ExpandProperty Name | Sort-Object
    $expectedStage = @('UniversalCaptions','manifest.txt') | Sort-Object
    $stageDiff = Compare-Object $stageNames $expectedStage
    if ($stageDiff) {
        Write-Host "  WARNING: Stage layout differs from expected:"
        $stageDiff | ForEach-Object { Write-Host ("    {0} {1}" -f $_.SideIndicator, $_.InputObject) }
        Write-Host "  (informational only; artifact-level checks above are authoritative)"
    } else {
        Write-Host "  Stage layout matches ISS source expectations."
    }
} else {
    Write-Host ("  Stage dir not present at {0} (build has not been run, or was cleaned up)" -f $stage)
}

# 7. Spot-check key files in the ZIP
Write-Host ""
Write-Host "=== Spot-check key files ==="
$check = @(
    @{ Path = "$scratch\manifest.txt";              ShouldBe = 'file' },
    @{ Path = "$scratch\UniversalCaptions.App.exe"; ShouldBe = 'file' }
)
foreach ($c in $check) {
    if (-not (Test-Path $c.Path)) { Write-Host ("  [MISSING] {0}" -f $c.Path); $failed += "Missing: $($c.Path)"; continue }
    $isDir = (Get-Item $c.Path).PSIsContainer
    $kind = if ($isDir) { 'dir' } else { 'file' }
    if ($kind -ne $c.ShouldBe) { Write-Host ("  [WRONG TYPE] {0} (expected {1}, got {2})" -f $c.Path, $c.ShouldBe, $kind); $failed += "Wrong type: $($c.Path)"; continue }
    if ($kind -eq 'file') {
        $size = (Get-Item $c.Path).Length
        Write-Host ("  [OK] {0} ({1} MB)" -f $c.Path, [Math]::Round($size/1MB,1))
    } else {
        Write-Host ("  [OK] {0} (dir)" -f $c.Path)
    }
}

# 8. Print manifest for the audit trail
Write-Host ""
Write-Host "=== manifest.txt (from staged closure) ==="
Get-Content "$scratch\manifest.txt"

# 9. Save inspection log
$out = [PSCustomObject]@{
    Version = $version
    SetupPath = $setup
    ZipPath = $zip
    SetupSizeBytes = $setupSize
    ZipSizeBytes = $zipSize
    SetupSha256 = $setupSha
    ZipSha256 = $zipSha
    ZipUnpackedBytes = $zipBytes
    ZipFileCount = $zipFiles.Count
    JunkCount = $junk.Count
    FailedInvariants = $failed
    InspectedAt = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
}
$outJson = $out | ConvertTo-Json -Depth 5
Write-Host ""
Write-Host "=== Inspection JSON ==="
Write-Host $outJson
Set-Content -LiteralPath "$scratch\inspection.json" -Encoding UTF8 -Value $outJson

Write-Host ""
Write-Host ("Inspection artifacts saved to: {0}" -f $scratch)
if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host ("=== INSPECTION FAILED ({0} invariant violation(s)) ===" -f $failed.Count)
    $failed | ForEach-Object { Write-Host ("  - {0}" -f $_) }
    throw "Inspection failed: $($failed.Count) invariant violation(s)"
}
Write-Host ""
Write-Host "=== Inspection PASS ==="
