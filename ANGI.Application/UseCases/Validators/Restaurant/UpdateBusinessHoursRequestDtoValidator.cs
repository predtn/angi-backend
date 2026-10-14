using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>
/// Validates the shape of each OWN-04 interval. Overlapping intervals are not a field error: the use case reports them
/// as INVALID_BUSINESS_HOURS, the code the API Design gives OWN-04.
/// </summary>
public sealed class UpdateBusinessHoursRequestDtoValidator : AbstractValidator<UpdateBusinessHoursRequestDto>
{
    public UpdateBusinessHoursRequestDtoValidator()
    {
        RuleFor(x => x.Hours)
            .NotNull().WithMessage("Danh sách giờ mở cửa là bắt buộc (gửi mảng rỗng nếu chưa khai báo).");

        RuleForEach(x => x.Hours)
            .NotNull().WithMessage("Khung giờ mở cửa không hợp lệ.")
            .SetValidator(new BusinessHourDtoValidator());
    }
}
