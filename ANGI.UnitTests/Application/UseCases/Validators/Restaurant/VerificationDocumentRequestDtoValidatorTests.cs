using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

public sealed class VerificationDocumentRequestDtoValidatorTests
{
    private readonly VerificationDocumentRequestDtoValidator _validator = new();

    // TEST-01: Accept every document type defined by the API enum.
    /// <summary>Verifies every documented docType value at the nested request boundary.</summary>
    [Theory]
    [InlineData("business_license")]
    [InlineData("food_safety_cert")]
    [InlineData("id_card")]
    [InlineData("storefront_photo")]
    [InlineData("other")]
    public async Task Validate_WithDocumentedType_ShouldBeValid(string documentType)
    {
        var result = await _validator.TestValidateAsync(new VerificationDocumentRequestDto
        {
            DocType = documentType,
            MediaId = 1
        });

        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-02: Reject blank, unknown, and incorrectly cased document types.
    /// <summary>Verifies that docType follows the exact snake_case API contract.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("Business_License")]
    public async Task Validate_WithInvalidDocumentType_ShouldHaveError(string documentType)
    {
        var result = await _validator.TestValidateAsync(new VerificationDocumentRequestDto
        {
            DocType = documentType,
            MediaId = 1
        });

        result.ShouldHaveValidationErrorFor(document => document.DocType);
    }

    // TEST-03: Reject zero and negative media identifiers.
    /// <summary>Verifies the positive long boundary for mediaId.</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task Validate_WithNonPositiveMediaId_ShouldHaveError(long mediaId)
    {
        var result = await _validator.TestValidateAsync(new VerificationDocumentRequestDto
        {
            DocType = "business_license",
            MediaId = mediaId
        });

        result.ShouldHaveValidationErrorFor(document => document.MediaId);
    }
}
