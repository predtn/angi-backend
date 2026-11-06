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

        /// <summary>
        /// The session that a refresh rotated this one into; null when it was revoked any other way
        /// (logout, password change, ban, reuse detection), which tells a recent rotation from a logout.
        /// </summary>
        public long? ReplacedBySessionId { get; set; }

        public User User { get; set; } = null!;
        public UserSession? ReplacedBySession { get; set; }
    }
}
