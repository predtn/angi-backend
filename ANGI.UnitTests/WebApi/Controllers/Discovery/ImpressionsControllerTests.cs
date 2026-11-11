using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Discovery;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Discovery;

/// <summary>Verifies the discovery controller contract.</summary>
public sealed class ImpressionsControllerTests
{
    // TEST-01: IMP-01 is TRAVELER only and answers 202 Accepted.
    /// <summary>IMP-01 is TRAVELER only and answers 202 Accepted.</summary>
    [Fact]
    public async Task Impressions_ShouldBeTravelerOnlyAndReturnAccepted()
    {
        typeof(ImpressionsController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().Should().ContainSingle().Which.Roles.Should().Be("TRAVELER");
        var request = new RecordImpressionsRequestDto { RequestId = Guid.NewGuid(), DishIds = [105] };
        var useCase = new Mock<IRecordImpressionsUseCase>();
        useCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordImpressionsResponseDto { Accepted = true });

        var action = await new ImpressionsController(useCase.Object).Record(request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        result.Value.Should().BeOfType<ApiResponse<RecordImpressionsResponseDto>>().Which.Data!.Accepted.Should().BeTrue();
    }
}
