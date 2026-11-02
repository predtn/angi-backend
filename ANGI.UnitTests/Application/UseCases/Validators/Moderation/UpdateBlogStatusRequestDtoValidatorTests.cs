using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Unit tests for UpdateBlogStatusRequestDtoValidator.</summary>
public sealed class UpdateBlogStatusRequestDtoValidatorTests
{
    private readonly UpdateBlogStatusRequestDtoValidator _validator = new();

    [Theory]
    [InlineData("published", "Lý do hợp lệ", true)]
    [InlineData("hidden", "Lý do hợp lệ", true)]
    [InlineData("removed", "Lý do hợp lệ", true)]
    [InlineData("", "Lý do hợp lệ", false)]
    [InlineData(null, "Lý do hợp lệ", false)]
    [InlineData("invalid", "Lý do hợp lệ", false)]
    [InlineData("published", "", false)]
    [InlineData("published", null, false)]
    public void Validates_Status_And_Reason(string? status, string? reason, bool expectedValid)
    {
        var request = new UpdateBlogStatusRequestDto
        {
            Status = status!,
            Reason = reason!
        };

        var result = _validator.Validate(request);
        result.IsValid.Should().Be(expectedValid);
    }
}
