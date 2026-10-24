using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Social;

/// <summary>Validates query parameters for BLOG-01 public blog listing.</summary>
public sealed class GetPublicBlogsRequestDtoValidator : AbstractValidator<GetPublicBlogsRequestDto>
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "newest",
        "popular"
    };

    /// <summary>Enforces filter, sort, and pagination constraints for BLOG-01.</summary>
    public GetPublicBlogsRequestDtoValidator()
    {
        RuleFor(x => x.AuthorId)
            .GreaterThan(0)
            .When(x => x.AuthorId.HasValue)
            .WithMessage("authorId phải lớn hơn 0.");

        RuleFor(x => x.Sort)
            .Must(sort => AllowedSorts.Contains(sort!))
            .When(x => !string.IsNullOrWhiteSpace(x.Sort))
            .WithMessage("sort chỉ nhận: newest, popular.");

        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page phải lớn hơn hoặc bằng 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("pageSize phải nằm trong khoảng 1 đến 100.");
    }
}
