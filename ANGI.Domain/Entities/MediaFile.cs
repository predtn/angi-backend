using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class MediaFile : BaseEntity<long>, IHasCreatedAt
    {
        public int? UploaderId { get; set; }
        public string StorageKey { get; set; } = null!;
        public string Url { get; set; } = null!;
        public string MimeType { get; set; } = null!;
        public long? SizeBytes { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
