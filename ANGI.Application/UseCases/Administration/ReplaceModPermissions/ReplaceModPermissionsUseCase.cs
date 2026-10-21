using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.ReplaceModPermissions;

/// <summary>Implements ADM-P3 by atomically replacing one MOD account's permission overrides.</summary>
public sealed class ReplaceModPermissionsUseCase : IReplaceModPermissionsUseCase
{
    private static readonly IReadOnlySet<string> AdminOnlyPermissions = new HashSet<string>(StringComparer.Ordinal)
    {
        "MOD_MANAGE",
        "SYSTEM_AUDIT_VIEW"
    };

    private readonly IValidator<ReplaceModPermissionsRequestDto> _validator;
    private readonly IAdministrationPermissionRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes validation, persistence, caller identity, audit, transaction, and time dependencies.</summary>
    public ReplaceModPermissionsUseCase(
        IValidator<ReplaceModPermissionsRequestDto> validator,
        IAdministrationPermissionRepository repository,
        ICurrentUserService currentUserService,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _repository = repository;
        _currentUserService = currentUserService;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Validates, replaces, audits, and returns the resulting MOD permission state.</summary>
    public async Task<ModPermissionsDto> ExecuteAsync(
        int userId,
        ReplaceModPermissionsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var actorId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var current = await _repository.GetModPermissionsAsync(userId, ct)
            ?? throw new NotFoundException("MOD_NOT_FOUND", "Không tìm thấy tài khoản Mod.");
        var catalog = await _repository.GetCatalogAsync(ct);
        var permissionByCode = catalog.Permissions.ToDictionary(item => item.Code, StringComparer.Ordinal);

        EnsureKnownAndGrantable(request.Overrides.Select(item => item.PermissionCode), permissionByCode);

        var replacements = request.Overrides
            .Select(item => new PermissionOverrideChange(
                permissionByCode[item.PermissionCode].Id,
                item.Effect == "allow" ? PermissionEffect.Allow : PermissionEffect.Deny))
            .OrderBy(item => item.PermissionId)
            .ToArray();
        var replacementSnapshots = replacements
            .Select(item => new PermissionOverrideSnapshot(
                item.PermissionId,
                catalog.Permissions.First(permission => permission.Id == item.PermissionId).Code,
                item.Effect))
            .ToArray();

        await _repository.ReplaceModOverridesAsync(
            userId,
            actorId,
            _timeProvider.GetUtcNow().UtcDateTime,
            replacements,
            ct);
        _auditLogService.Add(new AuditLogEntry
        {
            Action = AuditActions.ModPermissionsChanged,
            EntityType = AuditEntityTypes.User,
            EntityId = userId,
            SubjectUserId = userId,
            OldValues = new { Overrides = ToAuditValues(current.Overrides) },
            NewValues = new { Overrides = ToAuditValues(replacementSnapshots) }
        });
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return AdministrationPermissionMapper.ToModPermissionsDto(current.RoleDefaults, replacementSnapshots);
    }

    /// <summary>Rejects codes outside the catalog and permissions reserved exclusively for administrators.</summary>
    private static void EnsureKnownAndGrantable(
        IEnumerable<string> codes,
        IReadOnlyDictionary<string, PermissionDefinition> permissionByCode)
    {
        foreach (var code in codes)
        {
            if (!permissionByCode.ContainsKey(code))
            {
                throw new BadRequestException("UNKNOWN_PERMISSION", "Mã quyền không tồn tại.");
            }

            if (AdminOnlyPermissions.Contains(code))
            {
                throw new BadRequestException(
                    "PERMISSION_NOT_GRANTABLE",
                    "Quyền này chỉ dành cho quản trị viên.");
            }
        }
    }

    /// <summary>Creates a stable audit representation without persistence identifiers.</summary>
    private static object[] ToAuditValues(IReadOnlyList<PermissionOverrideSnapshot> overrides) =>
        overrides.Select(item => (object)new
        {
            item.PermissionCode,
            Effect = item.Effect == PermissionEffect.Allow ? "allow" : "deny"
        }).ToArray();
}
