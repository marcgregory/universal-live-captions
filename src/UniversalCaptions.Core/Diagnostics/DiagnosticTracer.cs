using System;
using System.Diagnostics;

namespace UniversalCaptions.Core.Diagnostics;

public static class DiagnosticTracer
{
    private static readonly Stopwatch _sw = new();
    private static TimeSpan?[] _times = new TimeSpan?[8];
    private static readonly object _lock = new();

    public static void StartSession()
    {
        lock (_lock)
        {
            _sw.Restart();
            Array.Clear(_times);
            RecordLocked(0, "Video/Audio playback starts (Capture started)");
        }
    }
    
    public static void Record(int stage, string name)
    {
        lock (_lock)
        {
            RecordLocked(stage, name);
        }
    }

    private static void RecordLocked(int stage, string name)
    {
        if (stage >= 0 && stage < 8 && _times[stage] == null)
        {
            _times[stage] = _sw.Elapsed;
            var current = _times[stage]!.Value;
            Console.Error.WriteLine($"[DIAGNOSTICS] T{stage}: {current.TotalSeconds:F3}s - {name}");
            
            if (stage > 0)
            {
                TimeSpan? prev = _times[stage - 1];
                if (prev != null)
                {
                     Console.Error.WriteLine($"[DIAGNOSTICS]       Delta (T{stage} - T{stage-1}): {(current - prev.Value).TotalSeconds:F3}s");
                }
            }
            if (stage == 7 && _times[0] != null)
            {
                Console.Error.WriteLine($"[DIAGNOSTICS] === ACTUAL E2E LATENCY (T7 - T0): {(current - _times[0]!.Value).TotalSeconds:F3}s ===");
            }
        }
    }
}
