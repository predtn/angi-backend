using ANGI.Application.DTOs.Discovery;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Discovery;

/// <summary>Validates the optional CAT-02 type.</summary>
public sealed class GetTagsRequestDtoValidator : AbstractValidator<GetTagsRequestDto>
{
    public static readonly IReadOnlySet<string> TagTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "meal_time",
        "dietary",
        "category",
        "meal_role",
        "cooking_method",
        "ingredient"
    };

    public GetTagsRequestDtoValidator()
    {
        RuleFor(x => x.Type)
            .Must(type => TagTypes.Contains(type!))
            .WithMessage("type chỉ có thể là meal_time, dietary, category, meal_role, cooking_method hoặc ingredient.")
            .When(x => x.Type is not null);
    }
}
