using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Checks MOD-R1 enum and pagination partitions.</summary>
public sealed class ListModRestaurantsRequestDtoValidatorTests
{
    // TEST-01: Accept default, minimum, maximum and very large valid pages without offset overflow.
    /// <summary>Verifies documented page sizes and one-based page numbering.</summary>
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 20, true)]
    [InlineData(1, 100, true)]
    [InlineData(int.MaxValue, 100, true)]
    [InlineData(0, 20, false)]
    [InlineData(-1, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, -1, false)]
    [InlineData(1, 101, false)]
    public void ValidatesPagination(int page, int size, bool valid) =>
        new ListModRestaurantsRequestDtoValidator().Validate(new ListModRestaurantsRequestDto { Page = page, PageSize = size }).IsValid.Should().Be(valid);

    // TEST-02: Accept every documented filter string and reject empty, numeric and wrong-case values.
    /// <summary>Covers all verification, operating and moderation status partitions.</summary>
    [Theory]
    [InlineData("VerificationStatus", "unverified", true)]
    [InlineData("VerificationStatus", "pending", true)]
    [InlineData("VerificationStatus", "verified", true)]
    [InlineData("VerificationStatus", "rejected", true)]
    [InlineData("OperatingStatus", "open", true)]
    [InlineData("OperatingStatus", "temporarily_closed", true)]
    [InlineData("OperatingStatus", "permanently_closed", true)]
    [InlineData("ModerationStatus", "visible", true)]
    [InlineData("ModerationStatus", "hidden", true)]
    [InlineData("ModerationStatus", "suspended", true)]
    [InlineData("VerificationStatus", "VERIFIED", false)]
    [InlineData("OperatingStatus", "0", false)]
    [InlineData("ModerationStatus", "", false)]
    public void ValidatesEnumFilters(string field, string value, bool valid)
    {
        var request = new ListModRestaurantsRequestDto();
        typeof(ListModRestaurantsRequestDto).GetProperty(field)!.SetValue(request, value);
        new ListModRestaurantsRequestDtoValidator().Validate(request).IsValid.Should().Be(valid);
    }
}
