using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.UseCases.Administration.GetModDetail;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetModDetail;

/// <summary>Verifies GetModDetailUseCase combines a MOD's account, permissions and handled work, and hides non-MOD accounts.</summary>
public sealed class GetModDetailUseCaseTests
{
    // TEST-01: Combine account, permission, and all handled-work data.
    /// <summary>Verifies ADM-M3 returns every documented response region and effective permissions.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExistingMod_ShouldReturnCompleteDetail()
    {
        // STEP-01: Mock IAdministrationModRepository and IAdministrationPermissionRepository; stub modRepository.GetModAsync, modRepository.GetHandledAsync and 1 more.
        var modRepository = new Mock<IAdministrationModRepository>();
        modRepository.Setup(item => item.GetModAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminUserSnapshot(
                42, "mod@angi.vn", "Mod", "https://avatar", "MOD", UserStatus.Active,
                null, null, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
        modRepository.Setup(item => item.GetHandledAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModHandledSnapshot(37, 52, 80));
        var permissionRepository = new Mock<IAdministrationPermissionRepository>();
        permissionRepository.Setup(item => item.GetModPermissionsAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModPermissionsSnapshot(
                42,
                ["USER_SUSPEND", "USER_BAN"],
                [new(3, "USER_BAN", PermissionEffect.Deny)]));
        // STEP-02: Create the GetModDetailUseCase under test as useCase.
        var useCase = new GetModDetailUseCase(modRepository.Object, permissionRepository.Object);

        // STEP-03: Run useCase.ExecuteAsync and keep result.
        var result = await useCase.ExecuteAsync(42, CancellationToken.None);

        // STEP-04: Assert result.Role is "MOD", result.Permissions.Effective equals "USER_SUSPEND" and result.Handled is equivalent to the expected value.
        result.Role.Should().Be("MOD");
        result.Permissions.Effective.Should().Equal("USER_SUSPEND");
        result.Handled.Should().BeEquivalentTo(new { Verifications = 37, Menus = 52, Reports = 80 });
    }

    // TEST-02: Return MOD_NOT_FOUND for a missing, deleted, or non-MOD account.
    /// <summary>Verifies account absence stops permission and statistics queries.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutMod_ShouldThrowDocumentedError()
    {
        // STEP-01: Mock IAdministrationModRepository and IAdministrationPermissionRepository; stub modRepository.GetModAsync.
        var modRepository = new Mock<IAdministrationModRepository>();
        modRepository.Setup(item => item.GetModAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminUserSnapshot?)null);
        var permissionRepository = new Mock<IAdministrationPermissionRepository>();
        // STEP-02: Create the GetModDetailUseCase under test as useCase.
        var useCase = new GetModDetailUseCase(modRepository.Object, permissionRepository.Object);

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(42, CancellationToken.None);

        // STEP-04: Assert act throws NotFoundException and exception.ErrorCode is "MOD_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("MOD_NOT_FOUND");
        // STEP-05: Verify permissionRepository receives no other calls and GetHandledAsync is never called.
        permissionRepository.VerifyNoOtherCalls();
        modRepository.Verify(item => item.GetHandledAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
