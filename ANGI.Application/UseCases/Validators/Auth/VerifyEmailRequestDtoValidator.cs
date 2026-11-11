using ANGI.Application.DTOs.Auth;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Auth
{
    public sealed class VerifyEmailRequestDtoValidator : AbstractValidator<VerifyEmailRequestDto>
    {
        /// <summary>Defines the required token rule for AUTH-02.</summary>
        public VerifyEmailRequestDtoValidator()
        {
            RuleFor(x => x.Token)
                .NotEmpty().WithMessage("Token xác minh là bắt buộc.");
        }
    }
}
