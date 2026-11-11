using ANGI.Application.Common.Models;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models;

/// <summary>Verifies LikePattern keeps user search text literal inside ILIKE.</summary>
public sealed class LikePatternTests
{
    // TEST-01: Wrap trimmed text in % so ILIKE matches values that contain it.
    /// <summary>Plain text only gains the surrounding wildcards.</summary>
    [Fact]
    public void Contains_WithPlainText_ShouldWrapTrimmedText()
    {
        LikePattern.Contains("  bún bò  ").Should().Be("%bún bò%");
    }

    // TEST-02: Escape %, _ and the escape character so a search for "50%" does not match everything.
    /// <summary>Wildcards typed by the user match themselves.</summary>
    [Theory]
    [InlineData("50%", "%50\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("c:\\x", "%c:\\\\x%")]
    [InlineData("%", "%\\%%")]
    public void Contains_WithLikeMetacharacters_ShouldEscapeThem(string text, string expected)
    {
        LikePattern.Contains(text).Should().Be(expected);
    }
}
