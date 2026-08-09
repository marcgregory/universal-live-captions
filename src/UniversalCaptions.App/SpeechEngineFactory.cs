using System;
using System.Globalization;
using System.IO;
using UniversalCaptions.Audio.Processing;
using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech;

namespace UniversalCaptions.App;

/// <summary>
/// Builds the production <see cref="ISpeechToTextEngine"/> from environment knobs. Extracted from the
/// DI composition root so the default/fallback selection is unit-testable (Entry 14).
/// </summary>
/// <remarks>
/// Promotion (2026-08-05, Entry 14): the production default is now the faster-whisper native
/// streaming engine with Chrome-style live partials enabled. <c>UC_STT_ENGINE=ggml-base</c> selects
/// the original local-Whisper engine as the explicit fallback; <c>fasterwhisper</c> selects the
/// windowed faster-whisper engine; <c>fasterwhisper-native</c> explicitly selects the same native
/// default path. There is deliberately no automatic runtime fallback — silently switching engines
/// mid-session would violate the ADR-0003 "no silent model switch" rule.
/// </remarks>
public static class SpeechEngineFactory
{
    /// <summary>
    /// Selects and constructs the speech-to-text engine from <c>UC_STT_ENGINE</c> (and the
    /// <c>UC_STT_*</c> / <c>UC_NATIVE_*</c> / <c>UC_FW_PYTHON</c> knobs).
    /// </summary>
    public static ISpeechToTextEngine Create(string? language)
    {
        string engine = Environment.GetEnvironmentVariable("UC_STT_ENGINE")?.Trim().ToLowerInvariant() ?? string.Empty;

        if (engine == "ggml-base")
        {
            return new WhisperSpeechToTextEngine(new WhisperEngineOptions
            {
                ModelPath = ResolveModelPath(),
                Language = Normalize(language),
                WindowDuration = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_WINDOW", 8)),
                // 0.5 s interval: decodes 2× per second so partials appear as the speaker talks
                // without triggering epoch boundary transitions too frequently (was 0.3 s, which
                // caused rapid duplicate caption replay due to Whisper sliding-window resets).
                DecodeInterval = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_INTERVAL", 0.5)),
                // 0.5 s minimum before first decode: Whisper can produce reliable output from
                // ~0.5 s of audio. The previous 2 s default guaranteed a 2 s silent wait before
                // the first caption ever appeared.
                MinimumAudioBeforeFirstDecode = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_MIN_AUDIO", 0.5)),
                StabilityWindow = ResolveIntEnv("UC_STT_STABILITY", 2),
            });
        }

        if (engine == "fasterwhisper")
        {
            return new FasterWhisperSpeechToTextEngine(new FasterWhisperEngineOptions
            {
                PythonExecutablePath = ResolveFasterWhisperPython(),
                Language = Normalize(language),
                WindowDuration = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_WINDOW", 8)),
                DecodeInterval = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_INTERVAL", 0.5)),
                MinimumAudioBeforeFirstDecode = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_STT_MIN_AUDIO", 0.5)),
                StabilityWindow = ResolveIntEnv("UC_STT_STABILITY", 2),
            });
        }

        // Default production path: faster-whisper native streaming (one FINAL per completed speech
        // segment, C#-side VAD) with Chrome-style live partials on (UC_NATIVE_PARTIAL_INTERVAL
        // seconds of new speech between partial decodes, each bounded to the last
        // UC_NATIVE_PARTIAL_WINDOW seconds; set the interval to 0 for the FINAL-only Slice 10/11
        // behavior). The 8 s MaxSegmentDuration cap is frozen (Slice 11 decision).
        return CreateNative(language);
    }

    /// <summary>
    /// Builds the faster-whisper native streaming engine with the validated production knobs
    /// (8 s segment cap, hangover 0.7 s, partials interval 1 s / window 4 s) and the CPU-optimized
    /// decode-thread cap: <c>UC_NATIVE_THREADS</c> (default 4, clamped to [1, ProcessorCount]) so the
    /// continuous FINAL+partial decode load stays ~26% of the machine instead of saturating all cores.
    /// </summary>
    public static FasterWhisperNativeStreamingEngine CreateNative(string? language)
    {
        return new FasterWhisperNativeStreamingEngine(
            new FasterWhisperEngineOptions
            {
                PythonExecutablePath = ResolveFasterWhisperPython(),
                Language = Normalize(language),
                Model = ResolveNativeModel(),
                PartialDecodeInterval = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_NATIVE_PARTIAL_INTERVAL", 1)),
                PartialDecodeWindow = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_NATIVE_PARTIAL_WINDOW", 4)),
                Threads = ResolveNativeThreads(),
            },
            new EnergyVad(new VadOptions(RmsThreshold: 0.008, MinActiveChunks: 1, SilenceHangoverChunks: 2)),
            new SpeechSegmentDetectorOptions
            {
                SampleRate = 16_000,
                MinSpeechDuration = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_NATIVE_MIN_SPEECH", 0.3)),
                SilenceHangover = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_NATIVE_HANGOVER", 0.7)),
                MaxSegmentDuration = TimeSpan.FromSeconds(ResolveDoubleEnv("UC_NATIVE_MAX_SEGMENT", 8)),
            });
    }

    private static string? Normalize(string? language)
    {
        return string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the Whisper model path: the <c>UC_STT_MODEL_PATH</c> environment variable when set,
    /// otherwise the repository-relative <c>artifacts/models/ggml-base.bin</c>.
    /// </summary>
    private static string ResolveModelPath()
    {
        string? configured = Environment.GetEnvironmentVariable("UC_STT_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Path.Combine("artifacts", "models", "ggml-base.bin");
    }

    /// <summary>
    /// Resolves the Python interpreter hosting faster-whisper. Delegates to
    /// <see cref="InstallPathResolver.ResolveFasterWhisperPython"/> so the resolution chain
    /// (env var → bundled install sibling → legacy <c>%TEMP%\fwv</c> venv → system
    /// <c>python</c>) is shared with the Argos resolver in <c>App.xaml.cs</c>.
    /// </summary>
    private static string ResolveFasterWhisperPython()
    {
        return InstallPathResolver.ResolveFasterWhisperPython();
    }

    /// <summary>
    /// Reads an optional integer benchmark override (for example <c>UC_STT_STABILITY</c>); returns
    /// <paramref name="fallback"/> when unset or unparseable. Overrides never change the built-in
    /// default — the fallback here is the validated Slice 6 baseline (8 s window / 1 s interval /
    /// StabilityWindow 2), the single authoritative configuration shared with the benchmark.
    /// </summary>
    private static int ResolveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int value) ? value : fallback;
    }

    private static double ResolveDoubleEnv(string name, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }

    /// <summary>
    /// Resolves the faster-whisper model for the production native path: the <c>UC_FW_MODEL</c>
    /// environment variable when set, otherwise the frozen production default <c>small</c>.
    /// <c>UC_FW_MODEL</c> exists so the packaged/installed app can point at a bundled offline model
    /// directory (the worker forwards it verbatim as <c>--model</c>, and faster-whisper accepts a
    /// directory path without touching the HuggingFace cache); when unset, behavior is identical to
    /// the pre-installer build. Deliberately scoped to the native default path only.
    /// </summary>
    private static string ResolveNativeModel()
    {
        string? configured = Environment.GetEnvironmentVariable("UC_FW_MODEL");
        return string.IsNullOrWhiteSpace(configured) ? "small" : configured;
    }

    /// <summary>
    /// Resolves the faster-whisper decode-thread count: <c>UC_NATIVE_THREADS</c> when set to a
    /// value in [1, ProcessorCount], otherwise the production default of 4 (Entry 16 CPU
    /// optimization — thread-count-invariant decode wall for real speech, so capping at 4 cuts
    /// sustained STT CPU from ~77% of the machine to ~26% without a caption regression).
    /// </summary>
    private static int ResolveNativeThreads()
    {
        int maxThreads = Math.Max(1, Environment.ProcessorCount);
        int fallback = Math.Min(4, maxThreads);
        string? raw = Environment.GetEnvironmentVariable("UC_NATIVE_THREADS");
        return int.TryParse(raw, out int value) && value >= 1 && value <= maxThreads
            ? value
            : fallback;
    }
}
