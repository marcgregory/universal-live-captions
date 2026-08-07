using UniversalCaptions.App.Settings;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Tests for the in-memory <see cref="ICredentialStore"/> fake. The fake exists so App tests can
/// exercise the credential seam (factory, settings UI plumbing) without coupling to the user's
/// real Windows Credential Manager. The real-OS P/Invoke path is covered separately by
/// <c>WindowsCredentialStoreTests</c>.
/// </summary>
public class InMemoryCredentialStoreTests
{
    [Fact]
    public void Set_Then_TryGet_Returns_Value()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential("UniversalCaptions:GeminiApiKey", "test-key-value");

        string? actual = store.TryGetCredential("UniversalCaptions:GeminiApiKey");

        Assert.Equal("test-key-value", actual);
    }

    [Fact]
    public void TryGet_When_Not_Set_Returns_Null()
    {
        InMemoryCredentialStore store = new();

        string? actual = store.TryGetCredential("UniversalCaptions:GeminiApiKey");

        Assert.Null(actual);
    }

    [Fact]
    public void HasCredential_Reflects_Presence()
    {
        InMemoryCredentialStore store = new();

        Assert.False(store.HasCredential("UniversalCaptions:GeminiApiKey"));
        store.SetCredential("UniversalCaptions:GeminiApiKey", "v");
        Assert.True(store.HasCredential("UniversalCaptions:GeminiApiKey"));
    }

    [Fact]
    public void Remove_Deletes_Value()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential("UniversalCaptions:GeminiApiKey", "v");

        bool removed = store.RemoveCredential("UniversalCaptions:GeminiApiKey");

        Assert.True(removed);
        Assert.False(store.HasCredential("UniversalCaptions:GeminiApiKey"));
        Assert.Null(store.TryGetCredential("UniversalCaptions:GeminiApiKey"));
    }

    [Fact]
    public void Remove_When_Not_Set_Is_NoOp()
    {
        InMemoryCredentialStore store = new();

        bool removed = store.RemoveCredential("UniversalCaptions:GeminiApiKey");

        Assert.True(removed);
    }

    [Fact]
    public void Set_Overwrites_Existing_Value()
    {
        InMemoryCredentialStore store = new();
        store.SetCredential("UniversalCaptions:GeminiApiKey", "old");

        store.SetCredential("UniversalCaptions:GeminiApiKey", "new");

        Assert.Equal("new", store.TryGetCredential("UniversalCaptions:GeminiApiKey"));
    }

    [Fact]
    public void Null_Or_Whitespace_Key_Is_Rejected()
    {
        InMemoryCredentialStore store = new();

        Assert.False(store.HasCredential(string.Empty));
        Assert.False(store.HasCredential("   "));
        Assert.Null(store.TryGetCredential(null!));
        Assert.False(store.SetCredential(string.Empty, "v"));
        Assert.False(store.RemoveCredential(null!));
    }

    [Fact]
    public void Null_Value_Is_Rejected()
    {
        InMemoryCredentialStore store = new();

        Assert.False(store.SetCredential("k", null!));
    }

    [Fact]
    public async Task Concurrent_Set_And_Remove_DoesNotThrow()
    {
        InMemoryCredentialStore store = new();
        await Task.Run(() =>
        {
            Parallel.Invoke(
                () =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        store.SetCredential("k", "v" + i);
                    }
                },
                () =>
                {
                    for (int i = 0; i < 1000; i++)
                    {
                        store.RemoveCredential("k");
                    }
                });
        });
        // No assertion needed — the test passes if no exception was thrown.
    }
}
