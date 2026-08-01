namespace UniversalCaptions.Speech;

/// <summary>
/// Configuration for <see cref="WhisperSpeechToTextEngine"/>.
/// </summary>
public sealed class WhisperEngineOptions
{
    /// <summary>Path to a whisper.cpp ggml model file (for example, <c>ggml-tiny.bin</c>).</summary>
    public string ModelPath { get; init; } = string.Empty;

    /// <summary>Optional language code (for example, "en"). Null lets Whisper auto-detect.</summary>
    public string? Language { get; init; }

    /// <summary>Number of inference threads.</summary>
    public int Threads { get; init; } = Environment.ProcessorCount;

    /// <summary>Sample rate the engine accepts. Whisper requires 16 kHz mono.</summary>
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
    /// final transcript. Must be at least 2 (smaller values would force every hypothesis to final).
    /// </summary>
    public int StabilityWindow { get; init; } = 3;

    /// <summary>
    /// When set, whisper.cpp is asked to cap each segment to this many characters, producing finer
    /// segment boundaries. Opt-in: behavior should be benchmarked before enabling (see BENCHMARK_REPORT).
    /// </summary>
    public int? MaxSegmentLength { get; init; }

    /// <summary>
    /// When true, whisper.cpp splits segments on word boundaries instead of on the model's default
    /// boundaries. Opt-in: behavior should be benchmarked before enabling (see BENCHMARK_REPORT).
    /// </summary>
    public bool SplitOnWord { get; init; }
}
