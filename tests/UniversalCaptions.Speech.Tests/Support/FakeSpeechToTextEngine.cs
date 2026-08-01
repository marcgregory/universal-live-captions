using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech.Tests.Support;

/// <summary>
/// A deterministic <see cref="ISpeechToTextEngine"/> used to test recognition behavior
/// without a real model. Scripts a timeline of transcripts/errors emitted as audio duration
/// accumulates, and exposes direct emit triggers for discrete event tests.
/// </summary>
public sealed class FakeSpeechToTextEngine : ISpeechToTextEngine
{
    private readonly List<ScheduledAction> _schedule = [];
    private int _scheduleIndex;
    private Exception? _startException;
    private SpeechRecognitionError? _startError;

    private sealed record ScheduledAction(
        TimeSpan AtDuration,
        bool IsError,
        string? Text,
        bool IsFinal,
        SpeechRecognitionErrorKind ErrorKind,
        string? ErrorMessage);

    /// <inheritdoc />
    public event EventHandler<PartialTranscript>? PartialTranscriptAvailable;

    /// <inheritdoc />
    public event EventHandler<FinalTranscript>? FinalTranscriptAvailable;

    /// <inheritdoc />
    public event EventHandler<SpeechRecognitionError>? RecognitionFailed;

    /// <inheritdoc />
    public bool IsRecognizing { get; private set; }

    /// <summary>Number of times <see cref="Start"/> has been called.</summary>
    public int StartCount { get; private set; }

    /// <summary>Number of times <see cref="Stop"/> has been called.</summary>
    public int StopCount { get; private set; }

    /// <summary>Total audio duration fed via <see cref="Process"/>.</summary>
    public TimeSpan ProcessedDuration { get; private set; }

    /// <summary>Number of chunks fed via <see cref="Process"/>.</summary>
    public long ProcessedChunks { get; private set; }

    /// <summary>Sequence number of the next transcript to emit.</summary>
    public long NextSequence { get; private set; }

    /// <summary>True once <see cref="Dispose"/> has been called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Makes the next <see cref="Start"/> throw.</summary>
    public void ThrowOnStart(Exception exception) => _startException = exception;

    /// <summary>Makes the next <see cref="Start"/> raise <see cref="RecognitionFailed"/>.</summary>
    public void RaiseErrorOnStart(SpeechRecognitionErrorKind kind, string message) =>
        _startError = new SpeechRecognitionError(kind, message);

    /// <summary>Schedules a partial to be emitted once the accumulated audio duration reaches <paramref name="at"/>.</summary>
    public void SchedulePartial(TimeSpan at, string text) =>
        _schedule.Add(new ScheduledAction(at, false, text, false, default, null));

    /// <summary>Schedules a final to be emitted once the accumulated audio duration reaches <paramref name="at"/>.</summary>
    public void ScheduleFinal(TimeSpan at, string text) =>
        _schedule.Add(new ScheduledAction(at, false, text, true, default, null));

    /// <summary>Schedules an error to be emitted once the accumulated audio duration reaches <paramref name="at"/>.</summary>
    public void ScheduleError(TimeSpan at, SpeechRecognitionErrorKind kind, string message) =>
        _schedule.Add(new ScheduledAction(at, true, null, false, kind, message));

    /// <summary>Raises a partial immediately with the current time.</summary>
    public void EmitPartialNow(string text) =>
        RaisePartial(text, DateTime.UtcNow);

    /// <summary>Raises a final immediately with the current time.</summary>
    public void EmitFinalNow(string text) =>
        RaiseFinal(text, DateTime.UtcNow);

    /// <inheritdoc />
    public void Start()
    {
        StartCount++;
        if (_startException is not null)
        {
            throw _startException;
        }

        if (_startError is not null)
        {
            var error = _startError;
            _startError = null;
            RecognitionFailed?.Invoke(this, error);
            return;
        }

        IsRecognizing = true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        StopCount++;
        IsRecognizing = false;
    }

    /// <inheritdoc />
    public void Process(AudioChunk chunk)
    {
        if (!IsRecognizing)
        {
            return;
        }

        ProcessedChunks++;
        ProcessedDuration += chunk.Duration;

        while (_scheduleIndex < _schedule.Count && _schedule[_scheduleIndex].AtDuration <= ProcessedDuration)
        {
            var action = _schedule[_scheduleIndex++];
            if (action.IsError)
            {
                RecognitionFailed?.Invoke(this, new SpeechRecognitionError(action.ErrorKind, action.ErrorMessage!));
            }
            else if (action.IsFinal)
            {
                RaiseFinal(action.Text!, chunk.CapturedAtUtc);
            }
            else
            {
                RaisePartial(action.Text!, chunk.CapturedAtUtc);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => Disposed = true;

    private void RaisePartial(string text, DateTime capturedAtUtc) =>
        PartialTranscriptAvailable?.Invoke(this, new PartialTranscript(text, capturedAtUtc, DateTime.UtcNow, NextSequence++));

    private void RaiseFinal(string text, DateTime capturedAtUtc) =>
        FinalTranscriptAvailable?.Invoke(this, new FinalTranscript(text, capturedAtUtc, DateTime.UtcNow, NextSequence++));
}
