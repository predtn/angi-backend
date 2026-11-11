using System.Reflection;

namespace ANGI.Application.Common.Models.Audit
{
    /// <summary>Values of audit_logs.entity_type: sheet "Enum" (audit_logs.entityType) of the API Design.</summary>
    public static class AuditEntityTypes
    {
        public const string User = "user";
        public const string Restaurant = "restaurant";
        public const string MenuSubmission = "menu_submission";
        public const string Report = "report";
        public const string Blog = "blog";
        public const string BlogComment = "blog_comment";
        public const string Review = "review";
        public const string ReviewReply = "review_reply";

        /// <summary>entity_id is roles.id.</summary>
        public const string Role = "role";

        /// <summary>Every value above.</summary>
        public static IReadOnlySet<string> All { get; } = typeof(AuditEntityTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
