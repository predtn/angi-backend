using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Discovery;

/// <summary>Validates the PREF-02 answers: 1..50 items, a required option code, and a scale value in 0..1.</summary>
public sealed class SubmitPreferenceSurveyRequestDtoValidator : AbstractValidator<SubmitPreferenceSurveyRequestDto>
{
    public const int MaxAnswers = 50;

    public SubmitPreferenceSurveyRequestDtoValidator()
    {
        RuleFor(x => x.Answers)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Câu trả lời khảo sát là bắt buộc.")
            .Must(answers => answers is { Count: >= 1 and <= MaxAnswers })
                .WithMessage($"Khảo sát phải có từ 1 đến {MaxAnswers} câu trả lời.");

        RuleForEach(x => x.Answers)
            .NotNull().WithMessage("Câu trả lời khảo sát không hợp lệ.")
            .ChildRules(answer =>
            {
                answer.RuleFor(x => x.OptionCode)
                    .NotEmpty().WithMessage("Mã lựa chọn là bắt buộc.");

                answer.RuleFor(x => x.ScaleValue)
                    .InclusiveBetween(0m, 1m).WithMessage("Mức độ phải nằm trong khoảng từ 0 đến 1.")
                    .When(x => x.ScaleValue.HasValue);
            });
    }
}
