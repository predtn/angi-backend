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
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Require _currentUserService.UserId.
        _ = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Yêu cầu đăng nhập.");

        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Load catalog through repository.GetCatalogAsync.
        var catalog = await _repository.GetCatalogAsync(ct);
        // STEP-05: Prepare permissionByCode, replacements and replacementCodes, then call repository.ReplaceModRoleDefaultsAsync.
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
        // STEP-06: Record audit action AuditActions.RolePermissionsChanged.
        _auditLogService.Add(new AuditLogEntry
        {
            Action = AuditActions.RolePermissionsChanged,
            EntityType = AuditEntityTypes.Role,
            EntityId = catalog.ModRoleId,
            OldValues = new { PermissionCodes = catalog.ModDefaults },
            NewValues = new { PermissionCodes = replacementCodes }
        });
        // STEP-07: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-08: Commit the transaction.
        await transaction.CommitAsync(ct);

        // STEP-09: Return a new ReplaceModRolePermissionsResponseDto.
        return new ReplaceModRolePermissionsResponseDto
        {
            Role = "MOD",
            PermissionCodes = replacementCodes
        };
    }

}
