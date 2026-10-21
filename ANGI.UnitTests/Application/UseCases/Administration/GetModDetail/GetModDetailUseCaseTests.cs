using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.UseCases.Administration.GetModDetail;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Administration.GetModDetail;

public sealed class GetModDetailUseCaseTests
{
    // TEST-01: Combine account, permission, and all handled-work data.
    /// <summary>Verifies ADM-M3 returns every documented response region and effective permissions.</summary>
    [Fact]
    public async Task ExecuteAsync_WithExistingMod_ShouldReturnCompleteDetail()
    {
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
        var useCase = new GetModDetailUseCase(modRepository.Object, permissionRepository.Object);

        var result = await useCase.ExecuteAsync(42, CancellationToken.None);

        result.Role.Should().Be("MOD");
        result.Permissions.Effective.Should().Equal("USER_SUSPEND");
        result.Handled.Should().BeEquivalentTo(new { Verifications = 37, Menus = 52, Reports = 80 });
    }

    // TEST-02: Return MOD_NOT_FOUND for a missing, deleted, or non-MOD account.
    /// <summary>Verifies account absence stops permission and statistics queries.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutMod_ShouldThrowDocumentedError()
    {
        var modRepository = new Mock<IAdministrationModRepository>();
        modRepository.Setup(item => item.GetModAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminUserSnapshot?)null);
        var permissionRepository = new Mock<IAdministrationPermissionRepository>();
        var useCase = new GetModDetailUseCase(modRepository.Object, permissionRepository.Object);

        var act = () => useCase.ExecuteAsync(42, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("MOD_NOT_FOUND");
        permissionRepository.VerifyNoOtherCalls();
        modRepository.Verify(item => item.GetHandledAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
