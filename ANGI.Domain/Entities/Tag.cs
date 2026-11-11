using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class Tag : BaseEntity<short>
    {
        public TagType TagType { get; set; }
        public string Code { get; set; } = null!;
        public string Name { get; set; } = null!;
    }
}
