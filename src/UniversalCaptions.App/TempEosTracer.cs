using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UniversalCaptions.App;

/// <summary>
/// TEMPORARY diagnostic tracer for the end-of-stream (last caption after Stop) latency investigation.
/// Records per-sequence timestamps across three events — FINAL (Whisper final ready), TRANSLATED
/// (CaptionLineUpdated, translation applied), and RENDER (overlay Render executed on the dispatcher) —
/// and writes the deltas to stderr. Diagnostic-only; no caption behavior/timing/config is changed.
/// This class is additive landscaping and will be deleted after the live measurement.
/// </summary>
internal static class TempEosTracer
{
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly Dictionary<long, double> Finals = new();
    private static readonly Dictionary<long, double> Translated = new();
    private static readonly Dictionary<long, double> Rendered = new();
    private static readonly Dictionary<long, bool> PairPrinted = new();

    public static void Final(long sequence)
    {
        lock (Gate)
        {
            if (Finals.TryAdd(sequence, Sw.Elapsed.TotalSeconds))
            {
                Console.Error.WriteLine($"[EOS] FINAL      #{sequence}  {Finals[sequence]:F3}s");
            }
        }
    }

    public static void Translated(long sequence)
    {
        lock (Gate)
        {
            if (Translated.TryAdd(sequence, Sw.Elapsed.TotalSeconds))
            {
                Console.Error.WriteLine($"[EOS] TRANSLATED #{sequence}  {Translated[sequence]:F3}s  (+{(Translated[sequence] - Finals.GetValueOrDefault(sequence, Translated[sequence])):F3}s from FINAL)");
                PairPrinted[sequence] = true;
            }
        }
    }

    public static void Render(long sequence)
    {
        if (sequence < 0)
        {
            return;
        }

        lock (Gate)
        {
            if (Rendered.ContainsKey(sequence))
            {
                return;
            }

            Rendered[sequence] = Sw.Elapsed.TotalSeconds;
            Console.Error.WriteLine($"[EOS] RENDER     #{sequence}  {Rendered[sequence]:F3}s  (+{(Rendered[sequence] - Translated.GetValueOrDefault(sequence, Rendered[sequence])):F3}s from TRANSLATED)");
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            Sw.Restart();
            Finals.Clear();
            Translated.Clear();
            Rendered.Clear();
            PairPrinted.Clear();
        }
    }
}