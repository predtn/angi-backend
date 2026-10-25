using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Validators.Restaurant;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace ANGI.UnitTests.Application.UseCases.Validators.Restaurant;

/// <summary>Tests validation rules for CreateRestaurantReportRequestDto.</summary>
public sealed class CreateRestaurantReportRequestDtoValidatorTests
{
    private readonly CreateRestaurantReportRequestDtoValidator _validator = new();

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
