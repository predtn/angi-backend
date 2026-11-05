using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.UseCases.Administration.GetModPermissions;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetModPermissions;

/// <summary>Verifies GetModPermissionsUseCase computes a MOD's effective permissions from role defaults and overrides.</summary>
public sealed class GetModPermissionsUseCaseTests
{
    // TEST-01: Calculate role defaults plus allows minus denies without duplicates.
    /// <summary>Verifies the effective-permission formula and lowercase effect mapping for ADM-P2.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOverrides_ShouldCalculateEffectivePermissions()
    {
        // STEP-01: Mock IAdministrationPermissionRepository; stub repository.GetModPermissionsAsync.
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetModPermissionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModPermissionsSnapshot(
                42,
                ["USER_SUSPEND", "USER_BAN"],
                [
                    new(3, "USER_BAN", PermissionEffect.Deny),
                    new(13, "DASHBOARD_VIEW", PermissionEffect.Allow)
                ]));
        // STEP-02: Create the GetModPermissionsUseCase under test as useCase.
        var useCase = new GetModPermissionsUseCase(repository.Object);

        // STEP-03: Run useCase.ExecuteAsync and keep result.
        var result = await useCase.ExecuteAsync(42, CancellationToken.None);

        // STEP-04: Assert result.RoleDefaults equals "USER_SUSPEND", result.Overrides.Select(filtered) equals "deny" and result.Effective equals "USER_SUSPEND".
        result.RoleDefaults.Should().Equal("USER_SUSPEND", "USER_BAN");
        result.Overrides.Select(item => item.Effect).Should().Equal("deny", "allow");
        result.Effective.Should().Equal("USER_SUSPEND", "DASHBOARD_VIEW");
    }

    // TEST-02: Return MOD_NOT_FOUND for a missing, deleted, or non-MOD account.
    /// <summary>Verifies the sole documented ADM-P2 resource error.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutMod_ShouldThrowModNotFound()
    {
        // STEP-01: Mock IAdministrationPermissionRepository; stub repository.GetModPermissionsAsync.
        var repository = new Mock<IAdministrationPermissionRepository>();
        repository.Setup(item => item.GetModPermissionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModPermissionsSnapshot?)null);
        // STEP-02: Create the GetModPermissionsUseCase under test as useCase.
        var useCase = new GetModPermissionsUseCase(repository.Object);

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(42, CancellationToken.None);

        // STEP-04: Assert act throws NotFoundException and exception.ErrorCode is "MOD_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("MOD_NOT_FOUND");
    }
}
