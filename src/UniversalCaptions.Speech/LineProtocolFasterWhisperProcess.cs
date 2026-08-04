using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace UniversalCaptions.Speech;

/// <summary>
/// An <see cref="IFasterWhisperProcess"/> implementation that spawns a local Python process running
/// the bundled <c>faster_whisper_worker.py</c> and exchanges binary-framed messages over
/// stdin/stdout. The worker loads the model once at startup and stays alive across decodes, so the
/// model-load cost is paid a single time per session.
/// </summary>
internal sealed class LineProtocolFasterWhisperProcess : IFasterWhisperProcess
{
    private const int Magic = 0x46574355; // "UCWF" read as a little-endian int32 (matching the worker's MAGIC_INT)
    private const int Version = 1;

    private readonly FasterWhisperEngineOptions _options;
    private readonly string _scriptPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrLock = new();
    private readonly bool _streamsInjected;

    private Process? _process;
    private Stream? _stdin;
    private Stream? _stdout;
    private bool _disposed;

    public LineProtocolFasterWhisperProcess(FasterWhisperEngineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scriptPath = options.ServerScriptPath ?? ResolveBundledScript();
    }

    /// <summary>
    /// Test seam: builds the process with pre-connected stdin/stdout streams instead of spawning a
    /// real Python child. Enables deterministic protocol-contract tests against a fake worker's byte
    /// stream with no Python/venv/model dependency. The real protocol reader is exercised unchanged.
    /// </summary>
    internal LineProtocolFasterWhisperProcess(FasterWhisperEngineOptions options, Stream stdin, Stream stdout)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scriptPath = options.ServerScriptPath ?? ResolveBundledScript();
        _stdin = stdin ?? throw new ArgumentNullException(nameof(stdin));
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        _streamsInjected = true;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is not null)
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            if (!_streamsInjected)
            {
                StartProcess();
                Console.Error.WriteLine($"[FW-DIAG] worker spawned: {sw.Elapsed.TotalSeconds:F3}s");
            }

            var (status, segmentCount) = await ExchangeRequestAsync(
                pcmSamples: ReadOnlyMemory<short>.Empty,
                language: null,
                _options.StartupTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!_streamsInjected)
            {
                Console.Error.WriteLine($"[FW-DIAG] ping + python import + model load: {sw.Elapsed.TotalSeconds:F3}s (since spawn)");
            }

            if (status != 0)
            {
                Kill();
                throw new FasterWhisperProcessException(
                    FasterWhisperErrorKind.EngineUnavailable,
                    $"The faster-whisper worker reported an error during startup: {GetStderrTail()}");
            }

            if (segmentCount != 0)
            {
                // A ping must not carry segments; any body left in the stream is a protocol error.
                Kill();
                throw new FasterWhisperProcessException(
                    FasterWhisperErrorKind.Protocol,
                    $"The faster-whisper worker returned an invalid startup response. {GetStderrTail()}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(
        ReadOnlyMemory<short> pcmSamples,
        string? language,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sw = Stopwatch.StartNew();
            var segments = await ExchangeSegmentsAsync(pcmSamples, language, _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            Console.Error.WriteLine($"[FW-DIAG] decode round-trip samples={pcmSamples.Length} segments={segments.Count}: {sw.Elapsed.TotalSeconds:F3}s");

            return segments;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        Kill();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void StartProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(_scriptPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_options.Model);
        startInfo.ArgumentList.Add("--compute");
        startInfo.ArgumentList.Add(_options.ComputeType);
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(_options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--beam-size");
        startInfo.ArgumentList.Add(_options.BeamSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        try
        {
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception exc)
        {
            throw new FasterWhisperProcessException(
                FasterWhisperErrorKind.EngineUnavailable,
                $"Failed to start the faster-whisper worker (python: {_options.PythonExecutablePath}, script: {_scriptPath}): {exc.Message}",
                exc);
        }

        _stdin = _process.StandardInput.BaseStream;
        _stdout = _process.StandardOutput.BaseStream;
        _ = Task.Run(() => DrainStderrAsync(_process.StandardError));
    }

    /// <summary>
    /// Sends one request and returns the response header plus the raw segment payload (for startup
    /// pings, where the payload must be empty).
    /// </summary>
    private async Task<(int Status, int SegmentCount)> ExchangeRequestAsync(
        ReadOnlyMemory<short> pcmSamples,
        string? language,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await WriteRequestAsync(pcmSamples, language, cancellationToken).ConfigureAwait(false);
        return await ReadResponseHeaderAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TranscriptSegment>> ExchangeSegmentsAsync(
        ReadOnlyMemory<short> pcmSamples,
        string? language,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await WriteRequestAsync(pcmSamples, language, cancellationToken).ConfigureAwait(false);
        var (status, segmentCount) = await ReadResponseHeaderAsync(timeout, cancellationToken).ConfigureAwait(false);
        return await ReadSegmentsAsync(status, segmentCount, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteRequestAsync(ReadOnlyMemory<short> pcmSamples, string? language, CancellationToken cancellationToken)
    {
        var stdin = _stdin;
        if (stdin is null)
        {
            throw new FasterWhisperProcessException(
                FasterWhisperErrorKind.EngineUnavailable,
                "The faster-whisper worker has not been started.");
        }

        byte[] languageBytes = language is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(language);
        var header = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), _options.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), pcmSamples.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), languageBytes.Length);

        try
        {
            await stdin!.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            if (languageBytes.Length > 0)
            {
                await stdin.WriteAsync(languageBytes, cancellationToken).ConfigureAwait(false);
            }

            if (pcmSamples.Length > 0)
            {
                var pcmBytes = new byte[pcmSamples.Length * 2];
                var pcmSpan = pcmSamples.Span;
                for (int i = 0; i < pcmSpan.Length; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(pcmBytes.AsSpan(i * 2, 2), pcmSpan[i]);
                }

                await stdin.WriteAsync(pcmBytes, cancellationToken).ConfigureAwait(false);
            }

            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Kill();
            throw new FasterWhisperProcessException(
                FasterWhisperErrorKind.Protocol,
                $"The faster-whisper worker protocol write failed: {exc.Message}. {GetStderrTail()}",
                exc);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw;
        }
    }

    private async Task<(int Status, int SegmentCount)> ReadResponseHeaderAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var header = await ReadExactlyAsync(16, timeoutCts.Token).ConfigureAwait(false);
            int magic = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            int status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8, 4));
            int segmentCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));

            if (magic != Magic || version != Version)
            {
                Kill();
                throw new FasterWhisperProcessException(
                    FasterWhisperErrorKind.Protocol,
                    $"The faster-whisper worker returned an invalid response header. {GetStderrTail()}");
            }

            return (status, segmentCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill();
            throw new FasterWhisperProcessException(
                FasterWhisperErrorKind.Timeout,
                $"The faster-whisper worker did not respond within {timeout.TotalSeconds:F1}s. {GetStderrTail()}");
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw;
        }
        catch (Exception exc) when (exc is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Kill();
            throw new FasterWhisperProcessException(
                FasterWhisperErrorKind.Protocol,
                $"The faster-whisper worker protocol failed: {exc.Message}. {GetStderrTail()}",
                exc);
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> ReadSegmentsAsync(int status, int segmentCount, CancellationToken cancellationToken)
    {
        var segments = new List<TranscriptSegment>(Math.Max(0, segmentCount));
        for (int i = 0; i < segmentCount; i++)
        {
            var segmentHeader = await ReadExactlyAsync(20, cancellationToken).ConfigureAwait(false);
            double start = BinaryPrimitives.ReadDoubleLittleEndian(segmentHeader.AsSpan(0, 8));
            double end = BinaryPrimitives.ReadDoubleLittleEndian(segmentHeader.AsSpan(8, 8));
            int textLength = BinaryPrimitives.ReadInt32LittleEndian(segmentHeader.AsSpan(16, 4));
            var textBytes = await ReadExactlyAsync(textLength, cancellationToken).ConfigureAwait(false);
            segments.Add(new TranscriptSegment(
                Encoding.UTF8.GetString(textBytes),
                TimeSpan.FromSeconds(start),
                TimeSpan.FromSeconds(end)));
        }

        if (status != 0)
        {
            throw new FasterWhisperProcessException(
                FasterWhisperErrorKind.EngineFailed,
                $"The faster-whisper worker reported a decode error. {GetStderrTail()}");
        }

        return segments;
    }

    private async Task<byte[]> ReadExactlyAsync(int length, CancellationToken cancellationToken)
    {
        var stdout = _stdout ?? throw new FasterWhisperProcessException(
            FasterWhisperErrorKind.EngineUnavailable,
            "The faster-whisper worker has not been started.");
        var buffer = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int read = await stdout.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                Kill();
                throw new FasterWhisperProcessException(
                    FasterWhisperErrorKind.EngineUnavailable,
                    $"The faster-whisper worker closed the protocol stream. {GetStderrTail()}");
            }

            offset += read;
        }

        return buffer;
    }

    private static string ResolveBundledScript()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "faster_whisper_worker.py"),
            Path.Combine(baseDirectory, "Server", "faster_whisper_worker.py"),
        };

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null)
        {
            throw new FasterWhisperProcessException(
                FasterWhisperErrorKind.EngineUnavailable,
                $"The bundled faster-whisper worker script could not be located under {baseDirectory}.");
        }

        return found;
    }

    private async Task DrainStderrAsync(StreamReader errorReader)
    {
        try
        {
            while (true)
            {
                var line = await errorReader.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                lock (_stderrLock)
                {
                    _stderr.AppendLine(line);
                }
            }
        }
        catch
        {
            // Stderr draining is best-effort diagnostics only.
        }
    }

    private string GetStderrTail()
    {
        lock (_stderrLock)
        {
            var text = _stderr.ToString().Trim();
            return text.Length == 0 ? "No stderr output captured." : $"Stderr: {text}";
        }
    }

    private void Kill()
    {
        var process = _process;
        var stdin = _stdin;
        var stdout = _stdout;
        _process = null;
        _stdin = null;
        _stdout = null;

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill();
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        process?.Dispose();
        stdin?.Dispose();
        stdout?.Dispose();
    }
}
