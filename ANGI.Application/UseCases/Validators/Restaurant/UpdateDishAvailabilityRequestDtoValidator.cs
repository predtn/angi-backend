using ANGI.Application.DTOs.Restaurant;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Restaurant;

/// <summary>Validates the required OWN-10 availability value.</summary>
public sealed class UpdateDishAvailabilityRequestDtoValidator : AbstractValidator<UpdateDishAvailabilityRequestDto>
{
    /// <summary>Requires a boolean value instead of accepting an omitted property.</summary>
    public UpdateDishAvailabilityRequestDtoValidator()
    {
        RuleFor(request => request.IsAvailable)
            .NotNull().WithMessage("Trạng thái còn món không được để trống.");
    }
}
