using ANGI.Domain.Enums;

namespace ANGI.Application.Common.Models.Auth
{
    /// <summary>The fields of a user that decide whether the account may call endpoints that need a token.</summary>
    public sealed record AccountStatusSnapshot(UserStatus Status, DateTime? SuspendedUntil);
}
