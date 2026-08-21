using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.Speech.Gemini.Tests.Spikes;

/// <summary>
/// One-shot real-wire spike runner. NOT an xUnit test, NOT part of the regression suite.
/// Drives <see cref="GeminiLiveTranslateEngine"/> against the live Gemini WebSocket using
/// <see cref="ClientWebSocketGeminiChannel"/> as the transport. Records the actual PASS/FAIL
/// evidence for the A1–A6 spike gate.
/// </summary>
/// <remarks>
/// <para>
/// Provenance: this runner is a pure Gemini-direct-wire chain — English WAV → Gemini WebSocket →
/// Gemini serverContent → Gemini generated audio → Gemini outputAudioTranscription → FinalText.
/// There is NO Whisper, NO Argos, and NO text-leg in this runner: the caption text the engine
/// publishes is Gemini's own side-channel transcript. A <see cref="ProvenanceObservingChannel"/>
/// decorator (spike/diagnostics layer only — the frozen production channel/protocol/engine are
/// untouched) watches every raw server frame and proves the text travelled with generated audio
/// (<c>serverContent.modelTurn.parts[].inlineData</c>) via <c>outputTranscription.text</c>.
/// Every utterance records <c>GeminiAudioFrames</c>/<c>GeminiAudioBytes</c>/<c>GeminiOutputTranscription</c>
/// with <c>ArgosCalls = 0</c> and <c>ArgosOutput = NONE</c>; the run exits non-zero (71) when
/// provenance is not verified.
/// </para>
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
    private const string ApiKeyCredentialTarget = "UniversalCaptions:GeminiApiKey";
    private const int ChunkMilliseconds = 100;
    private const int PostAudioSilenceMs = 2000;
    private const int DefaultMaxAudioSeconds = 10;
    private const string DefaultCorpusDir = "artifacts/spike-corpus";
    private const string DefaultOutputDir = "artifacts/spike-result";

    // ------------------------------------------------------------------------------------------
    // API-key resolution (spike layer only; the frozen production A1–A6 code never changes).
    // The provisioning rule (ADR-0009 / task #24) is that the Gemini API key lives ONLY in
    // Windows Credential Manager under the agreed target "UniversalCaptions:GeminiApiKey".
    // The spike prefers that store and falls back to UC_GEMINI_API_KEY only so a fresh key can
    // still be exercised without writing a credential; the key is never logged, echoed, or
    // written to any artifact. This mirrors the production WindowsCredentialStore P/Invoke.
    // ------------------------------------------------------------------------------------------

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

    private static string? ResolveApiKey()
    {
        // Credential Manager is the authoritative store (ADR-0009). The env var is a strict
        // fallback so the spike can still run with a throwaway key during development; it is never
        // the production source and is never written anywhere.
        return ReadApiKeyFromCredentialManager() ?? Environment.GetEnvironmentVariable(ApiKeyEnvVar);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        // --ab runs the controlled A/B regression experiment: on an identical corpus, variant A =
        // the frozen production setup frame (no inputAudioTranscription) and variant B = the same
        // frame plus the top-level inputAudioTranscription field the OLD working benchmark client
        // sent (src/UniversalCaptions.Benchmarks/Translation/GeminiLiveTranslateClient.cs). The
        // raw outputTranscription streams are compared directly so an untested wire variable
        // (Round 3 only proved the NESTED generationConfig path is rejected, not the top-level
        // path) can be isolated without touching any frozen A1–A6 code.
        if (args.Contains("--ab", StringComparer.Ordinal))
        {
            return await RunAbAsync(args, DefaultCorpusDir, DefaultOutputDir);
        }

        string corpusDir = ParseArg(args, "--corpus") ?? DefaultCorpusDir;
        string outputDir = ParseArg(args, "--output") ?? DefaultOutputDir;
        int maxAudioSeconds = int.TryParse(ParseArg(args, "--max-duration"), out int maxDur) ? maxDur : DefaultMaxAudioSeconds;

        Console.WriteLine("=== Gemini Direct-Wire Spike ===");
        Console.WriteLine($"corpus: {corpusDir}");
        Console.WriteLine($"output: {outputDir}");

        string? apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine($"FATAL: Gemini API key not found. Expected it in Windows Credential Manager under \"{ApiKeyCredentialTarget}\" (primary) or {ApiKeyEnvVar} (fallback).");

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

            // Exit 71 = the run completed but provenance was NOT proven: some utterance produced
            // no Gemini audio parts or no outputAudioTranscription text (or ArgosCalls != 0, which
            // cannot happen in this runner). Exit 70 = API-key leak. Otherwise 0.
            if (!leakage && !report.ProvenanceVerified)
            {
                Console.WriteLine();
                Console.WriteLine("PROVENANCE GATE FAILED: GeminiAudioFrames=0 or GeminiOutputTranscription missing on one or more utterances.");
                Console.WriteLine("The caption text was NOT proven to originate from Gemini's generated-audio outputAudioTranscription side-channel.");
            }

            return leakage ? 70 : report.ProvenanceVerified ? 0 : 71;
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

    // ------------------------------------------------------------------------------------------
    // A/B regression harness (spike layer only; frozen A1–A6 code untouched)
    // ------------------------------------------------------------------------------------------

    private static async Task<int> RunAbAsync(string[] args, string defaultCorpusDir, string defaultOutputDir)
    {
        string corpusDir = ParseArg(args, "--corpus") ?? defaultCorpusDir;
        string outputDir = ParseArg(args, "--output") ?? defaultOutputDir;
        int maxAudioSeconds = int.TryParse(ParseArg(args, "--max-duration"), out int maxDur) ? maxDur : DefaultMaxAudioSeconds;

        Console.WriteLine("=== Gemini Direct-Wire Spike: A/B Regression (inputAudioTranscription) ===");
        Console.WriteLine($"corpus: {corpusDir}");
        Console.WriteLine($"output: {outputDir}");

        string? apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine($"FATAL: Gemini API key not found. Expected it in Windows Credential Manager under \"{ApiKeyCredentialTarget}\" (primary) or {ApiKeyEnvVar} (fallback).");
            return 64;
        }

        string keyFingerprint = apiKey.Length >= 8 ? apiKey[..8] : apiKey;
        Directory.CreateDirectory(outputDir);

        var report = new AbReport
        {
            StartedAtUtc = DateTime.UtcNow,
            KeyFingerprint = keyFingerprint,
            Model = GeminiLiveTranslateEngineOptions.DefaultModel,
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
            Console.WriteLine($"endpoint: {GeminiLiveTranslateEngineOptions.DefaultEndpoint}");
            Console.WriteLine($"model: {report.Model}");
            Console.WriteLine($"resolved target language code: fil");

            int index = 0;
            foreach (string wavPath in wavFiles)
            {
                index++;
                AbUtterance ab = await RunAbOneAsync(apiKey, report.Model, wavPath, index, maxAudioSeconds);
                report.Utterances.Add(ab);
                PrintAbUtteranceSummary(index, ab);
            }

            report.FinishedAtUtc = DateTime.UtcNow;
            report.Classify();

            string jsonPath = Path.Combine(outputDir, "ab-result.json");
            await WriteJsonAsync(jsonPath, report);
            PrintAbSummary(report, jsonPath);

            bool leakage = CheckForApiKeyLeakage(report, apiKey, keyFingerprint);
            report.ApiKeyLeakageDetected = leakage;
            await WriteJsonAsync(jsonPath, report);

            if (leakage)
            {
                return 70;
            }

            if (!report.BothVariantsProven)
            {
                Console.WriteLine();
                Console.WriteLine("AB PROVENANCE GATE FAILED: variant A and/or variant B did not produce Gemini audio + outputTranscription.");
                return 71;
            }

            return 0;
        }
        catch (Exception ex)
        {
            report.Errors.Add($"FATAL: {ex.GetType().Name}: {ex.Message}");
            report.FinishedAtUtc = DateTime.UtcNow;
            string jsonPath = Path.Combine(outputDir, "ab-result.json");
            try
            {
                await WriteJsonAsync(jsonPath, report);
            }
            catch
            {
                // best-effort
            }

            Console.Error.WriteLine("FATAL during A/B spike run:");
            Console.Error.WriteLine(ex);
            return 66;
        }
    }

    private static async Task<AbUtterance> RunAbOneAsync(
        string apiKey,
        string model,
        string wavPath,
        int index,
        int maxAudioSeconds)
    {
        var ab = new AbUtterance { Index = index, File = Path.GetFileName(wavPath) };
        try
        {
            WavData wav = WavLoader.Load(wavPath);
            ab.SampleRate = wav.SampleRate;
            ab.DurationMs = (long)(wav.Samples.Length * 1000.0 / CanonicalAudioBoundary.CanonicalSampleRate);

            // ADR-0010: WavData.Samples is already canonical mono float32 @ 16 kHz.
            float[] mono16k = wav.Samples;
            int maxSamples = maxAudioSeconds * 16000;
            if (mono16k.Length > maxSamples)
            {
                Array.Resize(ref mono16k, maxSamples);
            }

            // Convert to the exact wire bytes the OLD benchmark client sent: PCM16 LE mono 16 kHz,
            // 100 ms chunks = 3200 bytes, real-time pacing. Both variants get the identical buffer.
            byte[] pcm16 = FloatToPcm16Le(mono16k);
            ab.AudioBytes = pcm16.Length;
            ab.ChunkBytes = ChunkBytesFor(16000, ChunkMilliseconds);

            string setupA = GeminiLiveTranslateProtocol.BuildSetupFrame(model, "fil");
            string setupB = BuildSetupWithInputAudioTranscription(setupA);

            ab.VariantA = await RunAbVariantAsync(apiKey, model, setupA, includeInputTranscription: false, pcm16, index, maxAudioSeconds);
            ab.VariantB = await RunAbVariantAsync(apiKey, model, setupB, includeInputTranscription: true, pcm16, index, maxAudioSeconds);

            ab.ClassifyCompare();
        }
        catch (Exception ex)
        {
            ab.Errors.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return ab;
    }

    private static async Task<AbVariantResult> RunAbVariantAsync(
        string apiKey,
        string model,
        string setupJson,
        bool includeInputTranscription,
        byte[] pcm16,
        int index,
        int maxAudioSeconds)
    {
        var result = new AbVariantResult
        {
            VariantLabel = includeInputTranscription ? "B" : "A",
            IncludeInputTranscription = includeInputTranscription,
            SetupJson = setupJson,
        };

        try
        {
            var options = new GeminiLiveTranslateEngineOptions
            {
                ApiKey = apiKey,
                Model = model,
                TargetLanguage = "tl",
            };
            Uri uri = options.BuildEndpoint();

            var channel = new ClientWebSocketGeminiChannel();
            var provenanceChannel = new ProvenanceObservingChannel(channel);
            await using var _ = provenanceChannel; // also disposes inner channel via decorator

            var outputs = new List<(string Text, bool IsPartial, long Ms)>();
            var inputOutputs = new List<(string Text, bool IsPartial, long Ms)>();
            var errors = new List<string>();
            bool setupCompleteObserved = false;
            bool turnCompleteObserved = false;
            string? lastOutputText = null;
            DateTime startedAtUtc = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds((maxAudioSeconds + 10) * 2));
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

                            if (!string.IsNullOrEmpty(content.InputText))
                            {
                                inputOutputs.Add((content.InputText, content.InputIsPartial, stopwatch.ElapsedMilliseconds));
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
                    await channel.CloseAsync("ab variant complete", CancellationToken.None).ConfigureAwait(false);
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
                result.Outputs.Add(new AbOutputRecord
                {
                    Text = output.Text,
                    IsPartial = output.IsPartial,
                    Ms = output.Ms,
                });
            }

            foreach ((string Text, bool IsPartial, long Ms) input in inputOutputs)
            {
                result.InputTranscriptions.Add(new AbOutputRecord
                {
                    Text = input.Text,
                    IsPartial = input.IsPartial,
                    Ms = input.Ms,
                });
            }

            foreach (string err in errors)
            {
                result.Errors.Add(err);
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
            return result;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"{ex.GetType().Name}: {ex.Message}");
            return result;
        }
    }

    /// <summary>
    /// Builds the variant-B setup frame: the frozen production frame (no inputAudioTranscription) plus
    /// the top-level inputAudioTranscription field the OLD working benchmark client sent. The frozen
    /// frame's own fields are re-emitted verbatim through <see cref="JsonElement.WriteTo"/> — this is
    /// a spike-local probe, not a protocol-layer change.
    /// </summary>
    internal static string BuildSetupWithInputAudioTranscription(string baseSetupJson)
    {
        using var doc = JsonDocument.Parse(baseSetupJson);
        JsonElement setup = doc.RootElement.GetProperty("setup");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("setup");
            foreach (JsonProperty property in setup.EnumerateObject())
            {
                property.WriteTo(writer);
                if (property.Name == "outputAudioTranscription")
                {
                    writer.WriteStartObject("inputAudioTranscription");
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
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
            result.DurationMs = (long)(wav.Samples.Length * 1000.0 / CanonicalAudioBoundary.CanonicalSampleRate);

            // ADR-0010: WavData.Samples is already canonical mono float32 @ 16 kHz.
            float[] mono16k = wav.Samples;
            // SPIKE-ONLY: cap audio to first N seconds so a long file doesn't make the spike
            // look stuck. The full audio is still useful for production runs.
            int maxSamples = maxAudioSeconds * 16000;
            if (mono16k.Length > maxSamples)
            {
                Array.Resize(ref mono16k, maxSamples);
            }
            result.ResampledSamples = mono16k.Length;

            // SPIKE-ONLY provenance harness: wrap the frozen production channel in a diagnostic
            // decorator that observes every raw server frame (structure only — no payload bytes,
            // no audio contents) so the evidence proves the final caption text originates from
            // Gemini's own outputAudioTranscription side-channel carried on the same serverContent
            // frames that contain the generated audio parts. Production channel/protocol/engine
            // code is untouched.
            var channel = new ClientWebSocketGeminiChannel();
            var provenanceChannel = new ProvenanceObservingChannel(channel);
            await using var engine = new GeminiLiveTranslateEngine(options, provenanceChannel);
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

            // SPIKE-ONLY provenance snapshot from the raw wire. ArgosCalls stays 0 / ArgosOutput
            // stays NONE for the whole spike — there is no Argos process or text-leg anywhere in
            // this chain. This proves the caption text came from Gemini's generated audio + its
            // own outputAudioTranscription side-channel, not from Whisper→Argos.
            result.Provenance = provenanceChannel.Provenance.ToSnapshot(result.FinalText);
            if (result.Provenance.UnknownFrames > 0)
            {
                Console.WriteLine($"  [diag] {Path.GetFileName(wavPath)}: {result.Provenance.UnknownFrames} unknown server frame(s): {string.Join("; ", result.Provenance.UnknownFingerprints)}");
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

    /// <summary>
    /// Float32 mono → 16-bit signed little-endian PCM, mirroring the frozen engine's
    /// <c>FloatToPcm16Le</c> exactly (clamp + scale to short.MaxValue, LE bytes) so both A/B
    /// variants send byte-identical audio to the wire with the same conditions the OLD benchmark
    /// client used (PCM16 LE mono 16 kHz, 3200-byte chunks, 100 ms pacing).
    /// ADR-0010: delegated to the canonical boundary's deterministic projection.
    /// </summary>
    internal static byte[] FloatToPcm16Le(float[] mono16k) => CanonicalAudioBoundary.ToPcm16Le(mono16k);

    /// <summary>PCM16 chunk size in bytes for a given sample rate and chunk duration (100 ms at 16 kHz = 3200 bytes).</summary>
    internal static int ChunkBytesFor(int sampleRate, int chunkMs)
    {
        return Math.Max(1, sampleRate * 2 * chunkMs / 1000);
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

    private static async Task WriteJsonAsync<T>(string path, T report)
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

            if (u.Provenance is not null)
            {
                if (Contains(u.Provenance.GeminiOutputTranscription))
                {
                    return true;
                }

                foreach (string fp in u.Provenance.UnknownFingerprints)
                {
                    if (Contains(fp))
                    {
                        return true;
                    }
                }
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

    private static bool CheckForApiKeyLeakage(AbReport report, string apiKey, string fingerprint)
    {
        foreach (AbUtterance u in report.Utterances)
        {
            if (Contains(u.File))
            {
                return true;
            }

            AbVariantResult?[] variants = [u.VariantA, u.VariantB];
            foreach (AbVariantResult? v in variants)
            {
                if (v is null)
                {
                    continue;
                }

                foreach (AbOutputRecord output in v.Outputs)
                {
                    if (Contains(output.Text))
                    {
                        return true;
                    }
                }

                if (Contains(v.FirstOutputText))
                {
                    return true;
                }

                if (Contains(v.FinalText))
                {
                    return true;
                }

                if (v.Provenance is not null)
                {
                    if (Contains(v.Provenance.GeminiOutputTranscription))
                    {
                        return true;
                    }

                    foreach (string fp in v.Provenance.UnknownFingerprints)
                    {
                        if (Contains(fp))
                        {
                            return true;
                        }
                    }
                }

                foreach (string err in v.Errors)
                {
                    if (Contains(err))
                    {
                        return true;
                    }
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

        if (u.Provenance is not null)
        {
            string sideChannel = string.IsNullOrWhiteSpace(u.Provenance.GeminiOutputTranscription)
                ? "(none)"
                : u.Provenance.GeminiOutputTranscription.Length > 80
                    ? u.Provenance.GeminiOutputTranscription[..80] + "…"
                    : u.Provenance.GeminiOutputTranscription;
            Console.WriteLine($"       GeminiAudioFrames = {u.Provenance.GeminiAudioFrames}");
            Console.WriteLine($"       GeminiAudioBytes = {u.Provenance.GeminiAudioBytes}");
            Console.WriteLine($"       GeminiOutputTranscription = \"{sideChannel}\"");
            Console.WriteLine($"       ArgosCalls = {u.Provenance.ArgosCalls}");
            Console.WriteLine($"       ArgosOutput = {u.Provenance.ArgosOutput}");
            Console.WriteLine($"       provenance: {(u.Provenance.ProvenanceVerified ? "PASS" : "FAIL")} "
                + $"serverContent={u.Provenance.ServerContentFrames} "
                + $"framesWithAudio={u.Provenance.ServerContentFramesWithAudio} "
                + $"geminiAudioFrames={u.Provenance.GeminiAudioFrames} geminiAudioBytes={u.Provenance.GeminiAudioBytes} "
                + $"mimeTypes=[{string.Join(", ", u.Provenance.AudioMimeTypes)}] "
                + $"outputTranscriptionFrames={u.Provenance.OutputTranscriptionFrames} "
                + $"modelTurnTextParts={u.Provenance.ModelTurnTextParts} "
                + $"turnCompleteFrames={u.Provenance.TurnCompleteFrames} "
                + $"setupCompleteFrames={u.Provenance.SetupCompleteFrames} "
                + $"goAwayFrames={u.Provenance.GoAwayFrames} "
                + $"sessionResumptionFrames={u.Provenance.SessionResumptionFrames} "
                + $"errorFrames={u.Provenance.ErrorFrames} "
                + $"unknownFrames={u.Provenance.UnknownFrames} "
                + $"malformedFrames={u.Provenance.MalformedFrames}");
            if (u.Provenance.UnknownFrames > 0)
            {
                Console.WriteLine($"       unknown fingerprints: {string.Join("; ", u.Provenance.UnknownFingerprints)}");
            }
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
        Console.WriteLine($"  provenance      : {(report.ProvenanceVerified ? "PASS" : "FAIL")} "
            + $"(GeminiAudioFrames={report.TotalGeminiAudioFrames} GeminiAudioBytes={report.TotalGeminiAudioBytes} "
            + $"ArgosCalls={report.ArgosCalls} ArgosOutput={report.ArgosOutput})");
        Console.WriteLine($"  final==outputTranscription : {(report.AllFinalTextsMatchOutputTranscription ? "PASS" : "FAIL")} "
            + "(every engine final matches Gemini's outputAudioTranscription side-channel)");
        Console.WriteLine($"  usable utterances : {report.UsableUtteranceCount}/{report.Utterances.Count}");
        Console.WriteLine($"  api-key leakage : {(report.ApiKeyLeakageDetected ? "LEAK DETECTED" : "none")}");
        Console.WriteLine($"  result JSON     : {jsonPath}");
    }

    private static void PrintAbUtteranceSummary(int index, AbUtterance u)
    {
        Console.WriteLine($"  [{index,2}] {u.File,-24} A-first={u.VariantA?.FirstOutputMs}ms "
            + $"B-first={u.VariantB?.FirstOutputMs}ms seq={u.OutputSequenceEquals} "
            + $"finalEqual={u.FinalTextsMatch} errsA={u.VariantA?.Errors.Count} errsB={u.VariantB?.Errors.Count}");
        if (!string.IsNullOrEmpty(u.VariantA?.FirstOutputText) && !string.IsNullOrEmpty(u.VariantB?.FirstOutputText))
        {
            string a = u.VariantA.FirstOutputText.Length > 60 ? u.VariantA.FirstOutputText[..60] + "…" : u.VariantA.FirstOutputText;
            string b = u.VariantB.FirstOutputText.Length > 60 ? u.VariantB.FirstOutputText[..60] + "…" : u.VariantB.FirstOutputText;
            Console.WriteLine($"       A-first: \"{a}\"");
            Console.WriteLine($"       B-first: \"{b}\"");
        }

        if (u.VariantB is not null)
        {
            Console.WriteLine($"       B setupComplete={u.VariantB.SetupCompleteObserved} B inputAudioTranscription field "
                + $"sent={(u.VariantB.IncludeInputTranscription ? "yes" : "no")}");
            Console.WriteLine($"       B inputTranscription frames={u.VariantB.InputTranscriptions.Count}");
            if (u.VariantB.InputTranscriptions.Count > 0)
            {
                string firstInput = u.VariantB.InputTranscriptions[0].Text ?? string.Empty;
                string preview = firstInput.Length > 60 ? firstInput[..60] + "…" : firstInput;
                Console.WriteLine($"       B inputTranscription first: \"{preview}\"");
            }

            if (u.VariantA is not null)
            {
                Console.WriteLine($"       A inputTranscription frames={u.VariantA.InputTranscriptions.Count} (expected 0 — field not sent)");
            }
        }
    }

    private static void PrintAbSummary(AbReport report, string jsonPath)
    {
        Console.WriteLine();
        Console.WriteLine("=== A/B summary ===");
        Console.WriteLine($"  variant A (frozen setup, no inputAudioTranscription) : "
            + $"{(report.Utterances.All(u => u.VariantA?.Provenance?.ProvenanceVerified == true) ? "proven" : "NOT proven")}");
        Console.WriteLine($"  variant B (+ top-level inputAudioTranscription)      : "
            + $"{(report.Utterances.All(u => u.VariantB?.Provenance?.ProvenanceVerified == true) ? "proven" : "NOT proven")}");
        Console.WriteLine($"  both variants usable -> finals compared : {report.BothVariantsProven}");
        Console.WriteLine($"  output-sequence identical (A vs B)      : {report.AllSequencesMatch}");
        Console.WriteLine($"  final text identical (A vs B)           : {report.AllFinalTextsMatch}");
        Console.WriteLine($"  inputTranscription streamed (variant B) : {report.InputTranscriptionObservedB}");
        Console.WriteLine($"  api-key leakage : {(report.ApiKeyLeakageDetected ? "LEAK DETECTED" : "none")}");
        Console.WriteLine($"  result JSON     : {jsonPath}");
    }
}

/// <summary>
/// A/B regression evidence. One entry per corpus WAV; each carries the identical audio buffer
/// through two raw wire sessions that differ ONLY in the top-level <c>inputAudioTranscription</c>
/// field. <c>VariantA</c> = frozen setup (no field); <c>VariantB</c> = the same frame plus
/// <c>inputAudioTranscription</c> at the setup top level (as the OLD benchmark client sent).
/// </summary>
public sealed class AbReport
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public string KeyFingerprint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "tl";
    public List<AbUtterance> Utterances { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    public int AudioFramesSentA { get; set; }
    public int AudioFramesSentB { get; set; }
    public long AudioBytesSentA { get; set; }
    public long AudioBytesSentB { get; set; }
    public bool BothVariantsProven { get; set; }
    public bool AllSequencesMatch { get; set; }
    public bool AllFinalTextsMatch { get; set; }

    /// <summary>Release-gate answer: did variant B receive any serverContent.inputTranscription text?</summary>
    public bool InputTranscriptionObservedB { get; set; }
    public bool ApiKeyLeakageDetected { get; set; }

    public void Classify()
    {
        AudioFramesSentA = Utterances.Sum(u => u.VariantA?.AudioFramesSent ?? 0);
        AudioFramesSentB = Utterances.Sum(u => u.VariantB?.AudioFramesSent ?? 0);
        AudioBytesSentA = Utterances.Sum(u => u.VariantA?.AudioBytesSent ?? 0);
        AudioBytesSentB = Utterances.Sum(u => u.VariantB?.AudioBytesSent ?? 0);
        BothVariantsProven = Utterances.Count > 0
                             && Utterances.All(u => u.VariantA?.Provenance?.ProvenanceVerified == true
                                                     && u.VariantB?.Provenance?.ProvenanceVerified == true);
        AllSequencesMatch = Utterances.Count > 0 && Utterances.All(u => u.FinalSequenceEquals || !u.VariantAHasOutput);
        AllFinalTextsMatch = Utterances.Count > 0 && Utterances.All(u => u.FinalTextsMatch || !u.VariantAHasOutput);
        InputTranscriptionObservedB = Utterances.Any(u => (u.VariantB?.InputTranscriptions.Count ?? 0) > 0);
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

    // Provenance evidence (spike/diagnostics layer only). ArgosCalls is pinned to 0 / ArgosOutput
    // to "NONE" for the whole run: this spike has no Argos process, no Whisper STT, and no text
    // leg — the caption text is Gemini's own generated-audio outputAudioTranscription side-channel.
    public int ArgosCalls { get; set; }
    public string ArgosOutput { get; set; } = "NONE";
    public int TotalGeminiAudioFrames { get; set; }
    public long TotalGeminiAudioBytes { get; set; }
    public bool ProvenanceVerified { get; set; }
    public bool AllFinalTextsMatchOutputTranscription { get; set; }

    public void Classify()
    {
        AuthOk = Utterances.Count > 0 && Utterances.All(u => string.IsNullOrEmpty(u.FinalText) == false || u.PartialCount > 0);
        SetupCompleteObserved = Utterances.Any(u => u.SetupCompleteObserved);
        OutputTranscriptionObserved = Utterances.Any(u => u.PartialCount > 0 || u.FinalCount > 0);
        TurnCompleteObserved = Utterances.Any(u => u.FinalCount > 0);
        UsableUtteranceCount = Utterances.Count(u => !string.IsNullOrWhiteSpace(u.FinalText));

        ArgosCalls = 0;
        ArgosOutput = "NONE";
        TotalGeminiAudioFrames = Utterances.Sum(u => u.Provenance?.GeminiAudioFrames ?? 0);
        TotalGeminiAudioBytes = Utterances.Sum(u => u.Provenance?.GeminiAudioBytes ?? 0);
        ProvenanceVerified = Utterances.Count > 0
            && Utterances.All(u => u.Provenance is not null && u.Provenance.ProvenanceVerified);
        AllFinalTextsMatchOutputTranscription = Utterances
            .Where(u => !string.IsNullOrWhiteSpace(u.FinalText))
            .All(u => u.Provenance is not null && u.Provenance.FinalTextMatchesOutputTranscription);
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
    public UtteranceProvenance? Provenance { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// SPIKE-ONLY WAV access that delegates all decoding/normalization to the ADR-0010 canonical
/// boundary (<see cref="CanonicalAudioBoundary"/>). The spike holds no resampling, down-mix, or
/// WAV-decoding logic of its own; it consumes the canonical mono float32/16 kHz representation.
/// </summary>
internal static class WavLoader
{
    /// <summary>
    /// Loads a WAV and returns its canonical representation described by <see cref="WavData"/>.
    /// </summary>
    public static WavData Load(string path)
    {
        CanonicalAudio audio = CanonicalAudioBoundary.FromWav(path);
        return new WavData(
            audio.SourceSampleRate,
            audio.SourceChannels,
            BitsPerSample: 16,
            audio.MonoSamples);
    }
}

/// <summary>
/// Canonical WAV payload for the spike: <see cref="Samples"/> is already canonical mono
/// float32 at 16 kHz (ADR-0010). SampleRate/Channels/BitsPerSample describe the SOURCE file.
/// </summary>
public sealed record WavData(int SampleRate, int Channels, int BitsPerSample, float[] Samples);

/// <summary>
/// SPIKE-ONLY diagnostic decorator over <see cref="IGeminiLiveTranslateChannel"/>. Wraps the frozen
/// production channel and observes every raw server frame as it passes through
/// <see cref="ReceiveTextAsync"/>, feeding a <see cref="ProvenanceAccumulator"/>. No frame payload
/// bytes (audio contents, API key, full transcripts) are retained or logged — only structural
/// metadata plus the <c>outputTranscription.text</c> side-channel value. Outbound frames are
/// forwarded untouched.
/// </summary>
internal sealed class ProvenanceObservingChannel : IGeminiLiveTranslateChannel
{
    private readonly IGeminiLiveTranslateChannel _inner;
    private readonly ProvenanceAccumulator _accumulator = new();

    public ProvenanceObservingChannel(IGeminiLiveTranslateChannel inner)
    {
        _inner = inner;
    }

    /// <summary>The structural provenance metadata accumulated from every observed server frame.</summary>
    public ProvenanceAccumulator Provenance => _accumulator;

    /// <inheritdoc />
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken) => _inner.OpenAsync(uri, cancellationToken);

    /// <inheritdoc />
    public Task SendTextAsync(string json, CancellationToken cancellationToken) => _inner.SendTextAsync(json, cancellationToken);

    /// <inheritdoc />
    public bool IsClosed => _inner.IsClosed;

    /// <inheritdoc />
    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        string? frame = await _inner.ReceiveTextAsync(cancellationToken).ConfigureAwait(false);
        if (frame is not null)
        {
            _accumulator.ObserveFrame(frame);
        }

        return frame;
    }

    /// <inheritdoc />
    public Task CloseAsync(string reason, CancellationToken cancellationToken) => _inner.CloseAsync(reason, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

/// <summary>
/// SPIKE-ONLY structural observer over the raw Gemini Live Translate wire. Watches every inbound
/// frame and records ONLY metadata: frame-kind counts, presence of generated audio parts
/// (<c>serverContent.modelTurn.parts[].inlineData</c>), the audio MIME type and decoded byte count
/// (never the bytes themselves), and the <c>outputTranscription.text</c> side-channel value. This
/// is the provenance evidence that the final caption text comes from Gemini's own generated-audio
/// side-channel — with Argos provably out of the chain.
/// </summary>
internal sealed class ProvenanceAccumulator
{
    public int ServerContentFrames { get; private set; }

    /// <summary>Number of <c>inlineData</c> audio parts observed across all frames (one part = one audio frame/chunk).</summary>
    public int AudioParts { get; private set; }

    /// <summary>Number of serverContent frames that carried at least one audio part.</summary>
    public int ServerContentFramesWithAudio { get; private set; }

    /// <summary>Total decoded audio byte count across all observed audio parts (base64 length math, no allocation).</summary>
    public long AudioBytes { get; private set; }

    private readonly List<string> _audioMimeTypes = new();
    public IReadOnlyList<string> AudioMimeTypes => _audioMimeTypes;

    public int OutputTranscriptionFrames { get; private set; }

    /// <summary>The last <c>outputTranscription.text</c> value observed on the wire (the side-channel transcript).</summary>
    public string? LastOutputTranscriptionText { get; private set; }

    public int ModelTurnTextParts { get; private set; }
    public int TurnCompleteFrames { get; private set; }
    public int PartialFrames { get; private set; }
    public int SetupCompleteFrames { get; private set; }
    public int GoAwayFrames { get; private set; }
    public int SessionResumptionFrames { get; private set; }
    public int ErrorFrames { get; private set; }
    public int UnknownFrames { get; private set; }
    private readonly List<string> _unknownFingerprints = new();
    public IReadOnlyList<string> UnknownFingerprints => _unknownFingerprints;
    public int MalformedFrames { get; private set; }

    /// <summary>
    /// Observes one raw server frame (a JSON document) and folds its structural metadata into the
    /// accumulated counters. Never throws on bad input — malformed / non-object frames are counted
    /// as <see cref="MalformedFrames"/> and skipped.
    /// </summary>
    public void ObserveFrame(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            MalformedFrames++;
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                MalformedFrames++;
                return;
            }

            JsonElement root = document.RootElement;
            if (root.TryGetProperty("serverContent", out JsonElement serverContent)
                && serverContent.ValueKind == JsonValueKind.Object)
            {
                ServerContentFrames++;
                ObserveServerContent(serverContent);
            }

            if (root.TryGetProperty("error", out _))
            {
                ErrorFrames++;
            }

            if (root.TryGetProperty("setupComplete", out _))
            {
                SetupCompleteFrames++;
            }

            if (root.TryGetProperty("goAway", out _))
            {
                GoAwayFrames++;
            }

            if (root.TryGetProperty("sessionResumptionUpdate", out _))
            {
                SessionResumptionFrames++;
            }

            bool recognized = root.TryGetProperty("serverContent", out _)
                || root.TryGetProperty("error", out _)
                || root.TryGetProperty("setupComplete", out _)
                || root.TryGetProperty("goAway", out _)
                || root.TryGetProperty("sessionResumptionUpdate", out _);
            if (!recognized)
            {
                UnknownFrames++;
                string fingerprint = DescribeTopLevel(root);
                if (!_unknownFingerprints.Contains(fingerprint, StringComparer.Ordinal))
                {
                    _unknownFingerprints.Add(fingerprint);
                }
            }
        }
    }

    private void ObserveServerContent(JsonElement serverContent)
    {
        if (serverContent.TryGetProperty("partial", out JsonElement partial)
            && partial.ValueKind == JsonValueKind.True)
        {
            PartialFrames++;
        }

        if (serverContent.TryGetProperty("turnComplete", out JsonElement turnComplete)
            && turnComplete.ValueKind == JsonValueKind.True)
        {
            TurnCompleteFrames++;
        }

        bool frameHasAudio = false;

        if (serverContent.TryGetProperty("modelTurn", out JsonElement modelTurn)
            && modelTurn.ValueKind == JsonValueKind.Object
            && modelTurn.TryGetProperty("parts", out JsonElement parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (part.TryGetProperty("text", out JsonElement textElement)
                    && textElement.ValueKind == JsonValueKind.String)
                {
                    ModelTurnTextParts++;
                }

                if (part.TryGetProperty("inlineData", out JsonElement inlineData)
                    && inlineData.ValueKind == JsonValueKind.Object)
                {
                    AudioParts++;
                    frameHasAudio = true;

                    if (inlineData.TryGetProperty("mimeType", out JsonElement mimeElement)
                        && mimeElement.ValueKind == JsonValueKind.String
                        && mimeElement.GetString() is string mime
                        && !_audioMimeTypes.Contains(mime, StringComparer.Ordinal))
                    {
                        _audioMimeTypes.Add(mime);
                    }

                    if (inlineData.TryGetProperty("data", out JsonElement dataElement)
                        && dataElement.ValueKind == JsonValueKind.String
                        && dataElement.GetString() is string base64)
                    {
                        AudioBytes += Base64DecodedLength(base64);
                    }
                }
            }
        }

        if (serverContent.TryGetProperty("outputTranscription", out JsonElement transcription)
            && transcription.ValueKind == JsonValueKind.Object)
        {
            OutputTranscriptionFrames++;
            if (transcription.TryGetProperty("text", out JsonElement textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                LastOutputTranscriptionText = textElement.GetString();
            }
        }

        if (frameHasAudio)
        {
            ServerContentFramesWithAudio++;
        }
    }

    /// <summary>
    /// Structural-only description of an unrecognized top-level object (property names + JSON value
    /// kinds, never payload bytes), so unknown future frame shapes are identifiable without logging
    /// their contents.
    /// </summary>
    private static string DescribeTopLevel(JsonElement root)
    {
        var properties = new List<string>(root.EnumerateObject().Count());
        foreach (JsonProperty property in root.EnumerateObject())
        {
            properties.Add($"{property.Name}:{property.Value.ValueKind}");
        }

        return properties.Count == 0 ? "(empty object)" : string.Join(", ", properties);
    }

    /// <summary>
    /// Exact decoded length of a base64 string without allocating the decoded buffer: every 4
    /// base64 chars (excluding '=' padding) encode 3 bytes, so length = len*3/4 truncated.
    /// </summary>
    private static long Base64DecodedLength(string base64)
    {
        int len = base64.Length;
        while (len > 0 && base64[len - 1] == '=')
        {
            len--;
        }

        return len == 0 ? 0 : (long)len * 3 / 4;
    }

    /// <summary>
    /// Snapshots the accumulated provenance into a serializable <see cref="UtteranceProvenance"/>.
    /// <c>ArgosCalls</c> is pinned to 0 / <c>ArgosOutput</c> to <c>"NONE"</c> because this spike has
    /// no Argos process anywhere in the chain. <see cref="UtteranceProvenance.ProvenanceVerified"/>
    /// requires generated audio parts AND a non-empty <c>outputAudioTranscription</c> side-channel;
    /// <see cref="UtteranceProvenance.FinalTextMatchesOutputTranscription"/> compares the
    /// engine-published final text against the raw side-channel text.
    /// </summary>
    public UtteranceProvenance ToSnapshot(string? finalText)
    {
        var snapshot = new UtteranceProvenance
        {
            ServerContentFrames = ServerContentFrames,
            GeminiAudioFrames = AudioParts,
            ServerContentFramesWithAudio = ServerContentFramesWithAudio,
            GeminiAudioBytes = AudioBytes,
            AudioMimeTypes = _audioMimeTypes.ToList(),
            OutputTranscriptionFrames = OutputTranscriptionFrames,
            GeminiOutputTranscription = LastOutputTranscriptionText,
            ModelTurnTextParts = ModelTurnTextParts,
            TurnCompleteFrames = TurnCompleteFrames,
            PartialFrames = PartialFrames,
            SetupCompleteFrames = SetupCompleteFrames,
            GoAwayFrames = GoAwayFrames,
            SessionResumptionFrames = SessionResumptionFrames,
            ErrorFrames = ErrorFrames,
            UnknownFrames = UnknownFrames,
            UnknownFingerprints = _unknownFingerprints.ToList(),
            MalformedFrames = MalformedFrames,
            ArgosCalls = 0,
            ArgosOutput = "NONE",
        };

        snapshot.ProvenanceVerified = snapshot.GeminiAudioFrames > 0
            && !string.IsNullOrWhiteSpace(snapshot.GeminiOutputTranscription)
            && snapshot.ArgosCalls == 0;
        snapshot.FinalTextMatchesOutputTranscription = !string.IsNullOrWhiteSpace(finalText)
            && !string.IsNullOrWhiteSpace(snapshot.GeminiOutputTranscription)
            && string.Equals(finalText.Trim(), snapshot.GeminiOutputTranscription.Trim(), StringComparison.Ordinal);
        return snapshot;
    }
}

/// <summary>
/// Serialized per-utterance provenance evidence (spike/diagnostics layer only). Field names are
/// JSON-stable; <c>GeminiAudioFrames</c> / <c>GeminiAudioBytes</c> / <c>GeminiOutputTranscription</c>
/// / <c>ArgosCalls</c> / <c>ArgosOutput</c> are the canonical names downstream tooling greps for.
/// </summary>
public sealed class UtteranceProvenance
{
    public int ServerContentFrames { get; set; }

    /// <summary>Number of <c>inlineData</c> audio parts Gemini returned (one part = one audio frame/chunk).</summary>
    public int GeminiAudioFrames { get; set; }

    public int ServerContentFramesWithAudio { get; set; }

    /// <summary>Total decoded byte count of the generated audio Gemini returned.</summary>
    public long GeminiAudioBytes { get; set; }

    public List<string> AudioMimeTypes { get; set; } = new();
    public int OutputTranscriptionFrames { get; set; }
    public string? GeminiOutputTranscription { get; set; }
    public int ModelTurnTextParts { get; set; }
    public int TurnCompleteFrames { get; set; }
    public int PartialFrames { get; set; }
    public int SetupCompleteFrames { get; set; }
    public int GoAwayFrames { get; set; }
    public int SessionResumptionFrames { get; set; }
    public int ErrorFrames { get; set; }
    public int UnknownFrames { get; set; }
    public List<string> UnknownFingerprints { get; set; } = new();
    public int MalformedFrames { get; set; }

    /// <summary>Always 0 in this spike — no Argos process is ever spawned.</summary>
    public int ArgosCalls { get; set; }

    /// <summary>Always "NONE" in this spike — no Argos output exists.</summary>
    public string ArgosOutput { get; set; } = "NONE";

    /// <summary>
    /// True only when Gemini returned generated audio parts AND a non-empty
    /// <c>outputAudioTranscription.text</c> side-channel with ArgosCalls == 0.
    /// </summary>
    public bool ProvenanceVerified { get; set; }

    /// <summary>True when the engine-published final text exactly matches Gemini's raw side-channel text.</summary>
    public bool FinalTextMatchesOutputTranscription { get; set; }
}

/// <summary>
/// One corpus WAV compared under two setup variants. The identical <paramref="name"/> audio buffer
/// is streamed twice: variant A (frozen setup, no inputAudioTranscription) and variant B (the frozen
/// setup plus the top-level inputAudioTranscription the OLD benchmark client sent).
/// </summary>
public sealed class AbUtterance
{
    public int Index { get; set; }
    public string File { get; set; } = string.Empty;
    public int SampleRate { get; set; }
    public long DurationMs { get; set; }
    public long AudioBytes { get; set; }
    public int ChunkBytes { get; set; }

    public AbVariantResult? VariantA { get; set; }
    public AbVariantResult? VariantB { get; set; }

    /// <summary>True when A and B emitted the same raw outputTranscription text sequence (order + content).</summary>
    public bool OutputSequenceEquals { get; set; }

    /// <summary>True when A and B's final output text is equal (ordinal, trimmed).</summary>
    public bool FinalTextsMatch { get; set; }

    /// <summary>True when A produced at least one output; comparison flags only then have meaning.</summary>
    public bool VariantAHasOutput { get; set; }

    public bool FinalSequenceEquals { get; set; }
    public List<string> Errors { get; set; } = new();

    public void ClassifyCompare()
    {
        VariantAHasOutput = VariantA?.Outputs.Count > 0;
        bool bothHaveOutput = VariantA?.Outputs.Count > 0 && VariantB?.Outputs.Count > 0;
        OutputSequenceEquals = bothHaveOutput
            && SequenceTextsEqual(VariantA!.Outputs, VariantB!.Outputs);
        FinalTextsMatch = bothHaveOutput
            && string.Equals(
                VariantA!.FinalText?.Trim(),
                VariantB!.FinalText?.Trim(),
                StringComparison.Ordinal);
        FinalSequenceEquals = OutputSequenceEquals || !VariantAHasOutput;
    }

    private static bool SequenceTextsEqual(List<AbOutputRecord> a, List<AbOutputRecord> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Text?.Trim(), b[i].Text?.Trim(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// One raw-wire session result for a single variant. Text values are Gemini's
/// <c>outputAudioTranscription</c> side-channel transcript (the raw output text — not modified).
/// </summary>
public sealed class AbVariantResult
{
    /// <summary>"A" = frozen setup (no inputAudioTranscription); "B" = + top-level field.</summary>
    public string VariantLabel { get; set; } = string.Empty;

    public bool IncludeInputTranscription { get; set; }
    public string SetupJson { get; set; } = string.Empty;
    public bool SetupCompleteObserved { get; set; }
    public bool TurnCompleteObserved { get; set; }
    public int AudioFramesSent { get; set; }
    public long AudioBytesSent { get; set; }
    public long? FirstOutputMs { get; set; }
    public string? FirstOutputText { get; set; }
    public long? FinalOutputMs { get; set; }
    public string? FinalText { get; set; }
    public List<AbOutputRecord> Outputs { get; set; } = new();

    /// <summary>Raw <c>serverContent.inputTranscription</c> updates observed on this variant (empty for A by design).</summary>
    public List<AbOutputRecord> InputTranscriptions { get; set; } = new();
    public UtteranceProvenance? Provenance { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>One raw <c>outputAudioTranscription</c> server update (text only; no audio bytes).</summary>
public sealed class AbOutputRecord
{
    public string? Text { get; set; }
    public bool IsPartial { get; set; }
    public long Ms { get; set; }
}
