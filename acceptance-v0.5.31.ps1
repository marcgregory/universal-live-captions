# v0.5.31 clean-machine acceptance harness.
#
# Run on a clean Windows machine or VM that does NOT have %TEMP%\fwv or %TEMP%\argosv
# pre-staged. The harness REMOVES those dev venvs and clears UC_FW_PYTHON /
# UC_ARGOS_PYTHON before each leg, so the only path the new InstallPathResolver can
# take is the install-root / extraction-root py\python.exe. This is the whole point.
#
# Usage:
#   pwsh -ExecutionPolicy Bypass -File acceptance-v0.5.31.ps1 `
#        -Setup packaging/output/UniversalCaptions-Setup-0.5.31.exe `
#        -Zip   packaging/output/UniversalCaptions-0.5.31-win-x64-full.zip
#
# Output:
#   acceptance-<timestamp>\log.txt          (per-event log)
#   acceptance-<timestamp>\summary.json     (machine-readable result)

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Zip,
    [int]$RunSeconds = 60,
    [switch]$SkipUninstall
)

$ErrorActionPreference = 'Stop'

$ts = Get-Date -Format 'yyyyMMdd-HHmmss'
$root = Split-Path -Parent $PSCommandPath
$base = Join-Path $root ("acceptance-{0}" -f $ts)
New-Item -ItemType Directory -Force -Path $base | Out-Null
$logPath = Join-Path $base 'log.txt'

function Write-Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Reset-HostileEnv {
    $venvNames = @('fwv','argosv')
    foreach ($name in $venvNames) {
        $p = Join-Path $env:TEMP $name
        if (Test-Path $p) {
            Write-Log ("Removing hostile venv: {0}" -f $p)
            Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    $envVars = @('UC_FW_PYTHON','UC_ARGOS_PYTHON','UC_FW_MODEL','UC_STT_MODEL_PATH',
                 'ARGOS_PACKAGES_DIR','HF_HOME','HF_HUB_OFFLINE','TRANSFORMERS_OFFLINE',
                 'PYTHONDONTWRITEBYTECODE')
    foreach ($name in $envVars) {
        if (Test-Path ("env:{0}" -f $name)) {
            Write-Log ("Unsetting env var: {0}" -f $name)
            Remove-Item ("env:{0}" -f $name) -ErrorAction SilentlyContinue
        }
    }
}

function Get-WorkerPythonProcesses() {
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" -ErrorAction SilentlyContinue
    $matched = @()
    foreach ($p in $procs) {
        $cl = $p.CommandLine
        if ($null -ne $cl -and ($cl -match 'faster_whisper_worker\.py' -or $cl -match 'argos_translate_server\.py')) {
            $matched += $p
        }
    }
    return $matched
}

function Assert-WorkerCmdline([string]$expectedPython, [string]$legLabel) {
    Write-Log ("[{0}] waiting for worker python.exe to spawn..." -f $legLabel)
    $deadline = (Get-Date).AddSeconds(30)
    $workers = @()
    while ((Get-Date) -lt $deadline) {
        $workers = Get-WorkerPythonProcesses
        if ($workers.Count -gt 0) { break }
        Start-Sleep -Seconds 2
    }
    Write-Log ("[{0}] worker python.exe count: {1}" -f $legLabel, $workers.Count)
    foreach ($w in $workers) {
        Write-Log ("  pid {0}: {1}" -f $w.ProcessId, $w.CommandLine)
    }
    if ($workers.Count -eq 0) {
        throw ("[{0}] FAIL: no worker python.exe spawned within 30s" -f $legLabel)
    }
    $ok = $false
    foreach ($w in $workers) {
        if ($w.CommandLine -like ("*{0}*" -f $expectedPython)) {
            $ok = $true
            break
        }
    }
    if (-not $ok) {
        throw ("[{0}] FAIL: no worker matched expected python {1}" -f $legLabel, $expectedPython)
    }
    Write-Log ("[{0}] PASS: worker(s) running on expected python: {1}" -f $legLabel, $expectedPython)
    return $workers
}

function Wait-ForAppExit([int]$timeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $procs = Get-Process -Name 'UniversalCaptions.App' -ErrorAction SilentlyContinue
        if (-not $procs) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Stop-AppAndWorkers() {
    $app = Get-Process -Name 'UniversalCaptions.App' -ErrorAction SilentlyContinue
    foreach ($p in $app) {
        Write-Log ("Closing App pid {0}" -f $p.Id)
        $p.CloseMainWindow() | Out-Null
    }
    if (-not (Wait-ForAppExit 30)) {
        Write-Log "App did not exit in 30s; killing"
        $app | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 5
    $orphans = Get-WorkerPythonProcesses
    foreach ($p in $orphans) {
        Write-Log ("Killing orphan python worker pid {0}" -f $p.ProcessId)
        Invoke-CimMethod -InputObject $p -MethodName 'Terminate' | Out-Null
    }
}

function Start-AppDirect([string]$exePath, [string]$legLabel) {
    Write-Log ("[{0}] starting: {1}" -f $legLabel, $exePath)
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.UseShellExecute = $false
    [System.Diagnostics.Process]::Start($psi) | Out-Null
}

$summary = [ordered]@{
    timestamp = $ts
    setup_path = (Resolve-Path $Setup).Path
    zip_path = (Resolve-Path $Zip).Path
    run_seconds = $RunSeconds
    legs = @()
}

Write-Log "=== v0.5.31 clean-machine acceptance ==="
Write-Log ("Setup: {0}" -f $summary.setup_path)
Write-Log ("Zip:   {0}" -f $summary.zip_path)
Write-Log ("Base:  {0}" -f $base)
if (-not (Test-Path $summary.setup_path)) { throw ("Setup.exe not found: {0}" -f $summary.setup_path) }
if (-not (Test-Path $summary.zip_path))   { throw ("ZIP not found: {0}" -f $summary.zip_path) }

# ============================================================
# LEG A — Setup.exe install -> direct App.exe (no launcher.cmd)
# ============================================================
Write-Log ""
Write-Log "=== LEG A: Setup.exe install -> direct App.exe (no launcher.cmd) ==="
Write-Log "Proves: installer contains everything; InstallPathResolver falls through to"
Write-Log "<install>\py\python.exe WITHOUT needing UC_FW_PYTHON / UC_ARGOS_PYTHON."

Reset-HostileEnv
$installRoot = Join-Path $env:LOCALAPPDATA 'UniversalCaptions-v0531-acceptance'
if (Test-Path $installRoot) {
    Write-Log ("Removing previous acceptance install at {0}" -f $installRoot)
    $u = Join-Path $installRoot 'Uninstall\unins000.exe'
    if (Test-Path $u) {
        Start-Process -FilePath $u -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES" -Wait -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Log ("Running Setup.exe silently: /DIR={0}" -f $installRoot)
$setupArgs = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"{0}`"" -f $installRoot
$setupProc = Start-Process -FilePath $summary.setup_path -ArgumentList $setupArgs -Wait -PassThru
Write-Log ("Setup.exe exit code: {0}" -f $setupProc.ExitCode)
if ($setupProc.ExitCode -ne 0) {
    throw ("Setup.exe failed with exit code {0}" -f $setupProc.ExitCode)
}

$appExe = Join-Path $installRoot 'UniversalCaptions.App.exe'
$pyExe  = Join-Path $installRoot 'py\python.exe'
if (-not (Test-Path $appExe)) { throw ("App.exe not at {0}" -f $appExe) }
if (-not (Test-Path $pyExe))  { throw ("python.exe not at {0}" -f $pyExe) }
Write-Log ("Install layout OK: {0}" -f $installRoot)

Reset-HostileEnv
Start-AppDirect $appExe 'A'

$workersA = Assert-WorkerCmdline $pyExe 'A'
$sttA = ($workersA | Where-Object { $_.CommandLine -match 'faster_whisper_worker\.py' }).Count
$argosA = ($workersA | Where-Object { $_.CommandLine -match 'argos_translate_server\.py' }).Count
Write-Log ("[A] smoke window {0}s..." -f $RunSeconds)
Start-Sleep -Seconds $RunSeconds

Stop-AppAndWorkers

$summary.legs += [ordered]@{
    leg = 'A'
    label = 'Setup.exe -> direct App.exe (no launcher.cmd)'
    install_root = $installRoot
    expected_python = $pyExe
    stt_workers = $sttA
    argos_workers = $argosA
    worker_cmdline_sample = ($workersA | Select-Object -First 1 -ExpandProperty CommandLine)
    pass = $true
}

if (-not $SkipUninstall) {
    Write-Log "[A] uninstalling acceptance install..."
    $u = Join-Path $installRoot 'Uninstall\unins000.exe'
    if (Test-Path $u) {
        Start-Process -FilePath $u -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES" -Wait -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 3
    if (Test-Path $installRoot) {
        $remaining = (Get-ChildItem $installRoot -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
        Write-Log ("[A] post-uninstall residual: {0} entries" -f $remaining)
        Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ============================================================
# LEG B — ZIP extract -> launcher.cmd
# ============================================================
Write-Log ""
Write-Log "=== LEG B: ZIP extract -> launcher.cmd ==="
Write-Log "Proves: the launcher.cmd path still works (it sets UC_FW_PYTHON env-var; resolver"
Write-Log "takes path 1 first)."

Reset-HostileEnv
$extractDirB = Join-Path $base 'leg-B-extract'
if (Test-Path $extractDirB) { Remove-Item -LiteralPath $extractDirB -Recurse -Force }
New-Item -ItemType Directory -Force -Path $extractDirB | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Log ("[B] extracting ZIP to {0}" -f $extractDirB)
[System.IO.Compression.ZipFile]::ExtractToDirectory($summary.zip_path, $extractDirB)
Write-Log ("[B] extracted; top-level: {0}" -f ((Get-ChildItem $extractDirB -Force | Select-Object -ExpandProperty Name) -join ', '))

$launcherB = Join-Path $extractDirB 'launcher.cmd'
$appExeB = Join-Path $extractDirB 'UniversalCaptions.App.exe'
$pyExeB = Join-Path $extractDirB 'py\python.exe'
if (-not (Test-Path $launcherB)) { throw "launcher.cmd missing in ZIP" }
if (-not (Test-Path $appExeB))   { throw "App.exe missing in ZIP" }
if (-not (Test-Path $pyExeB))    { throw "python.exe missing in ZIP" }

Write-Log "[B] running launcher.cmd..."
Start-Process -FilePath 'cmd.exe' -ArgumentList "/c", "`"$launcherB`"" -ErrorAction SilentlyContinue
Start-Sleep -Seconds 5

$workersB = Assert-WorkerCmdline $pyExeB 'B'
$sttB = ($workersB | Where-Object { $_.CommandLine -match 'faster_whisper_worker\.py' }).Count
$argosB = ($workersB | Where-Object { $_.CommandLine -match 'argos_translate_server\.py' }).Count
Write-Log ("[B] smoke window {0}s..." -f $RunSeconds)
Start-Sleep -Seconds $RunSeconds

Stop-AppAndWorkers

$summary.legs += [ordered]@{
    leg = 'B'
    label = 'ZIP extract -> launcher.cmd'
    extract_root = $extractDirB
    expected_python = $pyExeB
    stt_workers = $sttB
    argos_workers = $argosB
    worker_cmdline_sample = ($workersB | Select-Object -First 1 -ExpandProperty CommandLine)
    pass = $true
}

# ============================================================
# LEG C — ZIP extract -> direct App.exe (no launcher.cmd) — KEY LEG
# ============================================================
Write-Log ""
Write-Log "=== LEG C: ZIP extract -> direct App.exe (no launcher.cmd) ==="
Write-Log "KEY LEG: without launcher.cmd, UC_FW_PYTHON / UC_ARGOS_PYTHON are NOT set; the"
Write-Log "resolver must take path 2 (<extraction>\py\python.exe via AppContext.BaseDirectory)."

Reset-HostileEnv
$extractDirC = Join-Path $base 'leg-C-extract'
if (Test-Path $extractDirC) { Remove-Item -LiteralPath $extractDirC -Recurse -Force }
New-Item -ItemType Directory -Force -Path $extractDirC | Out-Null
Write-Log ("[C] extracting ZIP to {0}" -f $extractDirC)
[System.IO.Compression.ZipFile]::ExtractToDirectory($summary.zip_path, $extractDirC)

$appExeC = Join-Path $extractDirC 'UniversalCaptions.App.exe'
$pyExeC = Join-Path $extractDirC 'py\python.exe'
if (-not (Test-Path $appExeC)) { throw "App.exe missing in ZIP for leg C" }
if (-not (Test-Path $pyExeC))  { throw "python.exe missing in ZIP for leg C" }

Start-AppDirect $appExeC 'C'

$workersC = Assert-WorkerCmdline $pyExeC 'C'
$sttC = ($workersC | Where-Object { $_.CommandLine -match 'faster_whisper_worker\.py' }).Count
$argosC = ($workersC | Where-Object { $_.CommandLine -match 'argos_translate_server\.py' }).Count
Write-Log ("[C] smoke window {0}s..." -f $RunSeconds)
Start-Sleep -Seconds $RunSeconds

Stop-AppAndWorkers

$summary.legs += [ordered]@{
    leg = 'C'
    label = 'ZIP extract -> direct App.exe (no launcher.cmd) — KEY LEG'
    extract_root = $extractDirC
    expected_python = $pyExeC
    stt_workers = $sttC
    argos_workers = $argosC
    worker_cmdline_sample = ($workersC | Select-Object -First 1 -ExpandProperty CommandLine)
    pass = $true
}

$summary.all_pass = ($summary.legs | Where-Object { -not $_.pass }).Count -eq 0
$summaryJson = $summary | ConvertTo-Json -Depth 6
Set-Content -LiteralPath (Join-Path $base 'summary.json') -Value $summaryJson -Encoding UTF8
Write-Log ""
Write-Log "=== Acceptance summary ==="
Write-Log $summaryJson
Write-Log ""
Write-Log ("Evidence: {0}" -f $base)
Write-Log ("Done. all_pass = {0}" -f $summary.all_pass)
