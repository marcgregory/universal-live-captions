using System.Diagnostics;
using System.Globalization;
using System.Text;
using UniversalCaptions.Core.Translation;
using UniversalCaptions.Translation;
using UniversalCaptions.Translation.Argos;

/// <summary>
/// Benchmarks the Argos translation engine: process/model load time, first-translation latency,
/// steady-state latency, throughput, child-process memory, and a continuous finals stream.
/// </summary>
internal static class TranslationBenchmark
{
    private static readonly string[] Pairs = ["en->tl", "ja->en", "en->ja", "ja->tl"];

    private static readonly Dictionary<string, (string Text, string? Source, string Target)> Samples = new()
    {
        ["short"] = ("Hello world, this is a live caption test.", "en", "tl"),
        ["medium"] = ("Good morning everyone, welcome to today's engineering meeting. We have several items to review before lunch.", "en", "ja"),
        ["long"] = ("The quick brown fox jumps over the lazy dog while the rain pours on the tin roof. " +
                    "People gather under the awning to stay dry and talk about the weather and their plans for the weekend. " +
                    "Somewhere far away a radio plays a song that everyone seems to know.", "ja", "tl"),
    };

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string python = "python";
        int iterations = 3;
        bool reportQuality = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--python" when i + 1 < args.Length:
                    python = args[++i];
                    break;
                case "--iterations" when i + 1 < args.Length:
                    iterations = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--no-quality":
                    reportQuality = false;
                    break;
                case "--help":
                    PrintUsage();
                    return 0;
            }
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // Console encoding is best-effort; Japanese text may not render on some hosts.
        }

        Console.WriteLine($"Machine: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"Python:  {python}");
        Console.WriteLine($"Pairs:   {string.Join(", ", Pairs)}");
        Console.WriteLine();

        var options = new ArgosTranslationEngineOptions
        {
            PythonExecutablePath = python,
            StartupTimeout = TimeSpan.FromSeconds(180),
            RequestTimeout = TimeSpan.FromSeconds(120),
        };

        var swLoad = Stopwatch.StartNew();
        using var engine = new ArgosTranslationEngine(options);
        swLoad.Stop();
        Console.WriteLine($"engine construct:  {swLoad.ElapsedMilliseconds} ms");

        var results = new List<PairResult>();
        foreach (var pair in Pairs)
        {
            results.Add(await BenchmarkPairAsync(engine, pair, iterations, reportQuality, ct));
        }

        PrintSummary(results);
        return 0;
    }

    private static async Task<PairResult> BenchmarkPairAsync(
        ITranslationEngine engine, string pair, int iterations, bool reportQuality, CancellationToken ct)
    {
        var parts = pair.Split("->");
        string source = parts[0];
        string target = parts[1];
        var sample = Samples[source == "ja" && target == "tl" ? "long" : source == "en" && target == "ja" ? "medium" : "short"];
        var reference = References.GetValueOrDefault(pair);

        Console.WriteLine($"=== {pair} ===");

        var swFirst = Stopwatch.StartNew();
        var first = await engine.TranslateAsync(sample.Text, source, target, ct);
        swFirst.Stop();
        Console.WriteLine($"    process+model+first:  {swFirst.ElapsedMilliseconds,6} ms");
        Console.WriteLine($"    first out:            {Truncate(first.Text, 60)}");
        Console.WriteLine($"    usedPivot:            {first.UsedPivot} ({(first.PivotLanguage ?? "none")})");

        double latencyMs = 0;
        long chars = 0;
        var distinct = new List<double>();
        var identical = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            var distinctText = $"Live caption test {i + 1}: {sample.Text}";
            var sw = Stopwatch.StartNew();
            var result = await engine.TranslateAsync(distinctText, source, target, ct);
            sw.Stop();
            distinct.Add(sw.Elapsed.TotalMilliseconds);
            latencyMs += sw.Elapsed.TotalMilliseconds;
            chars += result.Text.Length;
        }

        var swId = Stopwatch.StartNew();
        var cached = await engine.TranslateAsync("Cache check repeat input.", source, target, ct);
        for (int i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            await engine.TranslateAsync("Cache check repeat input.", source, target, ct);
            sw.Stop();
            identical.Add(sw.Elapsed.TotalMilliseconds);
        }

        _ = cached;
        double avgLatencyMs = distinct.Average();
        double cachedMs = identical.Average();
        double charsPerSec = chars / (latencyMs / 1000.0);
        Console.WriteLine($"    steady latency:       {avgLatencyMs,6:0} ms avg over {iterations} distinct texts");
        Console.WriteLine($"    cached repeat:        {cachedMs,6:0} ms (Argos identical-input cache)");
        Console.WriteLine($"    throughput:           {charsPerSec,6:0} chars/s ({charsPerSec / avgLatencyMs * 1000:0} chars per call)");

        long peakWorkingSet = 0;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId FROM Win32_Process WHERE Name = 'python.exe' AND CommandLine LIKE '%argos_translate_server.py%'");
                foreach (var obj in searcher.Get())
                {
                    int pid = Convert.ToInt32(obj["ProcessId"], CultureInfo.InvariantCulture);
                    using var process = Process.GetProcessById(pid);
                    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                }
            }
        }
        catch
        {
            // Best-effort memory measurement.
        }

        Console.WriteLine($"    argos python working set: {peakWorkingSet / (1024.0 * 1024.0),6:0.0} MB");

        string referenceText = reference ?? "n/a";
        double quality = reportQuality && reference is not null
            ? ComputeSimilarity(first.Text, referenceText)
            : double.NaN;
        if (reportQuality)
        {
            string qualityText = reference is null
                ? "no reference (manual check)"
                : $"{quality * 100:0.0}% char similarity vs reference";
            string refSuffix = reference is null ? string.Empty : $"  ref={Truncate(referenceText, 50)}";
            Console.WriteLine($"    quality:              {qualityText}{refSuffix}");
        }

        var stream = await BenchmarkFinalsStreamAsync(engine, source, target, ct);
        Console.WriteLine($"    finals stream:        {stream.Count} segments in {stream.Wall.TotalSeconds:0.00}s; " +
                          $"{stream.AvgLatencyMs:0} ms avg segment latency; " +
                          $"ordered={stream.Ordered}");

        Console.WriteLine();
        return new PairResult(pair, swFirst.Elapsed, avgLatencyMs, charsPerSec, peakWorkingSet, quality, stream.Wall, stream.AvgLatencyMs, stream.Ordered);
    }

    private static async Task<StreamStats> BenchmarkFinalsStreamAsync(
        ITranslationEngine engine, string source, string target, CancellationToken ct)
    {
        string[] segments =
        [
            "Welcome to the live session.",
            "Today we will discuss the new captions feature.",
            "It translates speech into your chosen language.",
            "Thank you for joining, we will begin shortly.",
            "The first item on the agenda is audio capture.",
        ];

        var sw = Stopwatch.StartNew();
        var latencies = new List<double>();
        long previousSequence = 0;
        bool ordered = true;
        foreach (var segment in segments)
        {
            var swSeg = Stopwatch.StartNew();
            var result = await engine.TranslateAsync(segment, source, target, ct);
            swSeg.Stop();
            latencies.Add(swSeg.Elapsed.TotalMilliseconds);
            if (result.Sequence <= previousSequence)
            {
                ordered = false;
            }

            previousSequence = result.Sequence;
        }

        sw.Stop();
        return new StreamStats(segments.Length, sw.Elapsed, latencies.Average(), ordered);
    }

    private static void PrintSummary(List<PairResult> results)
    {
        Console.WriteLine("============================ TRANSLATION SUMMARY ============================");
        foreach (var r in results)
        {
            string quality = double.IsNaN(r.Quality) ? "n/a" : $"{r.Quality * 100:0.0}%";
            Console.WriteLine(
                $"{r.Pair,-10} first {r.First.TotalMilliseconds,6:0} ms  lat {r.AvgLatencyMs,5:0} ms  {r.CharsPerSec,6:0} ch/s  " +
                $"argosMem {r.PeakWorkingSetBytes / (1024.0 * 1024.0),4:0.0} MB  qual {quality,6}  stream {r.StreamLatencyMs,4:0} ms/fin (ord={r.StreamOrdered})");
        }

        Console.WriteLine("============================================================================");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project src/UniversalCaptions.Benchmarks -- translate [--python <path>] [--iterations N] [--no-quality]");
    }

    private static double ComputeSimilarity(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        int max = Math.Max(a.Length, b.Length);
        return max == 0 ? 1.0 : 1.0 - (double)d[a.Length, b.Length] / max;
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(max - 3)] + "...";
    }

    private static readonly Dictionary<string, string> References = new()
    {
        ["en->tl"] = "Kumusta mundo, ito ay isang live na pagsubok ng caption.",
        ["ja->en"] = "Hello world.",
        ["en->ja"] = "おはようございます。",
    };

    internal sealed record PairResult(
        string Pair,
        TimeSpan First,
        double AvgLatencyMs,
        double CharsPerSec,
        long PeakWorkingSetBytes,
        double Quality,
        TimeSpan StreamWall,
        double StreamLatencyMs,
        bool StreamOrdered);

    internal sealed record StreamStats(int Count, TimeSpan Wall, double AvgLatencyMs, bool Ordered);
}
