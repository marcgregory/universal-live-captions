using UniversalCaptions.App;
using UniversalCaptions.App.Settings;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Tests for <see cref="LiveTranslationEngineFactory"/> (ADR-0011: Gemini is the only engine, so the
/// factory has no provider selection). Its behavior is pinned here so ADR-0009's invariants (no
/// env-var key fallback, no thrown exceptions on missing credential, factory never throws) stay
/// enforced.
/// </summary>
public class LiveTranslationEngineFactoryTests
{
    private const string ApiKeyEnvVar = "UC_GEMINI_API_KEY";

    [Fact]
    public void Create_NoCredential_Returns_Null()
    {
        InMemoryCredentialStore store = new();

        ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

        Assert.Null(engine);
    }

    [Fact]
    public void Create_WithCredential_Returns_Engine()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "test-key-value");

        ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

        Assert.NotNull(engine);
        engine.Dispose();
    }

    [Fact]
    public void Create_Ignores_UC_GEMINI_API_KEY_EnvVar()
    {
        // Regression guard for ADR-0009: the env-var fallback must not exist in the App path. Even
        // with UC_GEMINI_API_KEY set, the factory only consults ICredentialStore. If the store is
        // empty the factory returns null.
        WithEnv(ApiKeyEnvVar, "leaked-key-from-env", () =>
        {
            InMemoryCredentialStore store = new();

            ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

            Assert.Null(engine);
        });
    }

    [Fact]
    public void Create_BlankCredential_Returns_Null()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "   ");

        ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, "en", "tl");

        Assert.Null(engine);
    }

    [Fact]
    public void Create_NullLanguages_StillBuildsEngine()
    {
        // Auto-detect source + default target is a valid session configuration.
        InMemoryCredentialStore store = new();
        store.SetCredential(LiveTranslationEngineFactory.GeminiApiKeyTarget, "test-key-value");

        ILiveAudioTranslationEngine? engine = LiveTranslationEngineFactory.Create(store, null, null);

        Assert.NotNull(engine);
        engine.Dispose();
    }

    [Fact]
    public void Create_StoreException_DoesNotThrow()
    {
        // ADR-0009 invariant: the factory never throws. A failing ICredentialStore degrades to
        // "no key", which the pipeline surfaces as an actionable missing-key error.
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
    }

    [Fact]
    public void Create_NullStore_DoesNotThrow()
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
