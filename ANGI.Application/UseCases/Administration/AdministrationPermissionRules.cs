using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Administration;

namespace ANGI.Application.UseCases.Administration;

/// <summary>Enforces the permission catalog rules shared by MOD creation and permission replacement.</summary>
internal static class AdministrationPermissionRules
{
    private static readonly IReadOnlySet<string> AdminOnlyPermissions = new HashSet<string>(StringComparer.Ordinal)
    {
        "MOD_MANAGE",
        "SYSTEM_AUDIT_VIEW"
    };

    /// <summary>Resolves known grantable permission codes or throws the documented administration error.</summary>
    public static IReadOnlyDictionary<string, PermissionDefinition> ResolveGrantable(
        IEnumerable<string> codes,
        PermissionCatalogSnapshot catalog,
        string ungrantableErrorCode = "PERMISSION_NOT_GRANTABLE")
    {
        var permissionByCode = catalog.Permissions.ToDictionary(item => item.Code, StringComparer.Ordinal);
        foreach (var code in codes)
        {
            if (!permissionByCode.ContainsKey(code))
            {
                throw new BadRequestException("UNKNOWN_PERMISSION", "Mã quyền không tồn tại.");
            }

            if (AdminOnlyPermissions.Contains(code))
            {
                throw new BadRequestException(
                    ungrantableErrorCode,
                    ungrantableErrorCode == "UNKNOWN_PERMISSION"
                        ? "Mã quyền không hợp lệ cho tài khoản Mod."
                        : "Quyền này chỉ dành cho quản trị viên.");
            }
        }

        return permissionByCode;
    }
}
