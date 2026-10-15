using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Discovery;

/// <summary>Validates the HOME-01 block size.</summary>
public sealed class GetHomeRequestDtoValidator : AbstractValidator<GetHomeRequestDto>
{
    public const int MaxLimit = 50;

    public GetHomeRequestDtoValidator()
    {
        RuleFor(x => x.Limit)
            .InclusiveBetween(1, MaxLimit).WithMessage($"limit phải từ 1 đến {MaxLimit}.")
            .When(x => x.Limit.HasValue);
    }
}
