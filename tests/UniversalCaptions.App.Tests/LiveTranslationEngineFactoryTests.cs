using UniversalCaptions.App;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Tests for <see cref="LiveTranslationEngineFactory"/>. The factory is the single seam where the
/// App decides whether to construct an <see cref="ILiveAudioTranslationEngine"/>; its behavior is
/// pinned here so ADR-0009's invariants (no env-var fallback, no thrown exceptions on missing
/// credential, factory never throws) stay enforced.
/// </summary>
public class LiveTranslationEngineFactoryTests
{
    private const string ProviderEnvVar = "UC_LIVE_TRANSLATION_PROVIDER";
    private const string ApiKeyEnvVar = "UC_GEMINI_API_KEY";

    [Fact]
    public void Create_ProviderUnset_Returns_Null()
    {
        WithEnv(ProviderEnvVar, null, () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_ProviderNone_Returns_Null()
    {
        WithEnv(ProviderEnvVar, "none", () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_ProviderOff_Returns_Null()
    {
        WithEnv(ProviderEnvVar, "off", () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_ProviderUnknown_Returns_Null()
    {
        WithEnv(ProviderEnvVar, "future-mt-provider", () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_ProviderArgos_Returns_Null()
    {
        // The App-side factory only constructs cloud live-translation engines (Gemini). Argos is a
        // local ITranslationEngine wired separately in App.xaml.cs.
        WithEnv(ProviderEnvVar, "argos", () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_ProviderGemini_NoCredential_Returns_Null()
    {
        WithEnv(ProviderEnvVar, "gemini", () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_ProviderGemini_WithCredential_Returns_Engine()
    {
        WithEnv(ProviderEnvVar, "gemini", () =>
        {
            InMemoryCredentialStore store = new();
            store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "test-key-value");

            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.NotNull(engine);
        });
    }

    [Fact]
    public void Create_ProviderGemini_Ignores_UC_GEMINI_API_KEY_EnvVar()
    {
        // Regression guard for ADR-0009: the env-var fallback must not exist in the App path. Even
        // with UC_GEMINI_API_KEY set, the factory only consults ICredentialStore. If the store is
        // empty the factory returns null.
        WithEnv(ProviderEnvVar, "gemini", () =>
        {
            WithEnv(ApiKeyEnvVar, "leaked-key-from-env", () =>
            {
                InMemoryCredentialStore store = new();
                ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

                Assert.Null(engine);
            });
        });
    }

    [Fact]
    public void Create_NoEnvVar_UiProviderNull_Returns_Null()
    {
        WithEnv(ProviderEnvVar, null, () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl", provider: null);

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_NoEnvVar_UiProviderArgos_Returns_Null()
    {
        // Argos is the local ITranslationEngine wired separately for caption-line translation; the
        // live-audio factory must not construct any engine for it.
        WithEnv(ProviderEnvVar, null, () =>
        {
            InMemoryCredentialStore store = new();
            store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "test-key-value");

            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl", TranslationProvider.Argos);

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_NoEnvVar_UiProviderGemini_NoCredential_Returns_Null()
    {
        WithEnv(ProviderEnvVar, null, () =>
        {
            InMemoryCredentialStore store = new();
            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl", TranslationProvider.Gemini);

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_NoEnvVar_UiProviderGemini_WithCredential_Returns_Engine()
    {
        // THE FIX: selecting "Gemini (cloud)" in the control window (translation enabled) must
        // construct the Gemini engine from the stored credential — with no environment variable at
        // all. Previously the UI provider was dead-ended in UserSettings and only
        // UC_LIVE_TRANSLATION_PROVIDER could produce an engine (hence zero Gemini usage).
        WithEnv(ProviderEnvVar, null, () =>
        {
            InMemoryCredentialStore store = new();
            store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "test-key-value");

            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl", TranslationProvider.Gemini);

            Assert.NotNull(engine);
        });
    }

    [Fact]
    public void Create_NoEnvVar_UiProviderGemini_Ignores_UC_GEMINI_API_KEY_EnvVar()
    {
        // ADR-0009 holds for the UI path too: a leaked UC_GEMINI_API_KEY must never be the App's key.
        WithEnv(ProviderEnvVar, null, () =>
        {
            WithEnv(ApiKeyEnvVar, "leaked-key-from-env", () =>
            {
                InMemoryCredentialStore store = new();
                ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl", TranslationProvider.Gemini);

                Assert.Null(engine);
            });
        });
    }

    [Fact]
    public void Create_UiProviderArgos_Beats_EnvVarGemini()
    {
        // Precedence guard: an explicit UI provider wins over UC_LIVE_TRANSLATION_PROVIDER (the env
        // var is only the no-arg default). A machine-level env var must never resurrect a cloud
        // engine the user turned off in the UI.
        WithEnv(ProviderEnvVar, "gemini", () =>
        {
            InMemoryCredentialStore store = new();
            store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "test-key-value");

            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl", TranslationProvider.Argos);

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_UiProviderGemini_Beats_EnvVarNone()
    {
        // Symmetric precedence guard: an explicit Gemini selection in the UI is not disabled by an
        // env "none" default — the user's runtime choice governs the App session.
        WithEnv(ProviderEnvVar, "none", () =>
        {
            InMemoryCredentialStore store = new();
            store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "test-key-value");

            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl", TranslationProvider.Gemini);

            Assert.NotNull(engine);
        });
    }

    [Fact]
    public void Create_ProviderGemini_StoreException_DoesNotThrow()
    {
        // ADR-0009 invariant: the factory never throws. A failing ICredentialStore must not break
        // the offline-only pipeline.
        WithEnv(ProviderEnvVar, "gemini", () =>
        {
            ICredentialStore throwing = new ThrowingCredentialStore();

            ILiveAudioTranslationEngine? engine = null;
            Exception? caught = null;
            try
            {
                engine = LiveTranslationEngineFactory.Create(throwing, "en", "tl");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.Null(caught);
            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_NullStore_DoesNotThrow()
    {
        WithEnv(ProviderEnvVar, "gemini", () =>
        {
            ILiveAudioTranslationEngine? engine = null;
            Exception? caught = null;
            try
            {
                engine = LiveTranslationEngineFactory.Create(null!, "en", "tl");
            }
            catch (Exception ex)
            {
                caught = ex;
            }

            Assert.Null(caught);
            Assert.Null(engine);
        });
    }

    [Fact]
    public void GeminiApiKeyTarget_Is_Documented_String()
    {
        // Pinning this constant prevents accidental rename that would orphan production credentials.
        Assert.Equal("UniversalCaptions:GeminiApiKey", LiveTranslationEngineFactory.GeminiApiKeyTarget);
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

    /// <summary>
    /// Test double that throws on every method. Used to prove the factory never throws when the
    /// underlying credential store fails. The methods throw <see cref="InvalidOperationException"/>
    /// because no <see cref="ICredentialStore"/> implementation in the codebase is expected to
    /// throw — these tests pin the factory's "never throws" invariant against future regression.
    /// </summary>
    private sealed class ThrowingCredentialStore : ICredentialStore
    {
        public bool HasCredential(string key) => throw new InvalidOperationException("store failure");
        public string? TryGetCredential(string key) => throw new InvalidOperationException("store failure");
        public bool SetCredential(string key, string value) => throw new InvalidOperationException("store failure");
        public bool RemoveCredential(string key) => throw new InvalidOperationException("store failure");
    }
}
