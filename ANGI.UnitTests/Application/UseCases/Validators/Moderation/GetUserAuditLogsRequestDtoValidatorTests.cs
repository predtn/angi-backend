using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Verifies FluentValidation rules for MOD-U5 GetUserAuditLogsRequestDto.</summary>
public sealed class GetUserAuditLogsRequestDtoValidatorTests
{
    private readonly GetUserAuditLogsRequestDtoValidator _validator = new();

    [Fact]
    public async Task Validate_ValidRequest_IsValid()
    {
        var dto = new GetUserAuditLogsRequestDto
        {
            Action = AuditActions.UserLogin,
            From = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            Page = 1,
            PageSize = 20
        };

        var result = await _validator.ValidateAsync(dto);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Validate_InvalidPagination_IsInvalid(int page, int pageSize)
    {
        var dto = new GetUserAuditLogsRequestDto { Page = page, PageSize = pageSize };

        var result = await _validator.ValidateAsync(dto);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_FromGreaterThanTo_IsInvalid()
    {
        var dto = new GetUserAuditLogsRequestDto
        {
            From = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var result = await _validator.ValidateAsync(dto);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_UnknownAction_IsInvalid()
    {
        var dto = new GetUserAuditLogsRequestDto { Action = "UNKNOWN_ACTION" };

        var result = await _validator.ValidateAsync(dto);

        result.IsValid.Should().BeFalse();
    }
}
