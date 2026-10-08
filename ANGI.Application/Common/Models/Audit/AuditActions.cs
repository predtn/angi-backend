using System.Reflection;

namespace ANGI.Application.Common.Models.Audit
{
    /// <summary>
    /// Values of audit_logs.action: sheet "Enum" (audit_logs.action) of the API Design. The endpoint
    /// that writes each one is named in column "Bảng DB" of sheet "Backend API".
    /// </summary>
    public static class AuditActions
    {
        // Auth, Account
        public const string UserRegistered = "USER_REGISTERED";
        public const string UserLogin = "USER_LOGIN";
        public const string PasswordReset = "PASSWORD_RESET";
        public const string ProfileUpdated = "PROFILE_UPDATED";
        public const string PasswordChanged = "PASSWORD_CHANGED";

        // Restaurant owner
        public const string RestaurantUpdated = "RESTAURANT_UPDATED";
        public const string RestaurantOperatingStatusChanged = "RESTAURANT_OPERATING_STATUS_CHANGED";

        // Moderation: users
        public const string UserWarned = "USER_WARNED";
        public const string UserSuspended = "USER_SUSPENDED";
        public const string UserBanned = "USER_BANNED";
        public const string UserUnbanned = "USER_UNBANNED";

        // Moderation: restaurants, verifications, menus, reports
        public const string RestaurantHidden = "RESTAURANT_HIDDEN";
        public const string RestaurantSuspended = "RESTAURANT_SUSPENDED";
        public const string RestaurantShown = "RESTAURANT_SHOWN";
        public const string RestaurantVerified = "RESTAURANT_VERIFIED";
        public const string RestaurantVerificationRejected = "RESTAURANT_VERIFICATION_REJECTED";
        public const string MenuApproved = "MENU_APPROVED";
        public const string MenuRejected = "MENU_REJECTED";
        public const string ReportResolved = "REPORT_RESOLVED";

        // Moderation: content
        public const string BlogHidden = "BLOG_HIDDEN";
        public const string BlogRemoved = "BLOG_REMOVED";
        public const string BlogRestored = "BLOG_RESTORED";
        public const string CommentHidden = "COMMENT_HIDDEN";
        public const string CommentRemoved = "COMMENT_REMOVED";
        public const string CommentRestored = "COMMENT_RESTORED";
        public const string ReviewHidden = "REVIEW_HIDDEN";
        public const string ReviewRemoved = "REVIEW_REMOVED";
        public const string ReviewRestored = "REVIEW_RESTORED";
        public const string ReviewReplyHidden = "REVIEW_REPLY_HIDDEN";
        public const string ReviewReplyRemoved = "REVIEW_REPLY_REMOVED";
        public const string ReviewReplyRestored = "REVIEW_REPLY_RESTORED";

        // Administration
        public const string ModCreated = "MOD_CREATED";
        public const string ModUpdated = "MOD_UPDATED";
        public const string ModPermissionsChanged = "MOD_PERMISSIONS_CHANGED";
        public const string RolePermissionsChanged = "ROLE_PERMISSIONS_CHANGED";

        /// <summary>Every value above.</summary>
        public static IReadOnlySet<string> All { get; } = typeof(AuditActions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
