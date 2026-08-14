using UniversalCaptions.App.Settings;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Tests for <see cref="GeminiAvailabilityEvaluator"/>: the fast local syntax gate and the
/// authoritative live validation. Pins the rule that drives the provider dropdown — Gemini is
/// usable only when a syntactically plausible key is stored and (live) accepted.
/// </summary>
public class GeminiAvailabilityEvaluatorTests
{
    private const string Target = "UniversalCaptions:GeminiApiKey";

    [Fact]
    public void Evaluate_NoKey_Missing()
    {
        InMemoryCredentialStore store = new();
        var evaluator = new GeminiAvailabilityEvaluator(store, new StubValidator(GeminiAvailability.Available));

        Assert.Equal(GeminiAvailability.MissingKey, evaluator.Evaluate());
    }

    [Fact]
    public void Evaluate_MalformedKey_Malformed()
    {
        // Truncated credentials must stay malformed even with a valid prefix (AQ.aaaa is far too
        // short to be a real Auth key).
        InMemoryCredentialStore store = new();
        store.SetCredential(Target, "AQ.aaaa");
        var evaluator = new GeminiAvailabilityEvaluator(store, new StubValidator(GeminiAvailability.Available));

        Assert.Equal(GeminiAvailability.MalformedKey, evaluator.Evaluate());
    }

    [Fact]
    public void Evaluate_ShortKey_Malformed()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential(Target, "AIza");
        var evaluator = new GeminiAvailabilityEvaluator(store, new StubValidator(GeminiAvailability.Available));

        Assert.Equal(GeminiAvailability.MalformedKey, evaluator.Evaluate());
    }

    [Theory]
    [InlineData("AIzaSyDeadBeefDeadBeefDeadBeefDeadBeefDeadBeef")]
    [InlineData("AIzaSyAaaAaaAaaAaaAaaAaaAaaAaaAaaAaaAaaAaa")]
    [InlineData("AQ.AbDeadBeefDeadBeefDeadBeefDeadBeefDeadBeef12345")]
    public void Evaluate_PlausibleKey_Available(string key)
    {
        InMemoryCredentialStore store = new();
        store.SetCredential(Target, key);
        var evaluator = new GeminiAvailabilityEvaluator(store, new StubValidator(GeminiAvailability.InvalidKey));

        // The local gate is optimistic: a plausible key passes without a network round-trip.
        Assert.Equal(GeminiAvailability.Available, evaluator.Evaluate());
    }

    [Fact]
    public async Task EvaluateLiveAsync_NoKey_Missing_WithoutCallingValidator()
    {
        InMemoryCredentialStore store = new();
        var validator = new StubValidator(GeminiAvailability.Available);
        var evaluator = new GeminiAvailabilityEvaluator(store, validator);

        GeminiAvailability result = await evaluator.EvaluateLiveAsync();

        Assert.Equal(GeminiAvailability.MissingKey, result);
        Assert.Equal(0, validator.Calls);
    }

    [Fact]
    public async Task EvaluateLiveAsync_MalformedKey_Malformed_WithoutCallingValidator()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential(Target, "AQ.not-a-key");
        var validator = new StubValidator(GeminiAvailability.Available);
        var evaluator = new GeminiAvailabilityEvaluator(store, validator);

        GeminiAvailability result = await evaluator.EvaluateLiveAsync();

        Assert.Equal(GeminiAvailability.MalformedKey, result);
        Assert.Equal(0, validator.Calls);
    }

    [Fact]
    public async Task EvaluateLiveAsync_ValidSyntax_DefersToValidator()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential(Target, "AIzaSyValidValidValidValidValidValidValid123");
        var validator = new StubValidator(GeminiAvailability.InvalidKey);
        var evaluator = new GeminiAvailabilityEvaluator(store, validator);

        GeminiAvailability result = await evaluator.EvaluateLiveAsync();

        Assert.Equal(GeminiAvailability.InvalidKey, result);
        Assert.Equal(1, validator.Calls);
    }

    [Fact]
    public async Task EvaluateLiveAsync_StoreFailure_DegradesToMissing()
    {
        var evaluator = new GeminiAvailabilityEvaluator(
            new ThrowingStore(),
            new StubValidator(GeminiAvailability.Available));

        Assert.Equal(GeminiAvailability.MissingKey, await evaluator.EvaluateLiveAsync());
        Assert.Equal(GeminiAvailability.MissingKey, evaluator.Evaluate());
    }

    [Fact]
    public void LooksLikeGeminiApiKey_AcceptsBothFormats()
    {
        // Google migrated Gemini key issuance from AIza (Standard) to AQ. (Auth) in 2026 — both
        // prefixes are legitimate and must pass the syntactic gate.
        Assert.True(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey("AIzaSyDeadBeefDeadBeefDeadBeefDeadBeefDeadBeef"));
        Assert.True(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey("AQ.AbDeadBeefDeadBeefDeadBeefDeadBeefDeadBeef12345"));
    }

    [Fact]
    public void LooksLikeGeminiApiKey_RejectsTruncatedOrWrongValues()
    {
        Assert.False(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey("AQ.aaaa"));
        Assert.False(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey("AIza"));
        Assert.False(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey("AQ."));
        Assert.False(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey("totally-not-a-key-xxxxxxxxxxxxxxxxxxxxxxxx"));
        Assert.False(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey(null));
        Assert.False(GeminiAvailabilityEvaluator.LooksLikeGeminiApiKey(string.Empty));
    }

    private sealed class StubValidator : IGeminiApiKeyValidator
    {
        private readonly GeminiAvailability _result;

        public StubValidator(GeminiAvailability result) => _result = result;

        public int Calls { get; private set; }

        public Task<GeminiAvailability> ValidateAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingStore : ICredentialStore
    {
        public bool HasCredential(string key) => throw new InvalidOperationException("store failure");
        public string? TryGetCredential(string key) => throw new InvalidOperationException("store failure");
        public bool SetCredential(string key, string value) => throw new InvalidOperationException("store failure");
        public bool RemoveCredential(string key) => throw new InvalidOperationException("store failure");
    }
}
