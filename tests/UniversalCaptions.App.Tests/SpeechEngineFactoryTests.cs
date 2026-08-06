using UniversalCaptions.Core.Speech;
using UniversalCaptions.Speech;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies the production engine selection (Entry 14 promotion): the default is the
/// faster-whisper native streaming engine with Chrome-style live partials; ggml-base remains the
/// explicit fallback; the windowed faster-whisper engine stays opt-in.
/// </summary>
public class SpeechEngineFactoryTests
{
    private const string EngineVar = "UC_STT_ENGINE";
    private const string PartialIntervalVar = "UC_NATIVE_PARTIAL_INTERVAL";
    private const string ThreadsVar = "UC_NATIVE_THREADS";
    private const string ModelVar = "UC_FW_MODEL";

    [Fact]
    public void Default_NoEngineVar_ReturnsFasterWhisperNativeStreamingEngine()
    {
        WithEnv(EngineVar, null, () =>
        {
            var engine = SpeechEngineFactory.Create("tl");
            Assert.IsType<FasterWhisperNativeStreamingEngine>(engine);
        });
    }

    [Fact]
    public void GgmlBaseEnv_ReturnsWhisperSpeechToTextEngine()
    {
        WithEnv(EngineVar, "ggml-base", () =>
        {
            var engine = SpeechEngineFactory.Create("tl");
            Assert.IsType<WhisperSpeechToTextEngine>(engine);
        });
    }

    [Fact]
    public void FasterWhisperEnv_ReturnsWindowedFasterWhisperEngine()
    {
        WithEnv(EngineVar, "fasterwhisper", () =>
        {
            var engine = SpeechEngineFactory.Create("tl");
            Assert.IsType<FasterWhisperSpeechToTextEngine>(engine);
        });
    }

    [Fact]
    public void FasterWhisperNativeEnv_ReturnsNativeEngine()
    {
        WithEnv(EngineVar, "fasterwhisper-native", () =>
        {
            var engine = SpeechEngineFactory.Create("tl");
            Assert.IsType<FasterWhisperNativeStreamingEngine>(engine);
        });
    }

    [Fact]
    public void PartialIntervalZero_StillReturnsNativeEngine()
    {
        // The FINAL-only Slice 10/11 knob (UC_NATIVE_PARTIAL_INTERVAL=0) tunes the native engine; it
        // does not change engine selection.
        WithEnv(EngineVar, null, () => WithEnv(PartialIntervalVar, "0", () =>
        {
            var engine = SpeechEngineFactory.Create("tl");
            Assert.IsType<FasterWhisperNativeStreamingEngine>(engine);
        }));
    }

    [Fact]
    public void NativeThreads_Default_IsFour()
    {
        // Entry 16 CPU optimization: the production default caps decode threads at 4 (vs the
        // FasterWhisperEngineOptions ProcessorCount default) so sustained STT CPU drops ~77%->~26%
        // of the machine. Decode wall is thread-count-invariant for real speech.
        WithEnv(EngineVar, null, () => WithEnv(ThreadsVar, null, () =>
        {
            var engine = (FasterWhisperNativeStreamingEngine)SpeechEngineFactory.Create("tl");
            Assert.Equal(4, engine.Options.Threads);
        }));
    }

    [Fact]
    public void NativeThreads_Override_IsRespected()
    {
        WithEnv(EngineVar, null, () => WithEnv(ThreadsVar, "6", () =>
        {
            var engine = (FasterWhisperNativeStreamingEngine)SpeechEngineFactory.Create("tl");
            Assert.Equal(6, engine.Options.Threads);
        }));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("99")]
    public void NativeThreads_InvalidOrOutOfRange_DefaultsToFour(string value)
    {
        // Unparseable, sub-1, or above-ProcessorCount values fall back to the production default 4.
        WithEnv(EngineVar, null, () => WithEnv(ThreadsVar, value, () =>
        {
            var engine = (FasterWhisperNativeStreamingEngine)SpeechEngineFactory.Create("tl");
            Assert.Equal(4, engine.Options.Threads);
        }));
    }

    [Fact]
    public void NativeModel_Unset_DefaultsToSmall()
    {
        // Installer seam: UC_FW_MODEL unset must keep the frozen production model ("small") —
        // behavior identical to the pre-installer build.
        WithEnv(EngineVar, null, () => WithEnv(ModelVar, null, () =>
        {
            var engine = (FasterWhisperNativeStreamingEngine)SpeechEngineFactory.Create("tl");
            Assert.Equal("small", engine.Options.Model);
        }));
    }

    [Fact]
    public void NativeModel_Override_IsRespected()
    {
        // Installer seam: the packaged app points UC_FW_MODEL at the bundled model directory; the
        // worker forwards it verbatim as --model, and faster-whisper resolves a directory path
        // offline (no HuggingFace cache).
        WithEnv(EngineVar, null, () => WithEnv(ModelVar, @"C:\apps\UniversalCaptions\models\small-int8", () =>
        {
            var engine = (FasterWhisperNativeStreamingEngine)SpeechEngineFactory.Create("tl");
            Assert.Equal(@"C:\apps\UniversalCaptions\models\small-int8", engine.Options.Model);
        }));
    }

    private static void WithEnv(string name, string? value, Action action)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
