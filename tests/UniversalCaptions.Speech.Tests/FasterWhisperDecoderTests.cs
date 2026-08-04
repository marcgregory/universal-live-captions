using UniversalCaptions.Core.Speech;

namespace UniversalCaptions.Speech.Tests;

/// <summary>
/// Verifies <see cref="FasterWhisperDecoder"/>: float-to-int16 conversion of the window before it is
/// sent to the worker, and mapping of worker failures onto the engine's error contract.
/// </summary>
public sealed class FasterWhisperDecoderTests
{
    private static FasterWhisperEngineOptions TestOptions() => new()
    {
        PythonExecutablePath = "python",
        Model = "small",
        SampleRate = 16_000,
        Language = "tl",
    };

    private sealed class RecordingProcess : IFasterWhisperProcess
    {
        public short[]? ReceivedPcm { get; private set; }
        public string? ReceivedLanguage { get; private set; }
        public Exception? ThrowOnStart { get; set; }
        public Exception? ThrowOnTranscribe { get; set; }
        public IReadOnlyList<TranscriptSegment>? Result { get; set; }
        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnStart is not null)
            {
                throw ThrowOnStart;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
            ReadOnlyMemory<short> pcmSamples,
            string? language,
            CancellationToken cancellationToken)
        {
            if (ThrowOnTranscribe is not null)
            {
                throw ThrowOnTranscribe;
            }

            ReceivedPcm = pcmSamples.ToArray();
            ReceivedLanguage = language;
            return Task.FromResult(Result ?? new List<TranscriptSegment>());
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task EnsureReady_WorkerStartupFailure_IsReportedAsModelLoadFailure()
    {
        var process = new RecordingProcess
        {
            ThrowOnStart = new FasterWhisperProcessException(FasterWhisperErrorKind.EngineUnavailable, "no python"),
        };
        await using var decoder = new FasterWhisperDecoder(TestOptions(), process);

        var ex = Assert.Throws<FileNotFoundException>(() => decoder.EnsureReady());
        Assert.Contains("small", ex.Message);
    }

    [Fact]
    public async Task Decode_ConvertsFloatWindowToInt16_AndPassesLanguage()
    {
        var process = new RecordingProcess
        {
            Result = new List<TranscriptSegment> { new("Kumusta", TimeSpan.Zero, TimeSpan.FromSeconds(1.0)) },
        };
        await using var decoder = new FasterWhisperDecoder(TestOptions(), process);

        var samples = new float[] { 0.0f, 1.0f, -1.0f, 0.5f };
        var segments = decoder.Decode(samples, CancellationToken.None);

        Assert.Single(segments);
        Assert.Equal("Kumusta", segments[0].Text);
        Assert.Equal("tl", process.ReceivedLanguage);
        Assert.NotNull(process.ReceivedPcm);
        Assert.Equal(new short[] { 0, short.MaxValue, short.MinValue, (short)(0.5f * short.MaxValue) }, process.ReceivedPcm);
    }

    [Fact]
    public async Task Decode_ClampsOutOfRangeSamples()
    {
        var process = new RecordingProcess();
        await using var decoder = new FasterWhisperDecoder(TestOptions(), process);

        decoder.Decode(new float[] { 1.5f, -2.0f }, CancellationToken.None);

        Assert.Equal(new short[] { short.MaxValue, short.MinValue }, process.ReceivedPcm);
    }

    [Fact]
    public async Task Decode_WorkerFailure_IsReportedAsEngineFailure()
    {
        var process = new RecordingProcess
        {
            ThrowOnTranscribe = new FasterWhisperProcessException(FasterWhisperErrorKind.EngineFailed, "decode crashed"),
        };
        await using var decoder = new FasterWhisperDecoder(TestOptions(), process);

        var ex = Assert.Throws<InvalidOperationException>(() => decoder.Decode(new float[16], CancellationToken.None));
        Assert.Contains("decode crashed", ex.Message);
    }
}
