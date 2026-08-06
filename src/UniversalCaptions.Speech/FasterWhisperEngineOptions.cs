namespace UniversalCaptions.Speech;

/// <summary>
/// Configuration for the faster-whisper decoder (a persistent Python child process running the
/// bundled <c>faster_whisper_worker.py</c>). Windowing/commit fields mirror
/// <see cref="WhisperEngineOptions"/> so the shared streaming engine drives the decoder with the
/// same orchestration as whisper.cpp.
/// </summary>
public sealed class FasterWhisperEngineOptions
{
    /// <summary>Path to the Python interpreter hosting faster-whisper.</summary>
    public string PythonExecutablePath { get; init; } = "python";

    /// <summary>Optional explicit path to <c>faster_whisper_worker.py</c>; null resolves the bundled copy.</summary>
    public string? ServerScriptPath { get; init; }

    /// <summary>faster-whisper model name (for example, "small", "tiny", "base").</summary>
    public string Model { get; init; } = "small";

    /// <summary>CTranslate2 compute type ("int8", "float16", "float32").</summary>
    public string ComputeType { get; init; } = "int8";

    /// <summary>Number of inference threads for the Python worker.</summary>
    public int Threads { get; init; } = Environment.ProcessorCount;

    /// <summary>Beam size for the decoder (larger = more accurate, slower).</summary>
    public int BeamSize { get; init; } = 5;

    /// <summary>How long to wait for the worker process to start and load its model.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>How long to wait for a decode round-trip to complete.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Optional language code hint (for example, "tl"). Null lets faster-whisper auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>Sample rate the engine accepts. faster-whisper requires 16 kHz mono.</summary>
    public int SampleRate { get; init; } = 16_000;

    /// <summary>Maximum audio buffered per window epoch before committed audio is dropped from the front.</summary>
    public TimeSpan WindowDuration { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>How much new audio must arrive before the next decode pass.</summary>
    public TimeSpan DecodeInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Audio at the end of each window always kept in the buffer (never trimmed) so the in-progress hypothesis survives.</summary>
    public TimeSpan CommitOverlap { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How much audio must accumulate before the first decode pass.</summary>
    public TimeSpan MinimumAudioBeforeFirstDecode { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many consecutive decode passes must confirm identical text before it is committed as a
    /// final transcript. Must be at least 2. Mirrors <see cref="WhisperEngineOptions.StabilityWindow"/>.
    /// </summary>
    public int StabilityWindow { get; init; } = 2;

    /// <summary>Maximum extra time a stable prefix waits for a segment boundary before the bounded fallback commits it.</summary>
    public TimeSpan BoundaryWaitBudget { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How much in-progress speech must accumulate before the native streaming engine decodes the
    /// current segment buffer and raises a live <c>PartialTranscriptAvailable</c> while the speaker
    /// is still talking. Zero disables live partials (the Slice 10/11 FINAL-only behavior). The
    /// App enables a 1 s cadence for the <c>fasterwhisper-native</c> path.
    /// </summary>
    public TimeSpan PartialDecodeInterval { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum tail of the in-progress segment handed to a partial decode. Bounds the partial decode
    /// cost so a live partial never delays the FINAL for the segment by more than one short decode.
    /// Ignored when <see cref="PartialDecodeInterval"/> is zero.
    /// </summary>
    public TimeSpan PartialDecodeWindow { get; init; } = TimeSpan.FromSeconds(4);
}
