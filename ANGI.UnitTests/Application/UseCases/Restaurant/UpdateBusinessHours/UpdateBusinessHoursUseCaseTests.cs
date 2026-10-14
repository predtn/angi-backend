using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.UpdateBusinessHours;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.UpdateBusinessHours;

public sealed class UpdateBusinessHoursUseCaseTests
{
    // Monday 2026-10-12 10:00 in Vietnam (03:00 UTC).
    private static readonly DateTimeOffset MondayTenAm = new(2026, 10, 12, 3, 0, 0, TimeSpan.Zero);
    private const int OwnerId = 21;
    private const long RestaurantId = 7;

    private readonly Mock<IRestaurantRepository> _restaurantRepository = new();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    public UpdateBusinessHoursUseCaseTests()
    {
        _currentUserService.SetupGet(x => x.UserId).Returns(OwnerId);
    }

    private UpdateBusinessHoursUseCase CreateUseCase() => new(
        new UpdateBusinessHoursRequestDtoValidator(),
        _restaurantRepository.Object,
        _currentUserService.Object,
        _unitOfWork.Object,
        new FixedTimeProvider(MondayTenAm));

    private static BusinessHourDto Hour(short day, string open, string close) =>
        new() { DayOfWeek = day, OpenTime = open, CloseTime = close };

    private static RestaurantBusinessHour Row(long id, short day, string open, string close) => new()
    {
        Id = id,
        RestaurantId = RestaurantId,
        DayOfWeek = day,
        OpenTime = TimeOnly.Parse(open),
        CloseTime = TimeOnly.Parse(close)
    };

    private Domain.Entities.Restaurant SetupRestaurant(params RestaurantBusinessHour[] hours)
    {
        var restaurant = new Domain.Entities.Restaurant { Id = RestaurantId, OwnerId = OwnerId };
        foreach (var hour in hours)
        {
            restaurant.BusinessHours.Add(hour);
        }

        _restaurantRepository
            .Setup(x => x.GetTrackedWithBusinessHoursAsync(OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        return restaurant;
    }

    // TEST-01: Keep, update, remove and add rows so the saved schedule equals the request.
    [Fact]
    public async Task ExecuteAsync_WithNewSchedule_ShouldReplaceTheWholeWeekAndSaveOnce()
    {
        var kept = Row(1, 1, "06:00", "13:30");
        var changed = Row(2, 1, "17:00", "21:00");
        var removed = Row(3, 2, "06:00", "10:00");
        var restaurant = SetupRestaurant(kept, changed, removed);

        var result = await CreateUseCase().ExecuteAsync(
            new UpdateBusinessHoursRequestDto
            {
                Hours = [Hour(1, "17:00", "22:30"), Hour(1, "06:00", "13:30"), Hour(0, "07:00", "11:00")]
            },
            CancellationToken.None);

        restaurant.BusinessHours.Should().HaveCount(3);
        restaurant.BusinessHours.Should().Contain(kept).And.Contain(changed).And.NotContain(removed);
        changed.CloseTime.Should().Be(new TimeOnly(22, 30));
        result.Hours.Should().BeEquivalentTo(
            [Hour(0, "07:00", "11:00"), Hour(1, "06:00", "13:30"), Hour(1, "17:00", "22:30")],
            options => options.WithStrictOrdering());
        result.IsOpenNow.Should().BeTrue();
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: An empty list clears the schedule; the restaurant is then closed.
    [Fact]
    public async Task ExecuteAsync_WithEmptyList_ShouldRemoveEveryRowAndReportClosed()
    {
        var restaurant = SetupRestaurant(Row(1, 1, "06:00", "13:30"));

        var result = await CreateUseCase().ExecuteAsync(new UpdateBusinessHoursRequestDto { Hours = [] }, CancellationToken.None);

        restaurant.BusinessHours.Should().BeEmpty();
        result.Hours.Should().BeEmpty();
        result.IsOpenNow.Should().BeFalse();
    }

    // TEST-03: Overlapping intervals return INVALID_BUSINESS_HOURS before anything is loaded or saved.
    [Fact]
    public async Task ExecuteAsync_WithOverlappingIntervals_ShouldThrowInvalidBusinessHours()
    {
        var act = () => CreateUseCase().ExecuteAsync(
            new UpdateBusinessHoursRequestDto { Hours = [Hour(1, "06:00", "13:30"), Hour(1, "13:00", "20:00")] },
            CancellationToken.None);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("INVALID_BUSINESS_HOURS");
        _restaurantRepository.VerifyNoOtherCalls();
        _unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-04: Overnight intervals overlap the next day's intervals, Saturday wraps into Sunday, and back-to-back
    // intervals (13:30 close, 13:30 open) do not overlap.
    [Theory]
    [InlineData((short)1, "22:00", "02:00", (short)2, "01:00", "05:00", true)]
    [InlineData((short)6, "22:00", "03:00", (short)0, "02:00", "08:00", true)]
    [InlineData((short)1, "06:00", "13:30", (short)1, "13:30", "20:00", false)]
    [InlineData((short)1, "22:00", "02:00", (short)2, "02:00", "05:00", false)]
    public async Task ExecuteAsync_WithBoundaryIntervals_ShouldDetectOnlyRealOverlaps(
        short firstDay, string firstOpen, string firstClose,
        short secondDay, string secondOpen, string secondClose,
        bool overlaps)
    {
        SetupRestaurant();

        var act = () => CreateUseCase().ExecuteAsync(
            new UpdateBusinessHoursRequestDto
            {
                Hours = [Hour(firstDay, firstOpen, firstClose), Hour(secondDay, secondOpen, secondClose)]
            },
            CancellationToken.None);

        if (overlaps)
        {
            (await act.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("INVALID_BUSINESS_HOURS");
        }
        else
        {
            (await act()).Hours.Should().HaveCount(2);
        }
    }

    // TEST-05: A malformed interval is a field error (VALIDATION_FAILED), not INVALID_BUSINESS_HOURS.
    [Fact]
    public async Task ExecuteAsync_WithMalformedTime_ShouldThrowValidationException()
    {
        var act = () => CreateUseCase().ExecuteAsync(
            new UpdateBusinessHoursRequestDto { Hours = [Hour(1, "6:00", "13:30")] },
            CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _restaurantRepository.VerifyNoOtherCalls();
    }

    // TEST-06: An owner without a restaurant gets RESTAURANT_NOT_FOUND and nothing is saved.
    [Fact]
    public async Task ExecuteAsync_WithoutRestaurant_ShouldThrowRestaurantNotFound()
    {
        _restaurantRepository
            .Setup(x => x.GetTrackedWithBusinessHoursAsync(OwnerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Domain.Entities.Restaurant?)null);

        var act = () => CreateUseCase().ExecuteAsync(
            new UpdateBusinessHoursRequestDto { Hours = [Hour(1, "06:00", "13:30")] },
            CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        _unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-07: An overnight interval of the previous day keeps the restaurant open after midnight.
    [Fact]
    public async Task ExecuteAsync_WithOvernightIntervalFromYesterday_ShouldReportOpen()
    {
        SetupRestaurant();
        var useCase = new UpdateBusinessHoursUseCase(
            new UpdateBusinessHoursRequestDtoValidator(),
            _restaurantRepository.Object,
            _currentUserService.Object,
            _unitOfWork.Object,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 12, 18, 30, 0, TimeSpan.Zero))); // Tue 01:30 Vietnam

        var result = await useCase.ExecuteAsync(
            new UpdateBusinessHoursRequestDto { Hours = [Hour(1, "18:00", "02:00")] },
            CancellationToken.None);

        result.IsOpenNow.Should().BeTrue();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
