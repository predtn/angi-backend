using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Moderation;

/// <summary>Validates MOD-U5 pagination, action code, and date range filter parameters.</summary>
public sealed class GetUserAuditLogsRequestDtoValidator : AbstractValidator<GetUserAuditLogsRequestDto>
{
    public GetUserAuditLogsRequestDtoValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Trang phải bắt đầu từ 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithMessage("Số phần tử mỗi trang phải từ 1 đến 100.");

        RuleFor(x => x.Action)
            .Must(action => action is null || (!string.IsNullOrWhiteSpace(action) && AuditActions.All.Contains(action.Trim().ToUpperInvariant())))
            .WithMessage("Mã hành động không hợp lệ.");

        RuleFor(x => x)
            .Must(x => !(x.From.HasValue && x.To.HasValue) || x.From.Value <= x.To.Value)
            .WithName("from")
            .WithMessage("Ngày bắt đầu phải nhỏ hơn hoặc bằng ngày kết thúc.");
    }
}
