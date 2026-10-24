using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Administration;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Administration;

public sealed class PermissionsControllerTests
{
    // TEST-01: Return the ADM-P1 payload in an HTTP 200 API envelope.
    /// <summary>Verifies catalog delegation and the documented success envelope.</summary>
    [Fact]
    public async Task GetPermissionCatalog_ShouldReturnOkApiResponse()
    {
        var expected = new PermissionCatalogDto();
        var useCase = new Mock<IGetPermissionCatalogUseCase>();
        useCase.Setup(item => item.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(getCatalog: useCase.Object);

        var action = await controller.GetPermissionCatalog(CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<PermissionCatalogDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-02: Pass the ADM-P2 route user ID and return its HTTP 200 API envelope.
    /// <summary>Verifies MOD permission query delegation and response typing.</summary>
    [Fact]
    public async Task GetModPermissions_ShouldDelegateUserIdAndReturnOkApiResponse()
    {
        var expected = new ModPermissionsDto();
        var useCase = new Mock<IGetModPermissionsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(42, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(getMod: useCase.Object);

        var action = await controller.GetModPermissions(42, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<ModPermissionsDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-03: Pass the ADM-P3 route and body and return its HTTP 200 API envelope.
    /// <summary>Verifies MOD override replacement delegation and response typing.</summary>
    [Fact]
    public async Task ReplaceModPermissions_ShouldDelegateRequestAndReturnOkApiResponse()
    {
        var request = new ReplaceModPermissionsRequestDto { Overrides = [] };
        var expected = new ModPermissionsDto();
        var useCase = new Mock<IReplaceModPermissionsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(42, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(replaceMod: useCase.Object);

        var action = await controller.ReplaceModPermissions(42, request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<ModPermissionsDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-04: Pass the ADM-P4 body and return its HTTP 200 API envelope.
    /// <summary>Verifies MOD role-default replacement delegation and response typing.</summary>
    [Fact]
    public async Task ReplaceModRolePermissions_ShouldDelegateRequestAndReturnOkApiResponse()
    {
        var request = new ReplaceModRolePermissionsRequestDto { PermissionCodes = [] };
        var expected = new ReplaceModRolePermissionsResponseDto();
        var useCase = new Mock<IReplaceModRolePermissionsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = Create(replaceRole: useCase.Object);

        var action = await controller.ReplaceModRolePermissions(request, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<ReplaceModRolePermissionsResponseDto>>()
            .Which.Data.Should().BeSameAs(expected);
    }

    /// <summary>Creates the controller while allowing one dependency at a time to be replaced by a strict test mock.</summary>
    private static PermissionsController Create(
        IGetPermissionCatalogUseCase? getCatalog = null,
        IGetModPermissionsUseCase? getMod = null,
        IReplaceModPermissionsUseCase? replaceMod = null,
        IReplaceModRolePermissionsUseCase? replaceRole = null) => new(
            getCatalog ?? Mock.Of<IGetPermissionCatalogUseCase>(),
            getMod ?? Mock.Of<IGetModPermissionsUseCase>(),
            replaceMod ?? Mock.Of<IReplaceModPermissionsUseCase>(),
            replaceRole ?? Mock.Of<IReplaceModRolePermissionsUseCase>());
}
