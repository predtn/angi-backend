namespace ANGI.Domain.Entities
{
    public class BlogImage
    {
        public long BlogId { get; set; }
        public long MediaId { get; set; }
        public short SortOrder { get; set; }

        public Blog Blog { get; set; } = null!;
        public MediaFile Media { get; set; } = null!;
    }
}
