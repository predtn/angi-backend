using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Audit;
using FluentAssertions;

namespace ANGI.Test.Application.Models.Audit
{
    public sealed class AuditCatalogTests
    {
        // TEST-01: Keep the 35 actions of sheet "Enum" (audit_logs.action), UPPER_SNAKE_CASE and within varchar(60).
        [Fact]
        public void AuditActions_ShouldMatchTheApiDesignCatalog()
        {
            AuditActions.All.Should().HaveCount(35);
            AuditActions.All.Should().OnlyContain(action => Regex.IsMatch(action, "^[A-Z]+(_[A-Z]+)*$") && action.Length <= 60);
            AuditActions.All.Should().Contain(["USER_LOGIN", "USER_SUSPENDED", "MENU_APPROVED", "BLOG_HIDDEN", "MOD_PERMISSIONS_CHANGED"]);
        }

        // TEST-02: Keep the 9 entity types of sheet "Enum" (audit_logs.entityType), snake_case and within varchar(40).
        [Fact]
        public void AuditEntityTypes_ShouldMatchTheApiDesignCatalog()
        {
            AuditEntityTypes.All.Should().BeEquivalentTo(
                "user", "restaurant", "menu_submission", "report", "blog", "blog_comment", "review", "review_reply", "role");
            AuditEntityTypes.All.Should().OnlyContain(type => type.Length <= 40);
        }
    }
}
