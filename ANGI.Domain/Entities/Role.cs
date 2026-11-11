using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class Role : BaseEntity<short>
    {
        public string Code { get; set; } = null!;
        public string Name { get; set; } = null!;

        public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
    }
}
