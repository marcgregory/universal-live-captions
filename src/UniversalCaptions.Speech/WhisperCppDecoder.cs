using Whisper.net;

namespace UniversalCaptions.Speech;

/// <summary>
/// <see cref="ISTTDecoder"/> implementation backed by whisper.cpp (via Whisper.net). Owns the
/// model factory/processor lifecycle and the segment decode loop; the streaming engine owns all
/// windowing, trimming, and commit orchestration.
/// </summary>
internal sealed class WhisperCppDecoder : ISTTDecoder
{
    private readonly WhisperEngineOptions _options;

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    public WhisperCppDecoder(WhisperEngineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public void EnsureReady()
    {
        if (_processor is not null)
        {
            return;
        }

        if (!File.Exists(_options.ModelPath))
        {
            throw new FileNotFoundException("Whisper model file not found.", _options.ModelPath);
        }

        _factory = WhisperFactory.FromPath(_options.ModelPath);
        var builder = _factory.CreateBuilder().WithThreads(_options.Threads);
        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            builder = builder.WithLanguage(_options.Language);
        }

        if (_options.MaxSegmentLength.HasValue)
        {
            builder = builder.WithMaxSegmentLength(_options.MaxSegmentLength.Value);
        }

        if (_options.SplitOnWord)
        {
            builder = builder.SplitOnWord();
        }

        _processor = builder.Build();
    }

    /// <inheritdoc />
    public IReadOnlyList<TranscriptSegment> Decode(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        var list = new List<TranscriptSegment>();
        var enumerator = _processor!.ProcessAsync(samples, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var segment = enumerator.Current;
                list.Add(new TranscriptSegment(segment.Text, segment.Start, segment.End));
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return list;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        WhisperProcessor? processor;
        WhisperFactory? factory;
        lock (this)
        {
            processor = _processor;
            factory = _factory;
            _processor = null;
            _factory = null;
        }

        if (processor is not null)
        {
            try
            {
                await processor.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort teardown; native memory is reclaimed when the process exits.
            }
        }

        factory?.Dispose();
    }
}
