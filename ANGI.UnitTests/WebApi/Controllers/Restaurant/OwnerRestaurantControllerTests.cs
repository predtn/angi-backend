using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.WebApi.Common.Models;
using ANGI.WebApi.Controllers.Restaurant;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace ANGI.UnitTests.WebApi.Controllers.Restaurant;

/// <summary>Verifies OwnerRestaurantController returns the documented status for each owner restaurant action.</summary>
public sealed class OwnerRestaurantControllerTests
{
    // TEST-01: Return the OWN-01 payload in ApiResponse with HTTP 201.
    /// <summary>Verifies that the owner restaurant controller delegates and wraps the result.</summary>
    [Fact]
    public async Task Register_WithValidRequest_ShouldReturnCreatedApiResponse()
    {
        // STEP-01: Create request as RegisterRestaurantRequestDto and create expected as OwnerRestaurantDto.
        var request = new RegisterRestaurantRequestDto
        {
            Name = "Bún Bò Bà Diệu",
            Phone = "02363812345",
            AddressLine = "12 Lê Duẩn",
            ProvinceName = "Thành phố Đà Nẵng",
            Latitude = 16.067812m,
            Longitude = 108.220116m
        };
        var expected = new OwnerRestaurantDto { Id = 7, Name = request.Name };
        // STEP-02: Mock IRegisterRestaurantUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IRegisterRestaurantUseCase>();
        useCase.Setup(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        // STEP-03: Create the OwnerRestaurantController under test as controller.
        var controller = new OwnerRestaurantController(
            useCase.Object,
            Mock.Of<IGetOwnerRestaurantUseCase>(),
            Mock.Of<IUpdateOwnerRestaurantUseCase>(),
            Mock.Of<ISubmitVerificationUseCase>(),
            Mock.Of<IGetVerificationStatusUseCase>(),
            Mock.Of<IResubmitVerificationUseCase>());

        // STEP-04: Run controller.Register with request and keep action.
        var action = await controller.Register(request, CancellationToken.None);

        // STEP-05: Assert action is ObjectResult, result.StatusCode is StatusCodes.Status201Created, result.Value is ApiResponse<OwnerRestaurantDto> and 2 more.
        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = result.Value.Should().BeOfType<ApiResponse<OwnerRestaurantDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        // STEP-06: Verify ExecuteAsync is called once.
        useCase.Verify(x => x.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Return the OWN-02 payload in ApiResponse with HTTP 200.
    /// <summary>Verifies GET delegation and response wrapping.</summary>
    [Fact]
    public async Task Get_WhenRestaurantExists_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create expected as OwnerRestaurantDto.
        var expected = new OwnerRestaurantDto { Id = 7, Name = "Bún Bò Bà Diệu" };
        // STEP-02: Mock IGetOwnerRestaurantUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IGetOwnerRestaurantUseCase>();
        useCase.Setup(item => item.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the OwnerRestaurantController under test as controller.
        var controller = new OwnerRestaurantController(
            Mock.Of<IRegisterRestaurantUseCase>(),
            useCase.Object,
            Mock.Of<IUpdateOwnerRestaurantUseCase>(),
            Mock.Of<ISubmitVerificationUseCase>(),
            Mock.Of<IGetVerificationStatusUseCase>(),
            Mock.Of<IResubmitVerificationUseCase>());

        // STEP-04: Run controller.Get and keep action.
        var action = await controller.Get(CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<OwnerRestaurantDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<OwnerRestaurantDto>>()
            .Which.Data.Should().BeSameAs(expected);
    }

    // TEST-03: Return the OWN-03 payload in ApiResponse with HTTP 200.
    /// <summary>Verifies PATCH delegation and response wrapping.</summary>
    [Fact]
    public async Task Update_WithValidRequest_ShouldReturnOkApiResponse()
    {
        // STEP-01: Create request as UpdateRestaurantRequestDto and create expected as OwnerRestaurantDto.
        var request = new UpdateRestaurantRequestDto { Name = "Tên mới" };
        var expected = new OwnerRestaurantDto { Id = 7, Name = "Tên mới" };
        // STEP-02: Mock IUpdateOwnerRestaurantUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<IUpdateOwnerRestaurantUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        // STEP-03: Create the OwnerRestaurantController under test as controller.
        var controller = new OwnerRestaurantController(
            Mock.Of<IRegisterRestaurantUseCase>(),
            Mock.Of<IGetOwnerRestaurantUseCase>(),
            useCase.Object,
            Mock.Of<ISubmitVerificationUseCase>(),
            Mock.Of<IGetVerificationStatusUseCase>(),
            Mock.Of<IResubmitVerificationUseCase>());

        // STEP-04: Run controller.Update with request and keep action.
        var action = await controller.Update(request, CancellationToken.None);

        // STEP-05: Assert action is OkObjectResult, result.Value is ApiResponse<OwnerRestaurantDto> and result.Value.Data is the same instance as expected.
        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeOfType<ApiResponse<OwnerRestaurantDto>>()
            .Which.Data.Should().BeSameAs(expected);
    }

    // TEST-04: Return the OWN-06 verification payload in ApiResponse with HTTP 201.
    /// <summary>Verifies verification submission delegation and response wrapping.</summary>
    [Fact]
    public async Task SubmitVerification_WithValidRequest_ShouldReturnCreatedApiResponse()
    {
        // STEP-01: Create request as SubmitVerificationRequestDto and create expected as VerificationDto.
        var request = new SubmitVerificationRequestDto
        {
            LegalName = "Hộ kinh doanh Bún Bò Bà Diệu",
            BusinessLicenseNo = "32A8012345",
            Documents =
            [
                new VerificationDocumentRequestDto
                {
                    DocType = "business_license",
                    MediaId = 9001
                }
            ]
        };
        var expected = new VerificationDto { Id = 21, RestaurantId = 7, Status = "pending" };
        // STEP-02: Mock ISubmitVerificationUseCase; stub useCase.ExecuteAsync.
        var useCase = new Mock<ISubmitVerificationUseCase>();
        useCase.Setup(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        // STEP-03: Create the OwnerRestaurantController under test as controller.
        var controller = new OwnerRestaurantController(
            Mock.Of<IRegisterRestaurantUseCase>(),
            Mock.Of<IGetOwnerRestaurantUseCase>(),
            Mock.Of<IUpdateOwnerRestaurantUseCase>(),
            useCase.Object,
            Mock.Of<IGetVerificationStatusUseCase>(),
            Mock.Of<IResubmitVerificationUseCase>());

        // STEP-04: Run controller.SubmitVerification with request and keep action.
        var action = await controller.SubmitVerification(request, CancellationToken.None);

        // STEP-05: Assert action is ObjectResult, result.StatusCode is StatusCodes.Status201Created, result.Value is ApiResponse<VerificationDto> and 2 more.
        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = result.Value.Should().BeOfType<ApiResponse<VerificationDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        // STEP-06: Verify ExecuteAsync is called once.
        useCase.Verify(item => item.ExecuteAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-05: Return the OWN-07 verification status payload in ApiResponse with HTTP 200.
    /// <summary>Verifies OWN-07 verification status delegation and response wrapping.</summary>
    [Fact]
    public async Task GetVerifications_WhenRestaurantExists_ShouldReturnOkApiResponse()
    {
        var expected = new GetVerificationStatusResponseDto
        {
            VerificationStatus = "pending",
            Items =
            [
                new VerificationDto { Id = 21, RestaurantId = 7, Status = "pending" }
            ]
        };
        var useCase = new Mock<IGetVerificationStatusUseCase>();
        useCase.Setup(item => item.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);
        var controller = new OwnerRestaurantController(
            Mock.Of<IRegisterRestaurantUseCase>(),
            Mock.Of<IGetOwnerRestaurantUseCase>(),
            Mock.Of<IUpdateOwnerRestaurantUseCase>(),
            Mock.Of<ISubmitVerificationUseCase>(),
            useCase.Object,
            Mock.Of<IResubmitVerificationUseCase>());

        var action = await controller.GetVerifications(CancellationToken.None);

        var result = action.Should().BeOfType<OkObjectResult>().Subject;
        var response = result.Value.Should().BeOfType<ApiResponse<GetVerificationStatusResponseDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-06: Return the OWN-08 verification payload in ApiResponse with HTTP 201 and forward the route id.
    /// <summary>Verifies resubmission delegation, route id forwarding and response wrapping.</summary>
    [Fact]
    public async Task ResubmitVerification_WithValidRequest_ShouldReturnCreatedApiResponse()
    {
        var request = new ResubmitVerificationRequestDto
        {
            LegalName = "Hộ kinh doanh Bún Bò Bà Diệu",
            BusinessLicenseNo = "32A8012345",
            Documents =
            [
                new VerificationDocumentRequestDto
                {
                    DocType = "food_safety_cert",
                    MediaId = 9002
                }
            ]
        };
        var expected = new VerificationDto { Id = 22, RestaurantId = 7, Status = "pending", PreviousId = 21 };
        var useCase = new Mock<IResubmitVerificationUseCase>();
        useCase.Setup(item => item.ExecuteAsync(21, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var controller = new OwnerRestaurantController(
            Mock.Of<IRegisterRestaurantUseCase>(),
            Mock.Of<IGetOwnerRestaurantUseCase>(),
            Mock.Of<IUpdateOwnerRestaurantUseCase>(),
            Mock.Of<ISubmitVerificationUseCase>(),
            Mock.Of<IGetVerificationStatusUseCase>(),
            useCase.Object);

        var action = await controller.ResubmitVerification(21, request, CancellationToken.None);

        var result = action.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status201Created);
        var response = result.Value.Should().BeOfType<ApiResponse<VerificationDto>>().Subject;
        response.Success.Should().BeTrue();
        response.Data.Should().BeSameAs(expected);
        useCase.Verify(item => item.ExecuteAsync(21, request, It.IsAny<CancellationToken>()), Times.Once);
    }
}
