using System.Diagnostics;
using System.Globalization;
using System.Text;
using NAudio.Dsp;
using NAudio.Wave;
using UniversalCaptions.Audio.Processing;
using Whisper.net;

/// <summary>
/// TD-001 benchmark: head-to-head of the current windowed-sinc <see cref="SampleRateConverter"/>
/// against NAudio's <see cref="WdlResampler"/>. Measures resampler CPU/throughput/allocations and,
/// crucially, STT impact (full-file decode WER on ggml-base) for the same representative audio.
/// Benchmark-only - no production replacement is made here.
/// </summary>
internal static class ResamplerBenchmark
{
    private const int SttRate = 16_000;
    private const string DefaultModelsDir = "artifacts/models";
    private const string DefaultSamplesDir = "artifacts/samples";
    private const string JfkUrl = "https://github.com/ggerganov/whisper.cpp/raw/master/samples/jfk.wav";
    private const string JfkReference =
        "And so my fellow Americans ask not what your country can do for you ask what you can do for your country";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string modelPath = Path.Combine(DefaultModelsDir, "ggml-base.bin");
        string wavPath = Path.Combine(DefaultSamplesDir, "jfk.wav");
        int threads = Math.Max(2, Environment.ProcessorCount / 2);
        int repeats = 5;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length:
                    modelPath = args[++i];
                    break;
                case "--wav" when i + 1 < args.Length:
                    wavPath = args[++i];
                    break;
                case "--threads" when i + 1 < args.Length:
                    threads = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--repeats" when i + 1 < args.Length:
                    repeats = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
            }
        }

        Console.WriteLine($"Machine: {Environment.OSVersion.VersionString}");
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores; STT threads: {threads}; repeats/row: {repeats}");
        Console.WriteLine($"Model: {Path.GetFullPath(modelPath)}");
        Console.WriteLine();

        string fullModelPath = Path.GetFullPath(modelPath);
        await EnsureModelAsync(fullModelPath, ct);

        if (!File.Exists(wavPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(wavPath))!);
            Console.WriteLine($"Downloading {Path.GetFileName(wavPath)} ...");
            using var http = new HttpClient();
            using var resp = await http.GetAsync(JfkUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(wavPath);
            await src.CopyToAsync(dst, ct);
            Console.WriteLine($"Downloaded {Path.GetFileName(wavPath)}.");
        }

        float[] base16k = ReadMono16k(wavPath);
        double audioSeconds = base16k.Length / (double)SttRate;
        Console.WriteLine($"Base speech: {wavPath} ({audioSeconds:0.00}s at 16 kHz mono).");
        Console.WriteLine();

        // 44.1 kHz / 48 kHz source created from the same 16 kHz speech via a reference upsampler,
        // so both candidates downsample byte-identical input (fair head-to-head).
        float[] source44100 = SincConvert(base16k, SttRate, 44_100);
        float[] source48000 = SincConvert(base16k, SttRate, 48_000);

        Console.WriteLine($"=== Resampler performance (best of {repeats} runs, 0.5 s input chunks, mono) ===");
        Console.WriteLine($"{"impl",-9} {"path",-12} {"wall_ms",7} {"realtime",7} {"cpu_ms",7} {"MB_alloc",9} {"out_frames",10}");
        Console.WriteLine("----------------------------------------------------------------------------------------");

        var rows = new List<RowResult>
        {
            // Control: no resampling - establishes the no-op behavior and the STT baseline.
            MeasureRow(repeats, "control", "16k->16k", base16k, SttRate, SttRate, ConverterKind.None),
            MeasureRow(repeats, "sinc", "44.1k->16k", source44100, 44_100, SttRate, ConverterKind.Sinc),
            MeasureRow(repeats, "wdl", "44.1k->16k", source44100, 44_100, SttRate, ConverterKind.Wdl),
            MeasureRow(repeats, "sinc", "48k->16k", source48000, 48_000, SttRate, ConverterKind.Sinc),
            MeasureRow(repeats, "wdl", "48k->16k", source48000, 48_000, SttRate, ConverterKind.Wdl),
        };

        foreach (var r in rows)
        {
            Console.WriteLine(r.Format());
        }

        Console.WriteLine();
        Console.WriteLine($"=== Audio equivalence / STT impact (ggml-base full-file decode, language en) ===");
        Console.WriteLine("Round-trip WER vs the control measures degradation added by each resampler on identical input.");
        Console.WriteLine();

        Console.WriteLine($"control 16k->16k : {await DecodeWerAsync(fullModelPath, base16k, threads, ct)}");
        Console.WriteLine($"sinc  44.1k->16k : {await DecodeWerAsync(fullModelPath, rows[1].Output, threads, ct)}");
        Console.WriteLine($"wdl   44.1k->16k : {await DecodeWerAsync(fullModelPath, rows[2].Output, threads, ct)}");
        Console.WriteLine($"sinc  48k->16k  : {await DecodeWerAsync(fullModelPath, rows[3].Output, threads, ct)}");
        Console.WriteLine($"wdl   48k->16k  : {await DecodeWerAsync(fullModelPath, rows[4].Output, threads, ct)}");

        return 0;
    }

    private static RowResult MeasureRow(int repeats, string impl, string path, float[] input, int inRate, int outRate, ConverterKind kind)
    {
        double bestWall = double.MaxValue;
        double bestCpu = 0;
        long bestAlloc = long.MaxValue;
        float[] output = null!;
        for (int i = 0; i < repeats; i++)
        {
            ConversionResult m = ConvertAll(kind, input, inRate, outRate);
            if (m.Wall.TotalMilliseconds < bestWall)
            {
                bestWall = m.Wall.TotalMilliseconds;
                bestCpu = m.Cpu.TotalMilliseconds;
                bestAlloc = m.AllocatedBytes;
                output = m.Output;
            }
        }

        double factor = (bestWall / 1000.0) / (input.Length / (double)inRate);
        double mb = bestAlloc / (1024.0 * 1024.0);
        return new RowResult(impl, path, bestWall, factor, bestCpu, mb, output);
    }

    /// <summary>Converts the whole input via the chosen resampler in 0.5 s input chunks, mirroring the pipeline.</summary>
    private static ConversionResult ConvertAll(ConverterKind kind, float[] input, int inRate, int outRate)
    {
        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        float[] output = kind switch
        {
            ConverterKind.None => input,
            ConverterKind.Sinc => SincConvert(input, inRate, outRate),
            ConverterKind.Wdl => WdlConvert(input, inRate, outRate),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        sw.Stop();
        var cpuEnd = Process.GetCurrentProcess().TotalProcessorTime;
        long endAlloc = GC.GetAllocatedBytesForCurrentThread();
        return new ConversionResult(output, sw.Elapsed, cpuEnd - cpuStart, endAlloc - startAlloc);
    }

    private static float[] SincConvert(float[] input, int inRate, int outRate)
    {
        var resampler = new SampleRateConverter(inRate, outRate, channels: 1);
        int chunkFrames = (int)(inRate * 0.5);
        var all = new List<float>(input.Length);
        for (int o = 0; o < input.Length; o += chunkFrames)
        {
            int count = Math.Min(chunkFrames, input.Length - o);
            var chunk = input.AsSpan(o, count).ToArray();
            all.AddRange(resampler.Convert(chunk));
        }

        return [.. all];
    }

    private static float[] WdlConvert(float[] input, int inRate, int outRate)
    {
        const int channels = 1;
        var resampler = new WdlResampler();
        resampler.SetMode(true, 2, false);
        resampler.SetFilterParms();
        resampler.SetFeedMode(true);
        resampler.SetRates(inRate, outRate);
        resampler.Reset();

        int chunkFrames = (int)(inRate * 0.5);
        double ratio = inRate / (double)outRate;
        int outCapacity = (int)((inRate * 0.5 * Math.Max(1.0, ratio)) + 64);
        var outBuf = new float[outCapacity * channels];
        var all = new List<float>(input.Length);
        int totalInFrames = input.Length / channels;

        for (int offsetFrames = 0; offsetFrames < totalInFrames; offsetFrames += chunkFrames)
        {
            int availFrames = totalInFrames - offsetFrames;
            int inFrames = Math.Min(chunkFrames, availFrames);
            int needed = resampler.ResamplePrepare(inFrames, channels, out float[] inBuffer, out int inBufferOffset);
            input.AsSpan(offsetFrames, needed * channels).CopyTo(inBuffer.AsSpan(inBufferOffset, needed * channels));
            int produced = resampler.ResampleOut(outBuf, 0, needed, outBuf.Length / channels, channels);
            for (int i = 0; i < produced * channels; i++)
            {
                all.Add(outBuf[i]);
            }
        }

        return [.. all];
    }

    private static async Task<string> DecodeWerAsync(string modelPath, float[] samples, int threads, CancellationToken ct)
    {
        using var factory = WhisperFactory.FromPath(modelPath);
        using var processor = factory.CreateBuilder().WithLanguage("en").WithThreads(threads).Build();
        var sb = new StringBuilder();
        var swDecode = Stopwatch.StartNew();
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            sb.Append(segment.Text);
        }

        swDecode.Stop();
        string transcript = sb.ToString();
        double wer = ComputeWer(transcript, JfkReference);
        double factor = swDecode.Elapsed.TotalSeconds / (samples.Length / (double)SttRate);
        return $"decode {swDecode.ElapsedMilliseconds,4} ms ({factor:0.00}x)  WER {wer * 100,5:0.0}%  `{Trim(transcript, 70)}`";
    }

    private static double ComputeWer(string transcript, string referenceText)
    {
        var hyp = Tokenize(transcript);
        var refWords = Tokenize(referenceText);
        if (refWords.Length == 0)
        {
            return 1.0;
        }

        var d = new int[hyp.Length + 1, refWords.Length + 1];
        for (int i = 0; i <= hyp.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= refWords.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= hyp.Length; i++)
        {
            for (int j = 1; j <= refWords.Length; j++)
            {
                int cost = hyp[i - 1] == refWords[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return (double)d[hyp.Length, refWords.Length] / refWords.Length;
    }

    private static string[] Tokenize(string text) => text.ToLowerInvariant().Split(
        new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '-', '—' },
        StringSplitOptions.RemoveEmptyEntries);

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static async Task EnsureModelAsync(string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            return;
        }

        string name = Path.GetFileName(path);
        string url = $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{name}";
        Console.WriteLine($"Downloading {name} from {url} ...");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var http = new HttpClient();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst, ct);
        Console.WriteLine($"Downloaded {name}.");
    }

    private static float[] ReadMono16k(string path)
    {
        using var reader = new WaveFileReader(path);
        WaveFormat format = reader.WaveFormat;
        if (format.BitsPerSample != 16)
        {
            throw new InvalidOperationException($"Expected 16-bit PCM but found {format.BitsPerSample}-bit.");
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

        if (format.SampleRate != SttRate)
        {
            throw new InvalidOperationException($"Expected 16 kHz but found {format.SampleRate} Hz.");
        }

        return mono;
    }

    private enum ConverterKind
    {
        None,
        Sinc,
        Wdl,
    }

    private sealed record ConversionResult(float[] Output, TimeSpan Wall, TimeSpan Cpu, long AllocatedBytes);

    private sealed record RowResult(string Impl, string Path, double WallMs, double Factor, double CpuMs, double AllocMb, float[] Output)
    {
        public string Format() =>
            $"{Impl,-9} {Path,-12} {WallMs,7:0}  {Factor,5:0.00}x {CpuMs,7:0}  {AllocMb,9:0.0}  {Output.Length,10}";
    }
}
