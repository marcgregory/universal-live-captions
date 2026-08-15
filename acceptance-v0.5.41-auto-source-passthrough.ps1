# Live acceptance: v0.5.41 Argos Auto-source en->tl translation + non-English passthrough.
# Scenario (user-observed): a Hindi-learning tutorial spoken in English. Source = Auto, provider
# = Argos, target = tl. Expected behavior:
#   - the English narration is auto-detected as en and translated to Tagalog (en->tl bundled pair)
#   - Hindi phrases (Devanagari) pass through UNTRANSLATED (no hi->tl path - graceful degradation)
#   - the status never shows "unavailable"/error
# The same harness also runs against a plain English WAV (default), where the Devanagari/Cyrillic
# counts are expected to be 0 and the en->tl checks still hold.
#
# Note: this file must stay ASCII-only (PowerShell 5.1 reads BOM-less .ps1 as ANSI). Non-ASCII
# matching is done via Unicode code points, not literal glyphs.
param(
    [string]$Wav = "artifacts\samples\english_sustained_90s.wav",
    [int]$FirstCaptionTimeoutSec = 60,
    [string]$Log = "v0541_auto_source_passthrough.log"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = Join-Path $PWD "src\UniversalCaptions.App\bin\Release\net8.0-windows\UniversalCaptions.App.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Release exe not found: $exe" }
$wav = if ([System.IO.Path]::IsPathRooted($Wav)) { $Wav } else { Join-Path $PWD $Wav }
if (-not (Test-Path -LiteralPath $wav)) { throw "Audio not found: $wav" }
$settingsPath = Join-Path $env:LOCALAPPDATA "UniversalCaptions\settings.json"
$utf8 = [System.Text.Encoding]::UTF8

function Log($msg) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg
    Write-Host $line
    [System.IO.File]::AppendAllText($Log, $line + [Environment]::NewLine, $utf8)
}

# ---- 0. baseline settings: Argos (provider 0), source Auto (null), translation ON, target tl ----
$baseline = '{"version":1,"deviceId":null,"language":null,"translationEnabled":true,"targetLanguage":"tl","provider":0,"opacity":1,"fontSize":16,"clickThrough":false,"overlayLeft":801.6,"overlayTop":231.2,"overlayExpanded":true}'
if (Test-Path -LiteralPath $settingsPath) { Copy-Item -LiteralPath $settingsPath -Destination "$settingsPath.bak" -Force }
New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
Set-Content -LiteralPath $settingsPath -Value $baseline -Encoding UTF8
Log "Installed baseline settings (Argos, source Auto, en->tl, translation ON)"

$env:PATH = "C:\Users\TOGODB~1\AppData\Local\Temp\argosv\Scripts;C:\Users\TOGODB~1\AppData\Local\Temp\fwv\Scripts;$env:PATH"
Remove-Item Env:UC_STT_ENGINE -ErrorAction SilentlyContinue

Get-Process -Name "UniversalCaptions.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
if (Test-Path -LiteralPath $Log) { Remove-Item -LiteralPath $Log -Force }

$errOut = Join-Path $PWD "v0541_auto_source_stderr.log"
if (Test-Path -LiteralPath $errOut) { Remove-Item -LiteralPath $errOut -Force }
Log "Launching app: $exe"
$p = Start-Process -FilePath $exe -WorkingDirectory $PWD -RedirectStandardError $errOut -PassThru
$procId = $p.Id

# ---------------- UIA helpers ----------------
function Find-ControlWindow([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $cands = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $cands) {
        $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Start Captions")
        $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
        if ($btn) { return $w }
    }
    return $null
}
function Find-OverlayWindow([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $cands = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $cands) {
        if ($w.Current.Name -eq "Captions") { return $w }
    }
    return $null
}
function Find-Ctrl($window, $autoId) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $autoId)
    return $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Invoke-Button($window, $name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $b = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $b) { throw "Button '$name' not found" }
    ($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Get-TextByName($window, $autoId) {
    $el = Find-Ctrl $window $autoId
    if ($el) { return $el.Current.Name }
    return ""
}
function Get-ComboCurrent($window, $autoId) {
    $combo = Find-Ctrl $window $autoId
    if (-not $combo) { return "" }
    $tCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $els = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tCond)
    foreach ($el in $els) { if ($el.Current.Name) { return $el.Current.Name } }
    return ""
}
function Get-PythonProcesses {
    Get-CimInstance Win32_Process -Filter "Name = 'python.exe' or Name = 'python3.exe'" -ErrorAction SilentlyContinue |
        Select-Object ProcessId, @{n='Cmd';e={$_.CommandLine}}
}
function Get-STTWorkerSet {
    $p = Get-PythonProcesses | Where-Object { $_.Cmd -match 'faster_whisper_worker' }
    return @($p | ForEach-Object { $_.ProcessId } | Sort-Object)
}
function Get-ArgosServer {
    Get-PythonProcesses | Where-Object { $_.Cmd -match 'argos' } | Select-Object -First 1
}
function Get-OverlayText([int]$procId) {
    $win = Find-OverlayWindow $procId
    if (-not $win) { return @() }
    $tCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $els = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tCond)
    $parts = @()
    foreach ($el in $els) {
        $n = $el.Current.Name
        if ($n -and $n.Length -gt 0) { $parts += $n }
    }
    return $parts
}

# ---------------- language classification (ASCII-only source) ----------------
$devanagariRx = '[\u0900-\u097F]'
$cyrillicRx = '[\u0400-\u04FF]'
$tagalogRx = '(?i)\b(?:mga|ang|namin|natin|tayo|inyong|iyong|ninyo|aming|ating|kanilang|sila|kami|kayo|lahat|ngayon|salamat|magandang|pakibuksan|pahina|magsalita|makinig|unang|pagbati|pagpapakilala|tandaan|pakikinig|sesyon|pagsasanay|kasalukuyan|wakas|mahusay|magsisimula|sanlinggo|pangalan|ako|nito|ito)\b'
function Has-Script([string]$t, [string]$rx) {
    if ([string]::IsNullOrWhiteSpace($t)) { return $false }
    return [regex]::IsMatch($t, $rx)
}
function Is-Tagalog([string]$t) {
    if ([string]::IsNullOrWhiteSpace($t)) { return $false }
    return [regex]::IsMatch($t, $tagalogRx)
}
function Is-RealCaption([string]$t) {
    if ([string]::IsNullOrWhiteSpace($t)) { return $false }
    if ($t -eq "Listening." -or $t -eq "Live Caption" -or $t -eq "EN" -or $t -eq "TL" -or $t -eq "JA" -or $t -eq "?") { return $false }
    if ($t.Length -le 1) { return $false }
    return $true
}
function Get-CaptionLines([int]$procId) {
    $parts = Get-OverlayText $procId
    return @($parts | Where-Object { Is-RealCaption $_ })
}
function Wait-CaptionsLang([int]$procId, [string]$lang, [int]$timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        foreach ($c in (Get-CaptionLines $procId)) {
            if ($lang -eq "tl" -and (Is-Tagalog $c)) { return $c }
            if ($lang -eq "dev" -and (Has-Script $c $devanagariRx)) { return $c }
        }
        Start-Sleep -Milliseconds 500
    }
    return ""
}
function Wait-AnyCaption([int]$procId, [int]$timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if ((Get-CaptionLines $procId).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

$script:results = @()
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail }
    Log ("CHECK|" + $name + "|" + $(if($ok){"PASS"}else{"FAIL"}) + "|" + $detail)
}

# ---------------- 1. control window ----------------
$win = $null
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw.Elapsed.TotalSeconds -lt 25) {
    $win = Find-ControlWindow $procId
    if ($win) { break }
    Start-Sleep -Milliseconds 500
}
if (-not $win) { Log "FAIL: control window not found."; if (-not $p.HasExited) { $p.Kill() }; exit 1 }
Log "Control window found."
Start-Sleep -Seconds 2
$providerUi = Get-ComboCurrent $win "ProviderCombo"
$sourceUi = Get-ComboCurrent $win "LanguageCombo"
$targetUi = Get-ComboCurrent $win "TargetLanguageCombo"
Log "Baseline UI: provider='$providerUi' source='$sourceUi' target='$targetUi'"
# The combo display text is not UIA-exposed in the collapsed state, so the configuration is pinned
# by the installed settings.json (deterministic) rather than the combo text.
$cfg = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
Check "SETUP:settings provider=Argos" ($cfg.provider -eq 0) ("provider=" + $cfg.provider)
Check "SETUP:settings source=Auto" ($null -eq $cfg.language) ("language=" + $cfg.language)
Check "SETUP:settings target=Tagalog" ($cfg.targetLanguage -eq "tl") ("target=" + $cfg.targetLanguage)
Check "SETUP:settings translation on" ($cfg.translationEnabled -eq $true) ("enabled=" + $cfg.translationEnabled)

# ---------------- 2. audio + start ----------------
Log "Starting audio: $wav (looped)"
$sp = $null
try {
    $sp = New-Object System.Media.SoundPlayer $wav
    $sp.PlayLooping()
} catch {
    Log "WARN: SoundPlayer failed; starting VLC loop"
    Start-Process -FilePath "C:\Program Files\VideoLAN\VLC\vlc.exe" -ArgumentList "--intf","dummy","--no-video","--loop","--volume","256",$wav | Out-Null
}

$argos0 = Get-ArgosServer
Log "Argos server pre-start: PID $($argos0.ProcessId)"
Invoke-Button $win "Start Captions"
Log "Clicked Start Captions"

$w = @()
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw2.Elapsed.TotalSeconds -lt 30) {
    $w = Get-STTWorkerSet
    if ($w.Count -gt 0) { break }
    Start-Sleep -Milliseconds 300
}
Check "ARGOS:STT worker spawned" ($w.Count -gt 0) ($w -join ",")

# ---------------- 3. caption assertions ----------------
$ok = Wait-CaptionsLang $procId "tl" $FirstCaptionTimeoutSec
$status = Get-TextByName $win "StatusText"
Check "ARGOS:first translated caption appears (Tagalog from English narration)" (-not [string]::IsNullOrEmpty($ok)) ("sample='" + $ok + "'")
Check "ARGOS:status is not error" ($status -notmatch 'unavailable|Error|error|failed|Failed') ("status=" + $status)

Start-Sleep -Seconds 5
$lines = Get-CaptionLines $procId
$devLines = @($lines | Where-Object { Has-Script $_ $devanagariRx })
$cyrLines = @($lines | Where-Object { Has-Script $_ $cyrillicRx })
$tlLines = @($lines | Where-Object { Is-Tagalog $_ })
Log "SNAP|lines=$($lines -join ' || ')"
Log "SNAP|tagalog=$($tlLines.Count) devanagari=$($devLines.Count) cyrillic=$($cyrLines.Count)"

# Devanagari passthrough: only meaningful when the source audio actually contains Hindi script.
# When present in the overlay it must appear as-is (source passthrough, never as a Tagalog
# translation); when the WAV is a pure-English sample the expected count is 0.
$devSample = ""
if ($devLines.Count -gt 0) { $devSample = $devLines[0] }
Check "ARGOS:non-English (Devanagari) passes through untranslated when present" ($devLines.Count -eq 0 -or ($devLines.Count -gt 0 -and -not (Is-Tagalog $devSample))) ("count=" + $devLines.Count + " sample='" + $devSample + "'")

$script:results | ForEach-Object { Log ("RESULT|" + $_.Check + "|" + $(if($_.Pass){"PASS"}else{"FAIL"}) + "|" + $_.Detail) }

# ---------------- stop / close ----------------
Log "Stopping audio + closing app..."
if ($sp) { try { $sp.Stop() } catch {} }
Invoke-Button $win "Stop"
Start-Sleep -Seconds 2
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(15000)) { $p.Kill() }
Start-Sleep -Seconds 2

$pass = @($script:results | Where-Object { $_.Pass }).Count
$fail = @($script:results | Where-Object { -not $_.Pass }).Count
Log "TOTAL: $pass PASS / $fail FAIL"
if (Test-Path -LiteralPath $errOut) {
    Log "---- stderr diagnostics ----"
    Get-Content -LiteralPath $errOut | Where-Object { $_ -match "\[DIAGNOSTICS\]" } | ForEach-Object { Log $_.Trim() }
}
exit $(if ($fail -gt 0) { 1 } else { 0 })
