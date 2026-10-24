using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.UseCases.Administration.GetModDetail;

/// <summary>Implements ADM-M3 by combining MOD account, permission, and handled-work data.</summary>
public sealed class GetModDetailUseCase : IGetModDetailUseCase
{
    private readonly IAdministrationModRepository _modRepository;
    private readonly IAdministrationPermissionRepository _permissionRepository;

    /// <summary>Initializes the detailed MOD query with its two administration repositories.</summary>
    public GetModDetailUseCase(
        IAdministrationModRepository modRepository,
        IAdministrationPermissionRepository permissionRepository)
    {
        _modRepository = modRepository;
        _permissionRepository = permissionRepository;
    }

    /// <summary>Returns ADM-M3 data or the documented MOD_NOT_FOUND error.</summary>
    public async Task<ModDetailDto> ExecuteAsync(int userId, CancellationToken ct)
    {
        var mod = await _modRepository.GetModAsync(userId, ct)
            ?? throw ModNotFound();
        var permissions = await _permissionRepository.GetModPermissionsAsync(userId, ct)
            ?? throw ModNotFound();
        var handled = await _modRepository.GetHandledAsync(userId, ct);
        var card = AdministrationModMapper.ToCardDto(mod);

        return new ModDetailDto
        {
            Id = card.Id,
            Email = card.Email,
            DisplayName = card.DisplayName,
            AvatarUrl = card.AvatarUrl,
            Role = card.Role,
            Status = card.Status,
            SuspendedUntil = card.SuspendedUntil,
            LastLoginAt = card.LastLoginAt,
            CreatedAt = card.CreatedAt,
            Permissions = AdministrationPermissionMapper.ToModPermissionsDto(
                permissions.RoleDefaults,
                permissions.Overrides),
            Handled = new ModHandledDto
            {
                Verifications = handled.Verifications,
                Menus = handled.Menus,
                Reports = handled.Reports
            }
        };
    }

    /// <summary>Creates the documented missing-MOD exception.</summary>
    private static NotFoundException ModNotFound() =>
        new("MOD_NOT_FOUND", "Không tìm thấy tài khoản Mod.");
}
