using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace UniversalCaptions.Benchmarks.Translation;

/// <summary>
/// A benchmark-only, hand-rolled client for the Gemini Live API translation mode
/// (<c>BidiGenerateContent</c> WebSocket protocol, model
/// <c>gemini-3.5-live-translate-preview</c>). Streams raw 16 kHz / 16-bit / mono PCM audio chunks
/// and surfaces the server's live <c>outputTranscription</c> (the translated audio's transcript —
/// exactly the text a caption overlay would consume) together with input transcription, network
/// byte counts and usage metadata. Lives exclusively inside <c>UniversalCaptions.Benchmarks</c> so
/// the frozen production architecture is untouched: this is a removable experiment adapter, not a
/// new translation provider. No Google SDK dependency is introduced.
/// </summary>
internal sealed class GeminiLiveTranslateClient : IAsyncDisposable
{
    /// <summary>A single received transcription update (input or output side of the session).</summary>
    internal sealed record CaptionEvent(string Kind, double ArrivalSec, string Text, string? LanguageCode);

    /// <summary>Aggregate session statistics used for the benchmark table.</summary>
    internal sealed record SessionStats(
        double ConnectSec,
        double SetupCompleteSec,
        long BytesSent,
        long BytesReceived,
        long AudioBytesSentDecoded,
        long AudioBytesReceivedDecoded,
        long InputTokens,
        long OutputTokens,
        int InputCaptionCount,
        int InputUpdateCount,
        int OutputCaptionCount,
        int OutputUpdateCount,
        int TurnCompleteCount,
        int ErrorFrameCount,
        string? SessionError,
        string? OutputLanguageCode,
        double LastMessageSec,
        double LastOutputSec);

    private const int ReceiveBufferBytes = 65536;
    private const int ChunkMs = 100;
    private const string InputMimeType = "audio/pcm;rate=16000";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed class Counters
    {
        internal long BytesSent;
        internal long BytesReceived;
        internal long AudioBytesSentDecoded;
        internal long AudioBytesReceivedDecoded;
        internal long InputTokens;
        internal long OutputTokens;
        internal int InputCaptions;
        internal int InputUpdates;
        internal int OutputCaptions;
        internal int OutputUpdates;
        internal int TurnCompletes;
        internal int ErrorFrames;
        internal string? SessionError;
        internal string? OutputLanguageCode;
        internal double ConnectSec = -1;
        internal double SetupCompleteSec = -1;
        internal double LastMessageSec = -1;
        internal double LastOutputSec = -1;
        internal string CurrentInput = string.Empty;
        internal string CurrentOutput = string.Empty;
    }

    private readonly ClientWebSocket _ws = new();
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _targetLanguageCode;
    private readonly bool _echoTargetLanguage;
    private readonly TimeSpan _tailFlush;
    private bool _disposed;

    public GeminiLiveTranslateClient(
        string apiKey,
        string model,
        string targetLanguageCode,
        bool echoTargetLanguage,
        TimeSpan tailFlush)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey)
            ? throw new ArgumentException("An API key is required.", nameof(apiKey))
            : apiKey;
        _model = model;
        _targetLanguageCode = targetLanguageCode;
        _echoTargetLanguage = echoTargetLanguage;
        _tailFlush = tailFlush;
    }

    /// <summary>
    /// Connects, sends the translation setup, streams <paramref name="pcm16"/> in 100 ms chunks, then
    /// drains the remaining output until no new output arrives for <see cref="_tailFlush"/> (or a
    /// session-end cap) and reports session statistics. Each <see cref="CaptionEvent"/> is raised on
    /// the receive loop with wall-clock seconds relative to <paramref name="sw"/>.
    /// </summary>
    public async Task<SessionStats> StreamAsync(
        byte[] pcm16,
        int sampleRate,
        bool paced,
        Stopwatch sw,
        Action<CaptionEvent> onEvent,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        ArgumentNullException.ThrowIfNull(sw);
        ArgumentNullException.ThrowIfNull(onEvent);

        var counters = new Counters();
        var uri = new Uri(
            $"wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key={Uri.EscapeDataString(_apiKey)}");

        Task sendTask = Task.CompletedTask;
        try
        {
            var connectSw = Stopwatch.StartNew();
            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            connectSw.Stop();
            counters.ConnectSec = sw.Elapsed.TotalSeconds;
            Console.Error.WriteLine($"[GEMINI-DIAG] ws connected in {connectSw.ElapsedMilliseconds} ms");

            var setup = new
            {
                setup = new
                {
                    model = $"models/{_model}",
                    generationConfig = new
                    {
                        responseModalities = new[] { "AUDIO" },
                        translationConfig = new
                        {
                            targetLanguageCode = _targetLanguageCode,
                            echoTargetLanguage = _echoTargetLanguage,
                        },
                    },
                    inputAudioTranscription = new { },
                    outputAudioTranscription = new { },
                },
            };

            await SendTextAsync(JsonSerializer.Serialize(setup, JsonOptions), counters, ct).ConfigureAwait(false);

            var receiveTask = ReceiveLoopAsync(sw, onEvent, counters, ct);
            sendTask = SendAudioAsync(pcm16, sampleRate, paced, counters, ct);
            await sendTask.ConfigureAwait(false);
            Console.Error.WriteLine($"[GEMINI-DIAG] audio feed complete at {sw.Elapsed.TotalSeconds:0.00}s; draining tail for {_tailFlush.TotalSeconds:0}s");

            await DrainTailAsync(receiveTask, counters, sw, ct).ConfigureAwait(false);
            Console.Error.WriteLine($"[GEMINI-DIAG] tail flush complete at {sw.Elapsed.TotalSeconds:0.00}s; closing session");

            await CloseSocketAsync().ConfigureAwait(false);

            try
            {
                await receiveTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exc) when (IsBenignSocketError(exc) || exc is TimeoutException)
            {
                // The receive loop ends once the socket is closed; a bounded wait avoids hanging
                // when the server keeps the connection open after the audio input ends.
            }
        }
        finally
        {
            try
            {
                await CloseSocketAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close.
            }

            try
            {
                await sendTask.ConfigureAwait(false);
            }
            catch
            {
                // Best-effort drain of the send loop.
            }
        }

        return new SessionStats(
            counters.ConnectSec,
            counters.SetupCompleteSec,
            counters.BytesSent,
            counters.BytesReceived,
            counters.AudioBytesSentDecoded,
            counters.AudioBytesReceivedDecoded,
            counters.InputTokens,
            counters.OutputTokens,
            counters.InputCaptions,
            counters.InputUpdates,
            counters.OutputCaptions,
            counters.OutputUpdates,
            counters.TurnCompletes,
            counters.ErrorFrames,
            counters.SessionError,
            counters.OutputLanguageCode,
            counters.LastMessageSec,
            counters.LastOutputSec);
    }

    private async Task SendAudioAsync(byte[] pcm16, int sampleRate, bool paced, Counters counters, CancellationToken ct)
    {
        int bytesPerMs = Math.Max(1, sampleRate * 2 / 1000);
        int chunkBytes = Math.Max(1, bytesPerMs * ChunkMs);
        var pacedSw = paced ? Stopwatch.StartNew() : null;
        int index = 0;
        while (index < pcm16.Length)
        {
            int count = Math.Min(chunkBytes, pcm16.Length - index);
            byte[] chunk = new byte[count];
            Array.Copy(pcm16, index, chunk, 0, count);

            var msg = new
            {
                realtimeInput = new
                {
                    audio = new { data = Convert.ToBase64String(chunk), mimeType = InputMimeType },
                },
            };
            await SendTextAsync(JsonSerializer.Serialize(msg, JsonOptions), counters, ct).ConfigureAwait(false);
            counters.AudioBytesSentDecoded += count;

            index += count;
            if (pacedSw is not null)
            {
                double targetMs = (index / (double)chunkBytes) * ChunkMs;
                double elapsedMs = pacedSw.Elapsed.TotalMilliseconds;
                int remaining = (int)(targetMs - elapsedMs);
                if (remaining > 0)
                {
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(
        Stopwatch sw,
        Action<CaptionEvent> onEvent,
        Counters counters,
        CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferBytes];
        using var messageStream = new MemoryStream();
        while (_ws.State == WebSocketState.Open)
        {
            messageStream.SetLength(0);
            ValueWebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await _ws.ReceiveAsync(new Memory<byte>(buffer), ct).ConfigureAwait(false);
                    counters.BytesReceived += result.Count;
                    if (result.Count > 0)
                    {
                        messageStream.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                counters.ErrorFrames++;
                counters.SessionError ??= $"WebSocket receive failed: {exc.Message}";
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (_ws.CloseStatus is not WebSocketCloseStatus.NormalClosure)
                {
                    counters.SessionError ??= $"Server closed WebSocket ({_ws.CloseStatus}) {_ws.CloseStatusDescription}".Trim();
                }

                break;
            }

            if (messageStream.Length == 0)
            {
                continue;
            }

            string frameText = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
            Volatile.Write(ref counters.LastMessageSec, sw.Elapsed.TotalSeconds);
            if (counters.SessionError is not null)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(frameText);
                var root = doc.RootElement;

                if (root.TryGetProperty("setupComplete", out _))
                {
                    counters.SetupCompleteSec = sw.Elapsed.TotalSeconds;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    counters.ErrorFrames++;
                    counters.SessionError = ReadError(error);
                    continue;
                }

                if (root.TryGetProperty("goAway", out var goAway))
                {
                    string reason = goAway.TryGetProperty("reason", out var r) ? r.GetString() ?? "goAway" : "goAway";
                    counters.SessionError ??= $"Server goAway: {reason}";
                    continue;
                }

                if (root.TryGetProperty("usageMetadata", out var usage))
                {
                    if (usage.TryGetProperty("promptTokenCount", out var prompt) && prompt.TryGetInt64(out long p))
                    {
                        counters.InputTokens += p;
                    }

                    if (usage.TryGetProperty("candidatesTokenCount", out var cand) && cand.TryGetInt64(out long c))
                    {
                        counters.OutputTokens += c;
                    }
                }

                if (root.TryGetProperty("serverContent", out var content))
                {
                    if (content.TryGetProperty("turnComplete", out var turnComplete) && turnComplete.ValueKind == JsonValueKind.True)
                    {
                        counters.TurnCompletes++;
                        if (counters.CurrentOutput.Length > 0)
                        {
                            onEvent(new CaptionEvent("output-final", sw.Elapsed.TotalSeconds, counters.CurrentOutput, counters.OutputLanguageCode));
                            counters.CurrentOutput = string.Empty;
                        }

                        if (counters.CurrentInput.Length > 0)
                        {
                            onEvent(new CaptionEvent("input-final", sw.Elapsed.TotalSeconds, counters.CurrentInput, null));
                            counters.CurrentInput = string.Empty;
                        }
                    }

                    if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.ValueKind == JsonValueKind.True)
                    {
                        onEvent(new CaptionEvent("interrupted", sw.Elapsed.TotalSeconds, string.Empty, null));
                    }

                    if (content.TryGetProperty("inputTranscription", out var inputTrans))
                    {
                        string text = ReadText(inputTrans);
                        if (text.Length > 0)
                        {
                            if (IsContinuation(counters.CurrentInput, text))
                            {
                                counters.CurrentInput = text;
                                counters.InputUpdates++;
                                onEvent(new CaptionEvent("input-update", sw.Elapsed.TotalSeconds, text, ReadLanguage(inputTrans)));
                            }
                            else
                            {
                                counters.CurrentInput = text;
                                counters.InputCaptions++;
                                onEvent(new CaptionEvent("input-new", sw.Elapsed.TotalSeconds, text, ReadLanguage(inputTrans)));
                            }
                        }
                    }

                    if (content.TryGetProperty("outputTranscription", out var outputTrans))
                    {
                        string text = ReadText(outputTrans);
                        if (text.Length > 0)
                        {
                            string? lang = ReadLanguage(outputTrans);
                            counters.OutputLanguageCode = lang ?? counters.OutputLanguageCode;
                            Volatile.Write(ref counters.LastOutputSec, sw.Elapsed.TotalSeconds);
                            if (IsContinuation(counters.CurrentOutput, text))
                            {
                                counters.CurrentOutput = text;
                                counters.OutputUpdates++;
                                onEvent(new CaptionEvent("output-update", sw.Elapsed.TotalSeconds, text, lang));
                            }
                            else
                            {
                                counters.CurrentOutput = text;
                                counters.OutputCaptions++;
                                onEvent(new CaptionEvent("output-new", sw.Elapsed.TotalSeconds, text, lang));
                            }
                        }
                    }

                    if (content.TryGetProperty("modelTurn", out var modelTurn) &&
                        modelTurn.TryGetProperty("parts", out var parts) &&
                        parts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.TryGetProperty("inlineData", out var inline) &&
                                inline.TryGetProperty("data", out var data) &&
                                data.ValueKind == JsonValueKind.String)
                            {
                                int len = data.GetString()?.Length ?? 0;
                                counters.AudioBytesReceivedDecoded += (long)(len * 3L / 4L);
                            }
                        }
                    }
                }
            }
            catch (JsonException exc)
            {
                counters.ErrorFrames++;
                counters.SessionError ??= $"Invalid server frame: {exc.Message}";
            }
        }
    }

    private async Task DrainTailAsync(
        Task receiveLoop,
        Counters counters,
        Stopwatch sw,
        CancellationToken ct)
    {
        double hardDeadline = sw.Elapsed.TotalSeconds + Math.Max(15.0, _tailFlush.TotalSeconds * 3);
        while (!receiveLoop.IsCompleted)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            if (sw.Elapsed.TotalSeconds >= hardDeadline)
            {
                break;
            }

            double lastOutput = Volatile.Read(ref counters.LastOutputSec);
            if (lastOutput >= 0 && sw.Elapsed.TotalSeconds - lastOutput > _tailFlush.TotalSeconds)
            {
                break;
            }
        }
    }

    private async Task SendTextAsync(string json, Counters counters, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        counters.BytesSent += payload.Length;
    }

    private async Task CloseSocketAsync()
    {
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "benchmark complete", timeout.Token).ConfigureAwait(false);
        }
    }

    private static bool IsContinuation(string accumulated, string next) =>
        accumulated.Length > 0 && next.StartsWith(accumulated, StringComparison.Ordinal) && next.Length > accumulated.Length;

    private static string ReadText(JsonElement element)
    {
        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? ReadLanguage(JsonElement element)
    {
        if (element.TryGetProperty("languageCode", out var lang) && lang.ValueKind == JsonValueKind.String)
        {
            return lang.GetString();
        }

        return null;
    }

    private static string ReadError(JsonElement error)
    {
        string? code = null;
        if (error.TryGetProperty("code", out var codeEl))
        {
            code = codeEl.ToString();
        }

        string message = error.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString() ?? string.Empty
            : string.Empty;
        string status = error.TryGetProperty("status", out var statusEl) ? statusEl.ToString() : string.Empty;
        return string.Join(" | ", new[] { code, status, message }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static bool IsBenignSocketError(Exception exc) =>
        exc is WebSocketException or OperationCanceledException
        || exc.InnerException is WebSocketException or IOException;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _ws.Dispose();
        }
        catch
        {
            // Best-effort disposal.
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
