using UniversalCaptions.App.Controls;
using UniversalCaptions.Core.Translation;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// The live-translation error messages must tell the user WHAT to do next. In particular a graceful
/// Gemini goAway (server-side session-end, e.g. the session ran past the provider's wall-clock
/// limit while audio was paused) must NOT be phrased as an "unavailable / check your connection"
/// problem — restarting the session is the fix, not changing the key or inspecting the network.
/// </summary>
public sealed class LiveTranslationErrorMessagingTests
{
    [Fact]
    public void SessionEnded_message_points_to_restart_not_connection()
    {
        var message = ControlWindow.DescribeLiveTranslationError(new LiveTranslationError(
            LiveTranslationErrorKind.SessionEnded,
            "Gemini session ended.",
            null));

        Assert.Contains("Restart captions to resume", message);
        Assert.DoesNotContain("unavailable", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Check your connection", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionRejected_message_keeps_the_actionable_key_guidance()
    {
        var message = ControlWindow.DescribeLiveTranslationError(new LiveTranslationError(
            LiveTranslationErrorKind.SessionRejected,
            "bad key",
            null));

        Assert.Contains("Update the key", message);
        Assert.DoesNotContain("Argos", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuotaExceeded_message_advises_wait_and_retry()
    {
        var message = ControlWindow.DescribeLiveTranslationError(new LiveTranslationError(
            LiveTranslationErrorKind.QuotaExceeded,
            "quota",
            null));

        Assert.Contains("Wait and retry", message);
    }

    [Fact]
    public void Unknown_kind_falls_back_to_connection_guidance()
    {
        var message = ControlWindow.DescribeLiveTranslationError(new LiveTranslationError(
            LiveTranslationErrorKind.Unknown,
            "something odd",
            null));

        Assert.Contains("Check your connection", message);
    }
}
