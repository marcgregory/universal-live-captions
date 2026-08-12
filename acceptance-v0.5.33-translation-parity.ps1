# Live acceptance: v0.5.33 translation-state provider parity.
# Criterion: "Whatever I can do with the Argos controls while RUNNING, I must be
# able to do with Gemini while RUNNING, with the same immediate UI behavior."
#  - toggle Translate OFF while running -> new captions are source English (Whisper keeps running)
#  - toggle ON -> immediately target
#  - change target while running -> immediately new target (no session restart)
# Note: the overlay header badge pill is NOT UIA-exposed (hover chrome), so badge
# text is verified by unit tests; this harness verifies the caption content + the
# control-window toggle state instead.
#
# Note: this file must stay ASCII-only (PowerShell 5.1 reads BOM-less .ps1 as ANSI).
# Non-ASCII matching is done via Unicode code points, not literal glyphs.
param(
    [string]$Wav = "artifacts\samples\english_sustained_90s.wav",
    [int]$FirstCaptionTimeoutSec = 45,
    [int]$ActionSettleSec = 6,
    [int]$TargetChangeSettleSec = 14,
    [string]$Log = "v0533_parity_acceptance.log"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = Join-Path $PWD "src\UniversalCaptions.App\bin\Release\net8.0-windows\UniversalCaptions.App.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Release exe not found: $exe" }
$wav = if ([System.IO.Path]::IsPathRooted($Wav)) { $Wav } else { Join-Path $PWD $Wav }
$settingsPath = Join-Path $env:LOCALAPPDATA "UniversalCaptions\settings.json"
$utf8 = [System.Text.Encoding]::UTF8

function Log($msg) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg
    Write-Host $line
    [System.IO.File]::AppendAllText($Log, $line + [Environment]::NewLine, $utf8)
}

# ---- 0. baseline settings: Argos, translation ON, target tl, source en ----
$baseline = '{"version":1,"deviceId":null,"language":"en","translationEnabled":true,"targetLanguage":"tl","provider":0,"opacity":1,"fontSize":16,"clickThrough":false,"overlayLeft":801.6,"overlayTop":231.2,"overlayExpanded":true}'
if (Test-Path -LiteralPath $settingsPath) { Copy-Item -LiteralPath $settingsPath -Destination "$settingsPath.bak" -Force }
New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null
Set-Content -LiteralPath $settingsPath -Value $baseline -Encoding UTF8
Log "Installed baseline settings (Argos, en->tl, translation ON)"

$env:PATH = "C:\Users\TOGODB~1\AppData\Local\Temp\argosv\Scripts;C:\Users\TOGODB~1\AppData\Local\Temp\fwv\Scripts;$env:PATH"
Remove-Item Env:UC_STT_ENGINE -ErrorAction SilentlyContinue

Get-Process -Name "UniversalCaptions.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "vlc" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
if (Test-Path -LiteralPath $Log) { Remove-Item -LiteralPath $Log -Force }

$errOut = Join-Path $PWD "v0533_app_stderr.log"
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
function Get-ToggleState($window, $autoId) {
    $el = Find-Ctrl $window $autoId
    if (-not $el) { throw "Control '$autoId' not found" }
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
}
function Set-Toggle($window, $autoId, [bool]$desired) {
    $el = Find-Ctrl $window $autoId
    if (-not $el) { throw "Control '$autoId' not found" }
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $on = $tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    if ($on -ne $desired) {
        $tp.Toggle()
        Start-Sleep -Milliseconds 500
        Log "Toggle '$autoId' -> $desired"
    } else {
        Log "Toggle '$autoId' already $desired"
    }
}
function Select-ComboItem($window, $autoId, $substring) {
    $combo = Find-Ctrl $window $autoId
    if (-not $combo) { throw "Combo '$autoId' not found" }
    $ecp = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ecp.Expand()
    Start-Sleep -Milliseconds 300
    $item = $null
    $liCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $tCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 4) {
        $items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $liCond)
        foreach ($i in $items) {
            $candidates = @($i.Current.Name)
            $texts = $i.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tCond)
            foreach ($t in $texts) { $candidates += $t.Current.Name }
            foreach ($cn in $candidates) {
                if ($cn -and $cn.IndexOf($substring, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $item = $i; break }
            }
            if ($item) { break }
        }
        if ($item) { break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $item) {
        try { $ecp.Collapse() } catch {}
        throw "Combo item '$substring' not found in '$autoId'"
    }
    ($item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 700
    try { $ecp.Collapse() } catch {}
    $cur = Get-ComboCurrent $window $autoId
    if ($cur -and $cur.IndexOf($substring, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        Log "Combo '$autoId' -> '$substring' (confirmed display)"
    } else {
        Log "Combo '$autoId' -> '$substring' (display='$cur')"
    }
}
function Get-ComboCurrent($window, $autoId) {
    $combo = Find-Ctrl $window $autoId
    if (-not $combo) { return "" }
    $tCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $els = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants, $tCond)
    foreach ($el in $els) { if ($el.Current.Name) { return $el.Current.Name } }
    return ""
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
# Target badge read by AutomationId (header pills); returns "TL"/"JA"/"" when absent.
function Get-BadgeText([int]$procId) {
    $win = Find-OverlayWindow $procId
    if (-not $win) { return "" }
    $el = Find-Ctrl $win "TargetLanguageBadge"
    if (-not $el) { return "" }
    return $el.Current.Name
}
function Get-TextByName($window, $autoId) {
    $el = Find-Ctrl $window $autoId
    if ($el) { return $el.Current.Name }
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

$script:results = @()
function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results += [pscustomobject]@{ Check = $name; Pass = $ok; Detail = $detail }
    Log ("CHECK|" + $name + "|" + $(if($ok){"PASS"}else{"FAIL"}) + "|" + $detail)
}

# ---------------- language classification (ASCII-only source) ----------------
$japaneseRx = '[\u3040-\u30FF\u4E00-\u9FFF]'
$tagalogRx = '(?i)\b(?:mga|ang|namin|natin|tayo|inyong|iyong|ninyo|aming|ating|kanilang|sila|kami|kayo|lahat|ngayon|salamat|magandang|pakibuksan|pahina|magsalita|makinig|unang|pagbati|pagpapakilala|tandaan|pakikinig|sesyon|pagsasanay|kasalukuyan|wakas|mahusay|magsisimula|sanlinggo|pangalan)\b'
function Is-Japanese([string]$t) {
    if ([string]::IsNullOrWhiteSpace($t)) { return $false }
    return [regex]::IsMatch($t, $japaneseRx)
}
function Is-Tagalog([string]$t) {
    if ([string]::IsNullOrWhiteSpace($t)) { return $false }
    return [regex]::IsMatch($t, $tagalogRx)
}
function Wait-NewEnglishCaption([int]$procId, $existing, [int]$timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        foreach ($c in (Get-CaptionLines $procId)) {
            $already = @($existing | Where-Object { $_ -eq $c }).Count -gt 0
            if (-not $already -and -not (Is-Japanese $c) -and -not (Is-Tagalog $c)) { return $c }
        }
        Start-Sleep -Milliseconds 500
    }
    return ""
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
function Wait-Captions([int]$procId, [int]$timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        if ((Get-CaptionLines $procId).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Wait-CaptionsLang([int]$procId, [string]$lang, [int]$timeoutSec) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        foreach ($c in (Get-CaptionLines $procId)) {
            if ($lang -eq "ja" -and (Is-Japanese $c)) { return $c }
            if ($lang -eq "tl" -and (Is-Tagalog $c)) { return $c }
        }
        Start-Sleep -Milliseconds 500
    }
    return ""
}
function Snapshot($window, [int]$procId, [string]$tag) {
    $parts = Get-OverlayText $procId
    $status = Get-TextByName $window "StatusText"
    $badge = Get-BadgeText $procId
    $caps = (Get-CaptionLines $procId) -join " || "
    $anyJa = @(Get-CaptionLines $procId | Where-Object { Is-Japanese $_ }).Count
    $anyTl = @(Get-CaptionLines $procId | Where-Object { Is-Tagalog $_ }).Count
    Log "SNAP|$tag|badge='$badge'|jaLines=$anyJa|tlLines=$anyTl|parts=$($parts -join ' / ')"
    Log "SNAP|$tag|captions=$caps"
    Log "SNAP|$tag|status='$status'"
    return @{ Badge = $badge; JaLines = $anyJa; TlLines = $anyTl; Status = $status }
}

# ---------------- 2. control window ----------------
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
Log ("Baseline UI: toggle=" + (Get-ToggleState $win "TranslationToggle") + " provider='" + (Get-ComboCurrent $win "ProviderCombo") + "' target='" + (Get-ComboCurrent $win "TargetLanguageCombo") + "'")

# ---------------- 3. audio ----------------
Log "Starting audio: $wav (looped)"
$sp = $null
try {
    $sp = New-Object System.Media.SoundPlayer $wav
    $sp.PlayLooping()
} catch {
    Log "WARN: SoundPlayer failed; starting VLC loop"
    Start-Process -FilePath "C:\Program Files\VideoLAN\VLC\vlc.exe" -ArgumentList "--intf","dummy","--no-video","--loop","--volume","256",$wav | Out-Null
}

# ================= ARGOS PHASE =================
Log "================ ARGOS PHASE ================"
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
Start-Sleep -Seconds 2
$w = Get-STTWorkerSet
Log "ARGOS STT worker set: $($w -join ',')"

# first translated caption while running
$ok = Wait-CaptionsLang $procId "tl" $FirstCaptionTimeoutSec
$snap = Snapshot $win $procId "argos-first-caption"
Check "ARGOS:first translated caption while running (Tagalog text)" (-not [string]::IsNullOrEmpty($ok)) ("sample='" + $ok + "'")
Check "ARGOS:first-caption status not error" ($snap.Status -notmatch 'unavailable|Error|error|failed|Failed') ("status=" + $snap.Status)

# Toggle OFF -> new captions are source English; Whisper keeps running
$preOff = Get-CaptionLines $procId
Set-Toggle $win "TranslationToggle" $false
Start-Sleep -Seconds $ActionSettleSec
$snapOff = Snapshot $win $procId "argos-toggle-off"
$newEngOff = Wait-NewEnglishCaption $procId $preOff 15
Check "ARGOS:toggle OFF -> new caption is source English (not translated)" (-not [string]::IsNullOrEmpty($newEngOff)) ("new='" + $newEngOff + "'")
Check "ARGOS:toggle OFF -> control toggle is off" (-not (Get-ToggleState $win "TranslationToggle")) ("state=" + (Get-ToggleState $win "TranslationToggle"))
Check "ARGOS:toggle OFF -> status still capturing (Whisper alive)" ($snapOff.Status -match 'Capturing') ("status=" + $snapOff.Status)
Log "Note: overlay header badge pill is not UIA-exposed (Visibility=Hidden hover chrome; UIA tree exposes only CaptionScroller/HintText), so badge text is verified by unit tests (CaptionDisplayPolicyTests assert LanguageBadge TL/null) rather than this harness."

# Toggle ON -> immediately Tagalog
Set-Toggle $win "TranslationToggle" $true
Start-Sleep -Seconds $ActionSettleSec
$tlLine = Wait-CaptionsLang $procId "tl" 10
$snapOn = Snapshot $win $procId "argos-toggle-on"
Check "ARGOS:toggle ON -> Tagalog returns while running" (-not [string]::IsNullOrEmpty($tlLine)) ("sample='" + $tlLine + "'")

# Target tl -> ja while running
Select-ComboItem $win "TargetLanguageCombo" "Japanese (ja)"
Start-Sleep -Seconds $TargetChangeSettleSec
$jaLine = Wait-CaptionsLang $procId "ja" 10
$snapJa = Snapshot $win $procId "argos-target-ja"
Check "ARGOS:target ja -> Japanese while running" (-not [string]::IsNullOrEmpty($jaLine)) ("sample='" + $jaLine + "'")

# Target ja -> tl while running
Select-ComboItem $win "TargetLanguageCombo" "Tagalog (tl)"
Start-Sleep -Seconds $TargetChangeSettleSec
$tlLine2 = Wait-CaptionsLang $procId "tl" 10
$snapTl2 = Snapshot $win $procId "argos-target-back-tl"
Check "ARGOS:target back tl -> Tagalog while running" (-not [string]::IsNullOrEmpty($tlLine2)) ("sample='" + $tlLine2 + "'")

$wA = Get-STTWorkerSet
$keptA = @($w | Where-Object { $wA -contains $_ }).Count
Check "ARGOS:Whisper workers not restarted across toggle/target (originals alive)" ($keptA -eq $w.Count -and $w.Count -gt 0) ("before=" + ($w -join ',') + " after=" + ($wA -join ','))

# ================= GEMINI PHASE =================
Log "================ GEMINI PHASE ================"
Log "Stopping captions to establish Gemini session state..."
Invoke-Button $win "Stop"
Start-Sleep -Seconds 4
Select-ComboItem $win "ProviderCombo" "Gemini (cloud)"
Start-Sleep -Seconds 1
Log "Provider set to Gemini. Start captions again."
Invoke-Button $win "Start Captions"

$g = @()
$sw3 = [System.Diagnostics.Stopwatch]::StartNew()
while ($sw3.Elapsed.TotalSeconds -lt 35) {
    $cur = Get-STTWorkerSet
    $overlap = @($w | Where-Object { $cur -contains $_ }).Count
    if ($overlap -eq 0 -and $cur.Count -gt 0) { $g = $cur; break }
    Start-Sleep -Milliseconds 500
}
Check "GEMINI:old Argos workers fully exited before new session" ($g.Count -gt 0) ("argos=" + ($w -join ',') + " new=" + ($g -join ','))
$sw3.Reset()
$sw3.Start()
while ($g.Count -eq 0 -and $sw3.Elapsed.TotalSeconds -lt 20) {
    $g = Get-STTWorkerSet
    Start-Sleep -Milliseconds 500
}
Check "GEMINI:STT worker spawned" ($g.Count -gt 0) ($g -join ",")
Check "GEMINI:STT worker is a NEW set (fresh session)" ((@($w | Where-Object { $g -contains $_ }).Count) -eq 0) ("argos=" + ($w -join ',') + " gemini=" + ($g -join ','))
Log "Gemini STT worker set: $($g -join ',')"

$gOk = Wait-CaptionsLang $procId "tl" $FirstCaptionTimeoutSec
$snapG = Snapshot $win $procId "gemini-first-caption"
Check "GEMINI:first translated caption while running (Tagalog text)" (-not [string]::IsNullOrEmpty($gOk)) ("sample='" + $gOk + "'")
Check "GEMINI:first-caption status not error" ($snapG.Status -notmatch 'unavailable|Error|error|failed|Failed') ("status=" + $snapG.Status)

# Toggle OFF -> new captions are source English; Whisper keeps running
$preGOff = Get-CaptionLines $procId
Set-Toggle $win "TranslationToggle" $false
Start-Sleep -Seconds $ActionSettleSec
$snapGOff = Snapshot $win $procId "gemini-toggle-off"
$newEngGOff = Wait-NewEnglishCaption $procId $preGOff 15
Check "GEMINI:toggle OFF -> new caption is source English (not translated)" (-not [string]::IsNullOrEmpty($newEngGOff)) ("new='" + $newEngGOff + "'")
Check "GEMINI:toggle OFF -> control toggle is off" (-not (Get-ToggleState $win "TranslationToggle")) ("state=" + (Get-ToggleState $win "TranslationToggle"))
$gAfterOff = Get-STTWorkerSet
$keptG = @($g | Where-Object { $gAfterOff -contains $_ }).Count
Check "GEMINI:Whisper continues after toggle OFF (originals alive)" ($keptG -eq $g.Count -and $g.Count -gt 0) ("set=" + ($gAfterOff -join ','))

Set-Toggle $win "TranslationToggle" $true
Start-Sleep -Seconds $ActionSettleSec
$gTlLine = Wait-CaptionsLang $procId "tl" 10
$snapGOn = Snapshot $win $procId "gemini-toggle-on"
Check "GEMINI:toggle ON -> Tagalog returns while running" (-not [string]::IsNullOrEmpty($gTlLine)) ("sample='" + $gTlLine + "'")

Select-ComboItem $win "TargetLanguageCombo" "Japanese (ja)"
Start-Sleep -Seconds $TargetChangeSettleSec
$gJaLine = Wait-CaptionsLang $procId "ja" 10
$snapGJa = Snapshot $win $procId "gemini-target-ja"
Check "GEMINI:target ja -> Japanese while running" (-not [string]::IsNullOrEmpty($gJaLine)) ("sample='" + $gJaLine + "'")

Select-ComboItem $win "TargetLanguageCombo" "Tagalog (tl)"
Start-Sleep -Seconds $TargetChangeSettleSec
$gTlLine2 = Wait-CaptionsLang $procId "tl" 10
$snapGTl = Snapshot $win $procId "gemini-target-back-tl"
Check "GEMINI:target back tl -> Tagalog while running" (-not [string]::IsNullOrEmpty($gTlLine2)) ("sample='" + $gTlLine2 + "'")

$gFinal = Get-STTWorkerSet
$keptGF = @($g | Where-Object { $gFinal -contains $_ }).Count
Check "GEMINI:Whisper workers not restarted across toggle/target (originals alive)" ($keptGF -eq $g.Count -and $g.Count -gt 0) ("before=" + ($g -join ',') + " after=" + ($gFinal -join ','))

# ---------------- stop / close ----------------
Log "Stopping audio + closing app..."
if ($sp) { try { $sp.Stop() } catch {} }
Invoke-Button $win "Stop"
Start-Sleep -Seconds 2
$p.CloseMainWindow() | Out-Null
if (-not $p.WaitForExit(15000)) { $p.Kill() }
Start-Sleep -Seconds 2

# ---------------- summary ----------------
Log "================ SUMMARY ================"
$pass = @($script:results | Where-Object { $_.Pass }).Count
$fail = @($script:results | Where-Object { -not $_.Pass }).Count
$script:results | ForEach-Object { Log ("RESULT|" + $_.Check + "|" + $(if($_.Pass){"PASS"}else{"FAIL"}) + "|" + $_.Detail) }
Log "TOTAL: $pass PASS / $fail FAIL"
if (Test-Path -LiteralPath $errOut) {
    Log "---- stderr diagnostics ----"
    Get-Content -LiteralPath $errOut | Where-Object { $_ -match "\[DIAGNOSTICS\]" } | ForEach-Object { Log $_.Trim() }
}
exit $(if ($fail -gt 0) { 1 } else { 0 })
