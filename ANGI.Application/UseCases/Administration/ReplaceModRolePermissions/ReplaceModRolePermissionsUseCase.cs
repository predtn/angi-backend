using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.ReplaceModRolePermissions;

/// <summary>Implements ADM-P4 by atomically replacing the MOD role's default permissions.</summary>
public sealed class ReplaceModRolePermissionsUseCase : IReplaceModRolePermissionsUseCase
{
    private readonly IValidator<ReplaceModRolePermissionsRequestDto> _validator;
    private readonly IAdministrationPermissionRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes validation, persistence, identity, audit, and transaction dependencies.</summary>
    public ReplaceModRolePermissionsUseCase(
        IValidator<ReplaceModRolePermissionsRequestDto> validator,
        IAdministrationPermissionRepository repository,
        ICurrentUserService currentUserService,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _repository = repository;
        _currentUserService = currentUserService;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Validates, replaces, audits, and returns the resulting MOD role defaults.</summary>
    public async Task<ReplaceModRolePermissionsResponseDto> ExecuteAsync(
        ReplaceModRolePermissionsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        _ = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var catalog = await _repository.GetCatalogAsync(ct);
        var permissionByCode = AdministrationPermissionRules.ResolveGrantable(request.PermissionCodes, catalog);

        var replacements = request.PermissionCodes
            .Select(code => permissionByCode[code])
            .OrderBy(permission => permission.Id)
            .ToArray();
        var replacementCodes = replacements.Select(permission => permission.Code).ToArray();

        await _repository.ReplaceModRoleDefaultsAsync(
            catalog.ModRoleId,
            replacements.Select(permission => permission.Id).ToArray(),
            ct);
        _auditLogService.Add(new AuditLogEntry
        {
            Action = AuditActions.RolePermissionsChanged,
            EntityType = AuditEntityTypes.Role,
            EntityId = catalog.ModRoleId,
            OldValues = new { PermissionCodes = catalog.ModDefaults },
            NewValues = new { PermissionCodes = replacementCodes }
        });
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ReplaceModRolePermissionsResponseDto
        {
            Role = "MOD",
            PermissionCodes = replacementCodes
        };
    }

}
