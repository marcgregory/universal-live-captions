using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.Speech.Gemini.Tests.Spikes;

/// <summary>
/// One-shot real-wire spike runner. NOT an xUnit test, NOT part of the 463/463 regression suite.
/// Drives <see cref="GeminiLiveTranslateEngine"/> against the live Gemini WebSocket using
/// <see cref="ClientWebSocketGeminiChannel"/> as the transport. Records the actual PASS/FAIL
/// evidence for the A1–A6 spike gate.
/// </summary>
/// <remarks>
/// <para>
/// Invocation: <c>dotnet run --project tests/UniversalCaptions.Speech.Gemini.Tests -- --corpus &lt;dir&gt; [--output &lt;dir&gt;]</c>.
/// The test host must NOT be used to run this; pass <c>--no-build</c> after a separate
/// <c>dotnet build</c> if you want, but the simplest invocation is the one above.
/// </para>
/// <para>
/// Reads the API key from <c>UC_GEMINI_API_KEY</c>. Does NOT log it, include it in any
/// exception message, or emit it in any output artifact. Performs a post-run substring check
/// to confirm no leakage.
/// </para>
/// <para>
/// Output: <c>spike-result.json</c> + a console summary table. The JSON is the authoritative
/// evidence; the table is for quick human inspection.
/// </para>
/// </remarks>
public static class GeminiDirectWireSpike
{
    private const string ApiKeyEnvVar = "UC_GEMINI_API_KEY";
    private const int ChunkMilliseconds = 100;
    private const int PostAudioSilenceMs = 2000;
    private const int DefaultMaxAudioSeconds = 10;
    private const string DefaultCorpusDir = "artifacts/spike-corpus";
    private const string DefaultOutputDir = "artifacts/spike-result";

    public static async Task<int> RunAsync(string[] args)
    {
        string corpusDir = ParseArg(args, "--corpus") ?? DefaultCorpusDir;
        string outputDir = ParseArg(args, "--output") ?? DefaultOutputDir;
        int maxAudioSeconds = int.TryParse(ParseArg(args, "--max-duration"), out int maxDur) ? maxDur : DefaultMaxAudioSeconds;

        Console.WriteLine("=== Gemini Direct-Wire Spike ===");
        Console.WriteLine($"corpus: {corpusDir}");
        Console.WriteLine($"output: {outputDir}");

        string? apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvVar);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine($"FATAL: {ApiKeyEnvVar} is not set.");
            return 64;
        }

        string keyFingerprint = apiKey.Length >= 8 ? apiKey[..8] : apiKey;
        Directory.CreateDirectory(outputDir);

        var report = new SpikeReport
        {
            StartedAtUtc = DateTime.UtcNow,
            KeyFingerprint = keyFingerprint,
            TargetLanguage = "tl",
        };

        try
        {
            var wavFiles = EnumerateWavFiles(corpusDir);
            if (wavFiles.Count == 0)
            {
                Console.Error.WriteLine($"FATAL: no .wav files found in {corpusDir}.");
                return 65;
            }

            Console.WriteLine($"found {wavFiles.Count} wav file(s)");

            var options = new GeminiLiveTranslateEngineOptions
            {
                ApiKey = apiKey,
                Model = GeminiLiveTranslateEngineOptions.DefaultModel,
                TargetLanguage = report.TargetLanguage,
                SourceLanguage = "en",
                SystemInstruction = "Output short caption-style translations only. Translate spoken English into natural Tagalog.",
                Endpoint = GeminiLiveTranslateEngineOptions.DefaultEndpoint,
            };
            Console.WriteLine($"endpoint: {options.Endpoint}");
            Console.WriteLine($"resolved target language code: {options.ResolveTargetLanguageCode()}");

            // SPIKE-ONLY: print the setup frame so we can verify what we sent to Gemini.
            string setupPreview = GeminiLiveTranslateProtocol.BuildSetupFrame(
                options.Model, options.ResolveTargetLanguageCode());
            Console.WriteLine($"setup-frame: {setupPreview}");

            int utteranceIndex = 0;
            foreach (string wavPath in wavFiles)
            {
                utteranceIndex++;
                UtteranceResult result = await RunOneAsync(options, wavPath, utteranceIndex, maxAudioSeconds);
                report.Utterances.Add(result);
                PrintUtteranceSummary(utteranceIndex, result);
            }

            report.FinishedAtUtc = DateTime.UtcNow;
            report.Classify();

            string jsonPath = Path.Combine(outputDir, "spike-result.json");
            await WriteJsonAsync(jsonPath, report);
            PrintSummaryTable(report, jsonPath);

            bool leakage = CheckForApiKeyLeakage(report, apiKey, keyFingerprint);
            report.ApiKeyLeakageDetected = leakage;
            await WriteJsonAsync(jsonPath, report);

            return leakage ? 70 : 0;
        }
        catch (Exception ex)
        {
            report.Errors.Add($"FATAL: {ex.GetType().Name}: {ex.Message}");
            report.FinishedAtUtc = DateTime.UtcNow;
            string jsonPath = Path.Combine(outputDir, "spike-result.json");
            try
            {
                await WriteJsonAsync(jsonPath, report);
            }
            catch
            {
                // best-effort
            }

            Console.Error.WriteLine("FATAL during spike run:");
            Console.Error.WriteLine(ex);
            return 66;
        }
    }

    private static async Task<UtteranceResult> RunOneAsync(
        GeminiLiveTranslateEngineOptions options,
        string wavPath,
        int index,
        int maxAudioSeconds)
    {
        var result = new UtteranceResult
        {
            Index = index,
            File = Path.GetFileName(wavPath),
        };

        try
        {
            WavData wav = WavLoader.Load(wavPath);
            result.SampleRate = wav.SampleRate;
            result.Channels = wav.Channels;
            result.DurationMs = (long)(wav.Samples.Length * 1000.0 / wav.SampleRate);

            float[] mono16k = WavLoader.ToMono16kFloat(wav);
            // SPIKE-ONLY: cap audio to first N seconds so a long file doesn't make the spike
            // look stuck. The full audio is still useful for production runs.
            int maxSamples = maxAudioSeconds * 16000;
            if (mono16k.Length > maxSamples)
            {
                Array.Resize(ref mono16k, maxSamples);
            }
            result.ResampledSamples = mono16k.Length;

            var channel = new ClientWebSocketGeminiChannel();
            await using var engine = new GeminiLiveTranslateEngine(options, channel);
            // SPIKE-ONLY: capture inner WebSocket state via reflection for diagnostic purposes.
            // A5/A6 production code is untouched; this is runner-side introspection only.
            System.Reflection.FieldInfo? socketField = typeof(ClientWebSocketGeminiChannel)
                .GetField("_socket", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Action<string> probeState = (string label) =>
            {
                if (socketField?.GetValue(channel) is System.Net.WebSockets.ClientWebSocket sock)
                {
                    Console.WriteLine($"  [diag] {label}: state={sock.State} closeStatus={sock.CloseStatus} closeDesc={sock.CloseStatusDescription}");
                }
            };
            probeState("before-start");

            var partials = new List<PartialTranslation>();
            var finals = new List<FinalTranslation>();
            var errors = new List<string>();
            bool setupCompleteObserved = false;

            engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
            engine.FinalTranslationAvailable += (_, f) => finals.Add(f);
            engine.TranslationFailed += (_, e) =>
            {
                // Capture full detail (kind + message + inner exception chain) so the spike
                // evidence is diagnostic-grade, not just the friendly user message.
                var sb = new System.Text.StringBuilder();
                sb.Append(e.Kind).Append(": ").Append(e.Message);
                Exception? inner = e.Exception;
                int depth = 0;
                while (inner != null && depth < 5)
                {
                    sb.Append(" | inner[").Append(depth).Append("]: ")
                      .Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                    inner = inner.InnerException;
                    depth++;
                }

                errors.Add(sb.ToString());
            };

            var stopwatch = Stopwatch.StartNew();
            DateTime startedAtUtc = DateTime.UtcNow;

            await engine.StartAsync();
            result.AuthOk = errors.Count == 0;
            result.OpenElapsedMs = stopwatch.ElapsedMilliseconds;
            probeState("after-start");

            // SPIKE-ONLY: race-condition diagnostic. Poll WebSocket state every 100 ms for
            // 2 seconds after StartAsync to find when the receive loop first fails.
            for (int probeIdx = 0; probeIdx < 20; probeIdx++)
            {
                await Task.Delay(100);
                probeState($"probe-{probeIdx}-{(probeIdx + 1) * 100}ms");
                if (errors.Count > 0)
                {
                    break;
                }
            }

            // Wait briefly for setupComplete by polling. The receive loop runs in the background;
            // we don't have a direct hook for setupComplete, so the absence of immediate failure
            // combined with the first successful audio round-trip is the implicit PASS signal.
            await Task.Delay(150);
            probeState("after-150ms");

            // Push audio in ~100 ms chunks.
            int chunkSamples = options_ChunkSamples(mono16k.Length);
            long sequence = 0;
            int totalChunks = (mono16k.Length + chunkSamples - 1) / chunkSamples;
            for (int offset = 0; offset < mono16k.Length; offset += chunkSamples)
            {
                int n = Math.Min(chunkSamples, mono16k.Length - offset);
                float[] chunkSamples_ = new float[n];
                Array.Copy(mono16k, offset, chunkSamples_, 0, n);
                var format = new AudioFormat(SampleRate: 16000, Channels: 1, BitsPerSample: 32);
                engine.PushAudio(new AudioChunk(chunkSamples_, format, DateTime.UtcNow, sequence++));
                await Task.Delay(ChunkMilliseconds);

                // SPIKE-ONLY: progress log every 10 chunks so a long audio file doesn't look stuck.
                if (sequence % 10 == 0)
                {
                    Console.WriteLine($"  [diag] pushed chunk {sequence}/{totalChunks} errors={errors.Count} partials={partials.Count} finals={finals.Count}");
                }
            }

            // Drain partials + wait for turnComplete / silence window.
            await Task.Delay(PostAudioSilenceMs);

            await engine.StopAsync();

            // No direct setupComplete event surfaced; treat absence of TranslationFailed during the
            // first 150 ms after StartAsync as the implicit signal. Capture if no error happened.
            setupCompleteObserved = errors.Count == 0 && partials.Count + finals.Count > 0;

            result.PartialCount = partials.Count;
            result.FinalCount = finals.Count;
            result.SetupCompleteObserved = setupCompleteObserved;
            result.Errors.AddRange(errors);

            if (partials.Count > 0)
            {
                PartialTranslation firstPartial = partials[0];
                result.FirstOutputTranscriptionMs = (long)(firstPartial.EmittedAtUtc - startedAtUtc).TotalMilliseconds;
            }

            if (finals.Count > 0)
            {
                FinalTranslation lastFinal = finals[^1];
                result.FinalOutputTranscriptionMs = (long)(lastFinal.CommittedAtUtc - startedAtUtc).TotalMilliseconds;
                result.FinalText = lastFinal.TranslatedText;
            }
            else if (partials.Count > 0)
            {
                result.FinalText = partials[^1].TranslatedText;
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    private static int options_ChunkSamples(int total)
    {
        // 16 kHz × 100 ms = 1600 samples.
        return 16000 * ChunkMilliseconds / 1000;
    }

    private static List<string> EnumerateWavFiles(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        return Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static async Task WriteJsonAsync(string path, SpikeReport report)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        string json = JsonSerializer.Serialize(report, options);
        await File.WriteAllTextAsync(path, json);
    }

    private static bool CheckForApiKeyLeakage(SpikeReport report, string apiKey, string fingerprint)
    {
        // Scan every user-visible string we emitted. The fingerprint is intentionally allowed
        // (it's already redacted from the full key); the full key is the leak.
        foreach (UtteranceResult u in report.Utterances)
        {
            if (Contains(u.File))
            {
                return true;
            }

            if (Contains(u.FinalText))
            {
                return true;
            }

            foreach (string err in u.Errors)
            {
                if (Contains(err))
                {
                    return true;
                }
            }
        }

        foreach (string err in report.Errors)
        {
            if (Contains(err))
            {
                return true;
            }
        }

        return false;

        bool Contains(string? s) =>
            !string.IsNullOrEmpty(s)
            && (s.Contains(apiKey, StringComparison.Ordinal)
                || (apiKey.Length >= 12 && s.Contains(apiKey[..12], StringComparison.Ordinal)));
    }

    private static void PrintUtteranceSummary(int index, UtteranceResult u)
    {
        Console.WriteLine(
            $"  [{index,2}] {u.File,-20} first={u.FirstOutputTranscriptionMs}ms final={u.FinalOutputTranscriptionMs}ms "
            + $"partials={u.PartialCount} finals={u.FinalCount} errs={u.Errors.Count}");
        if (!string.IsNullOrEmpty(u.FinalText))
        {
            string preview = u.FinalText.Length > 80 ? u.FinalText[..80] + "…" : u.FinalText;
            Console.WriteLine($"       final: {preview}");
        }

        foreach (string err in u.Errors)
        {
            Console.WriteLine($"       err:   {err}");
        }
    }

    private static void PrintSummaryTable(SpikeReport report, string jsonPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== Spike summary ===");
        Console.WriteLine($"  authentication  : {(report.AuthOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"  setupComplete   : {(report.SetupCompleteObserved ? "PASS" : "FAIL")}");
        Console.WriteLine($"  outputTranscription : {(report.OutputTranscriptionObserved ? "PASS" : "FAIL")}");
        Console.WriteLine($"  turnComplete    : {(report.TurnCompleteObserved ? "PASS" : "FAIL")}");
        Console.WriteLine($"  usable utterances : {report.UsableUtteranceCount}/{report.Utterances.Count}");
        Console.WriteLine($"  api-key leakage : {(report.ApiKeyLeakageDetected ? "LEAK DETECTED" : "none")}");
        Console.WriteLine($"  result JSON     : {jsonPath}");
    }
}

/// <summary>
/// The spike run's serialized evidence. Field names are JSON-stable; do not rename without
/// updating downstream tooling that reads <c>spike-result.json</c>.
/// </summary>
public sealed class SpikeReport
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public string KeyFingerprint { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "tl";
    public List<UtteranceResult> Utterances { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public bool AuthOk { get; set; }
    public bool SetupCompleteObserved { get; set; }
    public bool OutputTranscriptionObserved { get; set; }
    public bool TurnCompleteObserved { get; set; }
    public int UsableUtteranceCount { get; set; }
    public bool ApiKeyLeakageDetected { get; set; }

    public void Classify()
    {
        AuthOk = Utterances.Count > 0 && Utterances.All(u => string.IsNullOrEmpty(u.FinalText) == false || u.PartialCount > 0);
        SetupCompleteObserved = Utterances.Any(u => u.SetupCompleteObserved);
        OutputTranscriptionObserved = Utterances.Any(u => u.PartialCount > 0 || u.FinalCount > 0);
        TurnCompleteObserved = Utterances.Any(u => u.FinalCount > 0);
        UsableUtteranceCount = Utterances.Count(u => !string.IsNullOrWhiteSpace(u.FinalText));
    }
}

public sealed class UtteranceResult
{
    public int Index { get; set; }
    public string File { get; set; } = string.Empty;
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public long DurationMs { get; set; }
    public int ResampledSamples { get; set; }

    public bool AuthOk { get; set; }
    public long OpenElapsedMs { get; set; }
    public bool SetupCompleteObserved { get; set; }
    public int PartialCount { get; set; }
    public int FinalCount { get; set; }
    public long? FirstOutputTranscriptionMs { get; set; }
    public long? FinalOutputTranscriptionMs { get; set; }
    public string? FinalText { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Minimal WAV reader/decoder for the spike. Supports PCM16 LE (canonical) and PCM32 LE float.
/// Resamples to 16 kHz mono float32 for the engine.
/// </summary>
internal static class WavLoader
{
    public static WavData Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        // RIFF header
        var riff = new byte[4];
        if (br.Read(riff, 0, 4) != 4
            || riff[0] != (byte)'R' || riff[1] != (byte)'I' || riff[2] != (byte)'F' || riff[3] != (byte)'F')
        {
            throw new InvalidDataException("Not a RIFF/WAV file.");
        }

        br.ReadInt32(); // chunk size
        var wave = new byte[4];
        if (br.Read(wave, 0, 4) != 4
            || wave[0] != (byte)'W' || wave[1] != (byte)'A' || wave[2] != (byte)'V' || wave[3] != (byte)'E')
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int sampleRate = 0;
        int channels = 0;
        int bitsPerSample = 0;
        byte[]? data = null;

        while (true)
        {
            var chunkId = new byte[4];
            int read = br.Read(chunkId, 0, 4);
            if (read < 4)
            {
                break;
            }

            int chunkSize = br.ReadInt32();

            if (chunkId[0] == (byte)'f' && chunkId[1] == (byte)'m' && chunkId[2] == (byte)'t' && chunkId[3] == (byte)' ')
            {
                short audioFormat = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bitsPerSample = br.ReadInt16();
                int remaining = chunkSize - 16;
                if (remaining > 0)
                {
                    br.ReadBytes(remaining);
                }

                if (audioFormat != 1 && audioFormat != 3)
                {
                    throw new InvalidDataException($"Unsupported WAV audio format: {audioFormat} (only PCM=1 and FLOAT=3 are supported).");
                }
            }
            else if (chunkId[0] == (byte)'d' && chunkId[1] == (byte)'a' && chunkId[2] == (byte)'t' && chunkId[3] == (byte)'a')
            {
                data = br.ReadBytes(chunkSize);
            }
            else
            {
                br.ReadBytes(chunkSize);
            }

            if (chunkSize % 2 == 1)
            {
                br.ReadByte(); // pad byte
            }

            if (data != null && sampleRate > 0)
            {
                break;
            }
        }

        if (data == null)
        {
            throw new InvalidDataException("WAV file contains no data chunk.");
        }

        float[] samples = DecodeSamples(data, channels, bitsPerSample);
        return new WavData(sampleRate, channels, bitsPerSample, samples);
    }

    private static float[] DecodeSamples(byte[] data, int channels, int bitsPerSample)
    {
        if (bitsPerSample == 16)
        {
            int count = data.Length / sizeof(short);
            var result = new float[count];
            for (int i = 0; i < count; i++)
            {
                short s = (short)(data[2 * i] | (data[(2 * i) + 1] << 8));
                result[i] = s / (float)short.MaxValue;
            }

            return result;
        }

        if (bitsPerSample == 32)
        {
            int count = data.Length / sizeof(float);
            var result = new float[count];
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
            return result;
        }

        throw new InvalidDataException($"Unsupported bitsPerSample: {bitsPerSample} (only 16 and 32 are supported).");
    }

    /// <summary>
    /// Converts loaded WAV samples to 16 kHz mono float32 (the engine's input format). Naive
    /// nearest-neighbor resample is acceptable for the spike; the production audio path uses a
    /// proper resampler.
    /// </summary>
    public static float[] ToMono16kFloat(WavData wav)
    {
        // Step 1: downmix to mono by averaging channels.
        float[] mono;
        if (wav.Channels == 1)
        {
            mono = wav.Samples;
        }
        else
        {
            int frameCount = wav.Samples.Length / wav.Channels;
            mono = new float[frameCount];
            for (int f = 0; f < frameCount; f++)
            {
                float sum = 0f;
                for (int c = 0; c < wav.Channels; c++)
                {
                    sum += wav.Samples[(f * wav.Channels) + c];
                }

                mono[f] = sum / wav.Channels;
            }
        }

        // Step 2: resample to 16 kHz (nearest-neighbor).
        if (wav.SampleRate == 16000)
        {
            return mono;
        }

        double ratio = 16000.0 / wav.SampleRate;
        int newLen = (int)(mono.Length * ratio);
        var resampled = new float[newLen];
        for (int i = 0; i < newLen; i++)
        {
            int src = (int)(i / ratio);
            if (src >= mono.Length)
            {
                src = mono.Length - 1;
            }

            resampled[i] = mono[src];
        }

        return resampled;
    }
}

public sealed record WavData(int SampleRate, int Channels, int BitsPerSample, float[] Samples);
