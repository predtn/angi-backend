using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates request body for submitting a restaurant review (REV-01).</summary>
public sealed class CreateRestaurantReviewRequestDtoValidator : AbstractValidator<CreateRestaurantReviewRequestDto>
{
    public CreateRestaurantReviewRequestDtoValidator()
    {
        RuleFor(x => x.Rating)
            .InclusiveBetween((short)1, (short)5)
            .WithMessage("Điểm đánh giá phải từ 1 đến 5 sao.");

        RuleFor(x => x.Content)
            .MaximumLength(2000)
            .WithMessage("Nội dung đánh giá không được vượt quá 2000 ký tự.")
            .When(x => !string.IsNullOrEmpty(x.Content));

        RuleFor(x => x.ImageMediaIds)
            .Must(ids => ids == null || ids.Count <= 5)
            .WithMessage("Số lượng ảnh đính kèm không được vượt quá 5 ảnh.");

        RuleForEach(x => x.ImageMediaIds)
            .GreaterThan(0)
            .WithMessage("Mã ảnh đính kèm không hợp lệ.")
            .When(x => x.ImageMediaIds != null);

        RuleFor(x => x.RoadmapItemId)
            .GreaterThan(0)
            .WithMessage("Mã mục lịch trình không hợp lệ.")
            .When(x => x.RoadmapItemId.HasValue);
    }
}
