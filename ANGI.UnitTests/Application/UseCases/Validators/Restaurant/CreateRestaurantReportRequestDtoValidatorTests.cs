using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Tests validation rules for CreateRestaurantReportRequestDto.</summary>
public sealed class CreateRestaurantReportRequestDtoValidatorTests
{
    private readonly CreateRestaurantReportRequestDtoValidator _validator = new();

    // TEST-01: A request with a valid reason, description and evidence list passes validation.
    /// <summary>Ensures a complete report request is not blocked.</summary>
    [Fact]
    public void Validate_WhenValidRequest_ShouldPassValidation()
    {
        var model = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "hygiene",
            Description = "Quán mất vệ sinh",
            EvidenceMediaIds = [1, 2]
        };

        var result = _validator.TestValidate(model);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // TEST-02: An empty reason code fails validation.
    /// <summary>Ensures a report always states a reason.</summary>
    [Fact]
    public void Validate_WhenReasonCodeIsEmpty_ShouldFailValidation()
    {
        var model = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "",
            Description = "Chi tiết"
        };

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ReasonCode)
            .WithErrorMessage("Lý do báo cáo là bắt buộc.");
    }

    // TEST-03: A reason code outside the allowed list fails validation.
    /// <summary>Ensures only documented reason codes are accepted.</summary>
    [Fact]
    public void Validate_WhenReasonCodeIsInvalid_ShouldFailValidation()
    {
        var model = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "invalid_reason"
        };

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.ReasonCode)
            .WithErrorMessage("Lý do báo cáo không hợp lệ.");
    }

    // TEST-04: Reason other with an empty description fails validation.
    /// <summary>Ensures an other-reason report explains itself.</summary>
    [Fact]
    public void Validate_WhenReasonCodeIsOtherAndDescriptionIsEmpty_ShouldFailValidation()
    {
        var model = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "other",
            Description = ""
        };

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage("Mô tả chi tiết là bắt buộc khi chọn lý do khác.");
    }

    // TEST-05: A description of 1001 characters (max + 1) fails validation.
    /// <summary>Ensures the description limit is applied at max + 1.</summary>
    [Fact]
    public void Validate_WhenDescriptionExceeds1000Characters_ShouldFailValidation()
    {
        var model = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "spam",
            Description = new string('A', 1001)
        };

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.Description)
            .WithErrorMessage("Mô tả không được vượt quá 1000 ký tự.");
    }

    // TEST-06: Six evidence media ids (max + 1) fail validation.
    /// <summary>Ensures the evidence limit is applied at max + 1.</summary>
    [Fact]
    public void Validate_WhenEvidenceMediaIdsExceeds5_ShouldFailValidation()
    {
        var model = new CreateRestaurantReportRequestDto
        {
            ReasonCode = "spam",
            EvidenceMediaIds = [1, 2, 3, 4, 5, 6]
        };

        var result = _validator.TestValidate(model);

        result.ShouldHaveValidationErrorFor(x => x.EvidenceMediaIds)
            .WithErrorMessage("Số lượng bằng chứng không được vượt quá 5.");
    }
}
