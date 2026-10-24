using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.UseCases.Administration.GetModPermissions;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetModPermissions;

public sealed class GetModPermissionsUseCaseTests
{
    // TEST-01: Calculate role defaults plus allows minus denies without duplicates.
    /// <summary>Verifies the effective-permission formula and lowercase effect mapping for ADM-P2.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOverrides_ShouldCalculateEffectivePermissions()
    {
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetModPermissionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModPermissionsSnapshot(
                42,
                ["USER_SUSPEND", "USER_BAN"],
                [
                    new(3, "USER_BAN", PermissionEffect.Deny),
                    new(13, "DASHBOARD_VIEW", PermissionEffect.Allow)
                ]));
        var useCase = new GetModPermissionsUseCase(repository.Object);

        var result = await useCase.ExecuteAsync(42, CancellationToken.None);

        result.RoleDefaults.Should().Equal("USER_SUSPEND", "USER_BAN");
        result.Overrides.Select(item => item.Effect).Should().Equal("deny", "allow");
        result.Effective.Should().Equal("USER_SUSPEND", "DASHBOARD_VIEW");
    }

    // TEST-02: Return MOD_NOT_FOUND for a missing, deleted, or non-MOD account.
    /// <summary>Verifies the sole documented ADM-P2 resource error.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutMod_ShouldThrowModNotFound()
    {
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetModPermissionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModPermissionsSnapshot?)null);
        var useCase = new GetModPermissionsUseCase(repository.Object);

        var act = () => useCase.ExecuteAsync(42, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("MOD_NOT_FOUND");
    }
}
