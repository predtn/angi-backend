using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.UpdateOperatingStatus;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.UpdateOperatingStatus;

public sealed class UpdateOperatingStatusUseCaseTests
{
    private const int OwnerId = 21;

    private readonly Mock<IRestaurantRepository> _restaurantRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IAuditLogService> _auditLogService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public UpdateOperatingStatusUseCaseTests()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(OwnerId);
    }

    private UpdateOperatingStatusUseCase CreateUseCase() => new(
        new UpdateOperatingStatusRequestDtoValidator(),
        _restaurantRepository.Object,
        _currentUserService.Object,
        _auditLogService.Object,
        _unitOfWork.Object);

    private Domain.Entities.Restaurant SetupRestaurant(
        RestaurantOperatingStatus operating,
        RestaurantModerationStatus moderation = RestaurantModerationStatus.Visible)
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = OwnerId,
            OperatingStatus = operating,
            ModerationStatus = moderation
        };
        _restaurantRepository.Setup(x => x.GetTrackedByOwnerIdAsync(OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        return restaurant;
    }

    // TEST-01: Change the status, write one audit row with the old and new values and the note, and save once.
    [Fact]
    public async Task ExecuteAsync_WithNewStatus_ShouldSaveAndAudit()
    {
        var restaurant = SetupRestaurant(RestaurantOperatingStatus.Open);
        AuditLogEntry? audit = null;
        _auditLogService.Setup(x => x.Add(It.IsAny<AuditLogEntry>())).Callback<AuditLogEntry>(entry => audit = entry);

        var result = await CreateUseCase().ExecuteAsync(
            new UpdateOperatingStatusRequestDto { OperatingStatus = "temporarily_closed", Note = "  Nghỉ Tết  " },
            CancellationToken.None);

        result.OperatingStatus.Should().Be("temporarily_closed");
        restaurant.OperatingStatus.Should().Be(RestaurantOperatingStatus.TemporarilyClosed);
        audit.Should().NotBeNull();
        audit!.Action.Should().Be(AuditActions.RestaurantOperatingStatusChanged);
        audit.EntityType.Should().Be(AuditEntityTypes.Restaurant);
        audit.EntityId.Should().Be(7);
        audit.SubjectUserId.Should().Be(OwnerId);
        audit.OldValues.Should().BeEquivalentTo(new { OperatingStatus = RestaurantOperatingStatus.Open });
        audit.NewValues.Should().BeEquivalentTo(new
        {
            OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed,
            Reason = "Nghỉ Tết"
        });
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: The same status changes nothing: no audit row and no save.
    [Fact]
    public async Task ExecuteAsync_WithSameStatus_ShouldNotAuditOrSave()
    {
        SetupRestaurant(RestaurantOperatingStatus.PermanentlyClosed);

        var result = await CreateUseCase().ExecuteAsync(
            new UpdateOperatingStatusRequestDto { OperatingStatus = "permanently_closed" },
            CancellationToken.None);

        result.OperatingStatus.Should().Be("permanently_closed");
        _auditLogService.VerifyNoOtherCalls();
        _unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-03: A restaurant suspended by a Mod cannot be reopened by its owner.
    [Fact]
    public async Task ExecuteAsync_ReopeningSuspendedRestaurant_ShouldThrowRestaurantSuspended()
    {
        var restaurant = SetupRestaurant(RestaurantOperatingStatus.TemporarilyClosed, RestaurantModerationStatus.Suspended);

        var act = () => CreateUseCase().ExecuteAsync(
            new UpdateOperatingStatusRequestDto { OperatingStatus = "open" },
            CancellationToken.None);

        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("RESTAURANT_SUSPENDED");
        restaurant.OperatingStatus.Should().Be(RestaurantOperatingStatus.TemporarilyClosed);
        _unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-04: A suspended restaurant may still close, and an already open one may "stay" open.
    [Theory]
    [InlineData(RestaurantOperatingStatus.Open, "temporarily_closed")]
    [InlineData(RestaurantOperatingStatus.TemporarilyClosed, "permanently_closed")]
    [InlineData(RestaurantOperatingStatus.Open, "open")]
    public async Task ExecuteAsync_SuspendedRestaurantNotReopening_ShouldSucceed(
        RestaurantOperatingStatus current,
        string requested)
    {
        SetupRestaurant(current, RestaurantModerationStatus.Suspended);

        var result = await CreateUseCase().ExecuteAsync(
            new UpdateOperatingStatusRequestDto { OperatingStatus = requested },
            CancellationToken.None);

        result.OperatingStatus.Should().Be(requested);
    }

    // TEST-05: An owner without a restaurant gets RESTAURANT_NOT_FOUND.
    [Fact]
    public async Task ExecuteAsync_WithoutRestaurant_ShouldThrowRestaurantNotFound()
    {
        _restaurantRepository.Setup(x => x.GetTrackedByOwnerIdAsync(OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);

        var act = () => CreateUseCase().ExecuteAsync(
            new UpdateOperatingStatusRequestDto { OperatingStatus = "open" },
            CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
    }

    // TEST-06: Validate before loading anything.
    [Fact]
    public async Task ExecuteAsync_WithUnknownStatus_ShouldThrowValidationBeforeLoading()
    {
        var act = () => CreateUseCase().ExecuteAsync(
            new UpdateOperatingStatusRequestDto { OperatingStatus = "closed" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _restaurantRepository.VerifyNoOtherCalls();
    }
}
