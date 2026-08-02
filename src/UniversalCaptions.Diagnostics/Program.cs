using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Audio.Metering;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Capture;

namespace UniversalCaptions.Diagnostics;

internal static class Program
{
    private static int Main(string[] args)
    {
        int? deviceIndex = null;
        double? seconds = null;
        string? latencyWav = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--device" when i + 1 < args.Length && int.TryParse(args[i + 1], out int device):
                    deviceIndex = device;
                    i++;
                    break;
                case "--seconds" when i + 1 < args.Length && double.TryParse(args[i + 1], out double secs):
                    seconds = secs;
                    i++;
                    break;
                case "--latency" when i + 1 < args.Length:
                    latencyWav = args[i + 1];
                    i++;
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
            }
        }

        Console.WriteLine($"Universal Live Captions - Audio Diagnostics");
        Console.WriteLine($"Runtime: {Environment.Version} on {Environment.OSVersion.VersionString}");
        Console.WriteLine("Privacy: audio is processed in memory only; nothing is recorded or transmitted.");
        Console.WriteLine();

        IReadOnlyList<LoopbackDevice> devices = LoopbackDeviceEnumerator.EnumerateRenderDevices();
        Console.WriteLine($"Output devices found: {devices.Count}");
        for (int i = 0; i < devices.Count; i++)
        {
            Console.WriteLine($"  [{i}] {devices[i].FriendlyName}");
        }

        if (devices.Count == 0)
        {
            Console.Error.WriteLine("ERROR: No audio output device was found. Connect a speaker or headset and try again.");
            return 1;
        }

        if (latencyWav is not null)
        {
            int idx = deviceIndex ?? 0;
            return RunLatencyProbe(devices[idx], latencyWav, seconds);
        }

        IAudioCapture capture;
        try
        {
            capture = deviceIndex.HasValue
                ? WasapiLoopbackCaptureSource.CreateForDevice(devices[deviceIndex.Value].Id)
                : WasapiLoopbackCaptureSource.CreateDefault();
        }
        catch (AudioCaptureException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }

        using (capture)
        {
            return RunMeter(capture, seconds);
        }
    }

    // TEMP-DIAGNOSTIC (REMOVE AFTER ROOT-CAUSE INVESTIGATION)
    // Measures T0 (session init) -> T1 (device started) -> T2 (first buffer) -> T3 (first non-silent)
    // while playing a short WAV into the SAME render device via loopback.
    private static int RunLatencyProbe(LoopbackDevice device, string wavPath, double? seconds)
    {
        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"ERROR: WAV file not found: {wavPath}");
            return 2;
        }

        var sw = Stopwatch.StartNew();
        sw.Stop();
        double lastTag = 0;
        void Tag(string tag, string what, bool force = false)
        {
            double t = sw.Elapsed.TotalSeconds;
            Console.Error.WriteLine($"[LATENCY] {tag}: {t:F3}s - {what} (gap from prev {t - lastTag:F3}s)");
            lastTag = t;
        }

        using var done = new ManualResetEventSlim(false);
        bool firstByte = false;
        double? firstNonSilent = null;
        var vad = new EnergyVad(new VadOptions(RmsThreshold: 0.008, MinActiveChunks: 1, SilenceHangoverChunks: 6));

        Console.WriteLine($"Latency probe: playing '{Path.GetFileName(wavPath)}' into '{device.FriendlyName}'.");
        Console.WriteLine("START marker logged at capture Start(). Playback begins on the same call.");
        Console.WriteLine();

        // T0
        sw.Start();
        Tag("T0", "session init begins");

        IAudioCapture capture;
        try
        {
            capture = WasapiLoopbackCaptureSource.CreateForDevice(device.Id);
        }
        catch (AudioCaptureException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }

        capture.CaptureFailed += (_, error) =>
        {
            Console.Error.WriteLine($"\nERROR: {error.Message} ({error.Kind})");
            done.Set();
        };

        capture.AudioAvailable += (_, chunk) =>
        {
            if (!firstByte)
            {
                firstByte = true;
                Tag("T2", "first audio buffer/chunk received");
            }

            if (firstNonSilent is null && vad.IsSpeech(chunk))
            {
                firstNonSilent = sw.Elapsed.TotalSeconds;
                Tag("T3", "first NON-SILENT audio detected (rms >= 0.001)");
            }
        };

        // Play the WAV into the same device. If captures use the device mix, this routes playback
        // into loopback. (WAV format sample rate may not match device; NAudio resamples via ResamplerDmoStream.)
        WaveOutEvent? player = null;
        try
        {
            var reader = new AudioFileReader(wavPath);
            var targetRate = new WaveFormat(48000, 16, 2);
            player = new WaveOutEvent { DeviceNumber = -1 }; // default render device
            player.Init(new MediaFoundationResampler(reader, targetRate.SampleRate));
            player.Play();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: could not route playback to default device: {ex.Message}");
        }

        // T1
        try
        {
            capture.Start();
        }
        catch (AudioCaptureException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            player?.Dispose();
            return 2;
        }
        Tag("T1", "capture device/stream started (IsCapturing)");

        double waitSeconds = seconds ?? 8;
        double elapsed = sw.Elapsed.TotalSeconds;
        done.Wait(TimeSpan.FromSeconds(waitSeconds));

        double tNow = sw.Elapsed.TotalSeconds;
        Console.WriteLine();
        Console.WriteLine($"----- Probe complete after {tNow:F3}s. firstByte={firstByte} firstNonSilent={firstNonSilent?.ToString("F3") ?? "NEVER"}");

        if (firstNonSilent is not null)
        {
            Console.WriteLine($"[RESULT] T3 - T0 (first non-silent audio detected): {firstNonSilent:F3}s after Start()");
        }
        else
        {
            Console.WriteLine($"[RESULT] NO non-silent audio within {waitSeconds}s of Start(). If the source played and audio routes to the default render device, this indicates capture-side delay.");
        }

        capture.Stop();
        capture.Dispose();
        player?.Stop();
        player?.Dispose();
        return 0;
    }

    private static int RunMeter(IAudioCapture capture, double? seconds)
    {
        var meter = new AudioLevelMeter();
        var stopwatch = Stopwatch.StartNew();
        var lastPrint = Stopwatch.StartNew();
        double windowRms = 0;
        double windowPeak = 0;
        int windowChunks = 0;
        long lastSequence = 0;
        TimeSpan windowDuration = TimeSpan.Zero;

        using var done = new ManualResetEventSlim(false);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.Set();
        };

        capture.CaptureFailed += (_, error) =>
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERROR: {error.Message}");
            Console.Error.WriteLine($"  ({error.Kind})");
            done.Set();
        };

        meter.LevelUpdated += (_, reading) =>
        {
            windowRms = Math.Max(windowRms, reading.Rms);
            windowPeak = Math.Max(windowPeak, reading.Peak);
            windowDuration += reading.WindowDuration;
            windowChunks++;
            lastSequence = reading.Sequence;

            if (lastPrint.ElapsedMilliseconds >= 100)
            {
                PrintMeterLine(windowRms, windowPeak, windowChunks, windowDuration, lastSequence, stopwatch.Elapsed);
                windowRms = 0;
                windowPeak = 0;
                windowChunks = 0;
                windowDuration = TimeSpan.Zero;
                lastPrint.Restart();
            }
        };

        capture.AudioAvailable += (_, chunk) => meter.Process(chunk);

        capture.Start();
        if (!capture.IsCapturing)
        {
            return 3;
        }

        Console.WriteLine($"Capturing system audio via WASAPI loopback.");
        Console.WriteLine($"Format: {capture.Format}. Press Ctrl+C to stop.");
        Console.WriteLine();

        if (seconds.HasValue)
        {
            done.Wait(TimeSpan.FromSeconds(seconds.Value));
        }
        else
        {
            done.Wait();
        }

        capture.Stop();
        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"Capture stopped after {stopwatch.Elapsed.TotalSeconds:0.0}s. Last chunk sequence: {lastSequence}.");
        return 0;
    }

    private static void PrintMeterLine(double rms, double peak, int chunks, TimeSpan window, long sequence, TimeSpan elapsed)
    {
        const int barWidth = 32;
        int filled = (int)Math.Clamp(Math.Round(peak * barWidth), 0, barWidth);
        string bar = new('=', filled);
        string empty = new(' ', barWidth - filled);

        Console.Write(
            $"\r[{bar}{empty}] peak {peak,6:0.000}  rms {rms,6:0.000}  " +
            $"{chunks,3} chunks / {window.TotalMilliseconds,5:0} ms  seq #{sequence,-8} elapsed {elapsed:hh\\:mm\\:ss}");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: UniversalCaptions.Diagnostics [options]");
        Console.WriteLine("  --device <index>   Capture a specific output device (index from the device list).");
        Console.WriteLine("  --seconds <n>      Stop automatically after n seconds.");
        Console.WriteLine("  --latency <wav>    Latency probe: play <wav> into the default device and time T0..T3.");
        Console.WriteLine("  -h, --help         Show this help.");
    }
}
