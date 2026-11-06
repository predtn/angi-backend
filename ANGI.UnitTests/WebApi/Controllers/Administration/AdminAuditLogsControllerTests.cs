using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Administration;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Administration;

public sealed class AdminAuditLogsControllerTests
{
    [Fact]
    public void Controller_RequiresAdminRole()
    {
        typeof(AdminAuditLogsController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Should().ContainSingle(a => a.Roles == "ADMIN");
    }

    [Fact]
    public async Task GetAuditLogs_ReturnsOkApiResponse()
    {
        var request = new GetSystemAuditLogsRequestDto();
        var expected = new Paged<AuditLogDto>();
        var list = new Mock<IGetSystemAuditLogsUseCase>();
        list.Setup(u => u.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new AdminAuditLogsController(list.Object, Mock.Of<IGetSystemAuditLogDetailUseCase>());

        var action = await controller.GetAuditLogs(request, CancellationToken.None);

        var ok = action.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ApiResponse<Paged<AuditLogDto>>>().Which.Data.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetAuditLogDetail_ReturnsOkApiResponse()
    {
        var expected = new AuditLogDetailDto { Id = 5 };
        var detail = new Mock<IGetSystemAuditLogDetailUseCase>();
        detail.Setup(u => u.ExecuteAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new AdminAuditLogsController(Mock.Of<IGetSystemAuditLogsUseCase>(), detail.Object);

        var action = await controller.GetAuditLogDetail(5, CancellationToken.None);

        var ok = action.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ApiResponse<AuditLogDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }
}
