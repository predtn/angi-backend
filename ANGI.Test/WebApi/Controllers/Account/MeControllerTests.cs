using ANGI.Application.Common.Interfaces.UseCases.Account;
using ANGI.Application.DTOs.Account;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Account;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.Test.WebApi.Controllers.Account;

public sealed class MeControllerTests
{
    // TEST-01: Verify MeController requires [Authorize] attribute.
    [Fact]
    public void Controller_ShouldRequireAuthorization()
    {
        var authorizeAttribute = typeof(MeController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true);

        authorizeAttribute.Should().NotBeEmpty("MeController must be protected with [Authorize]");
    }

    // TEST-02: Verify endpoint has ProducesResponseType annotations for 200, 400, 401.
    [Fact]
    public void ChangePassword_ShouldHaveDocumentedOpenApiProducesResponseTypes()
    {
        var method = typeof(MeController).GetMethod(nameof(MeController.ChangePassword));
        method.Should().NotBeNull();

        var producesResponseTypes = method!
            .GetCustomAttributes(typeof(ProducesResponseTypeAttribute), inherit: true)
            .Cast<ProducesResponseTypeAttribute>()
            .Select(attr => attr.StatusCode)
            .ToList();

        producesResponseTypes.Should().Contain(StatusCodes.Status200OK);
        producesResponseTypes.Should().Contain(StatusCodes.Status400BadRequest);
        producesResponseTypes.Should().Contain(StatusCodes.Status401Unauthorized);
    }

    // TEST-03: Verify successful execution invokes use case and returns 200 OK with null Data.
    [Fact]
    public async Task ChangePassword_WhenSuccessful_ShouldReturnOkWithNullData()
    {
        var useCase = new Mock<IChangePasswordUseCase>();
        var controller = new MeController(useCase.Object);

        var request = new ChangePasswordRequestDto
        {
            CurrentPassword = "OldPassword123",
            NewPassword = "NewPassword456"
        };

        var actionResult = await controller.ChangePassword(request, CancellationToken.None);

        var objectResult = actionResult.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status200OK);

        var response = objectResult.Value.Should().BeOfType<ApiResponse<object>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeNull();

        useCase.Verify(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
