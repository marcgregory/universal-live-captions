using System.Diagnostics;
using UniversalCaptions.Audio.Capture;
using UniversalCaptions.Audio.Metering;
using UniversalCaptions.Core.Audio;
using UniversalCaptions.Core.Capture;

namespace UniversalCaptions.Diagnostics;

internal static class Program
{
    private static int Main(string[] args)
    {
        int? deviceIndex = null;
        double? seconds = null;

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
        Console.WriteLine("  -h, --help         Show this help.");
    }
}
