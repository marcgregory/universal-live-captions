// TEMP-DIAGNOSTIC (REMOVE AFTER ROOT-CAUSE INVESTIGATION)
// Isolated latency probe for the audio capture path. Not part of the product.
// Measures T0..T8. Gated behind env var UC_LATENCY_PROBE=1 so it is inert unless enabled.

using System;
using System.Diagnostics;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Core.Audio;

namespace UniversalCaptions.App.Pipeline;

internal static class TempaudioLatencyProbe
{
    private static readonly Stopwatch _sw = new();
    private static bool _enabled;
    private static bool _chunkSeen;
    private static bool _nonSilent;
    private static int _dispatched;
    private static EnergyVad? _vad;

    private static readonly double _rmsThreshold = 0.008;

    private static readonly object _lock = new();

    private static bool IsEnabled()
    {
        if (_enabled)
        {
            return true;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("UC_LATENCY_PROBE"), "1", StringComparison.Ordinal))
        {
            lock (_lock)
            {
                if (!_enabled)
                {
                    _sw.Start();
                    _vad = new EnergyVad(new VadOptions(_rmsThreshold, MinActiveChunks: 1, SilenceHangoverChunks: 6));
                    _enabled = true;
                }
            }
        }

        return _enabled;
    }

    public static void RecordCaptureStarted()
    {
        if (!IsEnabled())
        {
            return;
        }

        Log("T0", "session init begins / Start() called");
    }

    public static void RecordDeviceStarted()
    {
        if (!IsEnabled())
        {
            return;
        }

        Log("T1", "capture device/stream started (IsCapturing)");
    }

    public static void RecordChunk(AudioChunk chunk)
    {
        if (!IsEnabled())
        {
            return;
        }

        lock (_lock)
        {
            if (!_chunkSeen)
            {
                _chunkSeen = true;
                Log("T2", "first audio chunk received (any)");
            }

            if (!_nonSilent && _vad!.IsSpeech(chunk))
            {
                _nonSilent = true;
                Log("T3", $"first NON-SILENT audio detected (rms >= {_rmsThreshold})");
            }
        }
    }

    public static void RecordDispatch()
    {
        if (!IsEnabled())
        {
            return;
        }

        lock (_lock)
        {
            _dispatched++;
            if (_dispatched == 1)
            {
                Log("T4", "first audio chunk dispatched to STT/Whisper");
            }
        }
    }

    public static void RecordWhisperFirstDecode()
    {
        if (!IsEnabled())
        {
            return;
        }

        Log("T5", "Whisper first decode pass begins");
    }

    public static void RecordPartial()
    {
        if (!IsEnabled())
        {
            return;
        }

        Log("T6", "first Whisper Partial result");
    }

    public static void RecordFinal()
    {
        if (!IsEnabled())
        {
            return;
        }

        Log("T7", "first Whisper Final (committed) result");
    }

    private static void Log(string tag, string what)
    {
        Console.Error.WriteLine($"[LATENCY] {tag}: {_sw.Elapsed.TotalSeconds:F3}s - {what}");
    }
}
