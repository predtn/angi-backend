using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.Create;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.Create;

/// <summary>Verifies CreateRoadmapUseCase creates a blank roadmap with consecutive days and the documented origin defaults.</summary>
public sealed class CreateRoadmapUseCaseTests
{
    // TEST-01: Create consecutive empty days and return the complete blank-roadmap detail.
    /// <summary>Verifies the principal RM-01 aggregate creation and response mapping path.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPinnedOrigin_ShouldCreateRoadmapAndAllDays()
    {
        // STEP-01: Declare captured to capture a call argument; mock IRoadmapRepository and IUnitOfWork; stub repository.Add.
        var repository = new Mock<IRoadmapRepository>();
        Domain.Entities.Roadmap? captured = null;
        repository.Setup(item => item.Add(It.IsAny<Domain.Entities.Roadmap>()))
            .Callback<Domain.Entities.Roadmap>(value => captured = value);
        var unitOfWork = new Mock<IUnitOfWork>();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, unitOfWork, 12);

        // STEP-03: Run useCase.ExecuteAsync with CreateRoadmapRequestDto { Title, StartDate, NumDays, ... } and keep result.
        var result = await useCase.ExecuteAsync(new CreateRoadmapRequestDto
        {
            Title = "  Three-day trip  ",
            StartDate = new DateOnly(2026, 10, 10),
            NumDays = 3,
            BudgetAmount = 1_500_000,
            OriginMode = "PINNED",
            OriginLatitude = 16.061204m,
            OriginLongitude = 108.227531m,
            OriginLabel = "  Hotel  ",
            DestinationProvince = "  Thành phố Đà Nẵng  "
        }, CancellationToken.None);

        // STEP-04: Assert captured is not null, captured.UserId is 12, captured.Title is "Three-day trip", captured.OriginMode is RoadmapOriginMode.Pinned and 2 more.
        captured.Should().NotBeNull();
        captured!.UserId.Should().Be(12);
        captured.Title.Should().Be("Three-day trip");
        captured.OriginMode.Should().Be(RoadmapOriginMode.Pinned);
        captured.OriginLabel.Should().Be("Hotel");
        captured.DestinationProvince.Should().Be("Thành phố Đà Nẵng");
        captured.Days.Select(day => day.DayNumber).Should().Equal(1, 2, 3);
        result.Overview.DestinationProvince.Should().Be("Thành phố Đà Nẵng");
        // STEP-05: Assert result.Itinerary.Select(filtered) equals 1, result.Budget.TotalEstimatedCost is 0 and result.Overview.EndDate is a new DateOnly.
        result.Itinerary.Select(day => day.DayNumber).Should().Equal(1, 2, 3);
        result.Budget.TotalEstimatedCost.Should().Be(0);
        result.Overview.EndDate.Should().Be(new DateOnly(2026, 10, 12));
        // STEP-06: Verify SaveChangesAsync is called once.
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Apply the documented none default without storing origin coordinates or label.
    /// <summary>Verifies omitted origin data produces the database-safe default state.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOmittedOrigin_ShouldPersistNoneOrigin()
    {
        // STEP-01: Declare captured to capture a call argument; mock IRoadmapRepository; stub repository.Add.
        var repository = new Mock<IRoadmapRepository>();
        Domain.Entities.Roadmap? captured = null;
        repository.Setup(item => item.Add(It.IsAny<Domain.Entities.Roadmap>()))
            .Callback<Domain.Entities.Roadmap>(value => captured = value);

        // STEP-02: Run CreateUseCase().ExecuteAsync with CreateRoadmapRequestDto { Title = "Trip", NumDays = 1 }.
        await CreateUseCase(repository, new Mock<IUnitOfWork>(), 12).ExecuteAsync(
            new CreateRoadmapRequestDto { Title = "Trip", NumDays = 1 },
            CancellationToken.None);

        // STEP-03: Assert captured.OriginMode is RoadmapOriginMode.None, captured.OriginLatitude is null, captured.OriginLongitude is null and 1 more.
        captured!.OriginMode.Should().Be(RoadmapOriginMode.None);
        captured.OriginLatitude.Should().BeNull();
        captured.OriginLongitude.Should().BeNull();
        captured.OriginLabel.Should().BeNull();
        captured.DestinationProvince.Should().BeNull();
    }

    // TEST-03: Reject an unauthenticated caller before adding any aggregate.
    /// <summary>Verifies Application does not rely solely on controller authorization.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Mock IRoadmapRepository.
        var repository = new Mock<IRoadmapRepository>();
        // STEP-02: Wrap CreateUseCase().ExecuteAsync with CreateRoadmapRequestDto { Title = "Trip", NumDays = 1 } in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, new Mock<IUnitOfWork>(), null).ExecuteAsync(
            new CreateRoadmapRequestDto { Title = "Trip", NumDays = 1 },
            CancellationToken.None);

        // STEP-03: Assert act throws UnauthorizedException and the thrown exception.ErrorCode is "UNAUTHORIZED".
        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-04: Verify Add is never called.
        repository.Verify(item => item.Add(It.IsAny<Domain.Entities.Roadmap>()), Times.Never);
    }

    /// <summary>Creates RM-01 with deterministic identity and real validation.</summary>
    private static CreateRoadmapUseCase CreateUseCase(
        Mock<IRoadmapRepository> repository,
        Mock<IUnitOfWork> unitOfWork,
        int? userId)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(userId);
        return new CreateRoadmapUseCase(
            new CreateRoadmapRequestDtoValidator(),
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);
    }
}
