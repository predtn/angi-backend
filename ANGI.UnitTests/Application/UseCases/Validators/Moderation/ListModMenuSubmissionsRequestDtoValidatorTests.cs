using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Moderation;

/// <summary>Covers every MOD-M1 filter and pagination boundary.</summary>
public sealed class ListModMenuSubmissionsRequestDtoValidatorTests
{
    // TEST-01: Accept only pending, approved and rejected and reject missing or unsupported status values.
    /// <summary>Verifies the moderation status partition.</summary>
    [Theory]
    [InlineData("pending", true)]
    [InlineData("approved", true)]
    [InlineData("rejected", true)]
    [InlineData("draft", false)]
    [InlineData("cancelled", false)]
    [InlineData("PENDING", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validate_Status(string? status, bool expected) =>
        new ListModMenuSubmissionsRequestDtoValidator().Validate(
            new ListModMenuSubmissionsRequestDto { Status = status! }).IsValid.Should().Be(expected);

    // TEST-02: Validate negative, zero, minimum, maximum and overflowing pagination values.
    /// <summary>Prevents invalid page sizes and integer overflow in SQL offsets.</summary>
    [Theory]
    [InlineData(-1, 20, false)]
    [InlineData(0, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, -1, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 100, true)]
    [InlineData(1, 101, false)]
    [InlineData(int.MaxValue, 100, false)]
    [InlineData(int.MaxValue, 1, true)]
    public void Validate_Paging(int page, int size, bool expected) =>
        new ListModMenuSubmissionsRequestDtoValidator().Validate(
            new ListModMenuSubmissionsRequestDto { Page = page, PageSize = size }).IsValid.Should().Be(expected);

    // TEST-03: Accept omitted or positive restaurant identifiers and reject zero and negative identifiers.
    /// <summary>Verifies the restaurant filter identifier partition.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1L, true)]
    [InlineData(long.MaxValue, true)]
    public void Validate_RestaurantId(long? id, bool expected) =>
        new ListModMenuSubmissionsRequestDtoValidator().Validate(
            new ListModMenuSubmissionsRequestDto { RestaurantId = id }).IsValid.Should().Be(expected);
}
