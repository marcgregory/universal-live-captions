using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech;

/// <summary>
/// Additive measurement path for the Slice 10 experiment: drives the real
/// <see cref="FasterWhisperNativeStreamingEngine"/> (segment-based, VAD-gated, one FINAL per completed
/// segment) from a recorded 16 kHz mono WAV and records the gate table — first FINAL, FINAL commit
/// timeline, FINAL-only verification, stop flush, emit-lag proxy for backlog, CPU/realtime factor and
/// WER against a reference. Composes the engine exactly as the App does so the numbers are
/// representative of production. Mode is <c>sttnative</c>.
/// </summary>
internal static class NativeStreamingBenchmark
{
    private const int SampleRate = 16_000;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? wavPath = null;
        string? referencePath = null;
        string python = "python";
        string model = "small";
        string? language = null;
        int chunkMs = 10;
        bool realtime = true;
        double rms = 0.008;
        double minSpeechSeconds = 0.3;
        double hangoverSeconds = 0.7;
        double maxSegmentSeconds = 8.0;
        double partialIntervalSeconds = 0;
        double partialWindowSeconds = 4.0;
        int threads = 4;
        string? csvPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--wav" when i + 1 < args.Length:
                    wavPath = args[++i];
                    break;
                case "--reference" when i + 1 < args.Length:
                    referencePath = args[++i];
                    break;
                case "--python" when i + 1 < args.Length:
                    python = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--language" when i + 1 < args.Length:
                    language = args[++i];
                    break;
                case "--chunk-ms" when i + 1 < args.Length:
                    chunkMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--feed" when i + 1 < args.Length:
                    realtime = string.Equals(args[++i], "realtime", StringComparison.OrdinalIgnoreCase);
                    break;
                case "--rms" when i + 1 < args.Length:
                    rms = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--min-speech" when i + 1 < args.Length:
                    minSpeechSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--hangover" when i + 1 < args.Length:
                    hangoverSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--max-segment" when i + 1 < args.Length:
                    maxSegmentSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--partial-interval" when i + 1 < args.Length:
                    partialIntervalSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--partial-window" when i + 1 < args.Length:
                    partialWindowSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--threads" when i + 1 < args.Length:
                    threads = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--csv" when i + 1 < args.Length:
                    csvPath = args[++i];
                    break;
            }
        }

        if (wavPath is null)
        {
            Console.Error.WriteLine("sttnative requires --wav <path> (16 kHz mono 16-bit PCM WAV).");
            return 2;
        }

        Console.WriteLine("=== sttnative: faster-whisper native streaming (segment-based, FINAL per segment + live partials) ===");
        Console.WriteLine($"Machine: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"WAV: {Path.GetFullPath(wavPath)}");
        Console.WriteLine($"Python: {python}");
        Console.WriteLine($"Model: {model}; language: {language ?? "auto"}; feed chunk: {chunkMs}ms ({(realtime ? "realtime" : "fast")})");
        Console.WriteLine($"VAD: RMS {rms:0.###}, MinActive 1, hangover 2 chunks; segment: min {minSpeechSeconds:0.##}s, hangover {hangoverSeconds:0.##}s, max {maxSegmentSeconds:0.##}s");
        Console.WriteLine($"Partials: interval {partialIntervalSeconds:0.##}s{(partialIntervalSeconds <= 0 ? " (off, Slice 10/11 FINAL-only)" : $", window {partialWindowSeconds:0.##}s")}");
        Console.WriteLine($"Threads: {threads} (worker --threads; Entry 16 CPU gate)");
        Console.WriteLine();

        float[] samples = ReadAudioToMono16k(wavPath);
        double audioSeconds = samples.Length / (double)SampleRate;
        Console.WriteLine($"Audio: {audioSeconds:0.00}s, {samples.Length:N0} samples @ {SampleRate} Hz");

        string? reference = null;
        if (referencePath is not null && File.Exists(referencePath))
        {
            reference = File.ReadAllText(referencePath).Trim();
        }

        if (reference is not null)
        {
            Console.WriteLine($"WER reference: {reference.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words from {referencePath}");
        }
        else
        {
            Console.WriteLine("WER reference: none (WER will be n/a).");
        }

        var engine = new FasterWhisperNativeStreamingEngine(
            new FasterWhisperEngineOptions
            {
                PythonExecutablePath = python,
                Model = model,
                Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant(),
                PartialDecodeInterval = TimeSpan.FromSeconds(partialIntervalSeconds),
                PartialDecodeWindow = TimeSpan.FromSeconds(partialWindowSeconds),
                Threads = threads,
            },
            new EnergyVad(new VadOptions(RmsThreshold: rms, MinActiveChunks: 1, SilenceHangoverChunks: 2)),
            new SpeechSegmentDetectorOptions
            {
                SampleRate = SampleRate,
                MinSpeechDuration = TimeSpan.FromSeconds(minSpeechSeconds),
                SilenceHangover = TimeSpan.FromSeconds(hangoverSeconds),
                MaxSegmentDuration = TimeSpan.FromSeconds(maxSegmentSeconds),
            });

        int partials = 0;
        int finals = 0;
        int finalsAfterStop = 0;
        string? error = null;
        var finalsByStartSec = new List<(double EmitSec, double StartSec, string Text)>();
        var partialsByStartSec = new List<(double EmitSec, double StartSec, string Text)>();
        long fedSamples = 0;
        var sw = Stopwatch.StartNew();
        DateTime baseTime = DateTime.UtcNow;

        engine.PartialTranscriptAvailable += (_, t) =>
        {
            partials++;
            double startSec = (t.CapturedAtUtc - baseTime).TotalSeconds;
            double emitSec = sw.Elapsed.TotalSeconds;
            partialsByStartSec.Add((emitSec, startSec, t.Text));
            Console.WriteLine($"    PARTIAL[{partials,3}] emit {emitSec,7:0.00}s | winStart {startSec,7:0.00}s | {Truncate(t.Text, 60)}");
        };

        engine.FinalTranscriptAvailable += (_, t) =>
        {
            finals++;
            double startSec = (t.CapturedAtUtc - baseTime).TotalSeconds;
            double emitSec = sw.Elapsed.TotalSeconds;
            double fedSec = Interlocked.Read(ref fedSamples) / (double)SampleRate;
            finalsByStartSec.Add((emitSec, startSec, t.Text));
            Console.WriteLine($"    FINAL[{finals,3}] emit {emitSec,7:0.00}s | segStart {startSec,7:0.00}s | fedAtEmit {fedSec,7:0.00}s | {Truncate(t.Text, 90)}");
        };

        engine.RecognitionFailed += (_, e) => error ??= $"{e.Kind}: {e.Message}";

        var swStartup = Stopwatch.StartNew();
        engine.Start();
        swStartup.Stop();
        Console.WriteLine($"Worker/model start: {swStartup.ElapsedMilliseconds} ms");

        sw.Restart();

        if (realtime)
        {
            timeBeginPeriod(1);
        }

        try
        {
            int chunkFrames = Math.Max(1, SampleRate * chunkMs / 1000);
            long seq = 0;
            for (int offset = 0; offset < samples.Length && !ct.IsCancellationRequested; offset += chunkFrames)
            {
                int count = Math.Min(chunkFrames, samples.Length - offset);
                var chunk = new float[count];
                Array.Copy(samples, offset, chunk, 0, count);
                engine.Process(new AudioChunk(chunk, new AudioFormat(SampleRate, 1, 32), baseTime.AddSeconds(offset / (double)SampleRate), ++seq));
                Interlocked.Exchange(ref fedSamples, offset + count);
                if (realtime)
                {
                    Thread.Sleep(chunkMs);
                }
            }
        }
        finally
        {
            if (realtime)
            {
                timeEndPeriod(1);
            }
        }

        double feedWallSec = sw.Elapsed.TotalSeconds;
        int finalsBeforeStop = finals;

        engine.Stop();
        await engine.DisposeAsync();
        sw.Stop();

        finalsAfterStop = finals - finalsBeforeStop;
        Console.WriteLine();
        Console.WriteLine($"Feed finished at {feedWallSec:0.00}s wall; {finalsBeforeStop} FINALs committed during feed; {finalsAfterStop} FINAL(s) flushed on Stop.");
        Console.WriteLine($"Engine disposed; total wall {sw.Elapsed.TotalSeconds:0.00}s; final count {finals}; partials {partials}.");
        if (error is not null)
        {
            Console.WriteLine($"    RECOGNITION ERROR: {error}");
        }

        Console.WriteLine();
        Console.WriteLine("=== GATE TABLE ===");
        double firstFinal = finalsByStartSec.Count > 0 ? finalsByStartSec[0].EmitSec : double.NaN;
        double totalCpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
        double realtimeFactor = sw.Elapsed.TotalSeconds / audioSeconds;
        string concatenated = string.Join(" ", finalsByStartSec.Select(f => f.Text));
        double wer = reference is null ? double.NaN : ComputeWer(concatenated, reference);
        int unterminated = finalsByStartSec.Count(f => !EndsSentence(f.Text));
        int fragments = finalsByStartSec.Count(f => WordCount(f.Text) <= 2 && !EndsSentence(f.Text));
        var splitIndices = finalsByStartSec
            .Select((f, i) => (Index: i + 1, F: f))
            .Where(x => !EndsSentence(x.F.Text))
            .Select(x => x.Index)
            .ToArray();

        Console.WriteLine($"  first FINAL:             {FormatSec(firstFinal)} (from audio feed start)");
        Console.WriteLine($"  FINALs:                  {finals} total; {finals - finalsAfterStop} during feed; {finalsAfterStop} on Stop flush");
        Console.WriteLine($"  commit cadence:          {finals / (audioSeconds / 120.0):0.0} FINALs per 120 s of audio");
        Console.WriteLine($"  WER (committed, vs ref): {(double.IsNaN(wer) ? "n/a" : $"{wer * 100:0.0}%")}");
        Console.WriteLine($"  FINAL-only:              {(partials == 0 ? "yes" : $"NO ({partials} partials)")}");
        Console.WriteLine($"  stop flush:              {(finalsAfterStop > 0 ? "yes" : "no in-progress segment")}");
        Console.WriteLine($"  wall vs audio:           {sw.Elapsed.TotalSeconds:0.00}s wall / {audioSeconds:0.00}s audio = {realtimeFactor:0.00}x realtime");
        Console.WriteLine($"  process cpu:             {totalCpu:0.0}s");
        Console.WriteLine($"  mid-sentence splits:     {unterminated} of {finals} FINALs end without terminal punctuation (split points: {(splitIndices.Length == 0 ? "-" : string.Join(", ", splitIndices))})");
        Console.WriteLine($"  short fragments:         {fragments} FINALs <=2 words and unterminated");

        if (partialsByStartSec.Count > 0)
        {
            double firstPartialEmit = partialsByStartSec[0].EmitSec;
            double firstCaptionLag = partialsByStartSec[0].EmitSec - partialsByStartSec[0].StartSec;
            double[] partialLags = partialsByStartSec
                .Select(p => p.EmitSec - p.StartSec)
                .OrderBy(v => v)
                .ToArray();
            Console.WriteLine($"  first partial:           {FormatSec(firstPartialEmit)} (from audio feed start)");
            Console.WriteLine($"  first caption lag:       {FormatSec(firstCaptionLag)} (T4 from speech onset; first partial window start)");
            Console.WriteLine($"  partial update cadence:  {partials / (audioSeconds / 120.0):0.0} partials per 120 s of audio");
            Console.WriteLine($"  partial lag (vs winStart): min {partialLags[0]:0.00}s, median {partialLags[partialLags.Length / 2]:0.00}s, max {partialLags[^1]:0.00}s");
        }

        if (finalsByStartSec.Count > 0)
        {
            double[] lags = finalsByStartSec
                .Select(f => f.EmitSec - f.StartSec)
                .OrderBy(v => v)
                .ToArray();
            Console.WriteLine($"  emit-lag vs segStart:    min {lags[0]:0.00}s, median {lags[lags.Length / 2]:0.00}s, max {lags[^1]:0.00}s (segment duration + hangover + queue + decode; growth => backlog)");
        }

        Console.WriteLine();
        Console.WriteLine("--- COMMITTED FINAL STREAM ---");
        for (int i = 0; i < finalsByStartSec.Count; i++)
        {
            Console.WriteLine($"    FINAL[{i + 1,3}] (segStart {finalsByStartSec[i].StartSec,7:0.00}s) {finalsByStartSec[i].Text}");
        }

        if (partialsByStartSec.Count > 0)
        {
            Console.WriteLine("--- LIVE PARTIAL STREAM ---");
            for (int i = 0; i < partialsByStartSec.Count; i++)
            {
                Console.WriteLine($"    PARTIAL[{i + 1,3}] (winStart {partialsByStartSec[i].StartSec,7:0.00}s) {partialsByStartSec[i].Text}");
            }
        }

        if (csvPath is not null)
        {
            WriteCsv(csvPath, finalsByStartSec, partialsByStartSec, firstFinal, finals, partials, wer, realtimeFactor, totalCpu, audioSeconds, referencePath, unterminated, fragments);
        }

        return 0;
    }

    private static float[] ReadAudioToMono16k(string path)
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

        if (format.SampleRate == SampleRate)
        {
            return mono;
        }

        if (format.SampleRate == 8000)
        {
            return UpsampleLinear(mono, factor: 2);
        }

        throw new InvalidOperationException($"Unsupported sample rate {format.SampleRate} Hz (expected 8000 or 16000).");
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

    private static double ComputeWer(string transcript, string referenceText)
    {
        var hyp = Tokenize(transcript);
        var refWords = Tokenize(referenceText);
        if (refWords.Length == 0)
        {
            return 1.0;
        }

        var d = new int[hyp.Length + 1, refWords.Length + 1];
        for (int i = 0; i <= hyp.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= refWords.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= hyp.Length; i++)
        {
            for (int j = 1; j <= refWords.Length; j++)
            {
                int cost = hyp[i - 1] == refWords[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return (double)d[hyp.Length, refWords.Length] / refWords.Length;
    }

    private static string[] Tokenize(string text) => text.ToLowerInvariant().Split(
        new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '—' },
        StringSplitOptions.RemoveEmptyEntries);

    private static bool EndsSentence(string text)
    {
        string t = text.TrimEnd();
        if (t.Length == 0)
        {
            return true;
        }

        char last = t[^1];
        return last is '.' or '?' or '!' or '"' or '\'' or ')' or ']' or '}' or '…';
    }

    private static int WordCount(string text) => text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string FormatSec(double seconds) => double.IsNaN(seconds) ? "n/a" : $"{seconds:0.000}s";

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
        List<(double EmitSec, double StartSec, string Text)> finalsByStartSec,
        List<(double EmitSec, double StartSec, string Text)> partialsByStartSec,
        double firstFinal,
        int finals,
        int partials,
        double wer,
        double realtimeFactor,
        double cpuSeconds,
        double audioSeconds,
        string? referencePath,
        int unterminated,
        int fragments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("final_index,segment_start_s,emit_s,emit_lag_s,text");
        for (int i = 0; i < finalsByStartSec.Count; i++)
        {
            var f = finalsByStartSec[i];
            sb.AppendLine(string.Join(',',
                Csv((i + 1).ToString(CultureInfo.InvariantCulture)),
                Csv(f.StartSec.ToString("0.000", CultureInfo.InvariantCulture)),
                Csv(f.EmitSec.ToString("0.000", CultureInfo.InvariantCulture)),
                Csv((f.EmitSec - f.StartSec).ToString("0.000", CultureInfo.InvariantCulture)),
                Csv(f.Text)));
        }

        sb.AppendLine();
        sb.AppendLine("partial_index,window_start_s,emit_s,partial_lag_s,text");
        for (int i = 0; i < partialsByStartSec.Count; i++)
        {
            var p = partialsByStartSec[i];
            sb.AppendLine(string.Join(',',
                Csv((i + 1).ToString(CultureInfo.InvariantCulture)),
                Csv(p.StartSec.ToString("0.000", CultureInfo.InvariantCulture)),
                Csv(p.EmitSec.ToString("0.000", CultureInfo.InvariantCulture)),
                Csv((p.EmitSec - p.StartSec).ToString("0.000", CultureInfo.InvariantCulture)),
                Csv(p.Text)));
        }

        double firstPartial = partialsByStartSec.Count > 0 ? partialsByStartSec[0].EmitSec : double.NaN;
        double firstCaptionLag = partialsByStartSec.Count > 0
            ? partialsByStartSec[0].EmitSec - partialsByStartSec[0].StartSec
            : double.NaN;
        double partialCadence = audioSeconds > 0 ? partials / (audioSeconds / 120.0) : 0;
        double medianPartialLag = double.NaN;
        if (partialsByStartSec.Count > 0)
        {
            var lags = partialsByStartSec.Select(p => p.EmitSec - p.StartSec).OrderBy(v => v).ToArray();
            medianPartialLag = lags[lags.Length / 2];
        }

        sb.AppendLine();
        sb.AppendLine($"summary,first_final_s,finals,partials,first_partial_s,first_caption_lag_s,partial_cadence,median_partial_lag_s,wer,realtime_factor,cpu_s,audio_s,unterminated,short_fragments,reference");
        sb.AppendLine(string.Join(',',
            Csv(""),
            Csv(double.IsNaN(firstFinal) ? string.Empty : firstFinal.ToString("0.000", CultureInfo.InvariantCulture)),
            Csv(finals.ToString(CultureInfo.InvariantCulture)),
            Csv(partials.ToString(CultureInfo.InvariantCulture)),
            Csv(double.IsNaN(firstPartial) ? string.Empty : firstPartial.ToString("0.000", CultureInfo.InvariantCulture)),
            Csv(double.IsNaN(firstCaptionLag) ? string.Empty : firstCaptionLag.ToString("0.000", CultureInfo.InvariantCulture)),
            Csv(partialCadence.ToString("0.0", CultureInfo.InvariantCulture)),
            Csv(double.IsNaN(medianPartialLag) ? string.Empty : medianPartialLag.ToString("0.000", CultureInfo.InvariantCulture)),
            Csv(double.IsNaN(wer) ? string.Empty : wer.ToString("0.0000", CultureInfo.InvariantCulture)),
            Csv(realtimeFactor.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(cpuSeconds.ToString("0.0", CultureInfo.InvariantCulture)),
            Csv(audioSeconds.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(unterminated.ToString(CultureInfo.InvariantCulture)),
            Csv(fragments.ToString(CultureInfo.InvariantCulture)),
            Csv(referencePath ?? string.Empty)));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"CSV written to {Path.GetFullPath(path)}");
    }

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }
}
