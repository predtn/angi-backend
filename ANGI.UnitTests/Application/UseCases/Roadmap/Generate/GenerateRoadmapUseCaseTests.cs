using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.Generate;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.Generate;

public sealed class GenerateRoadmapUseCaseTests
{
    // TEST-01: Queue the job with the scope and the constraints, commit once and answer queued.
    /// <summary>Verifies the successful RM-10 transaction for a day scope.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedRoadmapAndDay_ShouldQueueJob()
    {
        var fixture = CreateFixture(Roadmap(RoadmapOriginMode.None));

        var result = await fixture.UseCase.ExecuteAsync(
            88, new GenerateRoadmapRequestDto { Scope = "day", DayId = 301, Province = "Huế", Note = "Ưu tiên món nước" },
            CancellationToken.None);

        result.Should().BeEquivalentTo(new GenerateRoadmapResponseDto { JobId = 77, Status = "queued" });
        var job = fixture.Added.Should().ContainSingle().Subject;
        job.Should().BeEquivalentTo(new { RoadmapId = 88L, RequestedBy = 12, Scope = GenerationScope.Day, Status = GenerationJobStatus.Queued });
        using var input = JsonDocument.Parse(job.Input);
        input.RootElement.GetProperty("request").GetProperty("note").GetString().Should().Be("Ưu tiên món nước");
        input.RootElement.GetProperty("origin").ValueKind.Should().Be(JsonValueKind.Null);
        input.RootElement.GetProperty("radiusKm").ValueKind.Should().Be(JsonValueKind.Null);
        fixture.Transaction.Verify(transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Store the origin the job will use: GPS for a gps roadmap, the pinned point, or none, with radius 10 by default.
    /// <summary>Verifies the documented origin rule of RM-10 step 1.</summary>
    [Theory]
    [InlineData(RoadmapOriginMode.Gps, true, "gps", 16.0678, null)]
    [InlineData(RoadmapOriginMode.Gps, false, null, null, null)]
    [InlineData(RoadmapOriginMode.Pinned, true, "pinned", 21.0285, 5.0)]
    [InlineData(RoadmapOriginMode.None, true, null, null, 5.0)]
    public async Task ExecuteAsync_ShouldStoreTheResolvedOriginAndRadius(
        RoadmapOriginMode mode,
        bool sendGps,
        string? expectedMode,
        double? expectedLatitude,
        double? radiusKm)
    {
        var fixture = CreateFixture(Roadmap(mode));

        await fixture.UseCase.ExecuteAsync(88, new GenerateRoadmapRequestDto
        {
            Scope = "full",
            Province = "Huế",
            CurrentLatitude = sendGps ? 16.0678m : null,
            CurrentLongitude = sendGps ? 108.2208m : null,
            RadiusKm = (decimal?)radiusKm
        }, CancellationToken.None);

        using var input = JsonDocument.Parse(fixture.Added.Single().Input);
        var origin = input.RootElement.GetProperty("origin");
        if (expectedMode is null)
        {
            origin.ValueKind.Should().Be(JsonValueKind.Null);
            input.RootElement.GetProperty("radiusKm").ValueKind.Should().Be(JsonValueKind.Null);
            return;
        }

        origin.GetProperty("mode").GetString().Should().Be(expectedMode);
        origin.GetProperty("latitude").GetDouble().Should().Be(expectedLatitude!.Value);
        input.RootElement.GetProperty("radiusKm").GetDecimal().Should().Be((decimal)(radiusKm ?? 10));
    }

    // TEST-03: Hide a foreign or deleted roadmap, refuse a day outside it, and refuse a second active job.
    /// <summary>Verifies the RM-10 errors and that nothing is queued.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidTarget_ShouldThrowDocumentedErrors()
    {
        var missing = CreateFixture(null);
        var act = () => missing.UseCase.ExecuteAsync(88, Full(), CancellationToken.None);
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_NOT_FOUND");

        var wrongDay = CreateFixture(Roadmap(RoadmapOriginMode.None));
        act = () => wrongDay.UseCase.ExecuteAsync(
            88, new GenerateRoadmapRequestDto { Scope = "slot", DayId = 999, MealSlot = "lunch", Province = "Huế" }, CancellationToken.None);
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("DAY_NOT_FOUND");

        var busy = CreateFixture(Roadmap(RoadmapOriginMode.None), activeJob: true);
        act = () => busy.UseCase.ExecuteAsync(88, Full(), CancellationToken.None);
        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("GENERATION_IN_PROGRESS");

        foreach (var fixture in new[] { missing, wrongDay, busy })
        {
            fixture.Added.Should().BeEmpty();
        }
    }

    // TEST-04: Reject an invalid body before opening a transaction.
    [Fact]
    public async Task ExecuteAsync_WithInvalidBody_ShouldThrowValidation()
    {
        var fixture = CreateFixture(Roadmap(RoadmapOriginMode.None));

        var act = () => fixture.UseCase.ExecuteAsync(88, new GenerateRoadmapRequestDto { Scope = "day", Province = "Huế" }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static GenerateRoadmapRequestDto Full() => new() { Scope = "full", Province = "Huế" };

    private static Domain.Entities.Roadmap Roadmap(RoadmapOriginMode mode) => new()
    {
        Id = 88,
        UserId = 12,
        OriginMode = mode,
        OriginLatitude = mode == RoadmapOriginMode.Pinned ? 21.0285m : null,
        OriginLongitude = mode == RoadmapOriginMode.Pinned ? 105.8542m : null,
        Days = { new RoadmapDay { Id = 301, DayNumber = 1 } }
    };

    private static Fixture CreateFixture(Domain.Entities.Roadmap? roadmap, bool activeJob = false)
    {
        var added = new List<RoadmapGenerationJob>();
        var roadmaps = new Mock<IRoadmapRepository>();
        roadmaps.Setup(value => value.LockAndGetTrackedAsync(88, 12, It.IsAny<CancellationToken>())).ReturnsAsync(roadmap);
        var jobs = new Mock<IRoadmapGenerationRepository>();
        jobs.Setup(value => value.HasActiveJobAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(activeJob);
        jobs.Setup(value => value.AddJob(It.IsAny<RoadmapGenerationJob>())).Callback<RoadmapGenerationJob>(added.Add);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(transaction.Object);
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => added.ForEach(job => job.Id = 77))
            .ReturnsAsync(1);

        return new Fixture(
            new GenerateRoadmapUseCase(
                new GenerateRoadmapRequestDtoValidator(), roadmaps.Object, jobs.Object, currentUser.Object, unitOfWork.Object),
            unitOfWork,
            transaction,
            added);
    }

    private sealed record Fixture(
        GenerateRoadmapUseCase UseCase,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction,
        List<RoadmapGenerationJob> Added);
}
