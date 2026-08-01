using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.Translation.Argos;

/// <summary>
/// A <see cref="IArgosProcess"/> implementation that spawns a local Python process running the
/// bundled <c>argos_translate_server.py</c> and exchanges newline-delimited JSON over stdin/stdout.
/// </summary>
internal sealed class LineProtocolArgosProcess : IArgosProcess
{
    private const long PingRequestId = 0;

    private readonly ArgosTranslationEngineOptions _options;
    private readonly string _scriptPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrLock = new();

    private Process? _process;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private bool _disposed;

    public LineProtocolArgosProcess(ArgosTranslationEngineOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scriptPath = options.ServerScriptPath ?? ResolveBundledScript();
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

            StartProcess();

            var ping = JsonSerializer.Serialize(new { id = PingRequestId, ping = true });
            var line = await ExchangeAsync(ping, _options.StartupTimeout, cancellationToken);
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                {
                    var message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "Argos process did not become ready";
                    Kill();
                    throw new TranslationProcessException(TranslationErrorKind.EngineUnavailable, message ?? "Argos process did not become ready");
                }
            }
            catch (JsonException exc)
            {
                Kill();
                throw new TranslationProcessException(
                    TranslationErrorKind.Unknown,
                    $"The Argos process returned an invalid startup response: {exc.Message}. {GetStderrTail()}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ArgosResponse> TranslateAsync(ArgosRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(new { id = request.Id, text = request.Text, source = request.Source, target = request.Target });
            var line = await ExchangeAsync(json, _options.RequestTimeout, cancellationToken);
            return ParseResponse(line, request.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Kill();
        _process?.Dispose();
        _process = null;
        _gate.Dispose();
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
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        try
        {
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception exc)
        {
            throw new TranslationProcessException(
                TranslationErrorKind.EngineUnavailable,
                $"Failed to start the Argos process (python: {_options.PythonExecutablePath}, script: {_scriptPath}): {exc.Message}",
                exc);
        }

        _writer = new StreamWriter(_process.StandardInput.BaseStream) { AutoFlush = true };
        _reader = _process.StandardOutput;
        _ = Task.Run(() => DrainStderrAsync(_process.StandardError));
    }

    private async Task<string> ExchangeAsync(string requestJson, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new TranslationProcessException(
            TranslationErrorKind.EngineUnavailable,
            "The Argos process has not been started.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await _writer!.WriteLineAsync(requestJson);
            var line = await _reader!.ReadLineAsync().WaitAsync(timeoutCts.Token);
            if (line is null)
            {
                Kill();
                throw new TranslationProcessException(
                    TranslationErrorKind.EngineUnavailable,
                    $"The Argos process closed the protocol stream. {GetStderrTail()}");
            }

            return line;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill();
            throw new TranslationProcessException(
                TranslationErrorKind.Timeout,
                $"The Argos process did not respond within {timeout.TotalSeconds:F1}s. {GetStderrTail()}");
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw;
        }
        catch (Exception exc) when (exc is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Kill();
            throw new TranslationProcessException(
                TranslationErrorKind.EngineUnavailable,
                $"The Argos process protocol failed: {exc.Message}. {GetStderrTail()}",
                exc);
        }
    }

    private static ArgosResponse ParseResponse(string line, long expectedId)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException exc)
        {
            throw new TranslationProcessException(
                TranslationErrorKind.Unknown,
                $"The Argos process returned an invalid response: {exc.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idElement) &&
                idElement.TryGetInt64(out var actualId) &&
                actualId != expectedId)
            {
                throw new TranslationProcessException(
                    TranslationErrorKind.Unknown,
                    $"Protocol mismatch: expected response id {expectedId}, received {actualId}.");
            }

            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var kindElement = root.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() : "The Argos process returned an error";
                var errorKind = Enum.TryParse<TranslationErrorKind>(kindElement, ignoreCase: true, out var parsed)
                    ? parsed
                    : TranslationErrorKind.Unknown;
                throw new TranslationProcessException(errorKind, message ?? "The Argos process returned an error");
            }

            return new ArgosResponse(
                Ok: true,
                Text: root.TryGetProperty("text", out var text) ? text.GetString() : null,
                DetectedSource: root.TryGetProperty("detectedSource", out var detected) ? detected.GetString() : null,
                UsedPivot: root.TryGetProperty("usedPivot", out var pivot) && pivot.GetBoolean(),
                PivotLanguage: root.TryGetProperty("pivotLanguage", out var pivotLang) ? pivotLang.GetString() : null,
                Models: null,
                ErrorKind: null,
                ErrorMessage: null);
        }
    }

    private static string ResolveBundledScript()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "argos_translate_server.py"),
            Path.Combine(baseDirectory, "Server", "argos_translate_server.py"),
        };

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is null)
        {
            throw new TranslationProcessException(
                TranslationErrorKind.EngineUnavailable,
                $"The bundled Argos server script could not be located under {baseDirectory}.");
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
        var writer = _writer;
        var reader = _reader;
        _process = null;
        _writer = null;
        _reader = null;

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
        writer?.Dispose();
        reader?.Dispose();
    }
}
