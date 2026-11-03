using ANGI.Application.Common.Models.Discovery;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Discovery;

/// <summary>Verifies ProvinceSearch builds a pattern that matches both the catalog's full name and a stored short name.</summary>
public sealed class ProvinceSearchTests
{
    // TEST-01: The "Thành phố" and "Tỉnh" prefixes are dropped in any letter case, with surrounding spaces trimmed.
    /// <summary>Ensures a catalog province name searches by its own name, however it was typed.</summary>
    [Theory]
    [InlineData("Thành phố Hà Nội", "%Hà Nội%")]
    [InlineData("thành phố Hà Nội", "%Hà Nội%")]
    [InlineData("THÀNH PHỐ HÀ NỘI", "%HÀ NỘI%")]
    [InlineData("  Thành phố  Hà Nội  ", "%Hà Nội%")]
    [InlineData("Tỉnh Quảng Ninh", "%Quảng Ninh%")]
    [InlineData("tỉnh Quảng Ninh", "%Quảng Ninh%")]
    public void Pattern_FullProvinceName_DropsAdministrativePrefix(string province, string expected)
    {
        ProvinceSearch.Pattern(province).Should().Be(expected);
    }

    // TEST-02: A short name is only trimmed, so existing searches keep their behavior.
    /// <summary>Ensures names without a prefix, including partial text, are searched as written.</summary>
    [Theory]
    [InlineData("Hà Nội", "%Hà Nội%")]
    [InlineData("  Hà Nội  ", "%Hà Nội%")]
    [InlineData("Nội", "%Nội%")]
    public void Pattern_ShortProvinceName_KeepsTheName(string province, string expected)
    {
        ProvinceSearch.Pattern(province).Should().Be(expected);
    }

    // TEST-03: A prefix word with no name after it, or one that only starts a longer word, stays part of the search.
    /// <summary>Ensures dropping the prefix can never produce an empty pattern that matches every province.</summary>
    [Theory]
    [InlineData("Thành phố", "%Thành phố%")]
    [InlineData("Tỉnh", "%Tỉnh%")]
    [InlineData("Tỉnhgia", "%Tỉnhgia%")]
    public void Pattern_PrefixWithoutName_KeepsTheText(string province, string expected)
    {
        ProvinceSearch.Pattern(province).Should().Be(expected);
    }
}
