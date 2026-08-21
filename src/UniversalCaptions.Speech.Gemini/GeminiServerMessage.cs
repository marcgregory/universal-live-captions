namespace UniversalCaptions.Speech.Gemini;

/// <summary>
/// A typed result of parsing one inbound Gemini Live Translate server frame. Sealed hierarchy: each
/// concrete case is a singleton or a record carrying only the fields the engine (A6) needs to act
/// on. The protocol layer never throws on unrecognized input — it returns <c>false</c> from
/// <see cref="GeminiLiveTranslateProtocol.TryParseServerFrame"/> instead, so this type only ever
/// holds successfully-parsed frames.
/// </summary>
internal abstract record GeminiServerMessage
{
    private GeminiServerMessage()
    {
    }

    /// <summary>The server acknowledged the setup frame; the session is open.</summary>
    public sealed record SetupComplete : GeminiServerMessage;

    /// <summary>
    /// A <c>serverContent</c> frame: carries source-language input-transcription text
    /// (<see cref="InputText"/>), translated output text (<see cref="Text"/>), a turn-completion
    /// marker, or any combination. Either text may be <c>null</c> when the server emits a
    /// turn-complete without a final token on that surface.
    /// </summary>
    public sealed record ServerContent(
        string? Text,
        bool IsPartial,
        bool TurnComplete,
        string? InputText = null,
        bool InputIsPartial = false) : GeminiServerMessage;

    /// <summary>The server is about to close the session (time/usage limit).</summary>
    public sealed record GoAway : GeminiServerMessage;

    /// <summary>
    /// A <c>sessionResumptionUpdate</c> frame. Google's Live API sends these when session
    /// resumption is configured on the session; the message carries an opaque resumption handle
    /// (<see cref="NewHandle"/>) when one becomes available, plus a <see cref="Resumable"/> flag
    /// that tells the client whether the latest received tokens are now resumable. Live Translate
    /// does NOT accept <c>sessionResumption</c> configuration today — this frame is informational
    /// on the Live Translate surface — so the engine currently treats it as a no-op (the frame
    /// must not end the session; A6 must continue receiving). See
    /// https://ai.google.dev/api/live#sessionresumptionupdate and
    /// https://ai.google.dev/gemini-api/docs/live-api/session-management.
    /// </summary>
    public sealed record SessionResumptionUpdate(string? NewHandle, bool Resumable) : GeminiServerMessage;

    /// <summary>
    /// A server-emitted error frame. All fields are optional because the Live Translate wire format
    /// is inconsistent across versions; A6 maps whatever is present into
    /// <see cref="UniversalCaptions.Core.Translation.LiveTranslationErrorKind"/>.
    /// </summary>
    public sealed record ErrorFrame(int? Code, string? Status, string? Message) : GeminiServerMessage;
}
