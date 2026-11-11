using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.Generate;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.Generate;

/// <summary>Verifies GetRoadmapGenerationJobUseCase maps a job's status and results for its owner only.</summary>
public sealed class GetRoadmapGenerationJobUseCaseTests
{
    private readonly Mock<IRoadmapGenerationRepository> _jobs = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();

    public GetRoadmapGenerationJobUseCaseTests()
    {
        _currentUser.SetupGet(value => value.UserId).Returns(12);
    }

    /// <summary>Creates the use case over the shared job repository and current-user mocks.</summary>
    private GetRoadmapGenerationJobUseCase CreateUseCase() => new(_jobs.Object, _currentUser.Object);

    // TEST-01: Map a finished job, reading created item ids from raw_output.
    /// <summary>Ensures a finished job reports every field, including the created item ids.</summary>
    [Fact]
    public async Task ExecuteAsync_WithSucceededJob_ShouldMapEveryField()
    {
        var createdAt = new DateTime(2026, 10, 2, 2, 20, 0, DateTimeKind.Utc);
        _jobs.Setup(value => value.GetOwnedJobAsync(88, 77, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoadmapGenerationJob
            {
                Id = 77,
                Scope = GenerationScope.Day,
                Status = GenerationJobStatus.Succeeded,
                RawOutput = "{\"days\":[],\"createdItemIds\":[1510,1511,1512]}",
                CreatedAt = createdAt,
                FinishedAt = createdAt.AddSeconds(4)
            });

        var result = await CreateUseCase().ExecuteAsync(88, 77, CancellationToken.None);

        result.Should().BeEquivalentTo(new RoadmapGenerationJobDto
        {
            JobId = 77,
            Scope = "day",
            Status = "succeeded",
            ErrorMessage = null,
            CreatedItemIds = [1510, 1511, 1512],
            CreatedAt = createdAt,
            FinishedAt = createdAt.AddSeconds(4)
        });
    }

    // TEST-02: A queued job has no created item yet; a failed one carries its message.
    /// <summary>Ensures unfinished and failed jobs report their state without invented results.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnfinishedOrFailedJob_ShouldMapStatus()
    {
        _jobs.Setup(value => value.GetOwnedJobAsync(88, 77, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoadmapGenerationJob { Id = 77, Scope = GenerationScope.Full, Status = GenerationJobStatus.Queued });
        _jobs.Setup(value => value.GetOwnedJobAsync(88, 78, 12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RoadmapGenerationJob
            {
                Id = 78,
                Scope = GenerationScope.Slot,
                Status = GenerationJobStatus.Failed,
                ErrorMessage = "Không tìm thấy món phù hợp với các điều kiện đã chọn."
            });

        var queued = await CreateUseCase().ExecuteAsync(88, 77, CancellationToken.None);
        var failed = await CreateUseCase().ExecuteAsync(88, 78, CancellationToken.None);

        (queued.Status, queued.Scope, queued.CreatedItemIds.Count, queued.FinishedAt).Should().Be(("queued", "full", 0, (DateTime?)null));
        (failed.Status, failed.Scope, failed.ErrorMessage).Should().Be(("failed", "slot", "Không tìm thấy món phù hợp với các điều kiện đã chọn."));
    }

    // TEST-03: Hide a job of another traveler or roadmap behind JOB_NOT_FOUND.
    /// <summary>Ensures a job of another user or roadmap cannot be read.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutOwnedJob_ShouldThrowNotFound()
    {
        var act = () => CreateUseCase().ExecuteAsync(88, 404, CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("JOB_NOT_FOUND");
    }
}
