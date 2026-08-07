using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Benchmarks.Translation;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech;
using UniversalCaptions.Translation;
using UniversalCaptions.Translation.Argos;

/// <summary>
/// Additive online-vs-offline translation experiment (<c>translatelive</c>): drives two legs over the
/// SAME captured English audio.
///   Path 1 (Argos baseline, fully local): faster-whisper native streaming STT (production default)
///     → English FINALs → Argos en→tl → Tagalog captions.
///   Path 2 (Gemini Live candidate): the same raw 16-bit PCM/16 kHz audio → Gemini Live API
///     <c>gemini-3.5-live-translate-preview</c> (speech-to-speech) → its output transcription is used
///     as the caption text, exactly as the overlay would consume it. The translated audio is NOT
///     compared — text captions only.
/// Reports the comparison table the user asked for: naturalness (side-by-side text), meaning
/// preservation (side-by-side + similarity), first-result latency, update cadence, final latency,
/// repetition/hallucination, sentence-boundary quality, CPU/RAM, network/bandwidth, and cost/hour.
/// This is a removable, additive benchmark: production architecture and the Argos default are
/// untouched.
/// </summary>
internal static class LiveTranslationBenchmark
{
    private const int SampleRate = 16_000;
    private const string DefaultModel = "gemini-3.5-live-translate-preview";

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    private sealed record LegArgs(
        string? WavPath,
        string Target,
        bool RunArgos,
        bool RunGemini,
        bool TagalogControl,
        string SttPython,
        string ArgosPython,
        string Model,
        int Threads,
        bool Realtime,
        double MaxSegmentSeconds,
        double HangoverSeconds,
        double PartialIntervalSeconds,
        double PartialWindowSeconds,
        double TailSeconds,
        string? CsvPath,
        string? ChromeRefPath,
        bool Naturalize,
        string? GeminiRefCsv);

    private sealed record CaptionRow(double EmitSec, double SourceStartSec, double TranslateMs, string Kind, string Text);

    private sealed record ArgosLegResult(
        int Captions,
        int Partials,
        int Finals,
        double FirstCaptionSec,
        double MedianTranslateMs,
        double CaptionsPer120S,
        int RepeatedBigrams,
        int ConsecutiveDuplicates,
        int Unterminated,
        double ProcCpuFraction,
        double WorkerCpuFraction,
        double PeakRamMb,
        string? Error,
        List<CaptionRow> Rows,
        int NaturalizedCaptions,
        List<CaptionRow>? NaturalizedRows);

    private sealed record GeminiLegResult(
        int OutputCaptions,
        int OutputUpdates,
        int TurnCompletes,
        double FirstOutputSec,
        double LastOutputSec,
        double LastOutputAfterFeedSec,
        double CaptionsPer120S,
        int RepeatedBigrams,
        int ConsecutiveDuplicates,
        int Unterminated,
        double ProcCpuFraction,
        double PeakRamMb,
        long BytesSent,
        long BytesReceived,
        long AudioBytesSent,
        long AudioBytesReceived,
        double CostUsd,
        double CostUsdPerHour,
        long InputTokens,
        long OutputTokens,
        double ConnectSec,
        double SetupCompleteSec,
        int ErrorFrames,
        string? SessionError,
        string? OutputLanguage,
        List<CaptionRow> Rows);

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var legs = ParseArgs(args);
        if (legs.WavPath is null)
        {
            Console.Error.WriteLine("translatelive requires --wav <path> (16-bit PCM WAV; 8k/16k mono or stereo).");
            PrintUsage();
            return 2;
        }

        if (!legs.RunArgos && !legs.RunGemini)
        {
            Console.Error.WriteLine("Nothing to run: use --legs argos|gemini|both.");
            PrintUsage();
            return 2;
        }

        string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (legs.RunGemini && string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine(
                "Gemini leg requires GEMINI_API_KEY in the environment. " +
                "It is never stored or committed. (Use --legs argos to run offline only.)");
            return 2;
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Console encoding is best-effort; Tagalog text may not render on some hosts.
        }

        var (samples, pcm16, audioSeconds) = LoadWav(legs.WavPath);
        Console.WriteLine("=== translatelive: Argos (offline) vs Gemini Live Translate (online) on the same captured audio ===");
        Console.WriteLine($"Machine: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"WAV: {Path.GetFullPath(legs.WavPath)} ({audioSeconds:0.00}s, {pcm16.Length:N0} PCM16 bytes)");
        Console.WriteLine($"Target (Gemini): {legs.Target}; Argos target: tl");
        Console.WriteLine($"Legs: {(legs.RunArgos ? "argos" : "")}{(legs.RunArgos && legs.RunGemini ? " + " : "")}{(legs.RunGemini ? "gemini" : "")}");
        Console.WriteLine($"Feed: {(legs.Realtime ? "realtime" : "fast")}; STT model: {legs.Model} (threads {legs.Threads}); Gemini chunk: 100ms; tail flush: {legs.TailSeconds:0}s");
        Console.WriteLine($"STT python: {legs.SttPython}");
        Console.WriteLine($"Argos python: {legs.ArgosPython}");
        Console.WriteLine();

        ArgosLegResult? argos = null;
        GeminiLegResult? gemini = null;
        List<CaptionRow>? tagalogEcho = null;

        if (legs.RunArgos)
        {
            argos = await RunArgosLegAsync(samples, audioSeconds, legs, ct);
        }

        if (legs.RunGemini)
        {
            gemini = await RunGeminiLegAsync(pcm16, audioSeconds, legs, apiKey!, ct);
        }

        if (legs.TagalogControl && !string.IsNullOrWhiteSpace(apiKey))
        {
            tagalogEcho = await RunTagalogEchoControlAsync(legs, apiKey!, ct);
        }

        Console.WriteLine();
        PrintComparison(argos, gemini, audioSeconds, legs);
        PrintCaptionStreams(argos, gemini);

        if (argos is not null && legs.Naturalize)
        {
            PrintNaturalization(argos);
        }

        if (argos is not null && gemini is null && legs.GeminiRefCsv is not null)
        {
            List<CaptionRow>? reference = LoadGeminiReferenceCsv(legs.GeminiRefCsv);
            if (reference is not null)
            {
                PrintNaturalnessTable(argos, reference);
            }
            else
            {
                Console.WriteLine($"  (no gemini reference rows found in {legs.GeminiRefCsv})");
            }
        }

        if (tagalogEcho is not null)
        {
            PrintTagalogControl(tagalogEcho);
        }

        if (legs.CsvPath is not null)
        {
            WriteCsv(legs.CsvPath, argos, gemini, tagalogEcho, audioSeconds);
        }

        return 0;
    }

    private static LegArgs ParseArgs(string[] args)
    {
        string? wavPath = null;
        string target = "fil";
        bool runArgos = true;
        bool runGemini = true;
        bool tagalogControl = false;
        string sttPython = ResolvePythonEnv("UC_FW_PYTHON", "fwv");
        string argosPython = ResolvePythonEnv("UC_ARGOS_PYTHON", "argosv");
        string model = "small";
        int threads = 4;
        bool realtime = true;
        double maxSegmentSeconds = 8.0;
        double hangoverSeconds = 0.7;
        double partialIntervalSeconds = 1.0;
        double partialWindowSeconds = 4.0;
        double tailSeconds = 5.0;
        string? csvPath = null;
        string? chromeRef = null;
        bool naturalize = true;
        string? geminiRefCsv = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--wav" when i + 1 < args.Length:
                    wavPath = args[++i];
                    break;
                case "--target" when i + 1 < args.Length:
                    target = args[++i];
                    break;
                case "--legs" when i + 1 < args.Length:
                    string legs = args[++i];
                    runArgos = legs.Contains("argos", StringComparison.OrdinalIgnoreCase);
                    runGemini = legs.Contains("gemini", StringComparison.OrdinalIgnoreCase);
                    break;
                case "--tagalog-control":
                    tagalogControl = true;
                    break;
                case "--stt-python" when i + 1 < args.Length:
                    sttPython = args[++i];
                    break;
                case "--argos-python" when i + 1 < args.Length:
                    argosPython = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--threads" when i + 1 < args.Length:
                    threads = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--feed" when i + 1 < args.Length:
                    realtime = string.Equals(args[++i], "realtime", StringComparison.OrdinalIgnoreCase);
                    break;
                case "--max-segment" when i + 1 < args.Length:
                    maxSegmentSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--hangover" when i + 1 < args.Length:
                    hangoverSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--partial-interval" when i + 1 < args.Length:
                    partialIntervalSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--partial-window" when i + 1 < args.Length:
                    partialWindowSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--tail-s" when i + 1 < args.Length:
                    tailSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--csv" when i + 1 < args.Length:
                    csvPath = args[++i];
                    break;
                case "--chrome-ref" when i + 1 < args.Length:
                    chromeRef = args[++i];
                    break;
                case "--no-naturalize":
                    naturalize = false;
                    break;
                case "--gemini-ref-csv" when i + 1 < args.Length:
                    geminiRefCsv = args[++i];
                    break;
                case "--help":
                    PrintUsage();
                    break;
            }
        }

        return new LegArgs(
            wavPath,
            target,
            runArgos,
            runGemini,
            tagalogControl,
            sttPython,
            argosPython,
            model,
            threads,
            realtime,
            maxSegmentSeconds,
            hangoverSeconds,
            partialIntervalSeconds,
            partialWindowSeconds,
            tailSeconds,
            csvPath,
            chromeRef,
            naturalize,
            geminiRefCsv);
    }

    private static string ResolvePythonEnv(string envName, string venvDir)
    {
        string? env = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        string? temp = Environment.GetEnvironmentVariable("TEMP");
        if (!string.IsNullOrWhiteSpace(temp))
        {
            var candidate = Path.Combine(temp, venvDir, "Scripts", "python.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "python";
    }

    private static async Task<ArgosLegResult> RunArgosLegAsync(
        float[] samples, double audioSeconds, LegArgs legs, CancellationToken ct)
    {
        Console.WriteLine("=== LEG 1: Argos (offline) — faster-whisper-native STT → Argos en→tl ===");

        var sttEngine = new FasterWhisperNativeStreamingEngine(
            new FasterWhisperEngineOptions
            {
                PythonExecutablePath = legs.SttPython,
                Model = legs.Model,
                Language = "en",
                Threads = legs.Threads,
                PartialDecodeInterval = TimeSpan.FromSeconds(legs.PartialIntervalSeconds),
                PartialDecodeWindow = TimeSpan.FromSeconds(legs.PartialWindowSeconds),
            },
            new EnergyVad(new VadOptions(RmsThreshold: 0.008, MinActiveChunks: 1, SilenceHangoverChunks: 2)),
            new SpeechSegmentDetectorOptions
            {
                SampleRate = SampleRate,
                MinSpeechDuration = TimeSpan.FromSeconds(0.3),
                SilenceHangover = TimeSpan.FromSeconds(legs.HangoverSeconds),
                MaxSegmentDuration = TimeSpan.FromSeconds(legs.MaxSegmentSeconds),
            });

        var argosOptions = new ArgosTranslationEngineOptions
        {
            PythonExecutablePath = legs.ArgosPython,
            StartupTimeout = TimeSpan.FromSeconds(180),
            RequestTimeout = TimeSpan.FromSeconds(120),
        };

        using var argos = new ArgosTranslationEngine(argosOptions);
        using var monitor = new ProcessMonitor("faster_whisper_worker.py", "argos_translate_server.py");
        monitor.Start();

        int partials = 0;
        int finals = 0;
        string? sttError = null;
        var baseTime = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var channel = Channel.CreateUnbounded<(double StartSec, string Text)>();
        var rows = new List<CaptionRow>();
        var translateErrors = 0;
        var translateTimesMs = new List<double>();

        var pump = TranslatePumpAsync(channel.Reader, argos, sw, rows, translateTimesMs, () => translateErrors++, ct);

        sttEngine.PartialTranscriptAvailable += (_, t) =>
        {
            partials++;
            Console.WriteLine($"    PARTIAL[{partials,3}] {Truncate(t.Text, 60)}");
        };

        sttEngine.FinalTranscriptAvailable += (_, t) =>
        {
            finals++;
            double startSec = (t.CapturedAtUtc - baseTime).TotalSeconds;
            double emitSec = sw.Elapsed.TotalSeconds;
            Console.WriteLine($"    FINAL[{finals,3}] emit {emitSec,6:0.00}s segStart {startSec,6:0.00}s | {Truncate(t.Text, 80)}");
            channel.Writer.TryWrite((startSec, t.Text));
        };

        sttEngine.RecognitionFailed += (_, e) => sttError ??= $"{e.Kind}: {e.Message}";

        var swWarm = Stopwatch.StartNew();
        await argos.TriggerPreWarmAsync("en", "tl", ct);
        Console.WriteLine($"    Argos pre-warm: {swWarm.ElapsedMilliseconds} ms");

        var swStart = Stopwatch.StartNew();
        sttEngine.Start();
        swStart.Stop();
        Console.WriteLine($"    faster-whisper worker/model start: {swStart.ElapsedMilliseconds} ms");

        sw.Restart();
        await FeedAsync(sttEngine, samples, baseTime, legs.Realtime, ct);
        double feedWallSec = sw.Elapsed.TotalSeconds;

        sttEngine.Stop();
        await sttEngine.DisposeAsync();
        channel.Writer.TryComplete();
        await pump;
        sw.Stop();

        Console.WriteLine($"    feed finished at {feedWallSec:0.00}s wall; {finals} STT FINALs, {partials} partials; {rows.Count} Argos captions committed.");
        if (translateErrors > 0)
        {
            Console.WriteLine($"    WARNING: {translateErrors} translation(s) failed.");
        }

        if (sttError is not null)
        {
            Console.WriteLine($"    STT ERROR: {sttError}");
        }

        string concatenated = string.Join(" ", rows.Select(r => r.Text));
        double firstCaptionSec = rows.Count > 0 ? rows[0].EmitSec : double.NaN;
        double medianTranslateMs = translateTimesMs.Count == 0 ? 0 : Median(translateTimesMs);
        var repeated = RepeatedBigrams(concatenated);
        int duplicates = ConsecutiveDuplicates(rows);
        int unterminated = rows.Count(r => !EndsTerminal(r.Text));

        List<CaptionRow>? naturalizedRows = null;
        int naturalizedCaptions = 0;
        if (legs.Naturalize)
        {
            naturalizedRows = rows
                .Select(r => r with { Text = TagalogNaturalizer.Naturalize(r.Text) })
                .ToList();
            naturalizedCaptions = rows
                .Where((r, i) => !string.Equals(r.Text, naturalizedRows[i].Text, StringComparison.Ordinal))
                .Count();
            Console.WriteLine($"  naturalizer: {naturalizedCaptions}/{rows.Count} captions rewritten by the rule table");
        }

        var summary = monitor.Stop();
        double procCpu = summary.SelfCpuFraction;
        double workerCpu = summary.WorkerCpuFraction;
        double peakRam = summary.PeakWorkingSetMb;

        Console.WriteLine();
        Console.WriteLine($"  first translated caption: {FormatSec(firstCaptionSec)} (from feed start)");
        Console.WriteLine($"  captions:                 {rows.Count}");
        Console.WriteLine($"  cadence:                  {rows.Count / (audioSeconds / 120.0):0.0} captions per 120 s");
        Console.WriteLine($"  median translate:         {medianTranslateMs:0} ms/final");
        Console.WriteLine($"  repeated bigrams:         {repeated}; consecutive duplicates: {duplicates}; unterminated: {unterminated}");
        Console.WriteLine($"  cpu: benchmark {procCpu * 100:0.0}% / worker python {workerCpu * 100:0.0}% of machine; peak RAM {peakRam:0.0} MB");

        return new ArgosLegResult(
            rows.Count,
            partials,
            finals,
            firstCaptionSec,
            medianTranslateMs,
            rows.Count / (audioSeconds / 120.0),
            repeated,
            duplicates,
            unterminated,
            procCpu,
            workerCpu,
            peakRam,
            sttError,
            rows,
            naturalizedCaptions,
            naturalizedRows);
    }

    private static async Task TranslatePumpAsync(
        ChannelReader<(double StartSec, string Text)> reader,
        ArgosTranslationEngine argos,
        Stopwatch sw,
        List<CaptionRow> rows,
        List<double> translateTimesMs,
        Action onError,
        CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var swTrans = Stopwatch.StartNew();
            try
            {
                var result = await argos.TranslateAsync(item.Text, "en", "tl", ct).ConfigureAwait(false);
                swTrans.Stop();
                translateTimesMs.Add(swTrans.Elapsed.TotalMilliseconds);
                double emitSec = sw.Elapsed.TotalSeconds;
                rows.Add(new CaptionRow(emitSec, item.StartSec, swTrans.Elapsed.TotalMilliseconds, "caption", result.Text));
                Console.WriteLine($"    TL[{rows.Count,3}] emit {emitSec,6:0.00}s segStart {item.StartSec,6:0.00}s +{swTrans.ElapsedMilliseconds,5}ms | {Truncate(result.Text, 90)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                swTrans.Stop();
                onError();
                Console.WriteLine($"    TL ERROR: {exc.Message}");
            }
        }
    }

    private static async Task<GeminiLegResult> RunGeminiLegAsync(
        byte[] pcm16, double audioSeconds, LegArgs legs, string apiKey, CancellationToken ct)
    {
        Console.WriteLine("=== LEG 2: Gemini Live Translate (online) — same PCM audio → output transcription as captions ===");
        await using var client = new GeminiLiveTranslateClient(
            apiKey,
            DefaultModel,
            legs.Target,
            echoTargetLanguage: false,
            TimeSpan.FromSeconds(legs.TailSeconds));

        using var monitor = new ProcessMonitor();
        monitor.Start();
        var sw = new Stopwatch();
        var rows = new List<CaptionRow>();
        var outputNew = new List<(double ArrivalSec, string Text)>();

        var onEvent = new Action<GeminiLiveTranslateClient.CaptionEvent>(ev =>
        {
            string line = $"{ev.Kind,-12} {ev.ArrivalSec,7:0.00}s | {Truncate(ev.Text, 80)}";
            Console.WriteLine($"    {line}");
            if (ev.Kind == "output-new")
            {
                rows.Add(new CaptionRow(ev.ArrivalSec, -1, 0, "caption", ev.Text));
                outputNew.Add((ev.ArrivalSec, ev.Text));
            }
            else if (ev.Kind == "output-final")
            {
                rows.Add(new CaptionRow(ev.ArrivalSec, -1, 0, "final", ev.Text));
            }
            else if (ev.Kind == "interrupted")
            {
                Console.WriteLine("    (model interrupted output)");
            }
        });

        sw.Start();
        if (legs.Realtime)
        {
            timeBeginPeriod(1);
        }

        GeminiLiveTranslateClient.SessionStats stats;
        try
        {
            stats = await client.StreamAsync(pcm16, SampleRate, legs.Realtime, sw, onEvent, ct);
        }
        finally
        {
            if (legs.Realtime)
            {
                timeEndPeriod(1);
            }
        }

        sw.Stop();
        var summary = monitor.Stop();

        double firstOutputSec = outputNew.Count > 0 ? outputNew[0].ArrivalSec : -1;
        double lastOutputSec = stats.LastOutputSec < 0 ? (outputNew.Count > 0 ? outputNew[^1].ArrivalSec : -1) : stats.LastOutputSec;
        double feedSec = pcm16.Length / (double)(SampleRate * 2);
        double lastAfterFeed = lastOutputSec < 0 ? -1 : Math.Max(0, lastOutputSec - feedSec);

        string concatenated = string.Join(" ", rows.Where(r => r.Kind == "caption").Select(r => r.Text));
        int repeated = RepeatedBigrams(concatenated);
        int duplicates = ConsecutiveDuplicates(rows);
        int unterminated = rows.Count(r => r.Kind == "caption" && !EndsTerminal(r.Text));

        double costUsd = (stats.InputTokens * 3.50 + stats.OutputTokens * 21.00) / 1_000_000.0;
        double costPerHour = audioSeconds > 0 ? costUsd / (audioSeconds / 3600.0) : 0;

        double upBytes = stats.BytesSent;
        double downBytes = stats.BytesReceived;
        double kbps = audioSeconds > 0 ? (upBytes + downBytes) * 8 / audioSeconds / 1000 : 0;

        Console.WriteLine();
        Console.WriteLine($"  connect: {stats.ConnectSec:0.00}s; setupComplete: {(stats.SetupCompleteSec < 0 ? "n/a" : $"{stats.SetupCompleteSec:0.00}s")}");
        Console.WriteLine($"  first output transcript: {FormatSec(firstOutputSec)} (from feed start)");
        Console.WriteLine($"  last output transcript:  {FormatSec(lastOutputSec)}; {lastAfterFeed:0.00}s after feed end (tail)");
        Console.WriteLine($"  output captions:         {stats.OutputCaptionCount} new + {stats.OutputUpdateCount} updates; {stats.TurnCompleteCount} turn completes");
        Console.WriteLine($"  cadence:                 {stats.OutputCaptionCount / (audioSeconds / 120.0):0.0} new captions per 120 s");
        Console.WriteLine($"  input side:              {stats.InputCaptionCount} new + {stats.InputUpdateCount} updates (Gemini's own English STT)");
        Console.WriteLine($"  output language:         {stats.OutputLanguageCode ?? "n/a"}");
        Console.WriteLine($"  repeated bigrams:        {repeated}; consecutive duplicates: {duplicates}; unterminated: {unterminated}");
        Console.WriteLine($"  cpu: benchmark {summary.SelfCpuFraction * 100:0.0}% of machine; peak RAM {summary.PeakWorkingSetMb:0.0} MB");
        Console.WriteLine($"  network: up {upBytes / 1024.0 / 1024.0:0.00} MB / down {downBytes / 1024.0 / 1024.0:0.00} MB; {kbps:0.0} kbps combined");
        Console.WriteLine($"  audio payload: up {stats.AudioBytesSentDecoded / 1024.0 / 1024.0:0.00} MB / down {stats.AudioBytesReceivedDecoded / 1024.0 / 1024.0:0.00} MB");
        Console.WriteLine($"  usage: {stats.InputTokens} input / {stats.OutputTokens} output tokens; cost ${costUsd:0.0000} ({costPerHour:0.00}/hr at this rate)");
        if (stats.SessionError is not null)
        {
            Console.WriteLine($"  SESSION ERROR: {stats.SessionError}");
        }

        return new GeminiLegResult(
            stats.OutputCaptionCount,
            stats.OutputUpdateCount,
            stats.TurnCompleteCount,
            firstOutputSec,
            lastOutputSec,
            lastAfterFeed,
            stats.OutputCaptionCount / (audioSeconds / 120.0),
            repeated,
            duplicates,
            unterminated,
            summary.SelfCpuFraction,
            summary.PeakWorkingSetMb,
            stats.BytesSent,
            stats.BytesReceived,
            stats.AudioBytesSentDecoded,
            stats.AudioBytesReceivedDecoded,
            costUsd,
            costPerHour,
            stats.InputTokens,
            stats.OutputTokens,
            stats.ConnectSec,
            stats.SetupCompleteSec,
            stats.ErrorFrameCount,
            stats.SessionError,
            stats.OutputLanguageCode,
            rows);
    }

    private static async Task<List<CaptionRow>> RunTagalogEchoControlAsync(LegArgs legs, string apiKey, CancellationToken ct)
    {
        string wav = Path.Combine("artifacts", "samples", "first_meeting_tagalog.wav");
        if (legs.WavPath is not null && Path.GetFileName(legs.WavPath).Contains("tagalog", StringComparison.OrdinalIgnoreCase))
        {
            wav = legs.WavPath;
        }

        if (!File.Exists(wav))
        {
            Console.WriteLine("Tagalog echo control skipped: no Tagalog WAV found.");
            return new List<CaptionRow>();
        }

        var (_, pcm16, audioSeconds) = LoadWav(wav);
        Console.WriteLine($"=== CONTROL: source already Tagalog ({audioSeconds:0.00}s, echoTargetLanguage=true) ===");
        await using var client = new GeminiLiveTranslateClient(
            apiKey,
            DefaultModel,
            legs.Target,
            echoTargetLanguage: true,
            TimeSpan.FromSeconds(legs.TailSeconds));
        var sw = new Stopwatch();
        var rows = new List<CaptionRow>();
        long outputChars = 0;
        var onEvent = new Action<GeminiLiveTranslateClient.CaptionEvent>(ev =>
        {
            if (ev.Kind.StartsWith("output", StringComparison.Ordinal))
            {
                outputChars += ev.Text.Length;
                rows.Add(new CaptionRow(ev.ArrivalSec, -1, 0, ev.Kind, ev.Text));
                Console.WriteLine($"    {ev.Kind,-12} {ev.ArrivalSec,7:0.00}s | {Truncate(ev.Text, 80)}");
            }
        });

        sw.Start();
        var stats = await client.StreamAsync(pcm16, SampleRate, legs.Realtime, sw, onEvent, ct);
        sw.Stop();
        Console.WriteLine($"  output chars: {outputChars} over {audioSeconds:0.00}s of Tagalog input; {stats.OutputCaptionCount} captions; language {stats.OutputLanguageCode ?? "n/a"}");
        Console.WriteLine($"  echo behavior: {(outputChars == 0 ? "SILENT (echo disabled/unsupported)" : outputChars < 50 ? "minimal output" : "echoing input")}");
        if (stats.SessionError is not null)
        {
            Console.WriteLine($"  SESSION ERROR: {stats.SessionError}");
        }

        return rows;
    }

    private static void PrintComparison(
        ArgosLegResult? argos, GeminiLegResult? gemini, double audioSeconds, LegArgs legs)
    {
        Console.WriteLine("========================= COMPARISON (en→tl, same audio) =========================");
        Console.WriteLine($"  sample: {legs.WavPath} ({audioSeconds:0.00}s)   Gemini target: {legs.Target}");
        Console.WriteLine();
        Console.WriteLine("  Metric                        Argos          Gemini Live     note");
        Console.WriteLine("  ----------------------------  -------------  --------------  ----------------");

        string F(double? v) => v is null or double.NaN ? "n/a" : $"{v:0.00}";
        string I(int? v) => v is null ? "n/a" : $"{v}";

        if (argos is not null && gemini is not null)
        {
            Console.WriteLine($"  first translated caption(s)  {F(argos.FirstCaptionSec),13}  {F(gemini.FirstOutputSec),14}  from feed start");
            Console.WriteLine($"  committed captions           {I(argos.Captions),13}  {I(gemini.OutputCaptions),14}  Argos=STT finals; Gemini=output utterances");
            Console.WriteLine($"  caption updates              {I(argos.Partials),13}  {I(gemini.OutputUpdates),14}  Argos=STT partials; Gemini=live transcript updates");
            Console.WriteLine($"  update cadence /120s         {F(argos.CaptionsPer120S),13}  {F(gemini.CaptionsPer120S),14}  committed captions");
            Console.WriteLine($"  median translate ms          {F(argos.MedianTranslateMs),13}  {"n/a",14}  Argos per-final; Gemini has no text leg");
            Console.WriteLine($"  last caption after feed(s)   {"n/a",13}  {F(gemini.LastOutputAfterFeedSec),14}  Gemini tail flush");
            Console.WriteLine($"  repeated bigrams             {I(argos.RepeatedBigrams),13}  {I(gemini.RepeatedBigrams),14}");
            Console.WriteLine($"  consecutive duplicates       {I(argos.ConsecutiveDuplicates),13}  {I(gemini.ConsecutiveDuplicates),14}");
            Console.WriteLine($"  unterminated captions        {I(argos.Unterminated),13}  {I(gemini.Unterminated),14}");
            if (legs.Naturalize)
            {
                Console.WriteLine($"  captions naturalized        {I(argos.NaturalizedCaptions),13}  {"n/a",14}  rule-based TagalogNaturalizer");
            }
            Console.WriteLine($"  proc CPU % machine           {F(argos.ProcCpuFraction * 100),13}  {F(gemini.ProcCpuFraction * 100),14}");
            Console.WriteLine($"  worker CPU % machine         {F(argos.WorkerCpuFraction * 100),13}  {"0.00",14}  Argos=local python; Gemini=none local");
            Console.WriteLine($"  peak RAM MB                  {F(argos.PeakRamMb),13}  {F(gemini.PeakRamMb),14}");
            Console.WriteLine($"  network up/down MB           {"bundled",13}  {$"{gemini.BytesSent / 1048576.0:0.00}/{gemini.BytesReceived / 1048576.0:0.00}",14}");
            Console.WriteLine($"  cost USD/hour                {"bundled",13}  {F(gemini.CostUsdPerHour),14}  from usage metadata");
        }
        else if (argos is not null)
        {
            Console.WriteLine($"  (Gemini leg not run; offline comparison unavailable)");
        }
        else
        {
            Console.WriteLine($"  (Argos leg not run; offline comparison unavailable)");
        }

        Console.WriteLine("==================================================================================");
        Console.WriteLine("  Naturalness / meaning preservation: review the side-by-side caption streams below");
        Console.WriteLine("  (Chrome reference from appval_chrome_captions.txt is a different recording and is");
        Console.WriteLine("  qualitative context only, not a same-audio numeric baseline.)");
        Console.WriteLine();
    }

    private static void PrintCaptionStreams(ArgosLegResult? argos, GeminiLegResult? gemini)
    {
        Console.WriteLine("--- SIDE-BY-SIDE CAPTION STREAMS ---");
        int max = Math.Max(argos?.Rows.Count ?? 0, gemini?.Rows.Count ?? 0);
        for (int i = 0; i < max; i++)
        {
            var a = argos is not null && i < argos.Rows.Count ? argos.Rows[i] : null;
            var g = gemini is not null && i < gemini.Rows.Count ? gemini.Rows[i] : null;
            string aText = a is null ? string.Empty : $"[{a.EmitSec,6:0.00}s] {a.Text}";
            string gText = g is null ? string.Empty : $"[{g.EmitSec,6:0.00}s] {g.Text}";
            Console.WriteLine($"  {(argos is null ? string.Empty : $"{aText,-62}")} {(gemini is null ? string.Empty : $"| {gText}")}");
        }
    }

    private static void PrintTagalogControl(List<CaptionRow> rows)
    {
        Console.WriteLine();
        Console.WriteLine("--- TAGALOG ECHO CONTROL OUTPUT ---");
        foreach (var row in rows)
        {
            Console.WriteLine($"  [{row.EmitSec,6:0.00}s] {row.Text}");
        }
    }

    private static void PrintNaturalization(ArgosLegResult argos)
    {
        if (argos.NaturalizedRows is null || argos.NaturalizedRows.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"--- ARGOS NATURALIZATION (TagalogNaturalizer; {argos.NaturalizedCaptions}/{argos.Rows.Count} rewritten) ---");
        for (int i = 0; i < argos.Rows.Count; i++)
        {
            string original = argos.Rows[i].Text;
            string naturalized = argos.NaturalizedRows[i].Text;
            if (string.Equals(original, naturalized, StringComparison.Ordinal))
            {
                continue;
            }

            Console.WriteLine($"  [{argos.Rows[i].EmitSec,6:0.00}s] {Truncate(original, 64)}");
            Console.WriteLine($"                       → {Truncate(naturalized, 64)}");
        }
    }

    private static void PrintNaturalnessTable(ArgosLegResult argos, List<CaptionRow> geminiReference)
    {
        Console.WriteLine();
        Console.WriteLine("--- NATURALIZED ARGOS vs GEMINI REFERENCE (rows from a prior --gemini-ref-csv run) ---");
        if (geminiReference.Count == 0)
        {
            Console.WriteLine("  (reference stream is empty)");
            return;
        }

        string argosStream = string.Join(" ", argos.Rows.Select(r => r.Text));
        string naturalizedStream = string.Join(" ", (argos.NaturalizedRows ?? argos.Rows).Select(r => r.Text));
        string geminiStream = string.Join(" ", geminiReference.Select(r => r.Text));

        double origVsRef = CharSimilarity(argosStream, geminiStream);
        double natVsRef = CharSimilarity(naturalizedStream, geminiStream);

        Console.WriteLine("  full-stream char similarity (Levenshtein, 0..1) vs Gemini reference:");
        Console.WriteLine($"    Argos original : {origVsRef:0.000}");
        Console.WriteLine($"    Argos naturalized: {natVsRef:0.000}   ({natVsRef - origVsRef:+0.000})");
        Console.WriteLine();
        Console.WriteLine("  index-aligned lines (Argos sentence FINALs vs nearest Gemini increments):");
        int max = Math.Max(argos.Rows.Count, geminiReference.Count);
        for (int i = 0; i < max; i++)
        {
            string aText = i < argos.Rows.Count ? $"[{argos.Rows[i].EmitSec,6:0.00}s] {argos.Rows[i].Text}" : string.Empty;
            string gText = i < geminiReference.Count ? $"[{geminiReference[i].EmitSec,6:0.00}s] {geminiReference[i].Text}" : string.Empty;
            Console.WriteLine($"  {aText,-62} | {gText}");
        }
    }

    private static List<CaptionRow>? LoadGeminiReferenceCsv(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"  --gemini-ref-csv file not found: {path}");
            return null;
        }

        var rows = new List<CaptionRow>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (!line.StartsWith("gemini,", StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = ParseCsvLine(line);
            if (fields.Length < 7)
            {
                continue;
            }

            double emitSec = double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double e)
                ? e
                : 0;
            rows.Add(new CaptionRow(emitSec, -1, 0, fields[5], fields[6]));
        }

        return rows;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static double CharSimilarity(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        int max = Math.Max(a.Length, b.Length);
        return max == 0 ? 1.0 : 1.0 - (double)d[a.Length, b.Length] / max;
    }

    private static async Task FeedAsync(
        FasterWhisperNativeStreamingEngine engine, float[] samples, DateTime baseTime, bool realtime, CancellationToken ct)
    {
        const int chunkMs = 10;
        int chunkFrames = Math.Max(1, SampleRate * chunkMs / 1000);
        long seq = 0;
        for (int offset = 0; offset < samples.Length && !ct.IsCancellationRequested; offset += chunkFrames)
        {
            int count = Math.Min(chunkFrames, samples.Length - offset);
            var chunk = new float[count];
            Array.Copy(samples, offset, chunk, 0, count);
            engine.Process(new AudioChunk(chunk, new AudioFormat(SampleRate, 1, 32), baseTime.AddSeconds(offset / (double)SampleRate), ++seq));
            if (realtime)
            {
                Thread.Sleep(chunkMs);
            }
        }
    }

    private static (float[] Samples, byte[] Pcm16, double Seconds) LoadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        if (format.BitsPerSample != 16)
        {
            throw new InvalidOperationException($"Expected 16-bit PCM but found {format.BitsPerSample}-bit ({format.Encoding}).");
        }

        var raw = new byte[reader.Length];
        int read = reader.Read(raw, 0, raw.Length);
        var pcm = new short[read / 2];
        Buffer.BlockCopy(raw, 0, pcm, 0, read);

        float[] mono;
        if (format.Channels == 1)
        {
            mono = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
            {
                mono[i] = pcm[i] / 32768f;
            }
        }
        else if (format.Channels == 2)
        {
            mono = new float[pcm.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = (pcm[i * 2] / 32768f + pcm[(i * 2) + 1] / 32768f) * 0.5f;
            }
        }
        else
        {
            throw new InvalidOperationException($"Unsupported channel count {format.Channels}.");
        }

        if (format.SampleRate == 8000)
        {
            mono = UpsampleLinear(mono, factor: 2);
        }
        else if (format.SampleRate != SampleRate)
        {
            throw new InvalidOperationException($"Unsupported sample rate {format.SampleRate} Hz (expected 8000 or 16000).");
        }

        byte[] pcm16 = ConvertToPcm16(mono);
        return (mono, pcm16, mono.Length / (double)SampleRate);
    }

    private static byte[] ConvertToPcm16(float[] samples)
    {
        var shorts = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            float v = Math.Clamp(samples[i], -1f, 1f);
            shorts[i] = (short)(v * 32767f);
        }

        var bytes = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] UpsampleLinear(float[] input, int factor)
    {
        var output = new float[(input.Length - 1) * factor + 1];
        for (int i = 0; i < input.Length - 1; i++)
        {
            for (int k = 0; k < factor; k++)
            {
                output[(i * factor) + k] = input[i] + (input[i + 1] - input[i]) * (k / (float)factor);
            }
        }

        output[^1] = input[^1];
        return output;
    }

    private static int RepeatedBigrams(string text)
    {
        string[] words = text.ToLowerInvariant().Split(
            new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '—' },
            StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2)
        {
            return 0;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int repeats = 0;
        for (int i = 0; i < words.Length - 1; i++)
        {
            string bigram = words[i] + " " + words[i + 1];
            if (!seen.Add(bigram))
            {
                repeats++;
            }
        }

        return repeats;
    }

    private static int ConsecutiveDuplicates(List<CaptionRow> rows)
    {
        int count = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Text, rows[i - 1].Text, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static bool EndsTerminal(string text)
    {
        string t = text.TrimEnd();
        if (t.Length == 0)
        {
            return true;
        }

        char last = t[^1];
        return last is '.' or '?' or '!' or '"' or '\'' or ')' or ']' or '}' or '…';
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static string FormatSec(double seconds) => double.IsNaN(seconds) || seconds < 0 ? "n/a" : $"{seconds:0.000}s";

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(max - 3)] + "...";
    }

    private static void WriteCsv(
        string path,
        ArgosLegResult? argos,
        GeminiLegResult? gemini,
        List<CaptionRow>? tagalogEcho,
        double audioSeconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine("leg,index,emit_s,source_start_s,translate_ms,kind,text,naturalized");
        if (argos is not null)
        {
            List<CaptionRow>? naturalized = argos.NaturalizedRows;
            for (int i = 0; i < argos.Rows.Count; i++)
            {
                var r = argos.Rows[i];
                sb.AppendLine(string.Join(',',
                    Csv("argos"),
                    Csv((i + 1).ToString(CultureInfo.InvariantCulture)),
                    Csv(r.EmitSec.ToString("0.000", CultureInfo.InvariantCulture)),
                    Csv(r.SourceStartSec.ToString("0.000", CultureInfo.InvariantCulture)),
                    Csv(r.TranslateMs.ToString("0", CultureInfo.InvariantCulture)),
                    Csv(r.Kind),
                    Csv(r.Text),
                    Csv(naturalized is not null ? naturalized[i].Text : string.Empty)));
            }
        }

        if (gemini is not null)
        {
            for (int i = 0; i < gemini.Rows.Count; i++)
            {
                var r = gemini.Rows[i];
                sb.AppendLine(string.Join(',',
                    Csv("gemini"),
                    Csv((i + 1).ToString(CultureInfo.InvariantCulture)),
                    Csv(r.EmitSec.ToString("0.000", CultureInfo.InvariantCulture)),
                    Csv(""),
                    Csv(""),
                    Csv(r.Kind),
                    Csv(r.Text),
                    Csv("")));
            }
        }

        if (tagalogEcho is not null)
        {
            for (int i = 0; i < tagalogEcho.Count; i++)
            {
                var r = tagalogEcho[i];
                sb.AppendLine(string.Join(',',
                    Csv("gemini_tagalog_echo"),
                    Csv((i + 1).ToString(CultureInfo.InvariantCulture)),
                    Csv(r.EmitSec.ToString("0.000", CultureInfo.InvariantCulture)),
                    Csv(""),
                    Csv(""),
                    Csv(r.Kind),
                    Csv(r.Text),
                    Csv("")));
            }
        }

        sb.AppendLine();
        sb.AppendLine("summary,leg,audio_s,first_caption_s,committed,updates,cadence_per120s,median_translate_ms,tail_after_feed_s,repeated_bigrams,duplicates,unterminated,proc_cpu_pct,worker_cpu_pct,peak_ram_mb,up_mb,down_mb,cost_usd,cost_usd_per_hour,connect_s,setup_s,error,naturalized_captions");
        if (argos is not null)
        {
            sb.AppendLine(string.Join(',',
                Csv(""),
                Csv("argos"),
                Csv(audioSeconds.ToString("0.00", CultureInfo.InvariantCulture)),
                Csv(FormatValue(argos.FirstCaptionSec)),
                Csv(argos.Captions.ToString(CultureInfo.InvariantCulture)),
                Csv(argos.Partials.ToString(CultureInfo.InvariantCulture)),
                Csv(argos.CaptionsPer120S.ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(argos.MedianTranslateMs.ToString("0", CultureInfo.InvariantCulture)),
                Csv(""),
                Csv(argos.RepeatedBigrams.ToString(CultureInfo.InvariantCulture)),
                Csv(argos.ConsecutiveDuplicates.ToString(CultureInfo.InvariantCulture)),
                Csv(argos.Unterminated.ToString(CultureInfo.InvariantCulture)),
                Csv((argos.ProcCpuFraction * 100).ToString("0.0", CultureInfo.InvariantCulture)),
                Csv((argos.WorkerCpuFraction * 100).ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(argos.PeakRamMb.ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(""),
                Csv(""),
                Csv(""),
                Csv(""),
                Csv(""),
                Csv(""),
                Csv(argos.Error ?? string.Empty),
                Csv(argos.NaturalizedCaptions.ToString(CultureInfo.InvariantCulture))));
        }

        if (gemini is not null)
        {
            sb.AppendLine(string.Join(',',
                Csv(""),
                Csv("gemini"),
                Csv(audioSeconds.ToString("0.00", CultureInfo.InvariantCulture)),
                Csv(FormatValue(gemini.FirstOutputSec)),
                Csv(gemini.OutputCaptions.ToString(CultureInfo.InvariantCulture)),
                Csv(gemini.OutputUpdates.ToString(CultureInfo.InvariantCulture)),
                Csv(gemini.CaptionsPer120S.ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(""),
                Csv(FormatValue(gemini.LastOutputAfterFeedSec)),
                Csv(gemini.RepeatedBigrams.ToString(CultureInfo.InvariantCulture)),
                Csv(gemini.ConsecutiveDuplicates.ToString(CultureInfo.InvariantCulture)),
                Csv(gemini.Unterminated.ToString(CultureInfo.InvariantCulture)),
                Csv((gemini.ProcCpuFraction * 100).ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(""),
                Csv(gemini.PeakRamMb.ToString("0.0", CultureInfo.InvariantCulture)),
                Csv((gemini.BytesSent / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture)),
                Csv((gemini.BytesReceived / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture)),
                Csv(gemini.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)),
                Csv(gemini.CostUsdPerHour.ToString("0.00", CultureInfo.InvariantCulture)),
                Csv(FormatValue(gemini.ConnectSec)),
                Csv(FormatValue(gemini.SetupCompleteSec)),
                Csv(gemini.SessionError ?? string.Empty),
                Csv("")));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"CSV written to {Path.GetFullPath(path)}");
    }

    private static string FormatValue(double value) => value < 0 || double.IsNaN(value) ? string.Empty : value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project src/UniversalCaptions.Benchmarks -- translatelive --wav <path> [options]");
        Console.WriteLine("  --wav <path>         16-bit PCM WAV (8k/16k mono or stereo); the SAME audio feeds both legs");
        Console.WriteLine("  --target <code>      Gemini target BCP-47 (default fil; tl also accepted)");
        Console.WriteLine("  --legs <set>         argos|gemini|both (default both)");
        Console.WriteLine("  --tagalog-control    also probe Gemini with source-already-Tagalog audio (echoTargetLanguage=true)");
        Console.WriteLine("  --stt-python <path>  faster-whisper worker python (env UC_FW_PYTHON or %TEMP%\\fwv auto-detected)");
        Console.WriteLine("  --argos-python <path> Argos python (env UC_ARGOS_PYTHON or %TEMP%\\argosv auto-detected)");
        Console.WriteLine("  --model <name>       faster-whisper STT model (default small)");
        Console.WriteLine("  --threads <n>        STT decode threads (default 4)");
        Console.WriteLine("  --feed realtime|fast realtime paces both legs at audio speed (default realtime)");
        Console.WriteLine("  --max-segment/hangover/partial-interval/partial-window  Argos-leg STT knobs");
        Console.WriteLine("  --tail-s <sec>       Gemini tail-flush window after the last chunk (default 5)");
        Console.WriteLine("  --csv <path>         write per-caption + summary CSV");
        Console.WriteLine("  --no-naturalize      disable the deterministic Tagalog naturalizer on Argos captions (default: on)");
        Console.WriteLine("  --gemini-ref-csv <p> load a prior translatelive CSV's gemini rows as the offline comparison");
        Console.WriteLine("                       reference instead of running the online leg (requires --legs argos)");
        Console.WriteLine("Requires GEMINI_API_KEY env var for the Gemini leg (never stored/committed).");
    }

    /// <summary>
    /// Samples this benchmark process and any matched python worker processes every 200 ms so the
    /// comparison table can report the real CPU/RAM cost of each leg.
    /// </summary>
    private sealed class ProcessMonitor : IDisposable
    {
        private readonly string[] _patterns;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<(double Sec, double SelfCpu, double WorkerCpu, double SelfWs, double WorkerWs)> _samples = new();
        private readonly object _lock = new();
        private Thread? _thread;
        private readonly Stopwatch _sw = new();

        internal ProcessMonitor(params string[] patterns) => _patterns = patterns;

        internal void Start()
        {
            lock (_lock)
            {
                if (_thread is not null)
                {
                    return;
                }

                _samples.Clear();
                _sw.Restart();
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "proc-monitor",
                };
                _thread.Start();
            }
        }

        internal (double SelfCpuFraction, double WorkerCpuFraction, double PeakWorkingSetMb) Stop()
        {
            lock (_lock)
            {
                _cts.Cancel();
                var thread = _thread;
                _thread = null;
                thread?.Join(TimeSpan.FromSeconds(3));
                _sw.Stop();

                if (_samples.Count == 0)
                {
                    return (0, 0, 0);
                }

                double wall = _sw.Elapsed.TotalSeconds;
                double cores = Math.Max(1, Environment.ProcessorCount);
                double selfFirst = _samples[0].SelfCpu;
                double selfLast = _samples[^1].SelfCpu;
                double workerFirst = _samples[0].WorkerCpu;
                double workerLast = _samples[^1].WorkerCpu;
                double selfFraction = Math.Max(0, (selfLast - selfFirst) / wall / cores);
                double workerFraction = Math.Max(0, (workerLast - workerFirst) / wall / cores);
                double peakWs = _samples.Max(s => s.SelfWs + s.WorkerWs);
                return (selfFraction, workerFraction, peakWs / (1024.0 * 1024.0));
            }
        }

        private void Run()
        {
            var ct = _cts.Token;
            var self = Process.GetCurrentProcess();
            while (!ct.IsCancellationRequested)
            {
                double selfCpu = self.TotalProcessorTime.TotalSeconds;
                double selfWs = self.WorkingSet64;
                double workerCpu = 0;
                double workerWs = 0;
                foreach (int pid in FindWorkerPids())
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        try
                        {
                            workerCpu += p.TotalProcessorTime.TotalSeconds;
                            workerWs += p.WorkingSet64;
                        }
                        catch (InvalidOperationException)
                        {
                            // Process exited between enumeration and sampling.
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Process exited between enumeration and sampling.
                    }
                }

                lock (_lock)
                {
                    _samples.Add((_sw.Elapsed.TotalSeconds, selfCpu, workerCpu, selfWs, workerWs));
                }

                try
                {
                    Thread.Sleep(200);
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
            }
        }

        private IEnumerable<int> FindWorkerPids()
        {
            var pids = new List<int>();
            if (!OperatingSystem.IsWindows())
            {
                return pids;
            }

            try
            {
                var query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE (Name = 'python.exe' OR Name = 'pythonw.exe')";
                using var searcher = new ManagementObjectSearcher(query);
                foreach (var obj in searcher.Get())
                {
                    string? commandLine = obj["CommandLine"] as string;
                    if (commandLine is null)
                    {
                        continue;
                    }

                    if (_patterns.Length > 0 && !_patterns.Any(p => commandLine.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (int.TryParse(Convert.ToString(obj["ProcessId"], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                    {
                        pids.Add(pid);
                    }
                }
            }
            catch
            {
                // Best-effort worker discovery.
            }

            return pids;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            lock (_lock)
            {
                _thread = null;
            }
        }
    }
}
