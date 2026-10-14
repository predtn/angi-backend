using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.Test.Application.UseCases.Validators.Restaurant;

public sealed class SubmitVerificationRequestDtoValidatorTests
{
    private readonly SubmitVerificationRequestDtoValidator _validator = new();

    // TEST-01: Accept every scalar exactly at its maximum length.
    /// <summary>Verifies all inclusive upper boundaries documented for OWN-06.</summary>
    [Fact]
    public async Task Validate_WithMaximumLengths_ShouldBeValid()
    {
        var request = ValidRequest();
        request.LegalName = new string('L', 255);
        request.BusinessLicenseNo = new string('B', 50);
        request.TaxCode = new string('T', 20);
        request.OwnerNote = new string('N', 1000);

        var result = await _validator.TestValidateAsync(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-02: Reject missing required legal fields.
    /// <summary>Verifies legalName and businessLicenseNo are mandatory.</summary>
    [Fact]
    public async Task Validate_WithoutRequiredLegalFields_ShouldHaveErrors()
    {
        var request = ValidRequest();
        request.LegalName = " ";
        request.BusinessLicenseNo = string.Empty;

        var result = await _validator.TestValidateAsync(request);

        result.ShouldHaveValidationErrorFor(item => item.LegalName);
        result.ShouldHaveValidationErrorFor(item => item.BusinessLicenseNo);
    }

    // TEST-03: Reject every scalar one character beyond its maximum length.
    /// <summary>Verifies all exclusive out-of-range string boundaries.</summary>
    [Fact]
    public async Task Validate_WithOverMaximumLengths_ShouldHaveErrors()
    {
        var request = ValidRequest();
        request.LegalName = new string('L', 256);
        request.BusinessLicenseNo = new string('B', 51);
        request.TaxCode = new string('T', 21);
        request.OwnerNote = new string('N', 1001);

        var result = await _validator.TestValidateAsync(request);

        result.ShouldHaveValidationErrorFor(item => item.LegalName);
        result.ShouldHaveValidationErrorFor(item => item.BusinessLicenseNo);
        result.ShouldHaveValidationErrorFor(item => item.TaxCode);
        result.ShouldHaveValidationErrorFor(item => item.OwnerNote);
    }

    // TEST-04: Reject a null or empty document collection.
    /// <summary>Verifies documents is required and contains at least one item.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Validate_WithoutDocuments_ShouldHaveError(bool useNull)
    {
        var request = ValidRequest();
        request.Documents = useNull ? null! : Array.Empty<VerificationDocumentRequestDto>();

        var result = await _validator.TestValidateAsync(request);

        result.ShouldHaveValidationErrorFor(item => item.Documents);
    }

    // TEST-05: Reject a document set that omits the required business license.
    /// <summary>Verifies the cross-item OWN-06 business-license rule.</summary>
    [Fact]
    public async Task Validate_WithoutBusinessLicense_ShouldHaveDocumentsError()
    {
        var request = ValidRequest();
        request.Documents =
        [
            new VerificationDocumentRequestDto { DocType = "id_card", MediaId = 2 }
        ];

        var result = await _validator.TestValidateAsync(request);

        result.ShouldHaveValidationErrorFor(item => item.Documents);
    }

    // TEST-06: Propagate nested document validation with an indexed property path.
    /// <summary>Verifies invalid nested media identifiers are addressable by clients.</summary>
    [Fact]
    public async Task Validate_WithInvalidNestedDocument_ShouldHaveIndexedError()
    {
        var request = ValidRequest();
        request.Documents =
        [
            new VerificationDocumentRequestDto { DocType = "business_license", MediaId = 0 }
        ];

        var result = await _validator.TestValidateAsync(request);

        result.Errors.Should().Contain(error => error.PropertyName == "Documents[0].MediaId");
    }

    // TEST-07: Reject a null element without throwing while traversing the document collection.
    /// <summary>Verifies defensive validation for malformed JSON array elements.</summary>
    [Fact]
    public async Task Validate_WithNullDocument_ShouldHaveIndexedError()
    {
        var request = ValidRequest();
        request.Documents = [null!];

        var result = await _validator.TestValidateAsync(request);

        result.Errors.Should().Contain(error => error.PropertyName == "Documents");
        result.Errors.Should().Contain(error => error.PropertyName == "Documents[0]");
    }

    /// <summary>Creates the smallest valid OWN-06 request.</summary>
    private static SubmitVerificationRequestDto ValidRequest() => new()
    {
        LegalName = "Hộ kinh doanh Bún Bò Bà Diệu",
        BusinessLicenseNo = "32A8012345",
        Documents =
        [
            new VerificationDocumentRequestDto
            {
                DocType = "business_license",
                MediaId = 9001
            }
        ]
    };
}
