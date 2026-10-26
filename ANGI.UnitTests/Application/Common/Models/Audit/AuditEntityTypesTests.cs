using ANGI.Application.Common.Models.Audit;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Common.Models.Audit;

/// <summary>Verifies the API audit catalog constraints.</summary>
public sealed class AuditEntityTypesTests
{
    // TEST-01: Keep the 9 entity types of sheet "Enum" (audit_logs.entityType), snake_case and within varchar(40).
    /// <summary>Keep the 9 entity types of sheet &quot;Enum&quot; (audit_logs.entityType), snake_case and within varchar(40).</summary>
    [Fact]
    public void AuditEntityTypes_ShouldMatchTheApiDesignCatalog()
    {
        AuditEntityTypes.All.Should().BeEquivalentTo(
            "user", "restaurant", "menu_submission", "report", "blog", "blog_comment", "review", "review_reply", "role");
        AuditEntityTypes.All.Should().OnlyContain(type => type.Length <= 40);
    }
}
