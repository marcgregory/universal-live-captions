using UniversalCaptions.App.Settings;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Integration tests for the production <see cref="WindowsCredentialStore"/>. These tests talk to
/// the real Windows Credential Manager via advapi32 and use a per-test target name
/// (<c>UniversalCaptions:Test:&lt;guid&gt;</c>) so they do not collide with the production entry
/// (<c>UniversalCaptions:GeminiApiKey</c>) or with each other. Each test cleans up after itself.
///
/// Marked with <see cref="TraitAttribute"/>(<c>"Category"</c>, <c>"WindowsOnly"</c>) so the host
/// can filter them out on non-Windows environments. CI is Windows per CLAUDE.md, so this is
/// defensive.
/// </summary>
[Trait("Category", "WindowsOnly")]
public class WindowsCredentialStoreTests : IDisposable
{
    private readonly string _targetName = $"UniversalCaptions:Test:{Guid.NewGuid():N}";
    private readonly WindowsCredentialStore _store = new();

    public void Dispose()
    {
        // Best-effort cleanup; ignore failures (e.g., test crashed before Set).
        _store.RemoveCredential(_targetName);
    }

    [Fact]
    public void Roundtrip_SetReadDelete()
    {
        Assert.False(_store.HasCredential(_targetName));

        bool setOk = _store.SetCredential(_targetName, "roundtrip-value");
        Assert.True(setOk);
        Assert.True(_store.HasCredential(_targetName));
        Assert.Equal("roundtrip-value", _store.TryGetCredential(_targetName));

        bool removeOk = _store.RemoveCredential(_targetName);
        Assert.True(removeOk);
        Assert.False(_store.HasCredential(_targetName));
        Assert.Null(_store.TryGetCredential(_targetName));
    }

    [Fact]
    public void Read_When_Not_Set_Returns_Null()
    {
        Assert.Null(_store.TryGetCredential(_targetName));
        Assert.False(_store.HasCredential(_targetName));
    }

    [Fact]
    public void Set_Overwrites_Existing_Value()
    {
        Assert.True(_store.SetCredential(_targetName, "first"));
        Assert.True(_store.SetCredential(_targetName, "second"));

        Assert.Equal("second", _store.TryGetCredential(_targetName));

        _store.RemoveCredential(_targetName);
    }

    [Fact]
    public void Remove_When_Not_Set_Returns_True()
    {
        // Per Windows semantics, removing a non-existent credential is a no-op success.
        Assert.True(_store.RemoveCredential(_targetName));
    }

    [Fact]
    public void Utf8_Roundtrip_Preserves_Unicode()
    {
        const string unicodeValue = "këy-with-ünïcödé-🔑";

        Assert.True(_store.SetCredential(_targetName, unicodeValue));
        Assert.Equal(unicodeValue, _store.TryGetCredential(_targetName));

        _store.RemoveCredential(_targetName);
    }

    [Fact]
    public void Empty_Value_Is_Stored_And_Returned()
    {
        Assert.True(_store.SetCredential(_targetName, string.Empty));
        Assert.True(_store.HasCredential(_targetName));
        Assert.Equal(string.Empty, _store.TryGetCredential(_targetName));

        _store.RemoveCredential(_targetName);
    }
}
