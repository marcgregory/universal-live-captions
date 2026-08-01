using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Wave;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech;
using Whisper.net;

const string DefaultModelsDir = "artifacts/models";
const string DefaultSamplesDir = "artifacts/samples";
const int SampleRate = 16_000;
const string JfkUrl = "https://github.com/ggerganov/whisper.cpp/raw/master/samples/jfk.wav";
const string OsrUrl = "https://www.voiptroubleshooter.com/open_speech/american/OSR_us_000_0010_8k.wav";
const string JfkReference =
    "And so my fellow Americans ask not what your country can do for you ask what you can do for your country";

string modelsDir = DefaultModelsDir;
int threads = Math.Max(2, Environment.ProcessorCount / 2);
List<string> candidates = new[] { "tiny", "base" }
    .Select(n => Path.Combine(DefaultModelsDir, $"ggml-{n}.bin"))
    .ToList();
var customWavs = new List<string>();
string? referenceText = null;
var sampleFilters = new List<string>();
double windowSeconds = 8;
double intervalSeconds = 1;
int stabilityWindow = 2;
FeedMode feed = FeedMode.Realtime;
string? csvPath = null;

if (args.Length > 0 && args[0] == "translate")
{
    return await TranslationBenchmark.RunAsync(args[1..], CancellationToken.None);
}

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--models" when i + 1 < args.Length:
            modelsDir = args[++i];
            candidates = candidates.Select(p => Path.Combine(modelsDir, Path.GetFileName(p))).ToList();
            break;
        case "--wav" when i + 1 < args.Length:
            customWavs.Add(args[++i]);
            break;
        case "--sample" when i + 1 < args.Length:
            sampleFilters.Add(args[++i]);
            break;
        case "--reference" when i + 1 < args.Length:
            referenceText = args[++i];
            break;
        case "--threads" when i + 1 < args.Length:
            threads = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--model" when i + 1 < args.Length:
            candidates = [args[++i]];
            break;
        case "--window" when i + 1 < args.Length:
            windowSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--interval" when i + 1 < args.Length:
            intervalSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--stability" when i + 1 < args.Length:
            stabilityWindow = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--feed" when i + 1 < args.Length:
            feed = Enum.Parse<FeedMode>(args[++i], ignoreCase: true);
            break;
        case "--csv" when i + 1 < args.Length:
            csvPath = args[++i];
            break;
    }
}

Console.WriteLine($"Machine: {Environment.OSVersion.VersionString}");
Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores; threads per decode: {threads}");
Console.WriteLine($"Model dir: {Path.GetFullPath(modelsDir)}");
Console.WriteLine($"Stream config: window {windowSeconds:0.#}s, interval {intervalSeconds:0.#}s, stability {stabilityWindow}, feed {feed}");
Console.WriteLine();

var samples = customWavs.Count > 0
    ? BuildCustomSamples(customWavs, referenceText)
    : await BuildDefaultSamplesAsync(CancellationToken.None);

if (sampleFilters.Count > 0)
{
    samples = samples.Where(s => sampleFilters.Any(f => s.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
    Console.WriteLine($"Samples filtered to: {string.Join(", ", samples.Select(s => s.Name))}");
}

foreach (var modelPath in candidates)
{
    await EnsureModelAsync(modelPath, CancellationToken.None);
}

var allResults = new List<ModelResult>();
foreach (var sample in samples)
{
    Console.WriteLine($"=== Sample: {sample.Name} ({sample.AudioSeconds:0.00}s) ===");
    string? reference = sample.Reference;
    if (reference is null && sample.PseudoReferenceModel is not null)
    {
        var pseudoPath = Path.Combine(modelsDir, $"ggml-{sample.PseudoReferenceModel}.bin");
        await EnsureModelAsync(pseudoPath, CancellationToken.None);
        reference = await TranscribePseudoReferenceAsync(pseudoPath, sample.Samples, sample.AudioSeconds, threads);
        Console.WriteLine($"  WER reference: pseudo-reference from {sample.PseudoReferenceModel} full-file decode.");
    }
    else if (reference is not null)
    {
        Console.WriteLine($"  WER reference: canonical transcript ({reference.Split(' ').Length} words).");
    }
    else
    {
        Console.WriteLine("  WER reference: none (WER will be n/a).");
    }

    foreach (var modelPath in candidates)
    {
        var result = await BenchmarkModelAsync(modelPath, sample, reference, threads, windowSeconds, intervalSeconds, stabilityWindow, feed);
        allResults.Add(result);
    }

    Console.WriteLine();
}

PrintSummary(allResults);
if (csvPath is not null)
{
    WriteCsv(csvPath, allResults);
}

return 0;

static List<Sample> BuildCustomSamples(List<string> wavPaths, string? referenceText)
{
    return wavPaths.Select(path =>
    {
        var name = Path.GetFileName(path);
        string? reference = referenceText ?? ReadReferenceFile(path);
        var audio = ReadAudioToMono16k(path);
        return new Sample(name, audio, audio.Length / (double)SampleRate, reference, null);
    }).ToList();
}

static async Task<List<Sample>> BuildDefaultSamplesAsync(CancellationToken ct)
{
    var samples = new List<Sample>();

    var jfkPath = EnsureWav(Path.Combine(DefaultSamplesDir, "jfk.wav"), JfkUrl);
    var jfk = ReadAudioToMono16k(jfkPath);
    samples.Add(new Sample("jfk.wav", jfk, jfk.Length / (double)SampleRate, JfkReference, null));

    var noisyPath = Path.Combine(DefaultSamplesDir, "jfk_noisy.wav");
    if (!File.Exists(noisyPath))
    {
        Directory.CreateDirectory(DefaultSamplesDir);
        WriteWav16(noisyPath, AddNoise(jfk, snrDb: 10));
        Console.WriteLine($"Generated {noisyPath} (jfk + 10 dB SNR white noise).");
    }

    var noisy = ReadAudioToMono16k(noisyPath);
    samples.Add(new Sample("jfk_noisy.wav", noisy, noisy.Length / (double)SampleRate, JfkReference, null));

    var longPath = Path.Combine(DefaultSamplesDir, "jfk_long.wav");
    if (!File.Exists(longPath))
    {
        var silence = new float[(int)(0.5 * SampleRate)];
        WriteWav16(longPath, Concat(jfk, silence, jfk));
        Console.WriteLine($"Generated {longPath} (jfk x2 with a pause, {2 * jfk.Length / (double)SampleRate:0.00}s).");
    }

    var longAudio = ReadAudioToMono16k(longPath);
    samples.Add(new Sample("jfk_long.wav", longAudio, longAudio.Length / (double)SampleRate, $"{JfkReference} {JfkReference}", null));

    var osrPath = EnsureWav(Path.Combine(DefaultSamplesDir, "OSR_us_000_0010_8k.wav"), OsrUrl);
    var osr = ReadAudioToMono16k(osrPath);
    samples.Add(new Sample("OSR_us_000_0010_8k.wav", osr, osr.Length / (double)SampleRate, null, "small"));

    await Task.CompletedTask;
    return samples;
}

static string? ReadReferenceFile(string wavPath)
{
    var candidate = Path.ChangeExtension(wavPath, ".reference.txt");
    return File.Exists(candidate) ? File.ReadAllText(candidate).Trim() : null;
}

static async Task<string> TranscribePseudoReferenceAsync(
    string modelPath, float[] samples, double audioSeconds, int threads)
{
    using var factory = WhisperFactory.FromPath(modelPath);
    using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(threads).Build();
    var sb = new StringBuilder();
    await foreach (var segment in processor.ProcessAsync(samples, CancellationToken.None))
    {
        sb.Append(segment.Text);
    }

    return sb.ToString();
}

static async Task<ModelResult> BenchmarkModelAsync(
    string modelPath, Sample sample, string? reference, int threads,
    double windowSeconds, double intervalSeconds, int stabilityWindow, FeedMode feed)
{
    string name = Path.GetFileName(modelPath);
    var samples = sample.Samples;
    double audioSeconds = sample.AudioSeconds;
    Console.WriteLine($"  === {name} (window {windowSeconds:0.#}s, interval {intervalSeconds:0.#}s, stability {stabilityWindow}, feed {feed}) ===");

    long ramBefore = Process.GetCurrentProcess().WorkingSet64;
    var swLoad = Stopwatch.StartNew();
    using var factory = WhisperFactory.FromPath(modelPath);
    using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(threads).Build();
    swLoad.Stop();
    long ramAfter = Process.GetCurrentProcess().WorkingSet64;
    long ramDelta = Math.Max(0, ramAfter - ramBefore);

    var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
    var swDecode = Stopwatch.StartNew();
    var sb = new StringBuilder();
    int segmentCount = 0;
    await foreach (var seg in processor.ProcessAsync(samples, CancellationToken.None))
    {
        segmentCount++;
        sb.Append(seg.Text);
    }

    swDecode.Stop();
    var cpuDecode = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
    string transcript = sb.ToString();
    double decodeFactor = swDecode.Elapsed.TotalSeconds / audioSeconds;
    double wer = reference is null ? double.NaN : ComputeWer(transcript, reference);

    Console.WriteLine($"    model load:      {swLoad.ElapsedMilliseconds,6} ms");
    Console.WriteLine($"    working set +:   {ramDelta / (1024.0 * 1024.0),6:0.0} MB");
    Console.WriteLine($"    full decode:     {swDecode.ElapsedMilliseconds,6} ms ({decodeFactor:0.00}x realtime, {cpuDecode.TotalSeconds:0.00}s cpu)");
    Console.WriteLine($"    segments:        {segmentCount,4}");
    Console.WriteLine($"    WER:             {(double.IsNaN(wer) ? "n/a" : $"{wer * 100:0.0}%")}");
    Console.WriteLine($"    transcript:      {Truncate(transcript, 100)}");

    var swStream = Stopwatch.StartNew();
    var cpuStreamBefore = Process.GetCurrentProcess().TotalProcessorTime;
    TimeSpan firstPartial = TimeSpan.MaxValue;
    TimeSpan firstFinal = TimeSpan.MaxValue;
    int partials = 0;
    int finals = 0;
    double partialLatencySumMs = 0;
    double finalLatencySumMs = 0;
    string? streamError = null;
    var streamedFinalText = new StringBuilder();
    var options = new WhisperEngineOptions
    {
        ModelPath = modelPath,
        Language = "en",
        Threads = threads,
        SampleRate = SampleRate,
        WindowDuration = TimeSpan.FromSeconds(windowSeconds),
        DecodeInterval = TimeSpan.FromSeconds(intervalSeconds),
        CommitOverlap = TimeSpan.FromSeconds(1.5),
        MinimumAudioBeforeFirstDecode = TimeSpan.FromSeconds(2),
        StabilityWindow = stabilityWindow,
    };
    var engine = new WhisperSpeechToTextEngine(options);
    engine.PartialTranscriptAvailable += (_, t) =>
    {
        partials++;
        partialLatencySumMs += t.Latency.TotalMilliseconds;
        if (firstPartial == TimeSpan.MaxValue)
        {
            firstPartial = swStream.Elapsed;
        }
    };
    engine.FinalTranscriptAvailable += (_, t) =>
    {
        finals++;
        finalLatencySumMs += t.Latency.TotalMilliseconds;
        streamedFinalText.Append(t.Text);
        if (firstFinal == TimeSpan.MaxValue)
        {
            firstFinal = swStream.Elapsed;
        }
    };
    engine.RecognitionFailed += (_, e) => streamError ??= $"{e.Kind}: {e.Message}";

    engine.Start();
    var baseTime = DateTime.UtcNow;
    const double chunkSeconds = 0.5;
    int chunkFrames = (int)(SampleRate * chunkSeconds);
    long seq = 0;
    for (int offset = 0; offset < samples.Length; offset += chunkFrames)
    {
        int count = Math.Min(chunkFrames, samples.Length - offset);
        var chunk = new float[count];
        Array.Copy(samples, offset, chunk, 0, count);
        engine.Process(new AudioChunk(chunk, new AudioFormat(SampleRate, 1, 32), baseTime.AddSeconds(offset / (double)SampleRate), ++seq));
        if (feed == FeedMode.Realtime)
        {
            Thread.Sleep((int)(chunkSeconds * 1000));
        }
    }

    engine.Stop();
    await engine.DisposeAsync();
    swStream.Stop();
    var cpuStream = Process.GetCurrentProcess().TotalProcessorTime - cpuStreamBefore;
    double streamFactor = swStream.Elapsed.TotalSeconds / audioSeconds;
    string streamedTranscript = streamedFinalText.ToString();
    double streamWer = reference is null ? double.NaN : ComputeWer(streamedTranscript, reference);

    string firstPartialText = firstPartial == TimeSpan.MaxValue ? "n/a" : $"{firstPartial.TotalSeconds:0.000}s";
    string firstFinalText = firstFinal == TimeSpan.MaxValue ? "n/a" : $"{firstFinal.TotalSeconds:0.000}s";
    string avgPartialLatText = partials == 0 ? "n/a" : $"{partialLatencySumMs / partials:0}ms";
    string avgFinalLatText = finals == 0 ? "n/a" : $"{finalLatencySumMs / finals:0}ms";

    Console.WriteLine($"    stream:          {swStream.Elapsed.TotalSeconds,6:0.00}s wall ({streamFactor:0.00}x realtime, {cpuStream.TotalSeconds:0.00}s cpu); {partials} partials, {finals} finals");
    Console.WriteLine($"    first partial:   {firstPartialText,10}  avg lat {avgPartialLatText}");
    Console.WriteLine($"    first final:     {firstFinalText,10}  avg lat {avgFinalLatText}");
    Console.WriteLine($"    stream WER:      {(double.IsNaN(streamWer) ? "n/a" : $"{streamWer * 100:0.0}%")}");
    if (streamError is not null)
    {
        Console.WriteLine($"    stream error:    {streamError}");
    }

    return new ModelResult(
        name,
        new FileInfo(modelPath).Length,
        ramDelta,
        swLoad.Elapsed,
        swDecode.Elapsed,
        decodeFactor,
        firstPartial,
        firstFinal,
        partials,
        finals,
        swStream.Elapsed,
        streamFactor,
        cpuDecode,
        cpuStream,
        partials == 0 ? 0 : partialLatencySumMs / partials,
        finals == 0 ? 0 : finalLatencySumMs / finals,
        wer,
        transcript,
        windowSeconds,
        intervalSeconds,
        stabilityWindow,
        feed.ToString(),
        streamWer,
        streamedTranscript);
}

static void PrintSummary(List<ModelResult> results)
{
    Console.WriteLine("================================ SUMMARY ================================");
    foreach (var r in results)
    {
        Console.WriteLine(
            $"{r.Name,-10} cfg {r.WindowSeconds:0.#}/{r.IntervalSeconds:0.#}s/st{r.StabilityWindow}  " +
            $"WER {(double.IsNaN(r.Wer) ? "n/a " : $"{r.Wer * 100,4:0.0}%")}  strWER {(double.IsNaN(r.StreamWer) ? "n/a  " : $"{r.StreamWer * 100,4:0.0}%")}  " +
            $"dec {r.DecodeFactor,5:0.00}x  strm {r.StreamFactor,5:0.00}x  " +
            $"1stPart {FormatLatency(r.FirstPartial),7}  1stFin {FormatLatency(r.FirstFinal),7}  part {r.PartialCount,3} fin {r.FinalCount,3}  " +
            $"cpuDec {r.DecodeCpu.TotalSeconds,5:0.0}s cpuStrm {r.StreamCpu.TotalSeconds,5:0.0}s");
    }

    Console.WriteLine("========================================================================");
}

static void WriteCsv(string path, List<ModelResult> results)
{
    var sb = new StringBuilder();
    sb.AppendLine("model,window_s,interval_s,stability,feed,size_bytes,ram_mb,model_load_ms,decode_ms,decode_factor,first_partial_s,first_final_s,partials,finals,stream_wall_s,stream_factor,decode_cpu_s,stream_cpu_s,avg_partial_lat_ms,avg_final_lat_ms,wer,stream_wer,transcript,streamed_transcript");
    foreach (var r in results)
    {
        sb.AppendLine(string.Join(',',
            Csv(r.Name),
            Csv(r.WindowSeconds.ToString("0.#", CultureInfo.InvariantCulture)),
            Csv(r.IntervalSeconds.ToString("0.#", CultureInfo.InvariantCulture)),
            Csv(r.StabilityWindow.ToString(CultureInfo.InvariantCulture)),
            Csv(r.FeedMode),
            Csv(r.SizeBytes.ToString(CultureInfo.InvariantCulture)),
            Csv((r.RamBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)),
            Csv(r.ModelLoad.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)),
            Csv(r.Decode.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)),
            Csv(r.DecodeFactor.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(FormatLatencyCsv(r.FirstPartial)),
            Csv(FormatLatencyCsv(r.FirstFinal)),
            Csv(r.PartialCount.ToString(CultureInfo.InvariantCulture)),
            Csv(r.FinalCount.ToString(CultureInfo.InvariantCulture)),
            Csv(r.StreamWall.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(r.StreamFactor.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(r.DecodeCpu.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(r.StreamCpu.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)),
            Csv(r.AvgPartialLatencyMs.ToString("0", CultureInfo.InvariantCulture)),
            Csv(r.AvgFinalLatencyMs.ToString("0", CultureInfo.InvariantCulture)),
            Csv(FormatWerCsv(r.Wer)),
            Csv(FormatWerCsv(r.StreamWer)),
            Csv(r.Transcript),
            Csv(r.StreamedTranscript)));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"CSV written to {Path.GetFullPath(path)}");
}

static string Csv(string value)
{
    if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    return value;
}

static string FormatLatencyCsv(TimeSpan latency) => latency == TimeSpan.MaxValue ? string.Empty : latency.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);

static string FormatWerCsv(double wer) => double.IsNaN(wer) ? string.Empty : wer.ToString("0.0000", CultureInfo.InvariantCulture);

static string FormatLatency(TimeSpan latency) => latency == TimeSpan.MaxValue ? "n/a" : $"{latency.TotalSeconds:0.000}s";

static string Truncate(string text, int max)
{
    if (text.Length <= max)
    {
        return text;
    }

    return text[..(max - 3)] + "...";
}

static double ComputeWer(string transcript, string referenceText)
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

static string[] Tokenize(string text) => text.ToLowerInvariant().Split(
    new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '—' },
    StringSplitOptions.RemoveEmptyEntries);

static string EnsureWav(string path, string url)
{
    if (File.Exists(path))
    {
        return path;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    Console.WriteLine($"Downloading {Path.GetFileName(path)} from {url} ...");
    using var http = new HttpClient();
    using var resp = http.GetAsync(url).GetAwaiter().GetResult();
    resp.EnsureSuccessStatusCode();
    using var src = resp.Content.ReadAsStream();
    using var dst = File.Create(path);
    src.CopyTo(dst);
    Console.WriteLine($"Downloaded {Path.GetFileName(path)} ({new FileInfo(path).Length:N0} bytes).");
    return path;
}

static async Task EnsureModelAsync(string path, CancellationToken ct)
{
    if (File.Exists(path))
    {
        return;
    }

    string name = Path.GetFileName(path);
    string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{name}";
    Console.WriteLine($"Downloading {name} from {url} ...");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    using var http = new HttpClient();
    using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
    resp.EnsureSuccessStatusCode();
    await using var src = await resp.Content.ReadAsStreamAsync(ct);
    await using var dst = File.Create(path);
    await src.CopyToAsync(dst, ct);
    Console.WriteLine($"Downloaded {name} ({new FileInfo(path).Length:N0} bytes).");
}

/// <summary>Reads a 16-bit PCM WAV (mono or stereo, 8/16 kHz) into 16 kHz mono float samples.</summary>
static float[] ReadAudioToMono16k(string path)
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

static float[] UpsampleLinear(float[] input, int factor)
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

static void WriteWav16(string path, float[] samples)
{
    using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
    writer.WriteSamples(samples, 0, samples.Length);
}

static float[] AddNoise(float[] samples, double snrDb)
{
    double speechRms = Math.Sqrt(samples.Average(s => (double)s * s));
    double noiseAmp = speechRms / Math.Pow(10, snrDb / 20.0);
    var rng = new Random(42);
    var noisy = new float[samples.Length];
    for (int i = 0; i < samples.Length; i++)
    {
        float noise = (float)((rng.NextDouble() * 2 - 1) * noiseAmp);
        noisy[i] = Math.Clamp(samples[i] + noise, -1f, 1f);
    }

    return noisy;
}

static float[] Concat(params float[][] arrays)
{
    int total = arrays.Sum(a => a.Length);
    var result = new float[total];
    int offset = 0;
    foreach (var array in arrays)
    {
        Array.Copy(array, 0, result, offset, array.Length);
        offset += array.Length;
    }

    return result;
}

internal sealed record Sample(string Name, float[] Samples, double AudioSeconds, string? Reference, string? PseudoReferenceModel);

/// <summary>How audio is fed to the streamed engine: realtime paces chunks with wall-clock sleeps, fast feeds as quickly as the engine can consume.</summary>
internal enum FeedMode
{
    Realtime,
    Fast,
}

internal sealed record ModelResult(
    string Name,
    long SizeBytes,
    long RamBytes,
    TimeSpan ModelLoad,
    TimeSpan Decode,
    double DecodeFactor,
    TimeSpan FirstPartial,
    TimeSpan FirstFinal,
    int PartialCount,
    int FinalCount,
    TimeSpan StreamWall,
    double StreamFactor,
    TimeSpan DecodeCpu,
    TimeSpan StreamCpu,
    double AvgPartialLatencyMs,
    double AvgFinalLatencyMs,
    double Wer,
    string Transcript,
    double WindowSeconds,
    double IntervalSeconds,
    int StabilityWindow,
    string FeedMode,
    double StreamWer,
    string StreamedTranscript);
