using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Checks every report filter enum and numeric boundary, including null and case sensitivity.</summary>
public sealed class ListReportsRequestDtoValidatorTests
{
    // TEST-01: Require an exact restaurant or blog target and report a missing value only once.
    /// <summary>Checks required reference values and rejects numeric, mixed-case and padded enum strings.</summary>
    [Theory]
    [InlineData(null, false)] [InlineData("", false)] [InlineData(" ", false)]
    [InlineData("0", false)] [InlineData("Restaurant", false)] [InlineData(" restaurant ", false)]
    [InlineData("restaurant", true)] [InlineData("blog", true)]
    public void ValidatesTargetType(string? type, bool valid)
    {
        var result = new ListReportsRequestDtoValidator().Validate(new ListReportsRequestDto { TargetType = type! });
        result.IsValid.Should().Be(valid);
        if (!valid) result.Errors.Should().ContainSingle(error => error.PropertyName == "TargetType");
    }

    // TEST-02: Accept every documented status and reject unsupported, numeric and non-exact values.
    /// <summary>Checks all optional status partitions independently from the required target.</summary>
    [Theory]
    [InlineData(null, true)] [InlineData("pending", true)] [InlineData("in_review", true)]
    [InlineData("resolved", true)] [InlineData("dismissed", true)]
    [InlineData("", false)] [InlineData(" ", false)] [InlineData("PENDING", false)] [InlineData("0", false)] [InlineData("invalid", false)]
    public void ValidatesStatus(string? status, bool valid) =>
        new ListReportsRequestDtoValidator().Validate(new ListReportsRequestDto { TargetType = "restaurant", Status = status }).IsValid.Should().Be(valid);

    // TEST-03: Accept all seven reasons and reject unsupported, numeric and non-exact values.
    /// <summary>Checks every reason-code equivalence class including absence.</summary>
    [Theory]
    [InlineData(null, true)] [InlineData("wrong_info", true)] [InlineData("closed", true)] [InlineData("spam", true)]
    [InlineData("offensive", true)] [InlineData("hygiene", true)] [InlineData("scam", true)] [InlineData("other", true)]
    [InlineData("", false)] [InlineData(" ", false)] [InlineData("SPAM", false)] [InlineData("0", false)] [InlineData("invalid", false)]
    public void ValidatesReason(string? reason, bool valid) =>
        new ListReportsRequestDtoValidator().Validate(new ListReportsRequestDto { TargetType = "restaurant", ReasonCode = reason }).IsValid.Should().Be(valid);

    // TEST-04: Bound pages and page sizes and allow positive target identifiers up to Int64.MaxValue.
    /// <summary>Checks values below, at and above the numeric limits without imposing an arbitrary page maximum.</summary>
    [Theory]
    [InlineData(1, 1, null, true)] [InlineData(1, 100, 1L, true)] [InlineData(int.MaxValue, 100, long.MaxValue, true)]
    [InlineData(0, 20, null, false)] [InlineData(-1, 20, null, false)]
    [InlineData(1, 0, null, false)] [InlineData(1, -1, null, false)] [InlineData(1, 101, null, false)]
    [InlineData(1, 20, 0L, false)] [InlineData(1, 20, -1L, false)]
    public void ValidatesNumericBoundaries(int page, int size, long? id, bool valid) =>
        new ListReportsRequestDtoValidator().Validate(new ListReportsRequestDto
        { TargetType = "restaurant", Page = page, PageSize = size, TargetId = id }).IsValid.Should().Be(valid);
}
