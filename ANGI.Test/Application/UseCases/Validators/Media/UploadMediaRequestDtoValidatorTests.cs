using ANGI.Application.DTOs.Media;
using ANGI.Application.UseCases.Validators.Media;
using FluentAssertions;

namespace ANGI.Test.Application.UseCases.Validators.Media;

public sealed class UploadMediaRequestDtoValidatorTests
{
    private readonly UploadMediaRequestDtoValidator _validator = new();

    private static UploadMediaRequestDto ValidRequest() => new()
    {
        Content = new MemoryStream([1, 2, 3]),
        FileName = "photo.jpg",
        MimeType = "image/jpeg",
        SizeBytes = 3,
        Purpose = "restaurant"
    };

    // TEST-01: Accept a complete upload.
    [Fact]
    public void Validate_WithCompleteUpload_ShouldPass()
    {
        _validator.Validate(ValidRequest()).IsValid.Should().BeTrue();
    }

    // TEST-02: Report a missing file once, under the multipart field name "File", not as four errors.
    [Fact]
    public void Validate_WithoutFile_ShouldReportOnlyTheFile()
    {
        var request = new UploadMediaRequestDto { Purpose = "restaurant" };

        var result = _validator.Validate(request);

        result.Errors.Should().ContainSingle()
            .Which.Should().Match<FluentValidation.Results.ValidationFailure>(error =>
                error.PropertyName == "File" && error.ErrorMessage == "Tệp là bắt buộc.");
    }

    // TEST-03: With a file present, still reject an empty file and a missing name or type.
    [Fact]
    public void Validate_WithEmptyFileMetadata_ShouldReportEachField()
    {
        var request = ValidRequest();
        request.FileName = string.Empty;
        request.MimeType = string.Empty;
        request.SizeBytes = 0;

        var result = _validator.Validate(request);

        result.Errors.Select(error => error.PropertyName)
            .Should().BeEquivalentTo("FileName", "MimeType", "SizeBytes");
    }

    // TEST-04: Reject a missing or unknown purpose.
    [Theory]
    [InlineData("")]
    [InlineData("menu")]
    public void Validate_WithInvalidPurpose_ShouldFail(string purpose)
    {
        var request = ValidRequest();
        request.Purpose = purpose;

        _validator.Validate(request).Errors.Should().ContainSingle(error => error.PropertyName == "Purpose");
    }
}
