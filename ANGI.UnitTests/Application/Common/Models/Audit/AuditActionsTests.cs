using System.Text.RegularExpressions;
using ANGI.Application.Common.Models.Audit;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Audit;

/// <summary>Verifies the API audit catalog constraints.</summary>
public sealed class AuditActionsTests
{
    // TEST-01: Keep the 35 actions of sheet "Enum" (audit_logs.action), UPPER_SNAKE_CASE and within varchar(60).
    /// <summary>Keep the 35 actions of sheet &quot;Enum&quot; (audit_logs.action), UPPER_SNAKE_CASE and within varchar(60).</summary>
    [Fact]
    public void AuditActions_ShouldMatchTheApiDesignCatalog()
    {
        AuditActions.All.Should().HaveCount(35);
        AuditActions.All.Should().OnlyContain(action => Regex.IsMatch(action, "^[A-Z]+(_[A-Z]+)*$") && action.Length <= 60);
        AuditActions.All.Should().Contain(["USER_LOGIN", "USER_SUSPENDED", "MENU_APPROVED", "BLOG_HIDDEN", "MOD_PERMISSIONS_CHANGED"]);
    }
}
