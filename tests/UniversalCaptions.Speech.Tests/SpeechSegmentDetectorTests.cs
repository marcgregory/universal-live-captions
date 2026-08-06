using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies the <see cref="SpeechSegmentDetector"/> state machine: speech onset, hangover bridging,
/// the maximum-duration cap, short-burst discarding, and flush-at-stop behavior. The detector is
/// driven directly with scripted voice-activity decisions, so these tests need no VAD or model.
/// </summary>
public sealed class SpeechSegmentDetectorTests
{
    private const int Rate = 16_000;
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SpeechSegmentDetectorOptions Options(
        TimeSpan? minSpeechDuration = null,
        TimeSpan? silenceHangover = null,
        TimeSpan? maxSegmentDuration = null) => new()
        {
            SampleRate = Rate,
            MinSpeechDuration = minSpeechDuration ?? TimeSpan.FromMilliseconds(300),
            SilenceHangover = silenceHangover ?? TimeSpan.FromMilliseconds(700),
            MaxSegmentDuration = maxSegmentDuration ?? TimeSpan.FromSeconds(8),
        };

    private static AudioChunk Chunk(double seconds, bool speech, long sequence)
    {
        var samples = speech
            ? Enumerable.Repeat(0.5f, (int)(Rate * seconds)).ToArray()
            : new float[(int)(Rate * seconds)];
        return new AudioChunk(samples, new AudioFormat(Rate, 1, 32), Base + TimeSpan.FromSeconds(0.5 * (sequence - 1)), sequence);
    }

    [Fact]
    public void SpeechThenSilence_EmitsOneSegmentCoveringBoth()
    {
        var detector = new SpeechSegmentDetector(Options());

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 2), isSpeech: false));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 3), isSpeech: false));

        CompletedSegment? segment = detector.Process(Chunk(0.5, speech: false, 4), isSpeech: false);

        Assert.NotNull(segment);
        // 0.5 s speech + up to a chunk of trailing silence beyond the hangover, all buffered.
        Assert.Equal(4 * (int)(Rate * 0.5), segment!.Samples.Length);
        Assert.Equal(Base, segment.CapturedAtUtc);
    }

    [Fact]
    public void SpeechResumedWithinHangover_IsOneSegment()
    {
        var detector = new SpeechSegmentDetector(Options());

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.4, speech: false, 2), isSpeech: false));
        Assert.Null(detector.Process(Chunk(0.5, speech: true, 3), isSpeech: true));

        CompletedSegment? segment = detector.Flush();

        Assert.NotNull(segment);
        Assert.Equal((int)(Rate * (0.5 + 0.4 + 0.5)), segment!.Samples.Length);
        Assert.Equal(Base, segment.CapturedAtUtc);
    }

    [Fact]
    public void TwoSpeechBursts_WithSilence_ProduceTwoSegmentsInOrder()
    {
        var detector = new SpeechSegmentDetector(Options());

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 2), isSpeech: false));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 3), isSpeech: false));
        CompletedSegment? first = detector.Process(Chunk(0.5, speech: false, 4), isSpeech: false);

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 5), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 6), isSpeech: false));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 7), isSpeech: false));
        CompletedSegment? second = detector.Process(Chunk(0.5, speech: false, 8), isSpeech: false);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(Base, first!.CapturedAtUtc);
        Assert.Equal(Base + TimeSpan.FromSeconds(2), second!.CapturedAtUtc);
    }

    [Fact]
    public void ContinuousSpeech_CapsAtMaxSegmentDuration_ThenContinuesBuffering()
    {
        var detector = new SpeechSegmentDetector(Options(maxSegmentDuration: TimeSpan.FromSeconds(2)));

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.5, speech: true, 2), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.5, speech: true, 3), isSpeech: true));
        CompletedSegment? capped = detector.Process(Chunk(0.5, speech: true, 4), isSpeech: true);

        Assert.NotNull(capped);
        Assert.Equal((int)(Rate * 2.0), capped!.Samples.Length);

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 5), isSpeech: true));
        CompletedSegment? next = detector.Flush();
        Assert.NotNull(next);
        Assert.Equal((int)(Rate * 0.5), next!.Samples.Length);
        Assert.Equal(Base + TimeSpan.FromSeconds(2), next.CapturedAtUtc);
    }

    [Fact]
    public void ShortSpeechBlip_ShorterThanMinSpeechDuration_IsDiscarded()
    {
        var detector = new SpeechSegmentDetector(Options(minSpeechDuration: TimeSpan.FromSeconds(0.5)));

        Assert.Null(detector.Process(Chunk(0.2, speech: true, 1), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 2), isSpeech: false));
        Assert.Null(detector.Process(Chunk(0.5, speech: false, 3), isSpeech: false));
        CompletedSegment? segment = detector.Process(Chunk(0.5, speech: false, 4), isSpeech: false);

        Assert.Null(segment);
    }

    [Fact]
    public void Flush_EmitsInProgressSegment()
    {
        var detector = new SpeechSegmentDetector(Options());

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        CompletedSegment? segment = detector.Flush();

        Assert.NotNull(segment);
        Assert.Equal((int)(Rate * 0.5), segment!.Samples.Length);
    }

    [Fact]
    public void Flush_WhenIdle_ReturnsNull()
    {
        var detector = new SpeechSegmentDetector(Options());

        Assert.Null(detector.Flush());
    }

    [Fact]
    public void Flush_DiscardsTooShortInProgressSegment()
    {
        var detector = new SpeechSegmentDetector(Options(minSpeechDuration: TimeSpan.FromSeconds(1.0)));

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        Assert.Null(detector.Flush());
    }

    [Fact]
    public void Reset_ClearsInProgressSegment()
    {
        var detector = new SpeechSegmentDetector(Options());

        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        detector.Reset();

        Assert.Null(detector.Flush());
    }

    [Fact]
    public void TryGetPartial_ReturnsWholeBufferWhenShorterThanMaxSamples()
    {
        var detector = new SpeechSegmentDetector(Options());
        detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true);

        bool result = detector.TryGetPartial(16_000, out float[] samples, out DateTime capturedAtUtc);

        Assert.True(result);
        Assert.Equal((int)(Rate * 0.5), samples.Length);
        Assert.Equal(Base, capturedAtUtc);
    }

    [Fact]
    public void TryGetPartial_ReturnsTrailingWindowCappedByMaxSamples_WithWindowStartCaptureTime()
    {
        var detector = new SpeechSegmentDetector(Options());
        detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true);
        detector.Process(Chunk(0.5, speech: true, 2), isSpeech: true);
        detector.Process(Chunk(0.5, speech: true, 3), isSpeech: true);

        bool result = detector.TryGetPartial((int)(Rate * 1.0), out float[] samples, out DateTime capturedAtUtc);

        Assert.True(result);
        Assert.Equal((int)(Rate * 1.0), samples.Length);
        // A 1 s window over 1.5 s of buffered speech starts at the 0.5 s mark of the segment.
        Assert.Equal(Base + TimeSpan.FromSeconds(0.5), capturedAtUtc);
    }

    [Fact]
    public void TryGetPartial_WhenIdle_ReturnsFalse()
    {
        var detector = new SpeechSegmentDetector(Options());

        Assert.False(detector.TryGetPartial(16_000, out _, out _));
    }

    [Fact]
    public void TryGetPartial_DuringHangover_ReturnsFalse()
    {
        // A FINAL is imminent once the trailing-silence hangover begins; a partial decode would be
        // wasted, so the detector refuses to snapshot during hangover.
        var detector = new SpeechSegmentDetector(Options());
        detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true);
        detector.Process(Chunk(0.5, speech: false, 2), isSpeech: false);

        Assert.False(detector.TryGetPartial(16_000, out _, out _));
    }

    [Fact]
    public void TryGetPartial_ZeroMaxSamples_ReturnsFalse()
    {
        var detector = new SpeechSegmentDetector(Options());
        detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true);

        Assert.False(detector.TryGetPartial(0, out _, out _));
    }

    [Fact]
    public void TryGetPartial_AfterSegmentCompletes_ReturnsFalse()
    {
        var detector = new SpeechSegmentDetector(Options(maxSegmentDuration: TimeSpan.FromSeconds(1)));
        Assert.Null(detector.Process(Chunk(0.5, speech: true, 1), isSpeech: true));
        CompletedSegment? capped = detector.Process(Chunk(0.5, speech: true, 2), isSpeech: true);

        Assert.NotNull(capped);
        Assert.False(detector.TryGetPartial(16_000, out _, out _));
    }

    [Fact]
    public void ResumedSpeech_CountsTowardMinSpeechDuration()
    {
        // Speech resumed after a hangover must count toward MinSpeechDuration, otherwise a segment
        // with enough real speech is spuriously discarded as too short.
        var detector = new SpeechSegmentDetector(Options(minSpeechDuration: TimeSpan.FromSeconds(0.5)));

        Assert.Null(detector.Process(Chunk(0.3, speech: true, 1), isSpeech: true));
        Assert.Null(detector.Process(Chunk(0.2, speech: false, 2), isSpeech: false));
        Assert.Null(detector.Process(Chunk(0.2, speech: true, 3), isSpeech: true));

        CompletedSegment? segment = detector.Flush();

        Assert.NotNull(segment);
        Assert.Equal((int)(Rate * 0.7), segment!.Samples.Length);
    }
}
