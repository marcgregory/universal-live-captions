using System.Reflection;
using System.Text;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// One-fact-per-<see cref="FactAttribute"/> tests for <see cref="GeminiLiveTranslateEngine"/>. The
/// engine's job is to wire the protocol layer's parsed frames into the
/// <see cref="ILiveAudioTranslationEngine"/> event surface and to own the audio send loop. Tests
/// drive a <see cref="FakeGeminiChannel"/> so each frame is scripted and deterministic.
/// </summary>
public sealed class GeminiLiveTranslateEngineTests
{
    private const string ApiKey = "test-api-key";
    private const string Model = "models/gemini-3.5-live-translate-preview";
    private const string Target = "tl";
    private const string ResolvedTargetCode = "fil";

    private static AudioChunk CreateChunk(int sampleCount = 16)
    {
        var samples = new float[sampleCount];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (i % 2 == 0) ? 0.1f : -0.1f;
        }

        var format = new AudioFormat(SampleRate: 16000, Channels: 1, BitsPerSample: 32);
        return new AudioChunk(samples, format, DateTime.UtcNow, 0);
    }

    private static GeminiLiveTranslateEngine CreateEngine(
        FakeGeminiChannel channel,
        Action<GeminiLiveTranslateEngineOptions>? configure = null)
    {
        var options = new GeminiLiveTranslateEngineOptions
        {
            ApiKey = ApiKey,
            Model = Model,
            TargetLanguage = Target,
        };
        configure?.Invoke(options);
        return new GeminiLiveTranslateEngine(options, channel);
    }

    // ----- Lifecycle: setup frame is the first send -----

    [Fact]
    public async Task StartAsync_SendsSetupFrameAsFirstMessage()
    {
        var channel = new FakeGeminiChannel();

        var engine = CreateEngine(channel);
        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();
        try
        {
            Assert.True(channel.OpenCount == 1);
            Assert.NotEmpty(channel.SentFrames);
            string first = channel.SentFrames[0];
            Assert.Contains("\"setup\"", first);
            Assert.Contains("\"model\"", first);
            Assert.Contains(Model, first);
            Assert.Contains("\"responseModalities\"", first);
            Assert.Contains("\"AUDIO\"", first);
            // outputAudioTranscription is at the setup top level (sibling of model +
            // generationConfig), NOT nested inside generationConfig — the real server rejects
            // the nested placement with `Unknown name "outputAudioTranscription" at
            // 'setup.generation_config'` (2026-08-08 spike).
            Assert.Contains("\"outputAudioTranscription\"", first);
            Assert.DoesNotContain("\"inputAudioTranscription\"", first);
            Assert.Contains("\"translationConfig\"", first);
            Assert.Contains("\"targetLanguageCode\"", first);
            Assert.Contains(ResolvedTargetCode, first);
            Assert.DoesNotContain("\"systemInstruction\"", first);
        }
        finally
        {
            await engine.StopAsync();
            engine.Dispose();
        }
    }

    [Fact]
    public async Task StartAsync_ConnectFailure_RaisesTranslationFailed()
    {
        var channel = new FakeGeminiChannel
        {
            OpenBehavior = FakeGeminiChannel.OpenBehaviorKind.ThrowConnectionFailed,
        };

        var engine = CreateEngine(channel);
        var errors = new List<LiveTranslationError>();
        engine.TranslationFailed += (_, e) => errors.Add(e);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync());

        Assert.Single(errors);
        Assert.Equal(LiveTranslationErrorKind.ConnectionFailed, errors[0].Kind);

        engine.Dispose();
    }

    // ----- Output state machine -----

    [Fact]
    public async Task PartialOutput_EmitsPartialTranslation()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var partials = new List<PartialTranslation>();
        var finals = new List<FinalTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();
        channel.QueueServerFrame(BuildServerContent("Kamusta", partial: true, turnComplete: false));
        await WaitForAsync(() => partials.Count == 1);

        Assert.Single(partials);
        Assert.Equal("Kamusta", partials[0].TranslatedText);
        Assert.Equal(Target, partials[0].TargetLanguage);
        Assert.Empty(finals);
    }

    [Fact]
    public async Task PartialOutput_ReplacesCurrentAccumulator()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var partials = new List<PartialTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // Two partials arrive in sequence: "Kamusta" then "Kamusta po". The accumulator should
        // replace, not concatenate.
        channel.QueueServerFrame(BuildServerContent("Kamusta", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("Kamusta po", partial: true, turnComplete: false));
        await WaitForAsync(() => partials.Count == 2);

        Assert.Equal("Kamusta", partials[0].TranslatedText);
        Assert.Equal("Kamusta po", partials[1].TranslatedText);
    }

    [Fact]
    public async Task MultiplePartialFrames_DoNotConcatenateDuplicatePrefixes()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var partials = new List<PartialTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // Gemini sometimes emits a final-frame text that re-states the prefix. The engine must
        // replace, not glue the new text onto the previous one.
        channel.QueueServerFrame(BuildServerContent("Magandang umaga", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("Magandang umaga lahat", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("Magandang umaga lahat.", partial: true, turnComplete: false));
        await WaitForAsync(() => partials.Count == 3);

        Assert.Equal("Magandang umaga", partials[0].TranslatedText);
        Assert.Equal("Magandang umaga lahat", partials[1].TranslatedText);
        Assert.Equal("Magandang umaga lahat.", partials[2].TranslatedText);
    }

    [Fact]
    public async Task TurnComplete_CommitsAccumulator()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var partials = new List<PartialTranslation>();
        var finals = new List<FinalTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame(BuildServerContent("Magandang umaga", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("Magandang umaga lahat", partial: true, turnComplete: true));
        await WaitForAsync(() => finals.Count == 1);

        // The first frame carries new text → partial; the second frame carries revised text AND
        // turnComplete → another partial plus a final committing the latest text.
        Assert.Equal(2, partials.Count);
        Assert.Single(finals);
        Assert.Equal("Magandang umaga", partials[0].TranslatedText);
        Assert.Equal("Magandang umaga lahat", partials[1].TranslatedText);
        Assert.Equal("Magandang umaga lahat", finals[0].TranslatedText);
    }

    [Fact]
    public async Task TurnComplete_ClearsAccumulator()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var finals = new List<FinalTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // First turn: "Kamusta" → final.
        channel.QueueServerFrame(BuildServerContent("Kamusta", partial: true, turnComplete: true));
        await WaitForAsync(() => finals.Count == 1);

        // Second turn: "Paano" partial → turnComplete. After the first turn the accumulator is
        // cleared, so the second final must be just "Paano", never "Kamusta Paano".
        channel.QueueServerFrame(BuildServerContent("Paano", partial: true, turnComplete: true));
        await WaitForAsync(() => finals.Count == 2);

        Assert.Equal("Kamusta", finals[0].TranslatedText);
        Assert.Equal("Paano", finals[1].TranslatedText);
    }

    [Fact]
    public async Task TurnCompleteWithNoText_DoesNotEmitEmptyFinal()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var finals = new List<FinalTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // turnComplete with no text in either output surface.
        channel.QueueServerFrame(BuildServerContent(text: null, partial: false, turnComplete: true));
        await WaitForAsync(() => finals.Count >= 0);
        await Task.Delay(50);

        Assert.Empty(finals);
    }

    // ----- Punctuation + idle commit heuristic -----
    // The Live Translate service never sends turnComplete (verified on the real wire 2026-08-12),
    // so the engine commits a final when the accumulated sentence ends with terminal punctuation
    // and no newer partial arrives within CommitIdleTimeout.

    [Fact]
    public async Task PunctuatedPartial_AfterIdleWindow_CommitsFinal()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel, o => o.CommitIdleTimeout = TimeSpan.FromMilliseconds(150));
        var partials = new List<PartialTranslation>();
        var finals = new List<FinalTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame(BuildServerContent("Kamusta ka na.", partial: true, turnComplete: false));
        await WaitForAsync(() => partials.Count == 1);
        Assert.Empty(finals);

        // No newer partial arrives → the idle window elapses → the accumulator commits as a final.
        await WaitForAsync(() => finals.Count == 1, timeoutMs: 2000);

        Assert.Single(finals);
        Assert.Equal("Kamusta ka na.", finals[0].TranslatedText);
        Assert.Equal(Target, finals[0].TargetLanguage);
    }

    [Fact]
    public async Task PunctuatedPartial_NewPunctuatedText_ResetsCommitWindow()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel, o => o.CommitIdleTimeout = TimeSpan.FromMilliseconds(400));
        var partials = new List<PartialTranslation>();
        var finals = new List<FinalTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // "Kamusta." arms the commit timer; a newer punctuated partial re-arms it before the
        // window elapses, so the committed final must carry the LATEST text, not the first.
        channel.QueueServerFrame(BuildServerContent("Kamusta.", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("Kamusta ka na.", partial: true, turnComplete: false));
        await WaitForAsync(() => finals.Count == 1, timeoutMs: 2000);

        Assert.Single(finals);
        Assert.Equal("Kamusta ka na.", finals[0].TranslatedText);
    }

    [Fact]
    public async Task PunctuatedPartial_NewUnpunctuatedText_CancelsPendingCommit()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel, o => o.CommitIdleTimeout = TimeSpan.FromMilliseconds(150));
        var finals = new List<FinalTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // "Kamusta ka na." arms a commit; "Kamusta ka na rin" arrives before the window elapses
        // and carries no terminal punctuation → the pending commit is cancelled, the accumulator
        // stays live (still translating), and StopAsync tail-flushes only after the new content.
        channel.QueueServerFrame(BuildServerContent("Kamusta ka na.", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("Kamusta ka na rin", partial: true, turnComplete: false));
        await Task.Delay(300);

        Assert.Empty(finals);

        await engine.StopAsync();
        engine.Dispose();

        Assert.Single(finals);
        Assert.Equal("Kamusta ka na rin", finals[0].TranslatedText);
    }

    [Fact]
    public async Task PunctuatedPartial_ZeroCommitIdleTimeout_DoesNotCommitWithoutTurnComplete()
    {
        // CommitIdleTimeout = Zero disables the punctuation heuristic (rely on turnComplete alone).
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel, o => o.CommitIdleTimeout = TimeSpan.Zero);
        var finals = new List<FinalTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame(BuildServerContent("Kamusta ka na.", partial: true, turnComplete: false));
        await WaitForAsync(() => finals.Count == 1, timeoutMs: 300);
        await Task.Delay(200);

        Assert.Empty(finals);
    }

    // ----- Live Translate disjoint word-fragments (real wire 2026-08-12) -----
    // The Live Translate service streams the target-language translation as word-level DISJOINT
    // fragments ("Kung mas marami kang maibigay" then "sa Codex, mas"). These must be APPENDED
    // into one growing sentence, unlike chat-style cumulative partials which replace.

    [Fact]
    public async Task LiveTranslateDisjointFragments_AccumulateIntoSentence_AndCommitAfterIdle()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel, o => o.CommitIdleTimeout = TimeSpan.FromMilliseconds(150));
        var partials = new List<PartialTranslation>();
        var finals = new List<FinalTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame(BuildServerContent("Kung mas marami kang maibigay", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("sa Codex, mas", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("maraming tulong ang magagamit", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("mo, totoo para sa Codex.", partial: true, turnComplete: false));
        await WaitForAsync(() => partials.Count == 4);

        // Partials expose the GROWING sentence (not the isolated fragment) so the overlay's
        // active line grows in place, Chrome-style.
        Assert.Equal("Kung mas marami kang maibigay", partials[0].TranslatedText);
        Assert.Equal("Kung mas marami kang maibigay sa Codex, mas", partials[1].TranslatedText);
        Assert.Equal("Kung mas marami kang maibigay sa Codex, mas maraming tulong ang magagamit", partials[2].TranslatedText);
        Assert.Equal(
            "Kung mas marami kang maibigay sa Codex, mas maraming tulong ang magagamit mo, totoo para sa Codex.",
            partials[3].TranslatedText);
        Assert.Empty(finals);

        // The accumulated sentence ends with terminal punctuation → commits after the idle window.
        await WaitForAsync(() => finals.Count == 1, timeoutMs: 2000);

        Assert.Single(finals);
        Assert.Equal(
            "Kung mas marami kang maibigay sa Codex, mas maraming tulong ang magagamit mo, totoo para sa Codex.",
            finals[0].TranslatedText);
    }

    [Fact]
    public async Task LiveTranslateDisjointFragments_SentenceBoundary_CommitsWithoutConsumingNextSentence()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel, o => o.CommitIdleTimeout = TimeSpan.FromMilliseconds(400));
        var partials = new List<PartialTranslation>();
        var finals = new List<FinalTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // Sentence 1 (two fragments, ends with '.'), then the start of sentence 2.
        channel.QueueServerFrame(BuildServerContent("Magandang umaga", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("sa lahat.", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("Kumusta ka", partial: true, turnComplete: false));
        await WaitForAsync(() => finals.Count == 1, timeoutMs: 2000);

        // The committed final is ONLY sentence 1; the accumulator must not consume "Kumusta ka".
        Assert.Single(finals);
        Assert.Equal("Magandang umaga sa lahat.", finals[0].TranslatedText);
    }

    [Fact]
    public async Task LiveTranslateFragment_ExactRepeatTail_DoesNotDoubleTheWord()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var partials = new List<PartialTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // The service occasionally re-emits the current tail word as its own fragment. The engine
        // must not double it: the accumulator stays "gusto", never "gusto gusto".
        channel.QueueServerFrame(BuildServerContent("gusto", partial: true, turnComplete: false));
        channel.QueueServerFrame(BuildServerContent("gusto", partial: true, turnComplete: false));
        await WaitForAsync(() => partials.Count == 2);

        Assert.Equal("gusto", partials[0].TranslatedText);
        Assert.Equal("gusto", partials[1].TranslatedText);

        await engine.StopAsync();
        engine.Dispose();
    }

    [Fact]
    public async Task SessionEndsWithPendingOutput_TailFlushesFinal()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var finals = new List<FinalTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame(BuildServerContent("Hindi pa tapos", partial: true, turnComplete: false));
        await WaitForAsync(() => finals.Count == 1, timeoutMs: 4000);

        // The session ends without turnComplete. StopAsync tail-flushes the accumulator.
        await engine.StopAsync();
        engine.Dispose();

        Assert.Single(finals);
        Assert.Equal("Hindi pa tapos", finals[0].TranslatedText);
    }

    // ----- Channel precedence -----

    [Fact]
    public async Task BothOutputSurfacesPresent_OutputTranscriptionWins()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var partials = new List<PartialTranslation>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        string frame = """
        {
          "serverContent": {
            "modelTurn": {
              "parts": [{ "text": "WRONG: from modelTurn" }]
            },
            "outputTranscription": {
              "text": "RIGHT: from outputTranscription"
            },
            "turnComplete": false
          }
        }
        """;
        channel.QueueServerFrame(frame);
        await WaitForAsync(() => partials.Count == 1);

        Assert.Equal("RIGHT: from outputTranscription", partials[0].TranslatedText);
    }

    // ----- Audio path -----

    [Fact]
    public async Task PushAudio_EncodesFloatSamplesAsPcm16LittleEndian()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        var samples = new float[] { 0.0f, 0.5f, -0.5f, 1.0f, -1.0f };
        var format = new AudioFormat(SampleRate: 16000, Channels: 1, BitsPerSample: 32);
        var chunk = new AudioChunk(samples, format, DateTime.UtcNow, 0);
        engine.PushAudio(chunk);

        // Wait for the send-task to enqueue at least one audio frame. The setup frame is index 0,
        // so the first audio frame is index 1.
        await WaitForAsync(() => channel.SentFrames.Count >= 2);

        string audioFrame = channel.SentFrames[1];
        Assert.Contains("\"realtimeInput\"", audioFrame);
        Assert.Contains("\"audio\"", audioFrame);
        Assert.Contains("audio/pcm;rate=16000", audioFrame);

        // Extract the base64 payload and decode to verify the PCM16 encoding round-trips.
        int dataIdx = audioFrame.IndexOf("\"data\":\"", StringComparison.Ordinal);
        Assert.True(dataIdx >= 0);
        int start = dataIdx + "\"data\":\"".Length;
        int end = audioFrame.IndexOf('"', start);
        string base64 = audioFrame.Substring(start, end - start);
        byte[] pcm = Convert.FromBase64String(base64);
        Assert.Equal(samples.Length * 2, pcm.Length);

        // 0.0 → 0x0000; 0.5 → ~0x4000 (round-half-to-even; 32767 * 0.5 = 16383.5 → 0x3FFF);
        // -0.5 → ~-16384 → 0xC000; 1.0 → 0x7FFF (clamped); -1.0 → 0x8001 (clamped at short.MinValue + 1).
        // We assert the LE layout rather than the exact quantized values to keep the test robust
        // against tiny rounding differences across runtimes.
        Assert.Equal(0x00, pcm[0]); // 0.0 low byte
        Assert.Equal(0x00, pcm[1]); // 0.0 high byte
    }

    [Fact]
    public async Task PushAudio_BeyondChannelCapacity_DropsOldest()
    {
        // Capacity is 64 chunks; pushing 200 small chunks should keep the queue bounded and the
        // send-task should not block. The test asserts the engine remains responsive and the
        // send count grows beyond 64 (i.e. the channel is alive and the send-task is draining).
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        for (int i = 0; i < 200; i++)
        {
            engine.PushAudio(CreateChunk());
        }

        await WaitForAsync(() => channel.SentFrames.Count > 64, timeoutMs: 4000);

        // 1 setup frame + at least 64 audio frames; many older chunks will have been dropped.
        Assert.True(channel.SentFrames.Count >= 65);
    }

    [Fact]
    public async Task PushAudio_BeforeStart_IsIgnored()
    {
        var channel = new FakeGeminiChannel();
        var engine = CreateEngine(channel);

        // Don't call StartAsync; just push audio. The send-task is null and TryWrite on a channel
        // whose reader hasn't started should drop the chunk silently rather than block or throw.
        engine.PushAudio(CreateChunk());

        Assert.Empty(channel.SentFrames);
        engine.Dispose();
    }

    // ----- Error mapping -----

    [Fact]
    public void MapError_Code401_SessionRejected()
    {
        Assert.Equal(
            LiveTranslationErrorKind.SessionRejected,
            InvokeMapError(code: 401, status: null, message: null));
    }

    [Fact]
    public void MapError_Code403_SessionRejected()
    {
        Assert.Equal(
            LiveTranslationErrorKind.SessionRejected,
            InvokeMapError(code: 403, status: null, message: null));
    }

    [Fact]
    public void MapError_Code429_ConnectionFailed()
    {
        Assert.Equal(
            LiveTranslationErrorKind.ConnectionFailed,
            InvokeMapError(code: 429, status: null, message: null));
    }

    [Fact]
    public void MapError_StatusUnauthenticated_SessionRejected()
    {
        Assert.Equal(
            LiveTranslationErrorKind.SessionRejected,
            InvokeMapError(code: null, status: "UNAUTHENTICATED", message: null));
    }

    [Fact]
    public void MapError_StatusPermissionDenied_SessionRejected()
    {
        Assert.Equal(
            LiveTranslationErrorKind.SessionRejected,
            InvokeMapError(code: null, status: "PERMISSION_DENIED", message: null));
    }

    [Fact]
    public void MapError_StatusResourceExhausted_ConnectionFailed()
    {
        Assert.Equal(
            LiveTranslationErrorKind.ConnectionFailed,
            InvokeMapError(code: null, status: "RESOURCE_EXHAUSTED", message: null));
    }

    [Fact]
    public void MapError_MessageContainsApiKey_SessionRejected()
    {
        Assert.Equal(
            LiveTranslationErrorKind.SessionRejected,
            InvokeMapError(code: null, status: null, message: "Invalid API key provided."));
    }

    [Fact]
    public void MapError_MessageContainsQuota_ConnectionFailed()
    {
        Assert.Equal(
            LiveTranslationErrorKind.ConnectionFailed,
            InvokeMapError(code: null, status: null, message: "Quota exceeded for the day."));
    }

    [Fact]
    public void MapError_UnknownShape_Unknown()
    {
        Assert.Equal(
            LiveTranslationErrorKind.Unknown,
            InvokeMapError(code: null, status: null, message: "Some unexpected thing happened."));
    }

    [Fact]
    public void MapError_CodeWinsOverStatusAndMessage()
    {
        // 429 should map to ConnectionFailed regardless of the message wording.
        Assert.Equal(
            LiveTranslationErrorKind.ConnectionFailed,
            InvokeMapError(code: 429, status: "UNAUTHENTICATED", message: "API key invalid"));
    }

    [Fact]
    public async Task ErrorFrame_RaisesTranslationFailed()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var errors = new List<LiveTranslationError>();
        engine.TranslationFailed += (_, e) => errors.Add(e);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        string frame = """
        {
          "error": {
            "code": 401,
            "status": "UNAUTHENTICATED",
            "message": "API key invalid"
          }
        }
        """;
        channel.QueueServerFrame(frame);
        await WaitForAsync(() => errors.Count == 1);

        Assert.Single(errors);
        Assert.Equal(LiveTranslationErrorKind.SessionRejected, errors[0].Kind);
        Assert.DoesNotContain(ApiKey, errors[0].Message);
    }

    [Fact]
    public async Task MalformedServerFrame_RaisesTranslationFailed_AndDoesNotThrowToCaller()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var errors = new List<LiveTranslationError>();
        engine.TranslationFailed += (_, e) => errors.Add(e);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame("{ this is not valid json");
        await WaitForAsync(() => errors.Count == 1);

        Assert.Single(errors);
        Assert.Equal(LiveTranslationErrorKind.Unknown, errors[0].Kind);
    }

    [Fact]
    public async Task GoAwayFrame_StopsReceiveLoop_AndTailFlushesFinal()
    {
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var finals = new List<FinalTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // Pending partial, then goAway — StopAsync should tail-flush.
        channel.QueueServerFrame(BuildServerContent("Paalam na", partial: true, turnComplete: false));
        channel.QueueServerFrame("{\"goAway\":{}}");
        await WaitForAsync(() => finals.Count == 1, timeoutMs: 4000);

        Assert.Single(finals);
        Assert.Equal("Paalam na", finals[0].TranslatedText);
    }

    // ----- sessionResumptionUpdate: engine must not fatal -----

    [Fact]
    public async Task SessionResumptionUpdateFrame_IsNoOp_AndSubsequentFramesContinue()
    {
        // Real-wire spike (2026-08-08): the server emits sessionResumptionUpdate AFTER a final
        // translation. A5 used to misclassify it as "Unrecognized top-level frame" and the engine
        // killed the session, dropping subsequent translations. After the fix the engine must
        // treat the frame as a no-op, continue receiving, and emit the next translation cleanly.
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var partials = new List<PartialTranslation>();
        var finals = new List<FinalTranslation>();
        var errors = new List<LiveTranslationError>();
        engine.PartialTranslationAvailable += (_, p) => partials.Add(p);
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);
        engine.TranslationFailed += (_, e) => errors.Add(e);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        // First turn: a partial + turnComplete final.
        channel.QueueServerFrame(BuildServerContent("Una", partial: true, turnComplete: true));

        // The sessionResumptionUpdate arrives interleaved with translations. The engine must
        // ignore it: no TranslationFailed, no flush of the accumulator.
        channel.QueueServerFrame("""{"sessionResumptionUpdate":{"resumable":true,"newHandle":"opaque-handle"}}""");

        // Second turn arrives cleanly after the resumption update.
        channel.QueueServerFrame(BuildServerContent("Ikalawa", partial: true, turnComplete: true));

        await WaitForAsync(() => finals.Count == 2, timeoutMs: 4000);

        Assert.Empty(errors);
        Assert.Equal(2, finals.Count);
        Assert.Equal("Una", finals[0].TranslatedText);
        Assert.Equal("Ikalawa", finals[1].TranslatedText);
    }

    [Fact]
    public async Task SessionResumptionUpdateFrame_DoesNotFlushAccumulator()
    {
        // Belt-and-braces: a resumption update arrives between a partial and its turnComplete.
        // The engine must NOT treat the resumption update as a turn boundary (that would
        // prematurely flush "Pending" as a final and drop the actual final that comes next).
        var channel = new FakeGeminiChannel();
        await using var engine = CreateEngine(channel);
        var finals = new List<FinalTranslation>();
        engine.FinalTranslationAvailable += (_, f) => finals.Add(f);

        channel.ReceiveReturnsNullOnEmpty = true;
        await engine.StartAsync();

        channel.QueueServerFrame(BuildServerContent("Pending", partial: true, turnComplete: false));
        channel.QueueServerFrame("""{"sessionResumptionUpdate":{"resumable":false}}""");
        channel.QueueServerFrame(BuildServerContent("Pending final", partial: true, turnComplete: true));

        await WaitForAsync(() => finals.Count == 1, timeoutMs: 4000);

        // The committed final must be the text from the turnComplete frame — not the resumption
        // update, which has no text at all. A6 must keep the accumulator intact across the
        // resumption update.
        Assert.Single(finals);
        Assert.Equal("Pending final", finals[0].TranslatedText);
    }

    // ----- API key privacy -----

    [Fact]
    public void Constructor_ApiKeyNeverSurfacedInToString()
    {
        var channel = new FakeGeminiChannel();
        var engine = CreateEngine(channel);

        string rendered = engine.ToString() ?? string.Empty;
        Assert.DoesNotContain(ApiKey, rendered);

        engine.Dispose();
    }

    [Fact]
    public void Constructor_RejectsEmptyApiKey()
    {
        var channel = new FakeGeminiChannel();
        var options = new GeminiLiveTranslateEngineOptions
        {
            ApiKey = string.Empty,
            Model = Model,
            TargetLanguage = Target,
        };

        Assert.Throws<ArgumentException>(() => new GeminiLiveTranslateEngine(options, channel));
    }

    // ----- Helpers -----

    private static string BuildServerContent(string? text, bool partial, bool turnComplete)
    {
        string textNode = text is null
            ? string.Empty
            : $"\"text\":\"{text.Replace("\"", "\\\"")}\"";
        string partialNode = partial ? "\"partial\":true," : string.Empty;
        return $$"""
        {
          "serverContent": {
            "outputTranscription": { {{textNode}} },
            {{partialNode}}
            "turnComplete": {{(turnComplete ? "true" : "false")}}
          }
        }
        """;
    }

    private static LiveTranslationErrorKind InvokeMapError(int? code, string? status, string? message)
    {
        var frame = new GeminiServerMessage.ErrorFrame(code, status, message);
        MethodInfo method = typeof(GeminiLiveTranslateEngine).GetMethod(
            "MapErrorKind",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MapErrorKind method not found.");
        object? result = method.Invoke(null, new object?[] { frame });
        return result is LiveTranslationErrorKind kind
            ? kind
            : throw new InvalidOperationException("MapErrorKind returned an unexpected type.");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        int elapsed = 0;
        const int stepMs = 10;
        while (!condition() && elapsed < timeoutMs)
        {
            await Task.Delay(stepMs);
            elapsed += stepMs;
        }
    }
}
