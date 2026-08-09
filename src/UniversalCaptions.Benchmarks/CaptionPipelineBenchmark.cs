using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Captions;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Captions;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Speech;
using UniversalCaptions.Translation;
using UniversalCaptions.Translation.Argos;

/// <summary>
/// Additive production-wiring diagnostics leg (<c>captionwire</c>): drives the EXACT App composition —
/// faster-whisper native streaming STT → <see cref="CaptionService"/> → <see cref="ArgosTranslationEngine"/>
/// (the single-gate Argos process) — instead of the channel pump <c>translatelive</c> uses. Records for
/// every published translated caption the stamps CaptionService already carries (captured → committed →
/// translation-started → completed), plus per-request engine-visibility duration via a pure timing
/// decorator, a pure serial survey of the identical captured texts to attribute "Argos service time"
/// apart from queue/backlog, and CPU split per python worker (STT vs Argos). Nothing in production is
/// touched; runs the same engine the App composes.
/// </summary>
internal static class CaptionPipelineBenchmark
{
    private const int SampleRate = 16_000;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uMilliseconds);

    private static void Usage()
    {
        Console.WriteLine("captionwire — production-wiring translation latency/CPU diagnostics (additive).");
        Console.WriteLine("  --wav <path>          16-bit PCM WAV (8k/16k mono or stereo).");
        Console.WriteLine("  --stt-python <path>   python for faster-whisper worker (default %TEMP%\\fwv\\Scripts\\python.exe).");
        Console.WriteLine("  --argos-python <path> python for Argos server (default %TEMP%\\argosv\\Scripts\\python.exe).");
        Console.WriteLine("  --model <name>        faster-whisper model (default small).");
        Console.WriteLine("  --threads <n>         faster-whisper decode threads (default 4).");
        Console.WriteLine("  --max-segment <s>     max speech segment (default 8).");
        Console.WriteLine("  --hangover <s>        silence hangover (default 0.7).");
        Console.WriteLine("  --interval <s>        partial decode interval (default 1).");
        Console.WriteLine("  --window <s>          partial decode window (default 4).");
        Console.WriteLine("  --prewarm             pre-warm Argos before feeding, wait for completion (default off).");
        Console.WriteLine("  --prewarm-race        fire-and-forget pre-warm exactly like the App startup path (default off).");
        Console.WriteLine("  --csv <path>          write per-line CSV (default none).");
        Console.WriteLine("  --help                show this help.");
    }

    private static (float[] Samples, double Seconds) LoadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        if (format.BitsPerSample != 16)
        {
            throw new InvalidOperationException($"Expected 16-bit PCM but found {format.BitsPerSample}-bit ({format.Encoding}).");
        }

        var raw = new byte[reader.Length];
        int read = reader.Read(raw, 0, raw.Length);
        var pcm = new short[read / 2];
        Buffer.BlockCopy(raw, 0, pcm, 0, read);

        float[] mono;
        if (format.Channels == 1)
        {
            mono = new float[pcm.Length];
            for (int i = 0; i < pcm.Length; i++)
            {
                mono[i] = pcm[i] / 32768f;
            }
        }
        else if (format.Channels == 2)
        {
            mono = new float[pcm.Length / 2];
            for (int i = 0; i < mono.Length; i++)
            {
                mono[i] = (pcm[i * 2] / 32768f + pcm[(i * 2) + 1] / 32768f) * 0.5f;
            }
        }
        else
        {
            throw new InvalidOperationException($"Unsupported channel count {format.Channels}.");
        }

        if (format.SampleRate == SampleRate)
        {
            return (mono, mono.Length / (double)SampleRate);
        }

        if (format.SampleRate == 8000)
        {
            var up = new float[(mono.Length - 1) * 2 + 1];
            for (int i = 0; i < mono.Length - 1; i++)
            {
                for (int k = 0; k < 2; k++)
                {
                    up[(i * 2) + k] = mono[i] + (mono[i + 1] - mono[i]) * (k / 2f);
                }
            }

            up[^1] = mono[^1];
            return (up, up.Length / (double)SampleRate);
        }

        throw new InvalidOperationException($"Unsupported sample rate {format.SampleRate} Hz (expected 8000 or 16000).");
    }

    private static string Ci(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        if (p >= 100)
        {
            return sorted[^1];
        }

        double rank = p / 100.0 * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double weight = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }

    private static string Pct(double fraction) => $"{fraction * 100:0.0}%";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? wav = null;
        string temp = Environment.GetEnvironmentVariable("TEMP") ?? ".";
        string sttPython = Path.Combine(temp, "fwv", "Scripts", "python.exe");
        string argosPython = Path.Combine(temp, "argosv", "Scripts", "python.exe");
        string model = "small";
        int threads = 4;
        double maxSegment = 8;
        double hangover = 0.7;
        double partialInterval = 1;
        double partialWindow = 4;
        bool prewarm = false;
        bool prewarmRace = false;
        string? csv = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--wav" when i + 1 < args.Length: wav = args[++i]; break;
                case "--stt-python" when i + 1 < args.Length: sttPython = args[++i]; break;
                case "--argos-python" when i + 1 < args.Length: argosPython = args[++i]; break;
                case "--model" when i + 1 < args.Length: model = args[++i]; break;
                case "--threads" when i + 1 < args.Length: threads = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max-segment" when i + 1 < args.Length: maxSegment = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--hangover" when i + 1 < args.Length: hangover = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--interval" when i + 1 < args.Length: partialInterval = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--window" when i + 1 < args.Length: partialWindow = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--prewarm": prewarm = true; break;
                case "--prewarm-race": prewarmRace = true; break;
                case "--csv" when i + 1 < args.Length: csv = args[++i]; break;
                case "--help":
                case "-h":
                    Usage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unrecognized argument: {args[i]}");
                    Usage();
                    return 2;
            }
        }

        if (wav is null)
        {
            Console.Error.WriteLine("captionwire requires --wav <path>.");
            Usage();
            return 2;
        }

        Console.OutputEncoding = Encoding.UTF8;
        var (samples, audioSeconds) = LoadWav(wav);
        Console.WriteLine("=== captionwire: production wiring (STT → CaptionService → Argos) ===");
        Console.WriteLine($"Machine: {Environment.OSVersion.VersionString}; CPU: {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"WAV: {Path.GetFullPath(wav)} ({audioSeconds:0.00}s)");
        Console.WriteLine($"STT {model}, threads {threads}, max-segment {maxSegment:0.#}s, hangover {hangover:0.#}s, partials {partialInterval:0.#}s/{partialWindow:0.#}s");
        Console.WriteLine($"STT python: {sttPython}");
        Console.WriteLine($"Argos python: {argosPython}");
        Console.WriteLine($"pre-warm: {(prewarm ? "ON (await)" : prewarmRace ? "RACE (fire-and-forget)" : "OFF")}");
        Console.WriteLine();

        using var sttEngine = new FasterWhisperNativeStreamingEngine(
            new FasterWhisperEngineOptions
            {
                PythonExecutablePath = sttPython,
                Model = model,
                Language = "en",
                Threads = threads,
                PartialDecodeInterval = TimeSpan.FromSeconds(partialInterval),
                PartialDecodeWindow = TimeSpan.FromSeconds(partialWindow),
            },
            new EnergyVad(new VadOptions(RmsThreshold: 0.008, MinActiveChunks: 1, SilenceHangoverChunks: 2)),
            new SpeechSegmentDetectorOptions
            {
                SampleRate = SampleRate,
                MinSpeechDuration = TimeSpan.FromSeconds(0.3),
                SilenceHangover = TimeSpan.FromSeconds(hangover),
                MaxSegmentDuration = TimeSpan.FromSeconds(8),
            });

        using var argos = new ArgosTranslationEngine(
            new ArgosTranslationEngineOptions
            {
                PythonExecutablePath = argosPython,
                StartupTimeout = TimeSpan.FromSeconds(180),
                RequestTimeout = TimeSpan.FromSeconds(120),
            });

        var decorator = new TimingTranslationEngine(argos);
        using var captions = new CaptionService(
            new CaptionServiceOptions(sourceLanguage: "en", targetLanguage: "tl", historyCapacity: 50),
            decorator);

        using var sttMonitor = new ProcessMonitor("faster_whisper_worker.py");
        using var argosMonitor = new ProcessMonitor("argos_translate_server.py");

        var rows = new List<CaptionRow>();
        var activeRows = new List<CaptionRow>();
        var errors = new List<string>();
        var baseTime = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        int partials = 0;
        int finals = 0;

        Action<CaptionLine, bool> captureLine = (line, isActive) =>
        {
            if (line.TranslationStatus != CaptionTranslationStatus.Completed)
            {
                return;
            }

            double captured = (line.CapturedAtUtc - baseTime).TotalSeconds;
            double committed = (line.CommittedAtUtc is { } c ? c - baseTime : line.CapturedAtUtc - baseTime).TotalSeconds;
            double started = (line.TranslationStartedAtUtc is { } stx ? stx - baseTime : line.CapturedAtUtc - baseTime).TotalSeconds;
            double completed = (line.TranslationCompletedAtUtc is { } ctx ? ctx - baseTime : line.CapturedAtUtc - baseTime).TotalSeconds;

            var target = isActive ? activeRows : rows;
            lock (target)
            {
                target.Add(new CaptionRow(
                    line.Sequence,
                    captured,
                    committed,
                    started,
                    completed,
                    line.Text,
                    line.TranslatedText ?? ""));
            }
        };

        captions.CaptionLineCommitted += (_, line) => captureLine(line, false);
        captions.CaptionLineUpdated += (_, line) => captureLine(line, line.State == CaptionLineState.Active);
        captions.ActiveLineChanged += (_, line) => captureLine(line, true);
        captions.StateChanged += (_, state) =>
        {
            if (state.ActiveLine is { } active)
            {
                captureLine(active, true);
            }
        };

        sttEngine.PartialTranscriptAvailable += (_, t) =>
        {
            partials++;
            captions.ProcessPartial(t);
        };
        sttEngine.FinalTranscriptAvailable += (_, t) =>
        {
            finals++;
            captions.ProcessFinal(t);
        };
        sttEngine.RecognitionFailed += (_, e) => errors.Add($"{e.Kind}: {e.Message}");

        if (prewarm)
        {
            var swWarm = Stopwatch.StartNew();
            Console.WriteLine("  pre-warming Argos (await completion) …");
            await argos.TriggerPreWarmAsync("en", "tl", ct);
            Console.WriteLine($"  Argos pre-warm: {swWarm.ElapsedMilliseconds} ms");
        }
        else if (prewarmRace)
        {
            // Fire-and-forget exactly like the App's startup/toggle path (OnLoaded / OnTranslationToggled):
            // the first real caption arrives before the warm-up necessarily completes, so it must await
            // the in-flight warm-up within the engine rather than racing it (Entry: engine fix).
            Console.WriteLine("  pre-warming Argos (fire-and-forget, racing the feed) …");
            _ = argos.TriggerPreWarmAsync("en", "tl", ct);
        }

        sttMonitor.Start();
        argosMonitor.Start();
        captions.Start();
        captions.SetTranslationEnabled(true, "tl");
        sttEngine.Start();
        baseTime = DateTime.UtcNow;
        sw.Restart();
        timeBeginPeriod(1);
        try
        {
            await FeedRealtimeAsync(sttEngine, samples, baseTime, ct);
        }
        finally
        {
            timeEndPeriod(1);
        }

        sttEngine.Stop();
        double feedWallSec = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"    feed finished at {feedWallSec:0.00}s wall; {finals} STT FINALs, {partials} partials.");

        var flushCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        flushCts.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await captions.FlushAsync(flushCts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("    WARNING: FlushAsync timed out; some commits may be pending.");
            errors.Add("FlushAsync timeout");
        }

        sw.Stop();
        Console.WriteLine($"    flush complete at {sw.Elapsed.TotalSeconds:0.00}s wall.");

        var sttSummary = sttMonitor.Stop();
        var argosSummary = argosMonitor.Stop();

        List<CaptionRow> snapshot;
        lock (rows)
        {
            snapshot = rows.OrderBy(r => r.Sequence).ToList();
        }

        var surveyTimes = new List<double>();
        foreach (var row in snapshot)
        {
            try
            {
                var proxy = Stopwatch.StartNew();
                _ = await argos.TranslateAsync(row.Text, "en", "tl", ct);
                proxy.Stop();
                lock (surveyTimes)
                {
                    surveyTimes.Add(proxy.Elapsed.TotalMilliseconds);
                }
            }
            catch (Exception exc)
            {
                errors.Add($"SURVEY: {exc.Message}");
            }
        }

        var e2eSec = snapshot.Select(r => r.CompletedSec - r.CapturedSec).ToList();
        var queueSec = snapshot.Select(r => r.StartedSec - r.CommittedSec).ToList();
        var requestSec = snapshot.Select(r => r.CompletedSec - r.StartedSec).ToList();

        List<CaptionRow> activeSnapshot;
        lock (activeRows)
        {
            activeSnapshot = activeRows
                .GroupBy(r => r.Sequence)
                .Select(g => g.OrderBy(r => r.StartedSec).Last())
                .OrderBy(r => r.CompletedSec)
                .ToList();
        }

        double firstActiveSec = activeSnapshot.Count > 0 ? activeSnapshot.Min(r => r.CompletedSec) : double.NaN;
        double firstFinalSec = snapshot.Count > 0 ? snapshot.Min(r => r.CompletedSec) : double.NaN;

        Console.WriteLine();
        Console.WriteLine($"  translated finals:          {snapshot.Count} (of {finals} STT FINALs / {partials} partials)");
        Console.WriteLine($"  live-translated active lines (overlay stream): {activeSnapshot.Count}");
        Console.WriteLine($"  first visible translated caption (from feed start): {FormatSec(firstActiveSec)}");
        Console.WriteLine($"  first committed translated caption (from feed start): {FormatSec(firstFinalSec)}");
        Console.WriteLine($"  max concurrent callers:    {decorator.MaxConcurrentRequests}");
        Console.WriteLine($"  errors/failures:           {errors.Distinct().Count()} distinct");
        foreach (var e in errors.Distinct().Take(6))
        {
            Console.WriteLine($"      {e}");
        }

        Console.WriteLine();
        Console.WriteLine("  --- production-wiring per-caption latency (wall seconds, committed finals) ---");
        PrintRow("  queue (commit → translation start)", queueSec);
        PrintRow("  Argos caller-visible (start → complete)", requestSec);
        PrintRow("  E2E (captured → complete)", e2eSec);
        Console.WriteLine();
        Console.WriteLine("  --- pure Argos service survey (serial, identical texts) ---");
        PrintRow("  service", surveyTimes.Select(t => t / 1000.0));
        Console.WriteLine();
        Console.WriteLine($"  cpu: STT python {Pct(sttSummary.WorkerCpuFraction)} / Argos python {Pct(argosSummary.WorkerCpuFraction)} " +
                          $" / benchmark {Pct(sttSummary.SelfCpuFraction + argosSummary.SelfCpuFraction)} of machine");
        Console.WriteLine($"  peak working set: {Math.Max(sttSummary.PeakWorkingSetMb, argosSummary.PeakWorkingSetMb):0.0} MB");

        if (csv is not null)
        {
            WriteCsv(csv, snapshot, activeSnapshot);
        }

        return 0;
    }

    private static string FormatSec(double seconds) => double.IsNaN(seconds) || seconds < 0 ? "n/a" : $"{seconds:0.000}s";

    private static void PrintRow(string label, IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            Console.WriteLine($"{label}: n/a");
            return;
        }

        Console.WriteLine(
            $"{label}: p50 {Median(list):0.000}s  p95 {Percentile(list, 95):0.000}s  p99 {Percentile(list, 99):0.000}s  max {list.Max():0.000}s  n={list.Count}");
    }

    private static void WriteCsv(string path, List<CaptionRow> rows, List<CaptionRow> active)
    {
        var sb = new StringBuilder();
        sb.AppendLine("seq,captured_s,committed_s,start_s,complete_s,e2e_s,kind,source,translated");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(',',
                r.Sequence.ToString(CultureInfo.InvariantCulture),
                Ci(r.CapturedSec),
                Ci(r.CommittedSec),
                Ci(r.StartedSec),
                Ci(r.CompletedSec),
                Ci(r.CompletedSec - r.CapturedSec),
                "final",
                Csv(r.Text),
                Csv(r.TranslatedText)));
        }

        foreach (var r in active)
        {
            sb.AppendLine(string.Join(',',
                r.Sequence.ToString(CultureInfo.InvariantCulture),
                Ci(r.CapturedSec),
                Ci(r.CommittedSec),
                Ci(r.StartedSec),
                Ci(r.CompletedSec),
                Ci(r.CompletedSec - r.CapturedSec),
                "active",
                Csv(r.Text),
                Csv(r.TranslatedText)));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"CSV written to {Path.GetFullPath(path)}");
    }

    private static async Task FeedRealtimeAsync(
        FasterWhisperNativeStreamingEngine engine, float[] samples, DateTime baseTime, CancellationToken ct)
    {
        const double chunkSeconds = 0.5;
        int chunkFrames = (int)(SampleRate * chunkSeconds);
        long seq = 0;
        for (int offset = 0; offset < samples.Length; offset += chunkFrames)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(chunkFrames, samples.Length - offset);
            var chunk = new float[count];
            Array.Copy(samples, offset, chunk, 0, count);
            engine.Process(new AudioChunk(chunk, new AudioFormat(SampleRate, 1, 32), baseTime.AddSeconds(offset / (double)SampleRate), ++seq));
            int sleepMs = (int)(chunkSeconds * 1000);
            if (sleepMs > 0)
            {
                await Task.Delay(sleepMs, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed record CaptionRow(
        long Sequence,
        double CapturedSec,
        double CommittedSec,
        double StartedSec,
        double CompletedSec,
        string Text,
        string TranslatedText);

    /// <summary>
    /// Pure timing decorator over <see cref="ITranslationEngine"/>: records how many translate calls
    /// overlap in the caller's view (queue depth handed to the single-gate Argos engine). No change.
    /// </summary>
    private sealed class TimingTranslationEngine : ITranslationEngine
    {
        private readonly ITranslationEngine _inner;
        private readonly object _lock = new();
        private int _active;
        private int _maxConcurrent;

        public TimingTranslationEngine(ITranslationEngine inner) => _inner = inner;

        public int MaxConcurrentRequests
        {
            get
            {
                lock (_lock)
                {
                    return _maxConcurrent;
                }
            }
        }

        public async Task<TranslationResult> TranslateAsync(
            string text, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        {
            int slot;
            lock (_lock)
            {
                _active++;
                slot = _active;
                if (_active > _maxConcurrent)
                {
                    _maxConcurrent = _active;
                }
            }

            try
            {
                return await _inner.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    _active--;
                }
            }
        }
    }

    /// <summary>Samples this benchmark process and matched python workers every 200 ms for real
    /// CPU/RAM per component. Additive, benchmark-only.</summary>
    internal sealed class ProcessMonitor : IDisposable
    {
        private readonly string[] _patterns;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<(double Ms, double SelfCpu, double WorkerCpu, double SelfWs, double WorkerWs)> _samples = new();
        private readonly object _lock = new();
        private readonly Stopwatch _sw = new();
        private Thread? _thread;

        internal ProcessMonitor(params string[] patterns) => _patterns = patterns;

        internal void Start()
        {
            lock (_lock)
            {
                if (_thread is not null)
                {
                    return;
                }

                _samples.Clear();
                _sw.Restart();
                _thread = new Thread(Run) { IsBackground = true, Name = "proc-monitor" };
                _thread.Start();
            }
        }

        internal (double SelfCpuFraction, double WorkerCpuFraction, double PeakWorkingSetMb) Stop()
        {
            lock (_lock)
            {
                _cts.Cancel();
                var thread = _thread;
                _thread = null;
                thread?.Join(TimeSpan.FromSeconds(3));
                _sw.Stop();

                if (_samples.Count == 0)
                {
                    return (0, 0, 0);
                }

                double wall = _sw.Elapsed.TotalSeconds;
                double cores = Math.Max(1, Environment.ProcessorCount);
                double selfFirst = _samples[0].SelfCpu;
                double selfLast = _samples[^1].SelfCpu;
                double workerFirst = _samples[0].WorkerCpu;
                double workerLast = _samples[^1].WorkerCpu;
                double selfFraction = Math.Max(0, (selfLast - selfFirst) / wall / cores);
                double workerFraction = Math.Max(0, (workerLast - workerFirst) / wall / cores);
                double peakWs = _samples.Max(s => s.SelfWs + s.WorkerWs);
                return (selfFraction, workerFraction, peakWs / (1024.0 * 1024.0));
            }
        }

        private void Run()
        {
            var ct = _cts.Token;
            var self = Process.GetCurrentProcess();
            while (!ct.IsCancellationRequested)
            {
                double selfCpu = self.TotalProcessorTime.TotalSeconds;
                double selfWs = self.WorkingSet64;
                double workerCpu = 0;
                double workerWs = 0;
                foreach (int pid in FindWorkerPids())
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        try
                        {
                            workerCpu += p.TotalProcessorTime.TotalSeconds;
                            workerWs += p.WorkingSet64;
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                }

                lock (_lock)
                {
                    _samples.Add((_sw.Elapsed.TotalSeconds, selfCpu, workerCpu, selfWs, workerWs));
                }

                try
                {
                    Thread.Sleep(200);
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
            }
        }

        private IEnumerable<int> FindWorkerPids()
        {
            var pids = new List<int>();
            if (!OperatingSystem.IsWindows())
            {
                return pids;
            }

            try
            {
                var query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE (Name = 'python.exe' OR Name = 'pythonw.exe')";
                using var searcher = new ManagementObjectSearcher(query);
                foreach (var obj in searcher.Get())
                {
                    string? commandLine = obj["CommandLine"] as string;
                    if (commandLine is null)
                    {
                        continue;
                    }

                    if (_patterns.Length > 0 && !_patterns.Any(p => commandLine.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (int.TryParse(Convert.ToString(obj["ProcessId"], CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
                    {
                        pids.Add(pid);
                    }
                }
            }
            catch
            {
            }

            return pids;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            lock (_lock)
            {
                var thread = _thread;
                _thread = null;
                thread?.Join(TimeSpan.FromSeconds(3));
            }
        }
    }
}
