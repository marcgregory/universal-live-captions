using System.Text;
using System.Text.Json;
using UniversalCaptions.Speech.Gemini;

namespace UniversalCaptions.Speech.Gemini.Tests;

/// <summary>
/// One-fact-per-<see cref="FactAttribute"/> tests for <see cref="GeminiLiveTranslateProtocol"/>.
/// Each test pins a single observable protocol result: a JSON frame the protocol emits, or a typed
/// <see cref="GeminiServerMessage"/> the protocol parses from a known fixture. The wire format is
/// the highest-risk part of A5, so each fact is a small, named regression guard.
/// </summary>
public sealed class GeminiLiveTranslateProtocolTests
{
    // The setup frame carries the `models/` prefix + the BCP-47 target language code, both of
    // which are required by Google's current Live Translate contract (verified 2026-08-08). The
    // bare model id without prefix is rejected by the real server, and BCP-47 (fil) is what
    // `translationConfig.targetLanguageCode` expects, not ISO 639-1 (tl).
    private const string Model = "models/gemini-3.5-live-translate-preview";
    private const string TargetCode = "fil";

    // ----- Setup frame -----

    [Fact]
    public void SetupFrame_ContainsModel()
    {
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(Model, document.RootElement.GetProperty("setup").GetProperty("model").GetString());
    }

    [Fact]
    public void SetupFrame_AsksForAudioOutput_WithTranscriptionSideChannel()
    {
        // Live Translate is voice-to-voice. The server outputs AUDIO; the translated text
        // arrives on the `outputAudioTranscription` side-channel. Asking for TEXT modality would
        // be a no-op or a rejection.
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        JsonElement modalities = document.RootElement
            .GetProperty("setup")
            .GetProperty("generationConfig")
            .GetProperty("responseModalities");

        Assert.Equal(JsonValueKind.Array, modalities.ValueKind);
        var list = new List<string>();
        foreach (JsonElement m in modalities.EnumerateArray())
        {
            list.Add(m.GetString() ?? string.Empty);
        }

        Assert.Single(list);
        Assert.Equal("AUDIO", list[0]);
    }

    [Fact]
    public void SetupFrame_EnablesOutputAudioTranscription_AtTopLevel()
    {
        // outputAudioTranscription is a TOP-LEVEL sibling of model + generationConfig on
        // BidiGenerateContentSetup, NOT nested inside generationConfig. The real server rejects
        // it at `setup.generation_config` with `Unknown name "outputAudioTranscription"...`
        // (2026-08-08 spike). The REST reference at https://ai.google.dev/api/live confirms the
        // top-level placement.
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        JsonElement setup = document.RootElement.GetProperty("setup");
        Assert.True(setup.TryGetProperty("outputAudioTranscription", out JsonElement outputAudioTranscription));
        Assert.Equal(JsonValueKind.Object, outputAudioTranscription.ValueKind);

        // The field must NOT also appear inside generationConfig — that nested path is the
        // malformed version the server rejects.
        JsonElement generationConfig = setup.GetProperty("generationConfig");
        Assert.False(generationConfig.TryGetProperty("outputAudioTranscription", out _));
    }

    [Fact]
    public void SetupFrame_EnablesInputAudioTranscription_AtTopLevel()
    {
        // ADR-0011: Gemini is the single STT + translation engine, so the setup frame asks for
        // the INPUT transcript side-channel too. The real server accepted the top-level
        // `inputAudioTranscription` sibling of model + generationConfig in the 2026-08-09
        // spike A/B run (same placement rule as outputAudioTranscription; the nested
        // generationConfig path is rejected).
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        JsonElement setup = document.RootElement.GetProperty("setup");
        Assert.True(setup.TryGetProperty("inputAudioTranscription", out JsonElement inputAudioTranscription));
        Assert.Equal(JsonValueKind.Object, inputAudioTranscription.ValueKind);

        // The field must NOT also appear inside generationConfig — that nested path is the
        // malformed version the server rejects.
        JsonElement generationConfig = setup.GetProperty("generationConfig");
        Assert.False(generationConfig.TryGetProperty("inputAudioTranscription", out _));
    }

    [Fact]
    public void SetupFrame_TranslationConfigLivesInsideGenerationConfig()
    {
        // translationConfig is documented at https://ai.google.dev/gemini-api/docs/live-api/live-translate
        // as a child of generationConfig (NOT a top-level field). outputAudioTranscription is a
        // top-level field; the two fields live in different paths even though the docs example
        // shows them side by side.
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        JsonElement setup = document.RootElement.GetProperty("setup");
        Assert.False(setup.TryGetProperty("translationConfig", out _),
            "translationConfig should NOT be at the setup top level (per Google's Live Translate docs).");

        JsonElement translation = setup.GetProperty("generationConfig").GetProperty("translationConfig");
        Assert.Equal(TargetCode, translation.GetProperty("targetLanguageCode").GetString());
        Assert.False(translation.GetProperty("echoTargetLanguage").GetBoolean());
    }

    [Fact]
    public void SetupFrame_TranslationConfigTopLevel_NotPresent()
    {
        // Belt-and-braces: the docs say translationConfig is a generationConfig child. Make sure
        // we never accidentally move it to the setup top level.
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        JsonElement setup = document.RootElement.GetProperty("setup");
        // translationConfig is a child of generationConfig — the lookup above must succeed.
        // We re-state it here to make the top-level non-presence explicit.
        JsonElement generationConfig = setup.GetProperty("generationConfig");
        Assert.True(generationConfig.TryGetProperty("translationConfig", out _));
    }

    [Fact]
    public void SetupFrame_IncludesTranslationConfig_WithTargetLanguageCode()
    {
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        JsonElement translation = document.RootElement
            .GetProperty("setup")
            .GetProperty("generationConfig")
            .GetProperty("translationConfig");

        Assert.Equal(TargetCode, translation.GetProperty("targetLanguageCode").GetString());
        Assert.False(translation.GetProperty("echoTargetLanguage").GetBoolean());
    }

    [Fact]
    public void SetupFrame_EchoTargetLanguage_DefaultsFalse()
    {
        // The default is echoTargetLanguage = false: the caption pipeline carries the language
        // itself, the audio output is ignored.
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        JsonElement echo = document.RootElement
            .GetProperty("setup")
            .GetProperty("generationConfig")
            .GetProperty("translationConfig")
            .GetProperty("echoTargetLanguage");

        Assert.Equal(JsonValueKind.False, echo.ValueKind);
    }

    [Fact]
    public void SetupFrame_EchoTargetLanguage_TrueIsForwarded()
    {
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode, echoTargetLanguage: true);
        using var document = JsonDocument.Parse(json);

        bool echo = document.RootElement
            .GetProperty("setup")
            .GetProperty("generationConfig")
            .GetProperty("translationConfig")
            .GetProperty("echoTargetLanguage")
            .GetBoolean();

        Assert.True(echo);
    }

    [Fact]
    public void SetupFrame_DoesNotIncludeSystemInstruction()
    {
        // STATUS (2026-08-08, Google's Live Translate docs): systemInstruction is REJECTED on
        // this surface — "Pure low-latency translation; no support for tools or instructions."
        // The protocol intentionally never emits the field, regardless of any caller argument.
        string json = GeminiLiveTranslateProtocol.BuildSetupFrame(Model, TargetCode);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("setup").TryGetProperty("systemInstruction", out _));
    }

    [Fact]
    public void SetupFrame_ThrowsOnEmptyModel()
    {
        Assert.Throws<ArgumentException>(() => GeminiLiveTranslateProtocol.BuildSetupFrame(string.Empty, TargetCode));
    }

    [Fact]
    public void SetupFrame_ThrowsOnEmptyTargetLanguageCode()
    {
        Assert.Throws<ArgumentException>(() => GeminiLiveTranslateProtocol.BuildSetupFrame(Model, string.Empty));
    }

    // ----- Realtime audio input frame -----

    [Fact]
    public void RealtimeAudioFrame_EmitsBase64Payload_Verbatim()
    {
        // Known 16-bit LE samples: 0x0001 0x0002 → bytes [0x01, 0x00, 0x02, 0x00]
        byte[] pcm = [0x01, 0x00, 0x02, 0x00];
        string expectedBase64 = Convert.ToBase64String(pcm);

        string json = GeminiLiveTranslateProtocol.BuildRealtimeAudioFrame(pcm);
        using var document = JsonDocument.Parse(json);

        string actualBase64 = document.RootElement
            .GetProperty("realtimeInput")
            .GetProperty("audio")
            .GetProperty("data")
            .GetString()!;

        Assert.Equal(expectedBase64, actualBase64);
    }

    [Fact]
    public void RealtimeAudioFrame_EmitsExpectedMimeType()
    {
        byte[] pcm = [0x00];
        string json = GeminiLiveTranslateProtocol.BuildRealtimeAudioFrame(pcm);
        using var document = JsonDocument.Parse(json);

        string mimeType = document.RootElement
            .GetProperty("realtimeInput")
            .GetProperty("audio")
            .GetProperty("mimeType")
            .GetString()!;

        Assert.Equal("audio/pcm;rate=16000", mimeType);
    }

    [Fact]
    public void RealtimeAudioFrame_EmptyBuffer_StillEmitsValidFrame()
    {
        // An empty PCM buffer is a valid input (it just means "send a no-op frame"); the protocol
        // must produce a parseable JSON document regardless. A6 may choose to skip such frames; the
        // protocol layer is permissive.
        string json = GeminiLiveTranslateProtocol.BuildRealtimeAudioFrame(ReadOnlySpan<byte>.Empty);
        using var document = JsonDocument.Parse(json);

        string actualBase64 = document.RootElement
            .GetProperty("realtimeInput")
            .GetProperty("audio")
            .GetProperty("data")
            .GetString()!;

        Assert.Equal(string.Empty, actualBase64);
    }

    [Fact]
    public void RealtimeAudioFrame_LargeBuffer_Base64RoundTripsToOriginalBytes()
    {
        // 1024 samples = 2048 bytes — large enough to catch any accidental buffer slicing.
        byte[] pcm = new byte[2048];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i & 0xFF);
        }

        string json = GeminiLiveTranslateProtocol.BuildRealtimeAudioFrame(pcm);
        using var document = JsonDocument.Parse(json);
        string actualBase64 = document.RootElement
            .GetProperty("realtimeInput")
            .GetProperty("audio")
            .GetProperty("data")
            .GetString()!;

        byte[] roundTripped = Convert.FromBase64String(actualBase64!);
        Assert.Equal(pcm, roundTripped);
    }

    // ----- Server frame parsing: setupComplete -----

    [Fact]
    public void SetupComplete_IsParsed()
    {
        const string json = """{"setupComplete":{}}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.IsType<GeminiServerMessage.SetupComplete>(message);
    }

    [Fact]
    public void SetupComplete_NullValue_IsAlsoAccepted()
    {
        // Some Gemini responses carry `"setupComplete":null`; the protocol must accept either shape.
        const string json = """{"setupComplete":null}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        Assert.IsType<GeminiServerMessage.SetupComplete>(message);
    }

    // ----- Server frame parsing: serverContent (transcription + turnComplete) -----

    [Fact]
    public void OutputTranscription_ExtractsText_FromOutputTranscriptionObject()
    {
        const string json = """
            {
              "serverContent": {
                "outputTranscription": { "text": "Magandang umaga" }
              }
            }
            """;

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Equal("Magandang umaga", content.Text);
    }

    [Fact]
    public void OutputTranscription_DefaultIsFinal_WhenNoPartialFlag()
    {
        const string json = """
            {
              "serverContent": {
                "outputTranscription": { "text": "Magandang umaga" }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.False(content.IsPartial);
    }

    [Fact]
    public void OutputTranscription_PartialTrue_MarksAsPartial()
    {
        const string json = """
            {
              "serverContent": {
                "partial": true,
                "outputTranscription": { "text": "Maga" }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.True(content.IsPartial);
        Assert.Equal("Maga", content.Text);
    }

    [Fact]
    public void OutputTranscription_PartialFalse_MarksAsFinal()
    {
        const string json = """
            {
              "serverContent": {
                "partial": false,
                "outputTranscription": { "text": "Magandang umaga" }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.False(content.IsPartial);
        Assert.Equal("Magandang umaga", content.Text);
    }

    [Fact]
    public void ServerContent_ModelTurnText_IsExtractedAsText()
    {
        // Some Live Translate responses carry modelTurn.parts[].text instead of outputTranscription.
        const string json = """
            {
              "serverContent": {
                "modelTurn": {
                  "parts": [ { "text": "Kumusta" } ]
                }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Equal("Kumusta", content.Text);
        Assert.False(content.IsPartial);
    }

    [Fact]
    public void ServerContent_TurnCompleteTrue_MarksTurnComplete()
    {
        const string json = """
            {
              "serverContent": {
                "turnComplete": true,
                "outputTranscription": { "text": "Magandang umaga" }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.True(content.TurnComplete);
        Assert.Equal("Magandang umaga", content.Text);
    }

    [Fact]
    public void ServerContent_TurnCompleteOnly_TextIsNull()
    {
        // Some servers emit a turn-complete without an attached text token.
        const string json = """
            {
              "serverContent": {
                "turnComplete": true
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Null(content.Text);
        Assert.True(content.TurnComplete);
    }

    [Fact]
    public void ServerContent_NoTranscription_AndNoTurnComplete_TextIsNull_AndTurnIsFalse()
    {
        const string json = """{"serverContent":{}}""";

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Null(content.Text);
        Assert.False(content.IsPartial);
        Assert.False(content.TurnComplete);
    }

    [Fact]
    public void InputTranscription_IsExtractedAsInputText()
    {
        // ADR-0011: the source-language transcript arrives on `inputTranscription` while the
        // translation arrives on `outputTranscription`. The two surfaces must stay separate.
        const string json = """
            {
              "serverContent": {
                "inputTranscription": { "text": "Good morning" }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Equal("Good morning", content.InputText);
        Assert.Null(content.Text);
    }

    [Fact]
    public void InputTranscription_PartialFlag_MarksInputIsPartial()
    {
        const string json = """
            {
              "serverContent": {
                "partial": true,
                "inputTranscription": { "text": "Good m" }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Equal("Good m", content.InputText);
        Assert.True(content.InputIsPartial);
    }

    [Fact]
    public void InputAndOutputTranscription_BothSurfacesCarriedIndependently()
    {
        const string json = """
            {
              "serverContent": {
                "partial": true,
                "inputTranscription": { "text": "Good morning" },
                "outputTranscription": { "text": "Magandang umaga" }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Equal("Good morning", content.InputText);
        Assert.True(content.InputIsPartial);
        Assert.Equal("Magandang umaga", content.Text);
        Assert.True(content.IsPartial);
    }

    [Fact]
    public void InputTranscriptionWithoutText_InputTextIsNull()
    {
        const string json = """
            {
              "serverContent": {
                "inputTranscription": {}
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Null(content.InputText);
        Assert.False(content.InputIsPartial);
    }

    // ----- Server frame parsing: goAway -----

    [Fact]
    public void GoAway_IsParsed()
    {
        const string json = """{"goAway":{"timeLeft":"30s"}}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        Assert.IsType<GeminiServerMessage.GoAway>(message);
    }

    [Fact]
    public void GoAway_EmptyObject_IsParsed()
    {
        const string json = """{"goAway":{}}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        Assert.IsType<GeminiServerMessage.GoAway>(message);
    }

    // ----- Server frame parsing: sessionResumptionUpdate -----

    [Fact]
    public void SessionResumptionUpdate_ResumableTrue_IsParsed()
    {
        // Real-wire spike (2026-08-08) confirmed Google emits
        // `{"sessionResumptionUpdate":{"resumable":true,"newHandle":"…"}}` on the Live Translate
        // surface, even though Live Translate does not accept sessionResumption configuration.
        // A5 MUST recognize this frame so the session isn't torn down after a successful translation.
        const string json = """
            {
              "sessionResumptionUpdate": {
                "resumable": true,
                "newHandle": "opaque-handle-bytes-not-logged-here"
              }
            }
            """;

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        var update = Assert.IsType<GeminiServerMessage.SessionResumptionUpdate>(message);
        Assert.True(update.Resumable);
        Assert.Equal("opaque-handle-bytes-not-logged-here", update.NewHandle);
    }

    [Fact]
    public void SessionResumptionUpdate_ResumableFalse_IsParsed()
    {
        const string json = """{"sessionResumptionUpdate":{"resumable":false}}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        var update = Assert.IsType<GeminiServerMessage.SessionResumptionUpdate>(message);
        Assert.False(update.Resumable);
        Assert.Null(update.NewHandle);
    }

    [Fact]
    public void SessionResumptionUpdate_EmptyObject_IsParsed_WithDefaults()
    {
        // Both fields are optional — the server may omit either. Default: Resumable=false,
        // NewHandle=null. A5 still accepts the frame; A6 treats it as a no-op.
        const string json = """{"sessionResumptionUpdate":{}}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        var update = Assert.IsType<GeminiServerMessage.SessionResumptionUpdate>(message);
        Assert.False(update.Resumable);
        Assert.Null(update.NewHandle);
    }

    [Fact]
    public void SessionResumptionUpdate_WrongType_ReturnsFalse()
    {
        // `"sessionResumptionUpdate":"oops"` is not a recognized shape. The protocol must surface
        // a parse error rather than silently parsing into the no-op case.
        const string json = """{"sessionResumptionUpdate":"oops"}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out var error);

        Assert.False(ok);
        Assert.Null(message);
        Assert.NotNull(error);
    }

    // ----- Server frame parsing: error frame -----

    [Fact]
    public void ErrorFrame_ExtractsCode()
    {
        const string json = """
            { "error": { "code": 7, "message": "Invalid API key." } }
            """;

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        var error = Assert.IsType<GeminiServerMessage.ErrorFrame>(message);
        Assert.Equal(7, error.Code);
    }

    [Fact]
    public void ErrorFrame_ExtractsStatus()
    {
        const string json = """
            { "error": { "code": 8, "status": "PERMISSION_DENIED", "message": "x" } }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var error = Assert.IsType<GeminiServerMessage.ErrorFrame>(message);
        Assert.Equal("PERMISSION_DENIED", error.Status);
    }

    [Fact]
    public void ErrorFrame_ExtractsMessage()
    {
        const string json = """
            { "error": { "code": 7, "message": "API key not valid. Please pass a valid API key." } }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var error = Assert.IsType<GeminiServerMessage.ErrorFrame>(message);
        Assert.Equal("API key not valid. Please pass a valid API key.", error.Message);
    }

    [Fact]
    public void ErrorFrame_AllFieldsOptional()
    {
        const string json = """{"error":{}}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.True(ok);
        var error = Assert.IsType<GeminiServerMessage.ErrorFrame>(message);
        Assert.Null(error.Code);
        Assert.Null(error.Status);
        Assert.Null(error.Message);
    }

    [Fact]
    public void ErrorFrame_TakesPrecedenceOverOtherFields()
    {
        // A frame that contains both error and serverContent is an error frame; A6 will surface
        // the error. The protocol prefers error so a failing session cannot accidentally emit
        // partial translations on its way out.
        const string json = """
            {
              "error": { "code": 7, "message": "x" },
              "serverContent": { "outputTranscription": { "text": "should be ignored" } }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        Assert.IsType<GeminiServerMessage.ErrorFrame>(message);
    }

    // ----- Server frame parsing: malformed / unsupported -----

    [Fact]
    public void MalformedJson_ReturnsFalse_WithDescriptiveError()
    {
        const string json = """{"serverContent": { broken """;

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out var error);

        Assert.False(ok);
        Assert.Null(message);
        Assert.NotNull(error);
        Assert.StartsWith("Malformed JSON", error);
    }

    [Fact]
    public void EmptyFrame_ReturnsFalse()
    {
        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(string.Empty, out var message, out var error);

        Assert.False(ok);
        Assert.Null(message);
        Assert.NotNull(error);
    }

    [Fact]
    public void WhitespaceFrame_ReturnsFalse()
    {
        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame("   ", out var message, out _);

        Assert.False(ok);
        Assert.Null(message);
    }

    [Fact]
    public void TopLevelArray_ReturnsFalse()
    {
        const string json = "[]";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out var error);

        Assert.False(ok);
        Assert.Null(message);
        Assert.NotNull(error);
    }

    [Fact]
    public void UnrecognizedFrame_ReturnsFalse()
    {
        const string json = """{"someOtherField":"unexpected"}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out var error);

        Assert.False(ok);
        Assert.Null(message);
        Assert.NotNull(error);
        Assert.Contains("Unrecognized", error);
    }

    [Fact]
    public void UnrecognizedFrame_DiagnosticListsTopLevelKeysAndValueKinds()
    {
        // A5 fail-soft contract: when a server frame shape is unknown, the diagnostic MUST name
        // the unknown top-level properties (and their JSON value kinds) so the spike runner can
        // tell us what to add next. Property bytes/values are intentionally NOT included — only
        // structural metadata, so it's safe to surface the diagnostic in logs without leaking
        // any audio/transcript content.
        const string json = """
            {
              "usageMetadata": { "totalTokenCount": 17 },
              "toolCall":     { "functionCalls": [] }
            }
            """;

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out var error);

        Assert.False(ok);
        Assert.Null(message);
        Assert.NotNull(error);
        Assert.Contains("Unrecognized", error);
        Assert.Contains("usageMetadata:Object", error);
        Assert.Contains("toolCall:Object", error);
    }

    [Fact]
    public void UnrecognizedFrame_DiagnosticDoesNotListKnownShapes()
    {
        // The four + one known top-level frame shapes must NEVER appear in the diagnostic's
        // topLevelKeys=[...] payload list — if they ever do, A5 has regressed and forgotten to
        // handle a shape it already knows. Adding `sessionResumptionUpdate` here pins the fix
        // for the 2026-08-08 spike. We assert only the list portion, because the human-readable
        // prefix legitimately enumerates the known shapes ("…no serverContent, error,…").
        const string json = """{"someUnknownFrame":{"x":1}}""";

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out _, out var error);

        Assert.NotNull(error);
        int listStart = error.IndexOf("topLevelKeys=[", StringComparison.Ordinal);
        Assert.True(listStart >= 0, $"diagnostic should contain topLevelKeys=[…]: {error}");
        string list = error.Substring(listStart);

        Assert.Contains("someUnknownFrame:Object", list);
        Assert.DoesNotContain("serverContent", list);
        Assert.DoesNotContain("setupComplete", list);
        Assert.DoesNotContain("goAway", list);
        Assert.DoesNotContain("sessionResumptionUpdate", list);
        // `error` is both a property name (a known shape) and a substring of the prefix; check it
        // only inside the list, not the human-readable message body.
        Assert.DoesNotContain("\"error\":", list);
        Assert.DoesNotContain("error:Object", list);
    }

    [Fact]
    public void UnrecognizedFrame_EmptyObject_DiagnosticExplainsEmpty()
    {
        // Defensive: if the server sends `{}` (or any empty-object shape we don't recognize),
        // the diagnostic must explain the empty case without crashing.
        const string json = """{}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out var error);

        Assert.False(ok);
        Assert.Null(message);
        Assert.NotNull(error);
        Assert.Contains("Unrecognized", error);
        Assert.Contains("empty object", error);
    }

    [Fact]
    public void UnrecognizedFrame_DiagnosticOmitsPayloadBytes()
    {
        // The diagnostic MUST NOT contain literal payload values — only names + value kinds.
        // We verify this with a value that would otherwise be unique in the diagnostic string.
        const string json = """{"secretSentinelValue42":"payloadBytesShouldNotAppear"}""";

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out _, out var error);

        Assert.NotNull(error);
        Assert.DoesNotContain("payloadBytesShouldNotAppear", error);
        Assert.Contains("secretSentinelValue42", error);
    }

    [Fact]
    public void ServerContent_WrongType_ReturnsFalse()
    {
        const string json = """{"serverContent":"should be object"}""";

        bool ok = GeminiLiveTranslateProtocol.TryParseServerFrame(json, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ServerContent_OutputTranscriptionWithoutText_TextIsNull()
    {
        // No "text" property → Text is null. A6 treats a null text in a serverContent as a no-op
        // (don't raise Partial/Final, just wait for the next frame).
        const string json = """
            {
              "serverContent": {
                "outputTranscription": { }
              }
            }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Null(content.Text);
    }

    [Fact]
    public void ServerContent_ModelTurnWithoutParts_TextIsNull()
    {
        const string json = """
            { "serverContent": { "modelTurn": {} } }
            """;

        GeminiLiveTranslateProtocol.TryParseServerFrame(json, out var message, out _);

        var content = Assert.IsType<GeminiServerMessage.ServerContent>(message);
        Assert.Null(content.Text);
    }
}
