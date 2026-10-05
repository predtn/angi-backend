using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class MenuSubmissionItem : BaseEntity<long>
    {
        public long SubmissionId { get; set; }
        public int? SourceDishId { get; set; }
        public string Name { get; set; } = null!;
        public string? Description { get; set; }
        public DishForm DishForm { get; set; } = DishForm.Other;
        public decimal Price { get; set; }
        public long? CoverMediaId { get; set; }
        public short[] TagIds { get; set; } = [];
        public short SortOrder { get; set; }
        public int? ResultDishId { get; set; }

        public MenuSubmission Submission { get; set; } = null!;
    }
}
