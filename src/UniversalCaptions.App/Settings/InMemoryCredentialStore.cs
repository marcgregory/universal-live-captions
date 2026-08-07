using System.Collections.Concurrent;

namespace UniversalCaptions.App.Settings;

/// <summary>
/// In-process <see cref="ICredentialStore"/> backed by a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// Used by tests to avoid coupling to the user's real Windows Credential Manager. Marked
/// <c>internal</c> because production code never needs a non-persistent store; tests reach it via
/// <c>InternalsVisibleTo("UniversalCaptions.App.Tests")</c> on <c>UniversalCaptions.App.csproj</c>.
///
/// All methods are tolerant: bad input is rejected with a documented return value, never an
/// exception, mirroring the policy enforced by <see cref="WindowsCredentialStore"/>.
/// </summary>
internal sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool HasCredential(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        return _store.ContainsKey(key);
    }

    /// <inheritdoc />
    public string? TryGetCredential(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        return _store.TryGetValue(key, out string? value) ? value : null;
    }

    /// <inheritdoc />
    public bool SetCredential(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        if (value is null)
        {
            return false;
        }
        _store[key] = value;
        return true;
    }

    /// <inheritdoc />
    public bool RemoveCredential(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }
        _store.TryRemove(key, out _);
        return true;
    }
}
