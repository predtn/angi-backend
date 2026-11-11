using ANGI.Application.DTOs.Media;
using ANGI.Application.UseCases.Validators.Media;
using FluentAssertions;

namespace ANGI.UnitTests.Application.UseCases.Validators.Media;

/// <summary>Verifies UploadMediaRequestDtoValidator checks the uploaded file, its metadata and its purpose.</summary>
public sealed class UploadMediaRequestDtoValidatorTests
{
    private readonly UploadMediaRequestDtoValidator _validator = new();

    /// <summary>Creates a complete three-byte JPEG upload for the restaurant purpose.</summary>
    private static UploadMediaRequestDto ValidRequest() => new()
    {
        Content = new MemoryStream([1, 2, 3]),
        FileName = "photo.jpg",
        MimeType = "image/jpeg",
        SizeBytes = 3,
        Purpose = "restaurant"
    };

    // TEST-01: Accept a complete upload.
    /// <summary>Ensures a complete upload is not blocked.</summary>
    [Fact]
    public void Validate_WithCompleteUpload_ShouldPass()
    {
        // STEP-01: Assert _validator.Validate().IsValid is true.
        _validator.Validate(ValidRequest()).IsValid.Should().BeTrue();
    }

    // TEST-02: Report a missing file once, under the multipart field name "File", not as four errors.
    /// <summary>Ensures a missing file yields one error on the multipart field, not a cascade.</summary>
    [Fact]
    public void Validate_WithoutFile_ShouldReportOnlyTheFile()
    {
        // STEP-01: Create request as UploadMediaRequestDto.
        var request = new UploadMediaRequestDto { Purpose = "restaurant" };

        // STEP-02: Validate request and keep result.
        var result = _validator.Validate(request);

        // STEP-03: Assert result.Errors has exactly one matching item and result.Errors matches the condition.
        result.Errors.Should().ContainSingle()
            .Which.Should().Match<FluentValidation.Results.ValidationFailure>(error =>
                error.PropertyName == "File" && error.ErrorMessage == "Tệp là bắt buộc.");
    }

    // TEST-03: With a file present, still reject an empty file and a missing name or type.
    /// <summary>Ensures empty content and missing metadata are each reported.</summary>
    [Fact]
    public void Validate_WithEmptyFileMetadata_ShouldReportEachField()
    {
        // STEP-01: Create request through ValidRequest, set request.FileName to string.Empty, set request.MimeType to string.Empty and set request.SizeBytes to 0.
        var request = ValidRequest();
        request.FileName = string.Empty;
        request.MimeType = string.Empty;
        request.SizeBytes = 0;

        // STEP-02: Validate request and keep result.
        var result = _validator.Validate(request);

        // STEP-03: Assert result.Errors.Select(filtered) is equivalent to "FileName".
        result.Errors.Select(error => error.PropertyName)
            .Should().BeEquivalentTo("FileName", "MimeType", "SizeBytes");
    }

    // TEST-04: Reject a missing or unknown purpose.
    /// <summary>Ensures only documented media purposes are accepted.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("menu")]
    public void Validate_WithInvalidPurpose_ShouldFail(string purpose)
    {
        // STEP-01: Create request through ValidRequest and set request.Purpose to purpose.
        var request = ValidRequest();
        request.Purpose = purpose;

        // STEP-02: Assert _validator.Validate().Errors has exactly one matching item.
        _validator.Validate(request).Errors.Should().ContainSingle(error => error.PropertyName == "Purpose");
    }
}
