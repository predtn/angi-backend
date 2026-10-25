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
public sealed class TagsControllerTests
{
    // TEST-01: CAT-02 is public and returns the tag array as data.
    /// <summary>CAT-02 is public and returns the tag array as data.</summary>
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
