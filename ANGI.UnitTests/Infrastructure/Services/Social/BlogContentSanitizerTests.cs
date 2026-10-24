using ANGI.Infrastructure.Services.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Infrastructure.Services.Social;

/// <summary>Security behavior tests for blog HTML sanitization.</summary>
public sealed class BlogContentSanitizerTests
{
    // TEST-01: Preserve safe article formatting while removing script elements and event handlers.
    /// <summary>Verifies executable HTML cannot be stored by BLOG-04.</summary>
    [Fact]
    public void Sanitize_WithExecutableMarkup_ShouldRemoveXssAndKeepSafeHtml()
    {
        var sanitizer = new BlogContentSanitizer();

        var result = sanitizer.Sanitize("<p onclick=\"alert(1)\">Safe <strong>text</strong></p><script>alert(2)</script>");

        result.Should().Contain("<p>Safe <strong>text</strong></p>");
        result.ToLowerInvariant().Should().NotContain("onclick");
        result.ToLowerInvariant().Should().NotContain("script");
        result.ToLowerInvariant().Should().NotContain("alert");
    }

    // TEST-02: Remove javascript URLs while retaining HTTPS links.
    /// <summary>Verifies BLOG-04 permits normal links but rejects an executable URL scheme.</summary>
    [Fact]
    public void Sanitize_WithMixedLinkSchemes_ShouldKeepOnlySafeHref()
    {
        var sanitizer = new BlogContentSanitizer();

        var result = sanitizer.Sanitize(
            "<a href=\"javascript:alert(1)\">bad</a><a href=\"https://angi.test/post\">good</a>");

        result.ToLowerInvariant().Should().NotContain("javascript");
        result.Should().Contain("https://angi.test/post");
    }
}
