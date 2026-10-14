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

public sealed class PreferenceSurveyControllerTests
{
    private readonly Mock<ISubmitPreferenceSurveyUseCase> _submitUseCase = new();
    private readonly Mock<ISkipPreferenceSurveyUseCase> _skipUseCase = new();

    private PreferenceSurveyController CreateController() => new(_submitUseCase.Object, _skipUseCase.Object);

    // TEST-01: Allow only Travelers, as column "Auth / Role" of PREF-02 and PREF-03 says.
    [Fact]
    public void Controller_ShouldAllowOnlyTravelers()
    {
        var attribute = typeof(PreferenceSurveyController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Should().ContainSingle().Subject;

        attribute.Roles.Should().Be("TRAVELER");
    }

    // TEST-02: Wrap the PREF-02 result in a 201 ApiResponse.
    [Fact]
    public async Task Submit_ShouldReturnCreatedWithUseCaseResult()
    {
        var request = new SubmitPreferenceSurveyRequestDto
        {
            Answers = [new SurveyAnswerDto { OptionCode = "broth_rich", ScaleValue = 0.8m }]
        };
        var expected = new SubmitPreferenceSurveyResponseDto { Completed = true, AnswerCount = 1, HasVector = true };
        _submitUseCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var action = await CreateController().Submit(request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = result.Value.Should().BeOfType<ApiResponse<SubmitPreferenceSurveyResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }

    // TEST-03: Wrap the PREF-03 result in a 201 ApiResponse.
    [Fact]
    public async Task Skip_ShouldReturnCreatedWithUseCaseResult()
    {
        var expected = new SkipPreferenceSurveyResponseDto { Completed = true, Skipped = true };
        _skipUseCase.Setup(x => x.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var action = await CreateController().Skip(CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = result.Value.Should().BeOfType<ApiResponse<SkipPreferenceSurveyResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
    }
}
