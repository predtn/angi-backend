using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.Test.Application.UseCases.Validators.Restaurant;

public sealed class ResubmitVerificationRequestDtoValidatorTests
{
    private readonly ResubmitVerificationRequestDtoValidator _validator = new();

    // TEST-01: Accept every scalar exactly at its maximum length.
    /// <summary>Verifies all inclusive upper boundaries documented for OWN-08.</summary>
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

    // TEST-02: OWN-08 only asks for at least one document, so a lone food_safety_cert (the API Design sample) is valid.
    /// <summary>Verifies no business-license rule is applied on resubmission.</summary>
    [Fact]
    public async Task Validate_WithOnlyFoodSafetyCert_ShouldBeValid()
    {
        var result = await _validator.TestValidateAsync(ValidRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-03: Reject missing required legal fields and report each explicit null once.
    /// <summary>Verifies required fields reach the validator as null and get Vietnamese messages.</summary>
    [Fact]
    public async Task Validate_WithNullRequiredFields_ShouldReportEachOnce()
    {
        var request = new ResubmitVerificationRequestDto { LegalName = null, BusinessLicenseNo = null, Documents = null };

        var result = await _validator.TestValidateAsync(request);

        result.Errors.Select(error => (error.PropertyName, error.ErrorMessage)).Should().BeEquivalentTo(
        [
            ("LegalName", "Tên pháp lý là bắt buộc."),
            ("BusinessLicenseNo", "Số giấy phép kinh doanh là bắt buộc."),
            ("Documents", "Danh sách giấy tờ là bắt buộc.")
        ]);
    }

    // TEST-04: Reject every scalar one character beyond its maximum length.
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

    // TEST-05: Reject an empty document collection.
    /// <summary>Verifies documents must contain at least one item.</summary>
    [Fact]
    public async Task Validate_WithEmptyDocuments_ShouldHaveError()
    {
        var request = ValidRequest();
        request.Documents = Array.Empty<VerificationDocumentRequestDto>();

        var result = await _validator.TestValidateAsync(request);

        result.ShouldHaveValidationErrorFor(item => item.Documents);
    }

    // TEST-06: Propagate nested document validation with an indexed property path.
    /// <summary>Verifies invalid nested document type and media id are addressable by clients.</summary>
    [Fact]
    public async Task Validate_WithInvalidNestedDocument_ShouldHaveIndexedErrors()
    {
        var request = ValidRequest();
        request.Documents =
        [
            new VerificationDocumentRequestDto { DocType = "passport", MediaId = 0 }
        ];

        var result = await _validator.TestValidateAsync(request);

        result.Errors.Should().Contain(error => error.PropertyName == "Documents[0].DocType");
        result.Errors.Should().Contain(error => error.PropertyName == "Documents[0].MediaId");
    }

    // TEST-07: Keep the required fields nullable. MVC adds an implicit [Required] with an English message to
    // non-nullable reference properties and rejects an explicit null before the validator runs.
    /// <summary>Guards the nullability that lets the validator own every required-field message.</summary>
    [Theory]
    [InlineData(nameof(ResubmitVerificationRequestDto.LegalName))]
    [InlineData(nameof(ResubmitVerificationRequestDto.BusinessLicenseNo))]
    [InlineData(nameof(ResubmitVerificationRequestDto.Documents))]
    public void RequiredFields_ShouldBeNullableSoTheValidatorReportsNull(string propertyName)
    {
        var property = typeof(ResubmitVerificationRequestDto).GetProperty(propertyName)!;

        new System.Reflection.NullabilityInfoContext().Create(property).WriteState
            .Should().Be(System.Reflection.NullabilityState.Nullable);
    }

    /// <summary>Creates the smallest valid OWN-08 request, mirroring the API Design sample.</summary>
    private static ResubmitVerificationRequestDto ValidRequest() => new()
    {
        LegalName = "Hộ kinh doanh Bún Bò Bà Diệu",
        BusinessLicenseNo = "32A8012345",
        Documents =
        [
            new VerificationDocumentRequestDto
            {
                DocType = "food_safety_cert",
                MediaId = 9002
            }
        ]
    };
}
