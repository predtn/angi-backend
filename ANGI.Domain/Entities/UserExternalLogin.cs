using ANGI.Domain.Common;
using ANGI.Domain.Enums;

namespace ANGI.Domain.Entities
{
    public class UserExternalLogin : BaseEntity<long>
    {
        public int UserId { get; set; }
        public ExternalLoginProvider Provider { get; set; } = ExternalLoginProvider.Google;
        public string ProviderUserId { get; set; } = null!;
        public DateTime LinkedAt { get; set; }

        public User User { get; set; } = null!;
    }
}
