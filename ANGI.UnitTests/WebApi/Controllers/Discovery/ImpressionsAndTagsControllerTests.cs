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

public sealed class ImpressionsAndTagsControllerTests
{
    // TEST-01: IMP-01 is TRAVELER only and answers 202 Accepted.
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

    // TEST-02: CAT-02 is public and returns the tag array as data.
    [Fact]
    public async Task Tags_ShouldBePublicAndReturnOk()
    {
        typeof(TagsController).GetMethod(nameof(TagsController.Get))!
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true).Should().ContainSingle();
        IReadOnlyList<TagDto> tags = [new TagDto { Id = 2, TagType = "meal_time", Code = "lunch", Name = "Bữa trưa" }];
        var useCase = new Mock<IGetTagsUseCase>();
        useCase.Setup(x => x.ExecuteAsync(It.IsAny<GetTagsRequestDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(tags);

        var action = await new TagsController(useCase.Object).Get(new GetTagsRequestDto(), CancellationToken.None);

        action.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ApiResponse<IReadOnlyList<TagDto>>>().Which.Data.Should().BeSameAs(tags);
    }
}
