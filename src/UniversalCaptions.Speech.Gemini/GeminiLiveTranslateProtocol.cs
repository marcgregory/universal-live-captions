using System.Text.Json;

namespace UniversalCaptions.Speech.Gemini;

/// <summary>
/// Encodes and decodes the Gemini Live Translate wire format. Pure data: no I/O, no session
/// lifecycle, no buffering — those belong in <see cref="GeminiLiveTranslateEngine"/>. The protocol
/// layer knows about frame shapes and JSON field names; it does not know about WebSockets,
/// cancellation budgets, or the Caption pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The protocol surface is intentionally small:
/// </para>
/// <list type="bullet">
///   <item><see cref="BuildSetupFrame"/> constructs the JSON for the initial setup message.</item>
///   <item><see cref="BuildRealtimeAudioFrame"/> constructs the JSON for a single
///         <c>realtimeInput.audio</c> frame from 16-bit PCM little-endian bytes.</item>
///   <item><see cref="TryParseServerFrame"/> parses one inbound JSON frame into a typed
///         <see cref="GeminiServerMessage"/>. Returns <c>false</c> on malformed input or
///         unsupported field shapes; A6 owns the failure policy.</item>
/// </list>
/// <para>
/// Audio is assumed to be 16-bit PCM little-endian at the protocol boundary. Float → PCM16
/// conversion belongs in the engine layer; the protocol does not know about <see cref="float"/>.
/// </para>
/// </remarks>
internal static class GeminiLiveTranslateProtocol
{
    /// <summary>The MIME type that Gemini Live Translate expects for raw 16-bit PCM input.</summary>
    internal const string PcmMimeType = "audio/pcm;rate=16000";

    /// <summary>
    /// Builds the JSON for the initial setup frame that opens a Gemini Live Translate session.
    /// </summary>
    /// <param name="model">
    /// The model identifier as the server expects it in the setup frame — the docs require the
    /// <c>models/</c> prefix (for example <c>models/gemini-3.5-live-translate-preview</c>).
    /// </param>
    /// <param name="targetLanguageCode">
    /// The BCP-47 target language code passed in <c>translationConfig.targetLanguageCode</c>
    /// (for example <c>fil</c> for Filipino / Tagalog). The App-side ISO 639-1 code is mapped
    /// to a BCP-47 code by <see cref="GeminiLiveTranslateEngineOptions.ResolveTargetLanguageCode"/>
    /// before reaching the protocol layer.
    /// </param>
    /// <param name="echoTargetLanguage">
    /// When <c>true</c>, the server attaches the target language tag to the output audio frames
    /// so downstream consumers know which language is being spoken. We default to <c>false</c>:
    /// the caption pipeline carries the language itself, and the audio side-channel is ignored.
    /// </param>
    /// <returns>The JSON document to send as the first frame of the session.</returns>
    /// <remarks>
    /// <para>
    /// STATUS (2026-08-08, verified against Google's
    /// <see href="https://ai.google.dev/gemini-api/docs/live-api/live-translate"/> docs):
    /// Live Translate is "audio restricted" and rejects <c>systemInstruction</c> outright — the
    /// docs describe it as "translation only. Pure low-latency translation; no support for tools
    /// or instructions." We intentionally do NOT expose a system-instruction parameter on this
    /// method. Output modality is fixed to <c>AUDIO</c>; the translated text arrives on the
    /// <c>outputAudioTranscription</c> side-channel parsed in <see cref="TryBuildServerContent"/>.
    /// </para>
    /// <para>
    /// ADR-0011: Gemini is the pipeline's ONLY speech engine, so the setup frame also requests
    /// top-level <c>inputAudioTranscription</c> — the source-language transcript of the input
    /// audio, parsed from <c>serverContent.inputTranscription.text</c>. The top-level placement is
    /// real-wire-proven (2026-08-09 A/B run: the field is accepted at the setup top level and is
    /// translation-neutral; nesting it inside <c>generationConfig</c> is rejected by the server —
    /// see docs/spikes/GEMINI_MODEL_DISCOVERY.md Round 3). Whether the server actually streams
    /// <c>inputTranscription</c> texts back remains a release-gate verification.
    /// </para>
    /// </remarks>
    internal static string BuildSetupFrame(string model, string targetLanguageCode, bool echoTargetLanguage = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguageCode);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("setup");
            writer.WriteString("model", model);

            writer.WriteStartObject("generationConfig");
            writer.WriteStartArray("responseModalities");
            writer.WriteStringValue("AUDIO");
            writer.WriteEndArray();

            // translationConfig belongs inside generationConfig per Google's Live Translate docs.
            writer.WriteStartObject("translationConfig");
            writer.WriteString("targetLanguageCode", targetLanguageCode);
            writer.WriteBoolean("echoTargetLanguage", echoTargetLanguage);
            writer.WriteEndObject();

            writer.WriteEndObject(); // generationConfig

            // outputAudioTranscription is a TOP-LEVEL sibling of model + generationConfig on the
            // BidiGenerateContentSetup message — NOT nested inside generationConfig. The real
            // server rejects `outputAudioTranscription` at `setup.generation_config` with
            // `Unknown name "outputAudioTranscription" at 'setup.generation_config': Cannot find
            // field` (2026-08-08 spike). The Docs WebSocket example misleadingly shows the field
            // next to translationConfig, but the field is at the setup top level per the REST
            // reference at https://ai.google.dev/api/live.
            writer.WriteStartObject("outputAudioTranscription");
            writer.WriteEndObject();

            // inputAudioTranscription is the matching top-level field for transcribing the input
            // audio back to us — the pipeline's ONLY source-caption surface since ADR-0011 removed
            // the local Whisper STT. Same path restriction as outputAudioTranscription: top-level
            // sibling of model + generationConfig (the nested generationConfig form is rejected;
            // the 2026-08-09 A/B run proved the top-level form is accepted and translation-neutral).
            writer.WriteStartObject("inputAudioTranscription");
            writer.WriteEndObject();

            writer.WriteEndObject(); // setup
            writer.WriteEndObject(); // root
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Builds the JSON for a single <c>realtimeInput.audio</c> frame. The bytes are base64-encoded
    /// verbatim — A5 does no encoding beyond that and assumes the caller has produced 16-bit
    /// little-endian PCM mono at the documented sample rate.
    /// </summary>
    /// <param name="pcm16LittleEndian">Raw PCM samples in 16-bit signed little-endian format.</param>
    /// <returns>The JSON document to send on the WebSocket text channel.</returns>
    internal static string BuildRealtimeAudioFrame(ReadOnlySpan<byte> pcm16LittleEndian)
    {
        // Utf8JsonWriter requires a non-empty path; write through a MemoryStream so we can hand the
        // exact base64 substring to JsonWriter without an intermediate string allocation.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("realtimeInput");
            writer.WriteStartObject("audio");
            writer.WriteString("mimeType", PcmMimeType);
            writer.WriteBase64String("data", pcm16LittleEndian);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parses one inbound JSON frame into a typed <see cref="GeminiServerMessage"/>. Returns
    /// <c>false</c> when the document is malformed JSON, is missing required fields, or carries
    /// unsupported values — callers should treat that as a single protocol error and continue
    /// processing the next frame.
    /// </summary>
    /// <param name="json">The raw UTF-8 text of a single server frame.</param>
    /// <param name="message">The parsed message on success; <c>null</c> on failure.</param>
    /// <param name="error">A short description of the failure when the method returns <c>false</c>.</param>
    /// <returns><c>true</c> on a successful parse; <c>false</c> otherwise.</returns>
    internal static bool TryParseServerFrame(string json, out GeminiServerMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Empty frame.";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"Malformed JSON: {ex.Message}";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Top-level JSON must be an object.";
                return false;
            }

            JsonElement root = document.RootElement;

            // Error frame first: a server-emitted `error` is a valid Gemini frame, not a malformed
            // protocol document. A6 maps its code/status into LiveTranslationErrorKind.
            if (root.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.Object)
            {
                if (TryBuildErrorFrame(errorElement, out var errFrame, out error))
                {
                    message = errFrame;
                    return true;
                }

                return false;
            }

            if (root.TryGetProperty("serverContent", out JsonElement serverContent) && serverContent.ValueKind == JsonValueKind.Object)
            {
                if (TryBuildServerContent(serverContent, out var content, out error))
                {
                    message = content;
                    return true;
                }

                return false;
            }

            // setupComplete arrives alone at the start of a session. Accept either the documented
            // empty-object shape (`"setupComplete":{}`) or the null shape some Gemini responses use
            // (`"setupComplete":null`) — the field's presence is the signal, not its value.
            if (root.TryGetProperty("setupComplete", out _))
            {
                message = new GeminiServerMessage.SetupComplete();
                return true;
            }

            if (root.TryGetProperty("goAway", out JsonElement goAway) && goAway.ValueKind == JsonValueKind.Object)
            {
                message = new GeminiServerMessage.GoAway();
                return true;
            }

            // sessionResumptionUpdate is a documented Live API server message
            // (https://ai.google.dev/api/live#sessionresumptionupdate). Google emits it on sessions
            // configured for resumption, carrying an opaque resumption handle. Live Translate does
            // not accept `sessionResumption` configuration today, so this frame is informational
            // on our surface — but the server still sends it, and A5 MUST recognize it so the
            // session is not torn down (real-wire spike 2026-08-08: the server emitted this frame
            // after the final translation; we incorrectly killed the session).
            if (root.TryGetProperty("sessionResumptionUpdate", out JsonElement sessionResumption)
                && sessionResumption.ValueKind == JsonValueKind.Object)
            {
                if (TryBuildSessionResumptionUpdate(sessionResumption, out var resumptionUpdate, out error))
                {
                    message = resumptionUpdate;
                    return true;
                }

                return false;
            }

            error = DescribeUnknownTopLevelFrame(root);
            return false;
        }
    }

    private static bool TryBuildSessionResumptionUpdate(
        JsonElement sessionResumption,
        out GeminiServerMessage.SessionResumptionUpdate update,
        out string? error)
    {
        update = null!;
        error = null;

        bool resumable = false;
        string? newHandle = null;

        if (sessionResumption.TryGetProperty("resumable", out JsonElement resumableElement)
            && (resumableElement.ValueKind == JsonValueKind.True || resumableElement.ValueKind == JsonValueKind.False))
        {
            resumable = resumableElement.ValueKind == JsonValueKind.True;
        }

        if (sessionResumption.TryGetProperty("newHandle", out JsonElement newHandleElement)
            && newHandleElement.ValueKind == JsonValueKind.String)
        {
            newHandle = newHandleElement.GetString();
        }

        update = new GeminiServerMessage.SessionResumptionUpdate(newHandle, resumable);
        return true;
    }

    /// <summary>
    /// Build a structural-only description of an inbound top-level object that did not match any
    /// known Gemini Live Translate frame shape. We intentionally do NOT include the payload bytes —
    /// only property names and their JSON value kinds — so the diagnostic is safe to log (no
    /// audio/byte leakage) and tells us exactly what A5 needs to learn to parse the next time.
    /// </summary>
    /// <remarks>
    /// This is the spike-driven failure path: when the server sends a frame shape we don't yet
    /// recognize (for example a new Live API message type added by Google, or a session-lifecycle
    /// frame we haven't seen before), we want to learn the identity of the unknown field, not its
    /// contents. The next round's fix is either to add a new <see cref="GeminiServerMessage"/>
    /// case or to extend <see cref="TryParseServerFrame"/> to match the documented shape.
    /// </remarks>
    private static string DescribeUnknownTopLevelFrame(JsonElement root)
    {
        var properties = new List<string>(root.EnumerateObject().Count());
        foreach (JsonProperty property in root.EnumerateObject())
        {
            properties.Add($"{property.Name}:{property.Value.ValueKind}");
        }

        return properties.Count == 0
            ? "Unrecognized top-level frame (empty object; no serverContent, error, setupComplete, or goAway)."
            : "Unrecognized top-level frame (no serverContent, error, setupComplete, or goAway); " +
              $"topLevelKeys=[{string.Join(", ", properties)}]";
    }

    private static bool TryBuildServerContent(JsonElement serverContent, out GeminiServerMessage.ServerContent content, out string? error)
    {
        content = null!;
        error = null;

        bool turnComplete = serverContent.TryGetProperty("turnComplete", out JsonElement turnCompleteElement)
                            && turnCompleteElement.ValueKind == JsonValueKind.True;

        string? partialText = null;
        bool partial = false;

        // modelTurn.token — newer wire format: each token is a separate frame with a `partial`
        // boolean. We expose IsPartial on the message so A6 can decide whether to forward.
        if (serverContent.TryGetProperty("modelTurn", out JsonElement modelTurn)
            && modelTurn.ValueKind == JsonValueKind.Object
            && modelTurn.TryGetProperty("parts", out JsonElement parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            partial = serverContent.TryGetProperty("partial", out JsonElement partialElement)
                      && partialElement.ValueKind == JsonValueKind.True;

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (part.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    partialText = textElement.GetString();
                    break;
                }
            }
        }

        // outputTranscription is the canonical channel on Live Translate. Same IsPartial contract.
        if (serverContent.TryGetProperty("outputTranscription", out JsonElement transcription)
            && transcription.ValueKind == JsonValueKind.Object)
        {
            partial = serverContent.TryGetProperty("partial", out JsonElement partialElement)
                      && partialElement.ValueKind == JsonValueKind.True;

            if (transcription.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                partialText = textElement.GetString();
            }
        }

        // inputTranscription is the source-language transcript of the input audio — the pipeline's
        // ONLY source-caption surface since ADR-0011 removed the local Whisper STT. Same shape as
        // outputTranscription ({ text: string }) and same frame-level `partial` flag contract.
        string? inputText = null;
        bool inputPartial = false;
        if (serverContent.TryGetProperty("inputTranscription", out JsonElement inputTranscription)
            && inputTranscription.ValueKind == JsonValueKind.Object)
        {
            inputPartial = serverContent.TryGetProperty("partial", out JsonElement partialElement)
                           && partialElement.ValueKind == JsonValueKind.True;

            if (inputTranscription.TryGetProperty("text", out JsonElement inputTextElement) && inputTextElement.ValueKind == JsonValueKind.String)
            {
                inputText = inputTextElement.GetString();
            }
        }

        content = new GeminiServerMessage.ServerContent(partialText, partial, turnComplete, inputText, inputPartial);
        return true;
    }

    private static bool TryBuildErrorFrame(JsonElement errorElement, out GeminiServerMessage.ErrorFrame frame, out string? error)
    {
        frame = null!;
        error = null;

        int? code = null;
        string? status = null;
        string? message = null;

        if (errorElement.TryGetProperty("code", out JsonElement codeElement) && codeElement.ValueKind == JsonValueKind.Number)
        {
            code = codeElement.GetInt32();
        }

        if (errorElement.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String)
        {
            status = statusElement.GetString();
        }

        if (errorElement.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString();
        }

        frame = new GeminiServerMessage.ErrorFrame(code, status, message);
        return true;
    }
}
