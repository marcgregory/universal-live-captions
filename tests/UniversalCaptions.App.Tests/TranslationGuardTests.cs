using UniversalCaptions.App.Controls;

namespace UniversalCaptions.App.Tests;

/// <summary>
/// Verifies <see cref="TranslationGuard"/> rejects a translation target that equals the caption
/// source language before it reaches the translation backend, which cannot translate a language
/// into itself.
/// </summary>
public class TranslationGuardTests
{
    [Fact]
    public void Validate_source_equals_target_returns_error()
    {
        string? error = TranslationGuard.Validate("en", "en");

        Assert.NotNull(error);
        Assert.Contains("already in en", error);
    }

    [Fact]
    public void Validate_source_equals_target_is_case_insensitive()
    {
        Assert.NotNull(TranslationGuard.Validate("EN", "en"));
    }

    [Fact]
    public void Validate_different_languages_returns_null()
    {
        Assert.Null(TranslationGuard.Validate("en", "tl"));
        Assert.Null(TranslationGuard.Validate("en", "ja"));
    }

    [Fact]
    public void Validate_auto_source_allows_any_target()
    {
        Assert.Null(TranslationGuard.Validate(null, "en"));
    }

    [Fact]
    public void Validate_null_target_returns_error()
    {
        Assert.NotNull(TranslationGuard.Validate("en", null));
        Assert.NotNull(TranslationGuard.Validate("en", " "));
    }
}
