param(
    [string]$Out = "",
    [int]$Repeat = 1
)
Add-Type -AssemblyName System.Speech
$base = @(
    "Hi everyone, I'm Alex, and this is my friend Maya.",
    "Welcome back to class. Did you all have a good weekend?",
    "Today let's talk about everyday plans and simple requests.",
    "First, who can tell me the time? It's almost nine thirty.",
    "My birthday falls on the twenty first of December, and I just turned thirty.",
    "Could you please pass me the notes from yesterday's meeting?",
    "Sure, here you go. Thanks a lot, I really appreciate it.",
    "The Green Valley Cooking Club meets every Saturday at the community center.",
    "She said he would bring his guitar, but we never saw it arrive.",
    "I'm feeling a bit under the weather today, so I'll take it easy.",
    "That joke you told earlier was hilarious. I couldn't stop laughing.",
    "We're running a little behind schedule, so please bear with us.",
    "Take care on your way home, and see you around soon.",
    "Let's catch up over lunch sometime. It's been a while since we talked.",
    "Could you speak up a little? The projector is a bit noisy today.",
    "Alright, that's all for today. Thanks for coming, and have a great week."
)
if (-not $Out) { $Out = $args[0] }
if (-not $Out) { throw "output path required" }
$txt = (1..$Repeat | ForEach-Object { $base -join " " }) -join " "
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::Female)
$synth.SetOutputToWaveFile($Out)
$synth.Rate = -2
$synth.Speak($txt)
$synth.Dispose()
$len = (Get-Item -LiteralPath $Out).Length
Write-Host "WAV written: $Out ($len bytes)"
