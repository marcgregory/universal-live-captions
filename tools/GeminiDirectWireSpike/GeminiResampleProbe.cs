using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.Speech.Gemini.Tests.Spikes;

/// <summary>
/// OPTION-2 SINGLE-VARIABLE PROBE (additive — no frozen A1–A6 or frozen spike file modified).
/// Isolates exactly ONE unverified wire variable: the <em>audio resampling</em> that converts the
/// source WAV to the 16 kHz PCM16 the Gemini live-translate wire expects.
/// <list type="bullet">
///   <item>Variant "nearest" reproduces the CURRENT spike path byte-for-byte
/// (<see cref="WavLoader.ToMono16kFloat"/> = nearest-neighbor) → the identical integer-scale
/// PCM16 conversion.</item>
///   <item>Variant "linear" reproduces the OLD benchmark path semantics
/// (<c>LiveTranslationBenchmark.UpsampleLinear</c>-style linear interpolation to 16 kHz) → the
/// same PCM16 conversion.</item>
/// </list>
/// Everything else is pinned identical across the two sessions: same frozen
/// <see cref="ClientWebSocketGeminiChannel"/> + <see cref="ProvenanceObservingChannel"/>, same
/// frozen <see cref="GeminiLiveTranslateProtocol.BuildSetupFrame"/> (no inputAudioTranscription),
/// same model/target, same realtime 100 ms / 3200 B pacing, same drain window, same
/// endpoint/key. The raw <c>outputAudioTranscription</c> streams are captured side-by-side so any
/// output difference is attributable to the resample alone.
/// </summary>
public static class GeminiResampleProbe
{
    private const string ApiKeyEnvVar = "UC_GEMINI_API_KEY";
    private const string ApiKeyCredentialTarget = "UniversalCaptions:GeminiApiKey";
    private const int ChunkMilliseconds = 100;
    private const int PostAudioSilenceMs = 4000;
    private const string DefaultOutputDir = "artifacts/spike-compare";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(
        string target,
        int type,
        int reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr credentialPtr);

    private static string? ReadApiKeyFromCredentialManager()
    {
        try
        {
            if (!CredRead(ApiKeyCredentialTarget, 1, 0, out IntPtr credentialPtr))
            {
                return null;
            }
            try
            {
                CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    return string.Empty;
                }
                byte[] blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                return Encoding.UTF8.GetString(blob);
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ResolveApiKey() =>
        ReadApiKeyFromCredentialManager() ?? Environment.GetEnvironmentVariable(ApiKeyEnvVar);

    public static async Task<int> RunAsync(string[] args)
    {
        string wavPath = ParseArg(args, "--wav") ?? string.Empty;
        string outputDir = ParseArg(args, "--output") ?? DefaultOutputDir;
        int maxAudioSeconds = int.TryParse(ParseArg(args, "--max-duration"), out int maxDur) && maxDur > 0
            ? maxDur
            : 120;

        if (string.IsNullOrWhiteSpace(wavPath) || !File.Exists(wavPath))
        {
            Console.Error.WriteLine("FATAL: --probe requires --wav <path-to-wav>.");
            return 64;
        }

        string? apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine($"FATAL: Gemini API key not found (Credential Manager \"{ApiKeyCredentialTarget}\" or {ApiKeyEnvVar}).");
            return 64;
        }

        string keyFingerprint = apiKey.Length >= 8 ? apiKey[..8] : apiKey;
        Directory.CreateDirectory(outputDir);

        Console.WriteLine("=== Gemini Resample Probe (Option 2: single-variable resample compare) ===");
        Console.WriteLine($"wav   : {wavPath}");
        Console.WriteLine($"output: {outputDir}");
        Console.WriteLine($"max-duration: {maxAudioSeconds}s");

        var report = new ProbeReport
        {
            StartedAtUtc = DateTime.UtcNow,
            KeyFingerprint = keyFingerprint,
            TargetLanguage = "tl",
            Wav = Path.GetFileName(wavPath),
            MaxAudioSeconds = maxAudioSeconds,
        };

        try
        {
            WavData wav = WavLoader.Load(wavPath);
            report.SampleRate = wav.SampleRate;
            report.Channels = wav.Channels;
            report.SourceDurationMs = (long)(wav.Samples.Length * 1000.0 / wav.SampleRate);
            Console.WriteLine($"source: {wav.SampleRate} Hz, {wav.Channels} ch, {report.SourceDurationMs} ms");

            float[] nearest = WavLoader.ToMono16kFloat(wav);
            CapSeconds(ref nearest, maxAudioSeconds);
            byte[] nearestPcm = FloatToPcm16Le(nearest);

            float[] linear = ToMono16kLinear(wav);
            CapSeconds(ref linear, maxAudioSeconds);
            byte[] linearPcm = FloatToPcm16Le(linear);

            Console.WriteLine($"nearest samples: {nearest.Length} ({nearest.Length / 16000.0:0.00} s)");
            Console.WriteLine($"linear  samples: {linear.Length} ({linear.Length / 16000.0:0.00} s)");
            Console.WriteLine($"probe: inputs differ only in resample algorithm; chunk/pacing/setup frozen");
            bool identicalBuffers = IsByteEqual(nearestPcm, linearPcm);
            Console.WriteLine($"resampled PCM16 byte-identical (true only for 16k source): {identicalBuffers}");

            var options = new GeminiLiveTranslateEngineOptions
            {
                ApiKey = apiKey,
                Model = GeminiLiveTranslateEngineOptions.DefaultModel,
                TargetLanguage = report.TargetLanguage,
                SourceLanguage = "en",
                SystemInstruction = "Output short caption-style translations only. Translate spoken English into natural Tagalog.",
                Endpoint = GeminiLiveTranslateEngineOptions.DefaultEndpoint,
            };

            string setupJson = GeminiLiveTranslateProtocol.BuildSetupFrame(
                options.Model, options.ResolveTargetLanguageCode());
            report.SetupJson = setupJson;

            Console.WriteLine($"setup-frame: {setupJson}");

            report.Nearest = await RunVariantAsync(options, setupJson, nearestPcm, "nearest", maxAudioSeconds);
            Console.WriteLine();
            report.Linear = await RunVariantAsync(options, setupJson, linearPcm, "linear", maxAudioSeconds);

            report.Classify();
            Summarize(report);

            string outPath = Path.Combine(outputDir, "resample-probe.json");
            JsonSerializerOptions jsonOptions = new()
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(report, jsonOptions));
            Console.WriteLine();
            Console.WriteLine($"report: {Path.GetFullPath(outPath)}");

            return report.Errors.Count == 0 ? 0 : 70;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 71;
        }
    }

    private static void CapSeconds(ref float[] mono, int maxSeconds)
    {
        long maxSamples = (long)maxSeconds * 16000;
        if (mono.Length > maxSamples)
        {
            Array.Resize(ref mono, (int)maxSamples);
        }
    }

    private static float[] ToMono16kLinear(WavData wav)
    {
        // Downmix to mono identically to WavLoader.ToMono16kFloat.
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

        if (wav.SampleRate == 16000)
        {
            return mono;
        }

        double ratio = 16000.0 / wav.SampleRate;
        int newLen = Math.Max(0, (int)(mono.Length * ratio));
        var resampled = new float[newLen];
        for (int i = 0; i < newLen; i++)
        {
            double srcPos = i / ratio;
            int lo = (int)Math.Floor(srcPos);
            int hi = lo + 1;
            double frac = srcPos - lo;
            float vLo = mono[Math.Min(lo, mono.Length - 1)];
            float vHi = mono[Math.Min(hi, mono.Length - 1)];
            resampled[i] = (float)(vLo + ((vHi - vLo) * frac));
        }

        return resampled;
    }

    private static byte[] FloatToPcm16Le(float[] mono16k)
    {
        var shorts = new short[mono16k.Length];
        for (int i = 0; i < mono16k.Length; i++)
        {
            float clamped = Math.Clamp(mono16k[i], -1f, 1f);
            shorts[i] = (short)(clamped * short.MaxValue);
        }

        var bytes = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static int ChunkBytesFor(int sampleRate, int chunkMs) => (sampleRate * 2 * chunkMs) / 1000;

    private static bool IsByteEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<ProbeVariantResult> RunVariantAsync(
        GeminiLiveTranslateEngineOptions options,
        string setupJson,
        byte[] pcm16,
        string label,
        int maxAudioSeconds)
    {
        var result = new ProbeVariantResult { VariantLabel = label };

        try
        {
            Uri uri = options.BuildEndpoint();
            var channel = new ClientWebSocketGeminiChannel();
            var provenanceChannel = new ProvenanceObservingChannel(channel);
            await using var _ = provenanceChannel;

            var outputs = new List<(string Text, bool IsPartial, long Ms)>();
            var errors = new List<string>();
            bool setupCompleteObserved = false;
            bool turnCompleteObserved = false;
            string? lastOutputText = null;
            var stopwatch = Stopwatch.StartNew();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds((maxAudioSeconds + 20) * 2));
            var receiveTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    string? frame;
                    try
                    {
                        frame = await provenanceChannel.ReceiveTextAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{ex.GetType().Name}: {ex.Message}");
                        return;
                    }

                    if (frame is null)
                    {
                        continue;
                    }

                    if (!GeminiLiveTranslateProtocol.TryParseServerFrame(frame, out GeminiServerMessage? message, out string? parseError))
                    {
                        errors.Add($"Unparsed server frame: {parseError}");
                        continue;
                    }

                    switch (message)
                    {
                        case GeminiServerMessage.SetupComplete:
                            setupCompleteObserved = true;
                            break;

                        case GeminiServerMessage.ServerContent content:
                            if (!string.IsNullOrEmpty(content.Text))
                            {
                                long ms = stopwatch.ElapsedMilliseconds;
                                outputs.Add((content.Text, content.IsPartial, ms));
                                lastOutputText = content.Text;
                            }

                            if (content.TurnComplete)
                            {
                                turnCompleteObserved = true;
                            }

                            break;

                        case GeminiServerMessage.GoAway:
                            return;

                        case GeminiServerMessage.SessionResumptionUpdate:
                            break;

                        case GeminiServerMessage.ErrorFrame error:
                            errors.Add($"server error: {error.Status} ({error.Code}): {error.Message}");
                            return;
                    }
                }
            });

            try
            {
                await channel.OpenAsync(uri, cts.Token).ConfigureAwait(false);
                await channel.SendTextAsync(setupJson, cts.Token).ConfigureAwait(false);

                int chunkBytes = ChunkBytesFor(16000, ChunkMilliseconds);
                for (int offset = 0; offset < pcm16.Length; offset += chunkBytes)
                {
                    int n = Math.Min(chunkBytes, pcm16.Length - offset);
                    byte[] chunk = new byte[n];
                    Array.Copy(pcm16, offset, chunk, 0, n);
                    string frame = GeminiLiveTranslateProtocol.BuildRealtimeAudioFrame(chunk);
                    await channel.SendTextAsync(frame, cts.Token).ConfigureAwait(false);
                    result.AudioFramesSent++;
                    result.AudioBytesSent += n;

                    if (offset + chunkBytes < pcm16.Length)
                    {
                        await Task.Delay(ChunkMilliseconds, cts.Token).ConfigureAwait(false);
                    }
                }

                // Drain the tail: wait for the final translation + turnComplete.
                await Task.Delay(PostAudioSilenceMs, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                errors.Add("Session timed out waiting for audio send/drain.");
            }
            catch (Exception ex)
            {
                errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try
                {
                    await channel.CloseAsync("probe variant complete", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort close
                }

                cts.Cancel();
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch
                {
                    // best-effort drain
                }
            }

            result.SetupCompleteObserved = setupCompleteObserved;
            result.TurnCompleteObserved = turnCompleteObserved;
            foreach ((string Text, bool IsPartial, long Ms) output in outputs)
            {
                result.Outputs.Add(new ProbeOutputRecord { Text = output.Text, IsPartial = output.IsPartial, Ms = output.Ms });
            }

            if (outputs.Count > 0)
            {
                result.FirstOutputMs = outputs[0].Ms;
                result.FirstOutputText = outputs[0].Text;
                result.FinalText = lastOutputText;
                result.FinalOutputMs = outputs[^1].Ms;
            }
            else
            {
                result.FinalText = lastOutputText;
            }

            result.Provenance = provenanceChannel.Provenance.ToSnapshot(result.FinalText);
            foreach (string err in errors)
            {
                result.Errors.Add(err);
            }

            Console.WriteLine($"--- variant {label} ---");
            Console.WriteLine($"  setupComplete: {result.SetupCompleteObserved}  turnComplete: {result.TurnCompleteObserved}");
            Console.WriteLine($"  frames sent   : {result.AudioFramesSent} bytes: {result.AudioBytesSent}");
            Console.WriteLine($"  first output  : {result.FirstOutputMs} ms: {(result.FirstOutputText ?? "(none)")}");
            foreach (ProbeOutputRecord output in result.Outputs)
            {
                Console.WriteLine($"    [{output.Ms,6}ms] {output.Text}");
            }
            foreach (string err in result.Errors)
            {
                Console.WriteLine($"    ERROR: {err}");
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{ex.GetType().Name}: {ex.Message}");
            return result;
        }
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

    private static void Summarize(ProbeReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=== Compare ===");
        string nearestFinal = report.Nearest?.FinalText ?? "(none)";
        string linearFinal = report.Linear?.FinalText ?? "(none)";
        Console.WriteLine($"nearest FINAL : {nearestFinal}");
        Console.WriteLine($"linear  FINAL : {linearFinal}");
        Console.WriteLine($"identical     : {string.Equals(nearestFinal, linearFinal, StringComparison.Ordinal)}");
        Console.WriteLine($"first ms      : nearest={report.Nearest?.FirstOutputMs} vs linear={report.Linear?.FirstOutputMs}");

        bool sameOutputCount = (report.Nearest?.Outputs.Count ?? 0) == (report.Linear?.Outputs.Count ?? 0);
        Console.WriteLine($"output frames : nearest={report.Nearest?.Outputs.Count ?? 0} vs linear={report.Linear?.Outputs.Count ?? 0} (equal={sameOutputCount})");
    }
}

public sealed record ProbeOutputRecord
{
    public string? Text { get; set; }
    public bool IsPartial { get; set; }
    public long Ms { get; set; }
}

public sealed class ProbeVariantResult
{
    public string? VariantLabel { get; set; }
    public string? FinalText { get; set; }
    public long? FirstOutputMs { get; set; }
    public string? FirstOutputText { get; set; }
    public long? FinalOutputMs { get; set; }
    public int AudioFramesSent { get; set; }
    public int AudioBytesSent { get; set; }
    public bool SetupCompleteObserved { get; set; }
    public bool TurnCompleteObserved { get; set; }
    public UtteranceProvenance? Provenance { get; set; }
    public List<ProbeOutputRecord> Outputs { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class ProbeReport
{
    public DateTime StartedAtUtc { get; set; }
    public string? KeyFingerprint { get; set; }
    public string? TargetLanguage { get; set; }
    public string? Wav { get; set; }
    public int MaxAudioSeconds { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public long SourceDurationMs { get; set; }
    public string? SetupJson { get; set; }
    public ProbeVariantResult? Nearest { get; set; }
    public ProbeVariantResult? Linear { get; set; }
    public bool FinalTextIdentical { get; set; }
    public bool OutputCountIdentical { get; set; }
    public List<string> Errors { get; set; } = new();

    public void Classify()
    {
        FinalTextIdentical = string.Equals(
            Nearest?.FinalText, Linear?.FinalText, StringComparison.Ordinal);
        OutputCountIdentical = (Nearest?.Outputs.Count ?? 0) == (Linear?.Outputs.Count ?? 0);
        foreach (ProbeVariantResult? variant in new[] { Nearest, Linear })
        {
            if (variant is null)
            {
                continue;
            }
            foreach (string err in variant.Errors)
            {
                Errors.Add($"{variant.VariantLabel} | {err}");
            }
        }
    }
}