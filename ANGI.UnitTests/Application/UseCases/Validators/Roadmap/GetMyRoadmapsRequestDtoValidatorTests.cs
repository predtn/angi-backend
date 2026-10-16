using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Roadmap;

public sealed class GetMyRoadmapsRequestDtoValidatorTests
{
    private readonly GetMyRoadmapsRequestDtoValidator _validator = new();

    // TEST-01: Accept the documented defaults and both inclusive pagination boundaries.
    /// <summary>Verifies page one and page sizes from one through one hundred are accepted.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 20)]
    [InlineData(int.MaxValue, 100)]
    public void Validate_WithPaginationInsideInclusiveBounds_ShouldBeValid(int page, int pageSize)
    {
        var result = _validator.TestValidate(new GetMyRoadmapsRequestDto { Page = page, PageSize = pageSize });

        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Reject values immediately below or above every pagination boundary.
    /// <summary>Verifies invalid pages and page sizes cannot reach the RM-02 repository query.</summary>
    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    [InlineData(1, int.MaxValue)]
    public void Validate_WithPaginationOutsideBounds_ShouldBeInvalid(int page, int pageSize)
    {
        var result = _validator.TestValidate(new GetMyRoadmapsRequestDto { Page = page, PageSize = pageSize });

        result.IsValid.Should().BeFalse();
    }
}
