namespace UniversalCaptions.App.Settings;

/// <summary>
/// Per-user credential storage seam. The production implementation
/// (<see cref="WindowsCredentialStore"/>) round-trips credentials through the Windows Credential
/// Manager (advapi32 <c>CredWriteW</c> / <c>CredReadW</c> / <c>CredDeleteW</c>); tests use
/// <see cref="InMemoryCredentialStore"/>. The interface speaks <see cref="string"/> so the App layer
/// never has to know about DPAPI, blobs, or native handles.
///
/// The raw credential value is a <see cref="string"/> for the same reason
/// <c>System.Security.SecureString</c> is discouraged in modern .NET — managed strings are
/// interned and copied freely, so any "secure" wrapper is largely theater. The practical policy
/// (enforced by callers) is: minimum lifetime, minimum copies, never log, never display, never
/// persist to <c>settings.json</c>, never put in an exception message.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Returns <c>true</c> if a credential is currently stored for <paramref name="key"/>.
    /// Does not surface the value. Never throws; returns <c>false</c> on any failure.
    /// </summary>
    /// <param name="key">
    /// The credential target name (e.g. <c>UniversalCaptions:GeminiApiKey</c>). Case-insensitive
    /// on Windows. Must not be null or whitespace.
    /// </param>
    bool HasCredential(string key);

    /// <summary>
    /// Returns the stored credential for <paramref name="key"/>, or <c>null</c> if no credential
    /// is stored or the read fails. Never throws.
    /// </summary>
    /// <param name="key">
    /// The credential target name (e.g. <c>UniversalCaptions:GeminiApiKey</c>). Case-insensitive
    /// on Windows. Must not be null or whitespace.
    /// </param>
    /// <returns>The credential value, or <c>null</c> when missing/unreadable.</returns>
    string? TryGetCredential(string key);

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>, replacing any existing
    /// value. Does not throw on failure (callers may not have a UI to surface an exception);
    /// returns <c>true</c> on success.
    /// </summary>
    /// <param name="key">
    /// The credential target name. Case-insensitive on Windows. Must not be null or whitespace.
    /// </param>
    /// <param name="value">The credential value. Must not be null. May be empty.</param>
    /// <returns><c>true</c> on success, <c>false</c> on failure.</returns>
    bool SetCredential(string key, string value);

    /// <summary>
    /// Removes the credential stored under <paramref name="key"/>. No-op (returns <c>true</c>)
    /// if no credential is stored. Does not throw.
    /// </summary>
    /// <param name="key">
    /// The credential target name. Case-insensitive on Windows. Must not be null or whitespace.
    /// </param>
    /// <returns><c>true</c> if the credential was removed or did not exist; <c>false</c> on failure.</returns>
    bool RemoveCredential(string key);
}
