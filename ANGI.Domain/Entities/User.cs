using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class User : BaseEntity<int>, IHasCreatedAt, IHasUpdatedAt, ISoftDelete
    {
        public string Email { get; set; } = null!;
        public short RoleId { get; set; }
        public string? PasswordHash { get; set; }
        public string DisplayName { get; set; } = null!;
        public long? AvatarMediaId { get; set; }
        public string? Phone { get; set; }
        public string? Bio { get; set; }
        public DateTime? EmailVerifiedAt { get; set; }
        public UserStatus Status { get; set; } = UserStatus.PendingVerification;
        public DateTime? SuspendedUntil { get; set; }
        public DateTime? LastLoginAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        public Role Role { get; set; } = null!;
        public MediaFile? AvatarMedia { get; set; }
    }
}
