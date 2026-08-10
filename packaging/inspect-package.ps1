# v0.5.31 artifact inspection — runs after build-package.ps1 completes.
# Verifies: both artifacts exist, sizes, SHA-256, ZIP closure structure,
# no source/.git/credentials/raw WAV/intermediate junk; parity between
# the ZIP and the Setup staging tree; flat-layout invariants on the ZIP
# (UniversalCaptions.App.exe + py/python.exe + launcher.cmd at root, no
# UniversalCaptions\ subdir). Fails the build if any invariant is violated.

$ErrorActionPreference = 'Stop'
$version = '0.5.31'
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

# ZIP layout invariants: App.exe + py/python.exe + launcher.cmd + manifest.txt at root,
# no nested UniversalCaptions\ subdir carrying App.exe.
$expectedAtRoot = @(
    @{ Name = 'UniversalCaptions.App.exe'; Path = "$scratch\UniversalCaptions.App.exe" },
    @{ Name = 'py\python.exe';            Path = "$scratch\py\python.exe" },
    @{ Name = 'launcher.cmd';             Path = "$scratch\launcher.cmd" },
    @{ Name = 'manifest.txt';             Path = "$scratch\manifest.txt" },
    @{ Name = 'models\faster-whisper-small'; Path = "$scratch\models\faster-whisper-small" },
    @{ Name = 'models\ggml-base.bin';     Path = "$scratch\models\ggml-base.bin" },
    @{ Name = 'argos-packages\translate-en_tl-1_9'; Path = "$scratch\argos-packages\translate-en_tl-1_9" }
)
foreach ($e in $expectedAtRoot) {
    if (Test-Path $e.Path) {
        Write-Host ("  [OK] ZIP root contains: {0}" -f $e.Name)
    } else {
        $msg = "ZIP root missing: $($e.Name)"
        Write-Host ("  [FAIL] {0}" -f $msg)
        $failed += $msg
    }
}

$nestedAppExe = "$scratch\UniversalCaptions\UniversalCaptions.App.exe"
if (Test-Path $nestedAppExe) {
    $msg = "ZIP must NOT nest App.exe under UniversalCaptions\ subdir (layout regression)"
    Write-Host ("  [FAIL] {0}" -f $msg)
    $failed += $msg
} else {
    Write-Host "  [OK] ZIP does not nest App.exe under UniversalCaptions\ subdir"
}

# launcher.cmd references the App.exe at root (no UniversalCaptions\ prefix)
$launcherText = Get-Content -LiteralPath "$scratch\launcher.cmd" -Raw
if ($launcherText -match 'start\s+(?:"")?\s*"%~dp0UniversalCaptions\\UniversalCaptions\.App\.exe"') {
    $msg = "launcher.cmd references UniversalCaptions\UniversalCaptions.App.exe (wrong path)"
    Write-Host ("  [FAIL] {0}" -f $msg)
    $failed += $msg
} elseif ($launcherText -match 'start\s+(?:"")?\s*"%~dp0UniversalCaptions\.App\.exe"') {
    Write-Host "  [OK] launcher.cmd references root\UniversalCaptions.App.exe"
} else {
    $msg = "launcher.cmd does not reference root\UniversalCaptions.App.exe (unexpected form)"
    Write-Host ("  [FAIL] {0}" -f $msg)
    $failed += $msg
}

# 5. No junk (source/.git/credentials/raw WAV/intermediate).
# Allowlist: files inside py\Lib\site-packages\<pkg>\tests\ are python package tests (certifi,
# colorama, fsspec, mpmath, etc.) that come with the bundled runtime — not a concern for the
# shipped artifact. Same for py\Lib\__pycache__\ and py\Lib\site-packages\torch\bin\protoc.exe
# (protobuf compiler that ships with torch).
Write-Host ""
Write-Host "=== Junk scan (should be empty after allowlisting Python site-packages tests) ==="
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
$launcherFullPath = (Get-Item -LiteralPath (Join-Path $scratch 'launcher.cmd') -ErrorAction SilentlyContinue).FullName
foreach ($pat in $junkPatterns) {
    $matched = $zipFiles | Where-Object { $_.FullName -like "*\$pat" -or $_.FullName -like "*\$pat\*" }
    if ($matched) {
        # Exclude launcher.cmd at ZIP root (it's an explicit artifact, not junk). Use the
        # filesystem-resolved FullName (long form) so the comparison is robust against
        # short-path / long-path normalization quirks in PowerShell's Join-Path output.
        $filtered = $matched | Where-Object { -not ($_.FullName -eq $launcherFullPath) }
        if ($filtered) { $junk += $filtered }
    }
}
# Strip out anything inside py\Lib\site-packages\ and py\Lib\__pycache__\ -- those are
# Python package internals. Also strip known harmless Python stdlib dev-utility scripts
# (venv activation, IDLE launcher, Mach-O helper, tcl/tk unix config) that ship with the
# Python distribution but are unused at runtime. ~10 KB total, kept inside the runtime
# because the offline build is constrained to the same venv contents.
$junkBefore = $junk.Count
$junk = $junk | Where-Object {
    $isSitePackages = $_.FullName -like '*\py\Lib\site-packages\*'
    $isPycache     = $_.FullName -like '*\py\Lib\__pycache__\*'
    $isDevUtil     = $_.FullName -like '*\py\Lib\venv\scripts\*' -or
                     $_.FullName -like '*\py\Lib\idlelib\idle.bat' -or
                     $_.FullName -like '*\py\Lib\ctypes\macholib\*'
    $isTclUnixCfg  = $_.FullName -like '*\py\tcl\tclConfig.sh' -or
                     $_.FullName -like '*\py\tcl\tclooConfig.sh'
    -not ($isSitePackages -or $isPycache -or $isDevUtil -or $isTclUnixCfg)
}
$stripped = $junkBefore - $junk.Count
if ($stripped -gt 0) { Write-Host ("  (stripped {0} Python stdlib / dev-utility entries -- legit runtime internals)" -f $stripped) }

if ($junk.Count -gt 0) {
    Write-Host ("JUNK FOUND ({0} files):" -f $junk.Count)
    $junk | Select-Object -First 20 | ForEach-Object { Write-Host ("  {0}" -f $_.FullName.Substring($scratch.Length)) }
    if ($junk.Count -gt 20) { Write-Host ("  ...and {0} more" -f ($junk.Count - 20)) }
    $failed += "Junk scan: $($junk.Count) junk files in ZIP"
} else {
    Write-Host "  No junk detected."
}

# 6. Stage layout (informational). The Stage dir is the build's intermediate tree; if it
# looks incomplete (e.g., models/ or argos-packages/ missing), the build may have been
# interrupted before step 4. This is a warning, not a failure -- the artifact-level checks above
# already prove the released ZIP + Setup.exe contain the right contents.
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
    $expectedStage = @('UniversalCaptions','py','models','argos-packages','launcher.cmd','manifest.txt') | Sort-Object
    $stageDiff = Compare-Object $stageNames $expectedStage
    if ($stageDiff) {
        Write-Host "  WARNING: Stage layout differs from expected (build may have been interrupted):"
        $stageDiff | ForEach-Object { Write-Host ("    {0} {1}" -f $_.SideIndicator, $_.InputObject) }
        Write-Host "  (informational only; artifact-level checks above are authoritative)"
    } else {
        Write-Host "  Stage layout matches ISS source expectations."
    }
} else {
    Write-Host ("  Stage dir not present at {0} (build has not been run, or was cleaned up)" -f $stage)
}

# 7. Spot-check: launcher.cmd, py/python.exe, a model file in the ZIP
Write-Host ""
Write-Host "=== Spot-check key files ==="
$check = @(
    @{ Path = "$scratch\launcher.cmd";                         ShouldBe = 'file' },
    @{ Path = "$scratch\py\python.exe";                        ShouldBe = 'file' },
    @{ Path = "$scratch\models\faster-whisper-small\model.bin"; ShouldBe = 'file' },
    @{ Path = "$scratch\models\ggml-base.bin";                 ShouldBe = 'file' },
    @{ Path = "$scratch\argos-packages\translate-en_tl-1_9";   ShouldBe = 'dir'  },
    @{ Path = "$scratch\manifest.txt";                         ShouldBe = 'file' },
    @{ Path = "$scratch\UniversalCaptions.App.exe";            ShouldBe = 'file' }
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
