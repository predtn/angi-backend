using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Roadmap;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Roadmap;

/// <summary>Verifies RoadmapsController returns the documented status and envelope for each roadmap endpoint.</summary>
public sealed class RoadmapsControllerTests
{
    // TEST-01: Return the RM-01 payload in ApiResponse with HTTP 201.
    /// <summary>Verifies create delegation and the documented success envelope.</summary>
    [Fact]
    public async Task Create_WithValidRequest_ShouldReturnCreatedApiResponse()
    {
        // STEP-01: Create request as CreateRoadmapRequestDto and create expected as RoadmapDetailDto.
        var request = new CreateRoadmapRequestDto { Title = "Trip", NumDays = 3 };
        var expected = new RoadmapDetailDto { Overview = new RoadmapSummaryDto { Id = 88 } };
        // STEP-02: Mock ICreateRoadmapUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<ICreateRoadmapUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            useCase.Object,
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.Create with request and keep action.
        var action = await controller.Create(request, CancellationToken.None);

        // STEP-05: Assert action is ObjectResult, result.StatusCode is StatusCodes.Status201Created, result.Value is ApiResponse<RoadmapDetailDto> and 1 more.
        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.Value.Should().BeOfType<ApiResponse<RoadmapDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-02: Return the RM-04 payload in ApiResponse with HTTP 200.
    /// <summary>Verifies update route-value delegation and the documented success envelope.</summary>
    [Fact]
    public async Task Update_WithValidRequest_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as UpdateRoadmapRequestDto and create expected as RoadmapDetailDto.
        var request = new UpdateRoadmapRequestDto { Title = "Updated trip" };
        var expected = new RoadmapDetailDto { Overview = new RoadmapSummaryDto { Id = 88 } };
        // STEP-02: Mock IUpdateRoadmapUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IUpdateRoadmapUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            useCase.Object,
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.Update with request and keep action.
        var action = await controller.Update(88, request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<RoadmapDetailDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<RoadmapDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-03: Return the RM-02 paged payload in ApiResponse with HTTP 200.
    /// <summary>Verifies query-model delegation and the documented list success envelope.</summary>
    [Fact]
    public async Task GetMyRoadmaps_WithValidQuery_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as GetMyRoadmapsRequestDto and create expected as Paged<RoadmapSummaryDto>.
        var request = new GetMyRoadmapsRequestDto { Page = 2, PageSize = 10 };
        var expected = new Paged<RoadmapSummaryDto>
        {
            Page = 2,
            PageSize = 10,
            TotalItems = 11,
            TotalPages = 2
        };
        // STEP-02: Mock IGetMyRoadmapsUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetMyRoadmapsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            useCase.Object,
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.GetMyRoadmaps with request and keep action.
        var action = await controller.GetMyRoadmaps(request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<Paged<RoadmapSummaryDto>> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<Paged<RoadmapSummaryDto>>>()
            .Which.Data.Should().BeSameAs(expected);
    }

    // TEST-04: Return the RM-03 detail payload in ApiResponse with HTTP 200.
    /// <summary>Verifies detail route-value delegation and the documented success envelope.</summary>
    [Fact]
    public async Task GetDetail_WithOwnedRoadmap_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create expected as RoadmapDetailDto.
        var expected = new RoadmapDetailDto { Overview = new RoadmapSummaryDto { Id = 88 } };
        // STEP-02: Mock IGetRoadmapDetailUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetRoadmapDetailUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            useCase.Object,
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.GetDetail and keep action.
        var action = await controller.GetDetail(88, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<RoadmapDetailDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<RoadmapDetailDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-05: Return the RM-14 daily route payload in ApiResponse with HTTP 200.
    /// <summary>Verifies both route values, query delegation, and the documented success envelope.</summary>
    [Fact]
    public async Task GetDayRoute_WithValidQuery_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as GetRoadmapDayRouteRequestDto and create expected as GetRoadmapDayRouteResponseDto.
        var request = new GetRoadmapDayRouteRequestDto { Profile = "walking" };
        var expected = new GetRoadmapDayRouteResponseDto { DayId = 301, Profile = "walking" };
        // STEP-02: Mock IGetRoadmapDayRouteUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetRoadmapDayRouteUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, 301, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            useCase.Object,
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.GetDayRoute with request and keep action.
        var action = await controller.GetDayRoute(88, 301, request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<GetRoadmapDayRouteResponseDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<GetRoadmapDayRouteResponseDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-06: Return the RM-05 empty ApiResponse with HTTP 200.
    /// <summary>Verifies delete route-value delegation and the documented null-data success envelope.</summary>
    [Fact]
    public async Task Delete_WithOwnedRoadmap_ShouldReturnOkApiResponseWithNullData()
    {
        // STEP-01: Mock IDeleteRoadmapUseCase.
        var useCase = new Mock<IDeleteRoadmapUseCase>();
        // STEP-02: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            useCase.Object,
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-03: Run controller.Delete and keep action.
        var action = await controller.Delete(88, CancellationToken.None);

        // STEP-04: Assert action is OkObjectResult, result.Value is ApiResponse<object>, envelope.Success is true and envelope.Data is null.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeNull();
        // STEP-05: Verify ExecuteAsync is called once.
        useCase.Verify(item => item.ExecuteAsync(88, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-07: Return the RM-06 suggestion payload in ApiResponse with HTTP 200.
    /// <summary>Verifies suggestion route/query delegation and its documented success envelope.</summary>
    [Fact]
    public async Task GetSuggestions_WithValidQuery_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as GetRoadmapSuggestionsRequestDto and create expected as GetRoadmapSuggestionsResponseDto.
        var request = new GetRoadmapSuggestionsRequestDto { Province = "Đà Nẵng", Limit = 20 };
        var expected = new GetRoadmapSuggestionsResponseDto { RequestId = Guid.NewGuid() };
        // STEP-02: Mock IGetRoadmapSuggestionsUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetRoadmapSuggestionsUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            useCase.Object,
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.GetSuggestions with request and keep action.
        var action = await controller.GetSuggestions(88, request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<GetRoadmapSuggestionsResponseDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<GetRoadmapSuggestionsResponseDto>>()
            .Which.Data.Should().BeSameAs(expected);
    }

    // TEST-08: Return the RM-07 RoadmapItem payload in ApiResponse with HTTP 201.
    /// <summary>Verifies both route values, body delegation, and the documented create envelope.</summary>
    [Fact]
    public async Task AddItem_WithValidBody_ShouldReturnCreatedApiResponse()
    {
        // STEP-01: Create request as AddRoadmapItemRequestDto and create expected as RoadmapItemDto.
        var request = new AddRoadmapItemRequestDto { DishId = 105, MealSlot = "lunch" };
        var expected = new RoadmapItemDto { Id = 1502, DayId = 301 };
        // STEP-02: Mock IAddRoadmapItemUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IAddRoadmapItemUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, 301, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            useCase.Object,
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.AddItem with request and keep action.
        var action = await controller.AddItem(88, 301, request, CancellationToken.None);

        // STEP-05: Assert action is ObjectResult, result.StatusCode is StatusCodes.Status201Created, result.Value is ApiResponse<RoadmapItemDto> and 1 more.
        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.Value.Should().BeOfType<ApiResponse<RoadmapItemDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-09: Return the RM-12 item-detail payload in ApiResponse with HTTP 200.
    /// <summary>Verifies both route values, use-case delegation, and the documented success envelope.</summary>
    [Fact]
    public async Task GetItemDetail_WithOwnedItem_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create expected as RoadmapItemDetailDto.
        var expected = new RoadmapItemDetailDto { Id = 1502, DayId = 301 };
        // STEP-02: Mock IGetRoadmapItemDetailUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetRoadmapItemDetailUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, 1502, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            useCase.Object,
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.GetItemDetail and keep action.
        var action = await controller.GetItemDetail(88, 1502, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<RoadmapItemDetailDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<RoadmapItemDetailDto>>().Which.Data.Should().BeSameAs(expected);
        // STEP-06: Verify ExecuteAsync is called once.
        useCase.Verify(item => item.ExecuteAsync(88, 1502, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-10: Return the RM-08 RoadmapItem payload in ApiResponse with HTTP 200.
    /// <summary>Verifies both route values, body delegation, and the documented update envelope.</summary>
    [Fact]
    public async Task UpdateItem_WithValidBody_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as UpdateRoadmapItemRequestDto and create expected as RoadmapItemDto.
        var request = new UpdateRoadmapItemRequestDto { DishId = 120, MealSlot = "lunch", SortOrder = 1 };
        var expected = new RoadmapItemDto { Id = 1600, DayId = 302 };
        // STEP-02: Mock IUpdateRoadmapItemUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IUpdateRoadmapItemUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, 1502, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        // STEP-03: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            useCase.Object,
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-04: Run controller.UpdateItem with request and keep action.
        var action = await controller.UpdateItem(88, 1502, request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<RoadmapItemDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<RoadmapItemDto>>().Which.Data.Should().BeSameAs(expected);
        // STEP-06: Verify ExecuteAsync is called once.
        useCase.Verify(item => item.ExecuteAsync(88, 1502, request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-11: Return the RM-09 empty ApiResponse with HTTP 200.
    /// <summary>Verifies both delete-item route values and the documented null-data success envelope.</summary>
    [Fact]
    public async Task DeleteItem_WithOwnedMatchingItem_ShouldReturnOkApiResponseWithNullData()
    {
        // STEP-01: Mock IDeleteRoadmapItemUseCase.
        var useCase = new Mock<IDeleteRoadmapItemUseCase>();
        // STEP-02: Create the RoadmapsController under test as controller.
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            useCase.Object,
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        // STEP-03: Run controller.DeleteItem and keep action.
        var action = await controller.DeleteItem(88, 1502, CancellationToken.None);

        // STEP-04: Assert action is OkObjectResult, result.Value is ApiResponse<object>, envelope.Success is true and envelope.Data is null.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        var envelope = result.Value.Should().BeOfType<ApiResponse<object>>().Subject;
        envelope.Success.Should().BeTrue();
        envelope.Data.Should().BeNull();
        // STEP-05: Verify ExecuteAsync is called once.
        useCase.Verify(value => value.ExecuteAsync(88, 1502, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-12: Return the RM-13 feedback payload in ApiResponse with HTTP 201.
    /// <summary>Verifies both route values, body delegation, and the documented created envelope.</summary>
    [Fact]
    public async Task RateItem_WithValidBody_ShouldReturnCreatedApiResponse()
    {
        var request = new RateRoadmapItemRequestDto { Value = "like" };
        var expected = new RateRoadmapItemResponseDto { FeedbackId = 2201, Value = "like", DishId = 105, LikeCount = 129 };
        var useCase = new Mock<IRateRoadmapItemUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, 1502, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            useCase.Object,
            Mock.Of<IGenerateRoadmapUseCase>(),
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        var action = await controller.RateItem(88, 1502, request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        result.Value.Should().BeOfType<ApiResponse<RateRoadmapItemResponseDto>>().Which.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(88, 1502, request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-13: Return the RM-10 queued job in ApiResponse with HTTP 202.
    /// <summary>Verifies route and body delegation and the documented accepted envelope.</summary>
    [Fact]
    public async Task Generate_WithValidBody_ShouldReturnAcceptedApiResponse()
    {
        var request = new GenerateRoadmapRequestDto { Scope = "full", Province = "Đà Nẵng" };
        var expected = new GenerateRoadmapResponseDto { JobId = 77, Status = "queued" };
        var useCase = new Mock<IGenerateRoadmapUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            useCase.Object,
            Mock.Of<IGetRoadmapGenerationJobUseCase>());

        var action = await controller.Generate(88, request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        result.Value.Should().BeOfType<ApiResponse<GenerateRoadmapResponseDto>>().Which.Data.Should().BeSameAs(expected);
    }

    // TEST-14: Return the RM-11 job in ApiResponse with HTTP 200.
    /// <summary>Verifies both route values and the documented success envelope.</summary>
    [Fact]
    public async Task GetGenerationJob_ShouldReturnOkApiResponse()
    {
        var expected = new RoadmapGenerationJobDto { JobId = 77, Scope = "day", Status = "succeeded" };
        var useCase = new Mock<IGetRoadmapGenerationJobUseCase>();
        useCase.Setup(item => item.ExecuteAsync(88, 77, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new RoadmapsController(
            Mock.Of<ICreateRoadmapUseCase>(),
            Mock.Of<IGetMyRoadmapsUseCase>(),
            Mock.Of<IGetRoadmapDetailUseCase>(),
            Mock.Of<IGetRoadmapItemDetailUseCase>(),
            Mock.Of<IGetRoadmapDayRouteUseCase>(),
            Mock.Of<IGetRoadmapSuggestionsUseCase>(),
            Mock.Of<IAddRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapItemUseCase>(),
            Mock.Of<IUpdateRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapUseCase>(),
            Mock.Of<IDeleteRoadmapItemUseCase>(),
            Mock.Of<IRateRoadmapItemUseCase>(),
            Mock.Of<IGenerateRoadmapUseCase>(),
            useCase.Object);

        var action = await controller.GetGenerationJob(88, 77, CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<RoadmapGenerationJobDto>>().Which.Data.Should().BeSameAs(expected);
    }
}
