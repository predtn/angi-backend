using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class UserPermission
    {
        public int UserId { get; set; }
        public short PermissionId { get; set; }
        public PermissionEffect Effect { get; set; } = PermissionEffect.Allow;
        public int? GrantedBy { get; set; }
        public DateTime GrantedAt { get; set; }

        public User User { get; set; } = null!;
        public Permission Permission { get; set; } = null!;
    }
}
