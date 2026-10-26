using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Validators.Social;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Social;

/// <summary>Unit tests for ReportBlogRequestDtoValidator.</summary>
public sealed class ReportBlogRequestDtoValidatorTests
{
    private readonly ReportBlogRequestDtoValidator _validator = new();

    // TEST-01: Verify Validate when valid reason; should be valid.
    /// <summary>Verify Validate when valid reason; should be valid.</summary>
    [Theory]
    [InlineData("spam")]
    [InlineData("offensive")]
    [InlineData("wrong_info")]
    [InlineData("scam")]
    public void Validate_WhenValidReason_ShouldBeValid(string reason)
    {
        var dto = new ReportBlogRequestDto { ReasonCode = reason };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeTrue();
    }

    // TEST-02: Verify Validate when reason is other with description; should be valid.
    /// <summary>Verify Validate when reason is other with description; should be valid.</summary>
    [Fact]
    public void Validate_WhenReasonIsOtherWithDescription_ShouldBeValid()
    {
        var dto = new ReportBlogRequestDto { ReasonCode = "other", Description = "Lý do khác rõ ràng" };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeTrue();
    }

    // TEST-03: Verify Validate when reason is other without description; should have error.
    /// <summary>Verify Validate when reason is other without description; should have error.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WhenReasonIsOtherWithoutDescription_ShouldHaveError(string? desc)
    {
        var dto = new ReportBlogRequestDto { ReasonCode = "other", Description = desc };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportBlogRequestDto.Description));
    }

    // TEST-04: Verify Validate when reason is invalid; should have error.
    /// <summary>Verify Validate when reason is invalid; should have error.</summary>
    [Fact]
    public void Validate_WhenReasonIsInvalid_ShouldHaveError()
    {
        var dto = new ReportBlogRequestDto { ReasonCode = "invalid_code" };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportBlogRequestDto.ReasonCode));
    }

    // TEST-05: Verify Validate when evidence media ids exceeds5; should have error.
    /// <summary>Verify Validate when evidence media ids exceeds5; should have error.</summary>
    [Fact]
    public void Validate_WhenEvidenceMediaIdsExceeds5_ShouldHaveError()
    {
        var dto = new ReportBlogRequestDto
        {
            ReasonCode = "spam",
            EvidenceMediaIds = [1, 2, 3, 4, 5, 6]
        };
        var result = _validator.Validate(dto);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(ReportBlogRequestDto.EvidenceMediaIds));
    }
}
