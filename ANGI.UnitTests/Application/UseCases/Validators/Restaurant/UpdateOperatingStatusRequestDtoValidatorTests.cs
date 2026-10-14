using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class UpdateOperatingStatusRequestDtoValidatorTests
{
    private readonly UpdateOperatingStatusRequestDtoValidator _validator = new();

    // TEST-01: Accept the three documented statuses, with or without a 500-character note.
    [Theory]
    [InlineData("open")]
    [InlineData("temporarily_closed")]
    [InlineData("permanently_closed")]
    public void Validate_WithDocumentedStatus_ShouldPass(string status)
    {
        _validator.Validate(new UpdateOperatingStatusRequestDto { OperatingStatus = status }).IsValid.Should().BeTrue();
        _validator.Validate(new UpdateOperatingStatusRequestDto { OperatingStatus = status, Note = new string('a', 500) })
            .IsValid.Should().BeTrue();
    }

    // TEST-02: Report a missing status once, and an unknown or differently cased one as invalid.
    [Theory]
    [InlineData(null, "Trạng thái hoạt động là bắt buộc.")]
    [InlineData("", "Trạng thái hoạt động là bắt buộc.")]
    [InlineData("closed", "Trạng thái hoạt động chỉ có thể là open, temporarily_closed hoặc permanently_closed.")]
    [InlineData("OPEN", "Trạng thái hoạt động chỉ có thể là open, temporarily_closed hoặc permanently_closed.")]
    public void Validate_WithInvalidStatus_ShouldReportOneMessage(string? status, string message)
    {
        var result = _validator.Validate(new UpdateOperatingStatusRequestDto { OperatingStatus = status });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "OperatingStatus")
            .Which.ErrorMessage.Should().Be(message);
    }

    // TEST-03: Reject a note longer than 500 characters.
    [Fact]
    public void Validate_WithNoteOver500_ShouldFail()
    {
        var result = _validator.Validate(new UpdateOperatingStatusRequestDto
        {
            OperatingStatus = "open",
            Note = new string('a', 501)
        });

        result.Errors.Should().ContainSingle(error => error.PropertyName == "Note");
    }
}
