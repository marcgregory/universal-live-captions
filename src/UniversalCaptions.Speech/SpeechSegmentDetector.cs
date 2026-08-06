using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.Speech;

/// <summary>
/// A completed speech segment: the buffered mono PCM samples and the capture time of the segment's
/// first sample, used for latency measurement and final-transcript ordering.
/// </summary>
internal sealed record CompletedSegment(float[] Samples, DateTime CapturedAtUtc);

/// <summary>
/// Turns a per-chunk voice-activity decision into whole speech segments. Idle → buffering speech →
/// trailing silence hangover → emit one segment. The hangover bridges short intra-sentence pauses so a
/// sentence is committed as a single FINAL; the maximum-duration cap bounds how stale a segment can get
/// during continuous speech. This class only tracks state; it never decodes audio.
/// </summary>
internal sealed class SpeechSegmentDetector
{
    private readonly SpeechSegmentDetectorOptions _options;
    private readonly List<float> _buffer = new();
    private DateTime _segmentStartUtc;
    private TimeSpan _speechDuration;
    private TimeSpan _hangoverRemaining;
    private bool _inSpeech;
    private bool _inHangover;

    public SpeechSegmentDetector(SpeechSegmentDetectorOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SampleRate must be positive.");
        }

        if (options.MinSpeechDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MinSpeechDuration must not be negative.");
        }

        if (options.SilenceHangover < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SilenceHangover must not be negative.");
        }

        if (options.MaxSegmentDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxSegmentDuration must be positive.");
        }

        if (options.MaxSegmentDuration < options.MinSpeechDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaxSegmentDuration must be at least MinSpeechDuration.");
        }
    }

    /// <summary>
    /// Evaluates one chunk against its voice-activity decision. The chunk must be mono PCM at the
    /// configured sample rate. Returns a completed segment when the chunk closed one, otherwise null.
    /// </summary>
    public CompletedSegment? Process(AudioChunk chunk, bool isSpeech)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Samples.Length == 0)
        {
            return null;
        }

        float[] samples = chunk.Samples;
        var duration = TimeSpan.FromSeconds((double)samples.Length / _options.SampleRate);

        if (_inSpeech)
        {
            if (isSpeech)
            {
                Append(samples);
                _speechDuration += duration;
                if (_speechDuration >= _options.MaxSegmentDuration)
                {
                    return Complete();
                }

                return null;
            }

            Append(samples);
            _inSpeech = false;
            _inHangover = true;
            _hangoverRemaining = _options.SilenceHangover;
            return null;
        }

        if (_inHangover)
        {
            if (isSpeech)
            {
                Append(samples);
                _speechDuration += duration;
                _inHangover = false;
                _inSpeech = true;
                return null;
            }

            Append(samples);
            _hangoverRemaining -= duration;
            if (_hangoverRemaining <= TimeSpan.Zero)
            {
                return Complete();
            }

            return null;
        }

        if (isSpeech)
        {
            _segmentStartUtc = chunk.CapturedAtUtc;
            _buffer.Clear();
            Append(samples);
            _speechDuration = duration;
            _inSpeech = true;
        }

        return null;
    }

    /// <summary>
    /// Snapshots the trailing <paramref name="maxSamples"/> samples of the in-progress segment so a
    /// streaming engine can decode a live partial while the speaker is still talking. Returns false
    /// when no speech is currently buffered (idle) or during the trailing-silence hangover — at that
    /// point a FINAL is imminent and a partial decode would be wasted. The returned window's
    /// <paramref name="capturedAtUtc"/> is the capture time of the window's first sample, so partial
    /// latency can be measured from the audio the partial actually covers.
    /// </summary>
    /// <param name="maxSamples">The maximum number of samples to return (a bound on partial-decode cost).</param>
    /// <param name="samples">A copy of the trailing window of the in-progress segment.</param>
    /// <param name="capturedAtUtc">Capture time of the first sample of <paramref name="samples"/>.</param>
    /// <returns>True when a partial window was produced.</returns>
    public bool TryGetPartial(int maxSamples, out float[] samples, out DateTime capturedAtUtc)
    {
        if (maxSamples <= 0 || !_inSpeech || _buffer.Count == 0)
        {
            samples = Array.Empty<float>();
            capturedAtUtc = default;
            return false;
        }

        int startIndex = Math.Max(0, _buffer.Count - maxSamples);
        int length = _buffer.Count - startIndex;
        samples = new float[length];
        _buffer.CopyTo(startIndex, samples, 0, length);
        capturedAtUtc = _segmentStartUtc + TimeSpan.FromSeconds((double)startIndex / _options.SampleRate);
        return true;
    }

    /// <summary>
    /// Closes the in-progress segment (used when the audio stream ends). A partial segment shorter
    /// than <see cref="SpeechSegmentDetectorOptions.MinSpeechDuration"/> is discarded. Safe to call
    /// when idle.
    /// </summary>
    public CompletedSegment? Flush()
    {
        if (!_inSpeech && !_inHangover)
        {
            Reset();
            return null;
        }

        return Complete();
    }

    /// <summary>Drops all buffered state. Safe to call at any time.</summary>
    public void Reset() => ResetState();

    private CompletedSegment? Complete()
    {
        if (_buffer.Count == 0)
        {
            ResetState();
            return null;
        }

        if (_speechDuration < _options.MinSpeechDuration)
        {
            ResetState();
            return null;
        }

        var samples = _buffer.ToArray();
        var capturedAtUtc = _segmentStartUtc;
        ResetState();
        return new CompletedSegment(samples, capturedAtUtc);
    }

    private void Append(float[] samples) => _buffer.AddRange(samples);

    private void ResetState()
    {
        _buffer.Clear();
        _inSpeech = false;
        _inHangover = false;
        _speechDuration = TimeSpan.Zero;
        _hangoverRemaining = TimeSpan.Zero;
        _segmentStartUtc = default;
    }
}
