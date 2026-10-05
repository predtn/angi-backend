using System.Net;
using ANGI.Domain.Common;

namespace ANGI.Domain.Entities
{
    public class UserSession : BaseEntity<long>, IHasCreatedAt
    {
        public int UserId { get; set; }
        public string RefreshTokenHash { get; set; } = null!;
        public string? UserAgent { get; set; }
        public IPAddress? IpAddress { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? RevokedAt { get; set; }

        public User User { get; set; } = null!;
    }
}
