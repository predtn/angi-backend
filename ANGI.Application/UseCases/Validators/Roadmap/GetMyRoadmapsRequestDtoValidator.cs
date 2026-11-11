using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Roadmap;

/// <summary>Validates pagination query parameters for RM-02.</summary>
public sealed class GetMyRoadmapsRequestDtoValidator : AbstractValidator<GetMyRoadmapsRequestDto>
{
    /// <summary>Enforces the shared one-based pagination contract and maximum page size.</summary>
    public GetMyRoadmapsRequestDtoValidator()
    {
        RuleFor(request => request.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Trang phải bắt đầu từ 1.");

        RuleFor(request => request.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("Số lượng phần tử mỗi trang phải từ 1 đến 100.");
    }
}
