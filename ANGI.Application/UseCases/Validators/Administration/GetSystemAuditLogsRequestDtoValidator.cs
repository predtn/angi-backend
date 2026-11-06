using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates ADM-A1 pagination, enum filters and the from/to range.</summary>
public sealed class GetSystemAuditLogsRequestDtoValidator : AbstractValidator<GetSystemAuditLogsRequestDto>
{
    private static readonly string[] ActorRoles = ["TRAVELER", "RESTAURANT_OWNER", "MOD", "ADMIN"];

    public GetSystemAuditLogsRequestDtoValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1).WithMessage("Trang phải bắt đầu từ 1.");
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100).WithMessage("Số phần tử mỗi trang phải từ 1 đến 100.");
        RuleFor(x => x.ActorId).GreaterThan(0).When(x => x.ActorId.HasValue).WithMessage("actorId không hợp lệ.");
        RuleFor(x => x.SubjectUserId).GreaterThan(0).When(x => x.SubjectUserId.HasValue).WithMessage("subjectUserId không hợp lệ.");
        RuleFor(x => x.EntityId).GreaterThan(0).When(x => x.EntityId.HasValue).WithMessage("entityId không hợp lệ.");

        RuleFor(x => x.ActorRole)
            .Must(role => ActorRoles.Contains(role!.Trim().ToUpperInvariant()))
            .When(x => x.ActorRole is not null)
            .WithMessage("Vai trò không hợp lệ.");

        RuleFor(x => x.Action)
            .Must(action => AuditActions.All.Contains(action!.Trim().ToUpperInvariant()))
            .When(x => x.Action is not null)
            .WithMessage("Mã hành động không hợp lệ.");

        RuleFor(x => x.EntityType)
            .Must(type => AuditEntityTypes.All.Contains(type!.Trim().ToLowerInvariant()))
            .When(x => x.EntityType is not null)
            .WithMessage("Loại đối tượng không hợp lệ.");

        RuleFor(x => x.From)
            .Must((x, from) => from!.Value <= x.To!.Value)
            .When(x => x.From.HasValue && x.To.HasValue)
            .WithMessage("Ngày bắt đầu phải nhỏ hơn hoặc bằng ngày kết thúc.");
    }
}
