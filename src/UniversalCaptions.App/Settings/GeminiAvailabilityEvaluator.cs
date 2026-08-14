namespace UniversalCaptions.App.Settings;

/// <summary>
/// Evaluates whether the Gemini translation provider is usable right now, from the stored credential
/// plus (optionally) a live validation round-trip. Two layers:
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Evaluate"/> is the fast local gate: it reads the credential and applies a strict
/// syntactic check (a Gemini API key is an <c>AIza</c>-prefixed Standard key OR an <c>AQ.</c>-
/// prefixed Auth key — Google migrated new key issuance to the Auth format in 2026). It is called
/// synchronously
/// at startup and after every credential change so the dropdown reacts instantly to missing /
/// malformed keys without a network round-trip.
/// </para>
/// <para>
/// <see cref="EvaluateLiveAsync"/> additionally calls <see cref="IGeminiApiKeyValidator"/> to catch
/// server-side rejections (API_KEY_INVALID, quota, etc.) that syntax alone cannot see. It is the
/// authoritative check, run in the background so the UI is never blocked on the network. The key is
/// held only inside this call; it is never logged, persisted, or exposed.
/// </para>
/// </remarks>
public sealed class GeminiAvailabilityEvaluator
{
    private readonly ICredentialStore _credentialStore;
    private readonly IGeminiApiKeyValidator _validator;
    private readonly string _keyTarget;

    /// <summary>
    /// Creates an evaluator.
    /// </summary>
    /// <param name="credentialStore">The credential store holding the Gemini API key.</param>
    /// <param name="validator">The live key validator (network) used by <see cref="EvaluateLiveAsync"/>.</param>
    /// <param name="keyTarget">
    /// The credential target name; defaults to the App's documented
    /// <c>UniversalCaptions:GeminiApiKey</c> target.
    /// </param>
    public GeminiAvailabilityEvaluator(
        ICredentialStore credentialStore,
        IGeminiApiKeyValidator validator,
        string? keyTarget = null)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _keyTarget = keyTarget ?? "UniversalCaptions:GeminiApiKey";
    }

    /// <summary>
    /// Fast local availability gate: reads the stored credential and applies the syntactic check.
    /// Never performs network I/O and never throws.
    /// </summary>
    public GeminiAvailability Evaluate()
    {
        string? key = TryReadKey();
        if (key is null)
        {
            return GeminiAvailability.MissingKey;
        }

        return LooksLikeGeminiApiKey(key) ? GeminiAvailability.Available : GeminiAvailability.MalformedKey;
    }

    /// <summary>
    /// Authoritative availability: the local gate first, then a live validation round-trip when a
    /// key is present. A store failure degrades to <see cref="GeminiAvailability.MissingKey"/> so a
    /// broken credential store is treated the same as "no key" (Gemini unavailable, Argos usable).
    /// </summary>
    public async Task<GeminiAvailability> EvaluateLiveAsync(CancellationToken cancellationToken = default)
    {
        GeminiAvailability local = Evaluate();
        if (local is GeminiAvailability.MissingKey or GeminiAvailability.MalformedKey)
        {
            return local;
        }

        string? key = TryReadKey();
        if (key is null)
        {
            return GeminiAvailability.MissingKey;
        }

        return await _validator.ValidateAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when a stored value is shaped like a Gemini API key. Google is migrating from the legacy
    /// <c>AIza</c>-prefixed Standard/Traffic keys to the new <c>AQ.</c>-prefixed Auth keys (verified
    /// 2026-08-14: Google now issues <c>AQ.Ab</c>-prefixed keys in AI Studio), so BOTH prefixes are
    /// accepted. The prefix check alone is not authentication — the live validator decides
    /// server-side validity — but it reliably rejects the truncated / pasted-wrong values that
    /// otherwise surface as confusing "Gemini selected, nothing happens".
    /// </summary>
    public static bool LooksLikeGeminiApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        bool legacyStandard = apiKey.StartsWith("AIza", StringComparison.Ordinal);
        bool newAuthKey = apiKey.StartsWith("AQ.", StringComparison.Ordinal);
        return (legacyStandard || newAuthKey)
            && apiKey.Length >= 20
            && apiKey.Length <= 300;
    }

    private string? TryReadKey()
    {
        try
        {
            return _credentialStore.TryGetCredential(_keyTarget);
        }
        catch (Exception)
        {
            // The evaluator never throws: a failing credential store degrades to "no key" so the
            // offline/Argos path keeps working (mirrors the factory's ADR-0009 invariant).
            return null;
        }
    }
}
