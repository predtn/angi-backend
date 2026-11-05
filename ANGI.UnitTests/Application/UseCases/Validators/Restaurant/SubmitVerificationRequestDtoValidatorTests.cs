using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Verifies SubmitVerificationRequestDtoValidator checks legal fields, length limits and the required documents.</summary>
public sealed class SubmitVerificationRequestDtoValidatorTests
{
    private readonly SubmitVerificationRequestDtoValidator _validator = new();

    // TEST-01: Accept every scalar exactly at its maximum length.
    /// <summary>Verifies all inclusive upper boundaries documented for OWN-06.</summary>
    [Fact]
    public async Task Validate_WithMaximumLengths_ShouldBeValid()
    {
        // STEP-01: Create request through ValidRequest, set request.LegalName to a new string, set request.BusinessLicenseNo to a new string and 2 more.
        var request = ValidRequest();
        request.LegalName = new string('L', 255);
        request.BusinessLicenseNo = new string('B', 50);
        request.TaxCode = new string('T', 20);
        request.OwnerNote = new string('N', 1000);

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert no validation errors are reported.
        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-02: Reject missing required legal fields.
    /// <summary>Verifies legalName and businessLicenseNo are mandatory.</summary>
    [Fact]
    public async Task Validate_WithoutRequiredLegalFields_ShouldHaveErrors()
    {
        // STEP-01: Create request through ValidRequest, set request.LegalName to " " and set request.BusinessLicenseNo to string.Empty.
        var request = ValidRequest();
        request.LegalName = " ";
        request.BusinessLicenseNo = string.Empty;

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert a validation error on LegalName and a validation error on BusinessLicenseNo.
        result.ShouldHaveValidationErrorFor(item => item.LegalName);
        result.ShouldHaveValidationErrorFor(item => item.BusinessLicenseNo);
    }

    // TEST-03: Reject every scalar one character beyond its maximum length.
    /// <summary>Verifies all exclusive out-of-range string boundaries.</summary>
    [Fact]
    public async Task Validate_WithOverMaximumLengths_ShouldHaveErrors()
    {
        // STEP-01: Create request through ValidRequest, set request.LegalName to a new string, set request.BusinessLicenseNo to a new string and 2 more.
        var request = ValidRequest();
        request.LegalName = new string('L', 256);
        request.BusinessLicenseNo = new string('B', 51);
        request.TaxCode = new string('T', 21);
        request.OwnerNote = new string('N', 1001);

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert a validation error on LegalName, a validation error on BusinessLicenseNo, a validation error on TaxCode and a validation error on OwnerNote.
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
        // STEP-01: Create request through ValidRequest and set request.Documents to a test value.
        var request = ValidRequest();
        request.Documents = useNull ? null! : Array.Empty<VerificationDocumentRequestDto>();

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert a validation error on Documents.
        result.ShouldHaveValidationErrorFor(item => item.Documents);
    }

    // TEST-05: Reject a document set that omits the required business license.
    /// <summary>Verifies the cross-item OWN-06 business-license rule.</summary>
    [Fact]
    public async Task Validate_WithoutBusinessLicense_ShouldHaveDocumentsError()
    {
        // STEP-01: Create request through ValidRequest and set request.Documents to a test items.
        var request = ValidRequest();
        request.Documents =
        [
            new VerificationDocumentRequestDto { DocType = "id_card", MediaId = 2 }
        ];

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert a validation error on Documents.
        result.ShouldHaveValidationErrorFor(item => item.Documents);
    }

    // TEST-06: Propagate nested document validation with an indexed property path.
    /// <summary>Verifies invalid nested media identifiers are addressable by clients.</summary>
    [Fact]
    public async Task Validate_WithInvalidNestedDocument_ShouldHaveIndexedError()
    {
        // STEP-01: Create request through ValidRequest and set request.Documents to a test items.
        var request = ValidRequest();
        request.Documents =
        [
            new VerificationDocumentRequestDto { DocType = "business_license", MediaId = 0 }
        ];

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert result.Errors contains a matching item.
        result.Errors.Should().Contain(error => error.PropertyName == "Documents[0].MediaId");
    }

    // TEST-07: Reject a null element without throwing while traversing the document collection.
    /// <summary>Verifies defensive validation for malformed JSON array elements.</summary>
    [Fact]
    public async Task Validate_WithNullDocument_ShouldHaveIndexedError()
    {
        // STEP-01: Create request through ValidRequest and set request.Documents to a test items.
        var request = ValidRequest();
        request.Documents = [null!];

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert result.Errors contains a matching item.
        result.Errors.Should().Contain(error => error.PropertyName == "Documents");
        result.Errors.Should().Contain(error => error.PropertyName == "Documents[0]");
    }

    // TEST-08: Report explicit nulls once each, with the validator's Vietnamese messages.
    /// <summary>Verifies null required fields reach the validator instead of failing elsewhere.</summary>
    [Fact]
    public async Task Validate_WithNullRequiredFields_ShouldReportEachOnce()
    {
        // STEP-01: Create request as SubmitVerificationRequestDto.
        var request = new SubmitVerificationRequestDto { LegalName = null, BusinessLicenseNo = null, Documents = null };

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert result.Errors.Select(filtered) is equivalent to the expected items.
        result.Errors.Select(error => (error.PropertyName, error.ErrorMessage)).Should().BeEquivalentTo(
        [
            ("LegalName", "Tên pháp lý là bắt buộc."),
            ("BusinessLicenseNo", "Số giấy phép kinh doanh là bắt buộc."),
            ("Documents", "Danh sách giấy tờ là bắt buộc.")
        ]);
    }

    // TEST-09: Report a null document type once, as required rather than invalid.
    /// <summary>Verifies the document-type rule stops after its first failure.</summary>
    [Fact]
    public async Task Validate_WithNullDocType_ShouldReportRequiredOnce()
    {
        // STEP-01: Create request through ValidRequest and set request.Documents to a test items.
        var request = ValidRequest();
        request.Documents =
        [
            new VerificationDocumentRequestDto { DocType = "business_license", MediaId = 9001 },
            new VerificationDocumentRequestDto { DocType = null, MediaId = 9002 }
        ];

        // STEP-02: Validate request and keep result.
        var result = await _validator.TestValidateAsync(request);

        // STEP-03: Assert result.Errors has exactly one matching item and result.Errors.ErrorMessage is the expected text.
        result.Errors.Should().ContainSingle(error => error.PropertyName == "Documents[1].DocType")
            .Which.ErrorMessage.Should().Be("Loại giấy tờ là bắt buộc.");
    }

    // TEST-10: Keep the required fields nullable. MVC adds an implicit [Required] with an English message to
    // non-nullable reference properties and rejects an explicit null before the validator runs.
    /// <summary>Guards the nullability that lets the validator own every required-field message.</summary>
    [Theory]
    [InlineData(typeof(SubmitVerificationRequestDto), nameof(SubmitVerificationRequestDto.LegalName))]
    [InlineData(typeof(SubmitVerificationRequestDto), nameof(SubmitVerificationRequestDto.BusinessLicenseNo))]
    [InlineData(typeof(SubmitVerificationRequestDto), nameof(SubmitVerificationRequestDto.Documents))]
    [InlineData(typeof(VerificationDocumentRequestDto), nameof(VerificationDocumentRequestDto.DocType))]
    public void RequiredFields_ShouldBeNullableSoTheValidatorReportsNull(Type dtoType, string propertyName)
    {
        // STEP-01: Derive property from dtoType.GetProperty().
        var property = dtoType.GetProperty(propertyName)!;

        // STEP-02: Assert NullabilityInfoContext().Create().WriteState is Reflection.NullabilityState.Nullable.
        new System.Reflection.NullabilityInfoContext().Create(property).WriteState
            .Should().Be(System.Reflection.NullabilityState.Nullable);
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
