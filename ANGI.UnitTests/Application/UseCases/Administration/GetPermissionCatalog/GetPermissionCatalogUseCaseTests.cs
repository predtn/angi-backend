using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.UseCases.Administration.GetPermissionCatalog;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetPermissionCatalog;

public sealed class GetPermissionCatalogUseCaseTests
{
    // TEST-01: Map every catalog field and the documented ADMIN wildcard.
    /// <summary>Verifies ADM-P1 mapping, ordering, nullable descriptions, and role-default semantics.</summary>
    [Fact]
    public async Task ExecuteAsync_WithCatalog_ShouldReturnDocumentedResponse()
    {
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PermissionCatalogSnapshot(
                3,
                [new(1, "USER_BAN", "Ban user"), new(2, "DASHBOARD_VIEW", null)],
                ["USER_BAN"]));
        var useCase = new GetPermissionCatalogUseCase(repository.Object);

        var result = await useCase.ExecuteAsync(CancellationToken.None);

        result.Permissions.Select(item => item.Code).Should().Equal("USER_BAN", "DASHBOARD_VIEW");
        result.Permissions[1].Description.Should().BeEmpty();
        result.RoleDefaults.Mod.Should().Equal("USER_BAN");
        result.RoleDefaults.Admin.Should().Equal("*");
    }
}
