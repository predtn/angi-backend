using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class Permission : BaseEntity<short>
    {
        public string Code { get; set; } = null!;
        public string? Description { get; set; }
    }
}
