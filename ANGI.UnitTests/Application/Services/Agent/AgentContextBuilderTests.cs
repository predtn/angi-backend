using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Agent;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies context uses authoritative snapshots rather than stale or failed chat output.</summary>
public sealed class AgentContextBuilderTests
{
    // TEST-01: After commit, context reloads the latest official roadmap revision and preserves the source item ID.
    /// <summary>Verifies future edits start from the latest saved aggregate.</summary>
    [Fact]
    public async Task BuildAsync_CommittedSource_LoadsLatestRoadmap()
    {
        var repository = new Mock<IAgentRepository>(); var roadmaps = new Mock<IRoadmapRepository>();
        var roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88, UserId = 3, Version = 6, Title = "Latest", NumDays = 1 };
        roadmap.Days.Add(new RoadmapDay { DayNumber = 1, Items = [new RoadmapItem { Id = 9, DishId = 1, MealSlot = MealSlot.Lunch, Dish = AgentTestData.Dish() }] });
        roadmaps.Setup(x => x.GetOwnedDetailAsync(88, 3, It.IsAny<CancellationToken>())).ReturnsAsync(roadmap);
        repository.Setup(x => x.GetCompletedHistoryAsync(7, 20, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AgentMessage>());
        var conversation = new AgentConversation { Id = 7, UserId = 3, RoadmapId = 88, State = AgentJson.Write(AgentTestData.State()) };
        var result = await new AgentContextBuilder(repository.Object, roadmaps.Object, new(), TimeProvider.System).BuildAsync(conversation, new(), CancellationToken.None);
        result.Source!.BaseRoadmapVersion.Should().Be(6); result.Source.Items.Single().SourceItemId.Should().Be(9);
        result.Source.Items.Single().ItemKey.Should().Be("r9");
        result.State.Title.Should().Be("Latest"); result.State.NumDays.Should().Be(1);
    }

    // TEST-02: A pending proposal based on an old roadmap revision is rejected rather than silently rebased.
    /// <summary>Checks context loading detects a stale edit before any LLM or tools.</summary>
    [Fact]
    public async Task BuildAsync_StaleReadyProposal_Rejects()
    {
        var repository = new Mock<IAgentRepository>(); var roadmaps = new Mock<IRoadmapRepository>();
        roadmaps.Setup(x => x.GetOwnedDetailAsync(88, 3, It.IsAny<CancellationToken>())).ReturnsAsync(new ANGI.Domain.Entities.Roadmap { Id = 88, Version = 6 });
        repository.Setup(x => x.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentProposal
        {
            Id = 9, ConversationId = 7, Status = AgentProposalStatus.Ready, ExpiresAt = DateTime.MaxValue,
            Payload = AgentJson.Write(AgentTestData.Snapshot(AgentTestData.Item()) with { BaseRoadmapId = 88, BaseRoadmapVersion = 5 })
        });
        var conversation = new AgentConversation { Id = 7, UserId = 3, RoadmapId = 88, CurrentProposalId = 9, State = AgentJson.Write(AgentTestData.State()) };
        var act = () => new AgentContextBuilder(repository.Object, roadmaps.Object, new(), TimeProvider.System).BuildAsync(conversation, new SendAgentMessageRequestDto(), CancellationToken.None);
        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("ROADMAP_VERSION_CONFLICT");
    }

    // TEST-03: A roadmap with items from multiple provinces leaves the destination unknown instead of guessing.
    /// <summary>Verifies initial context records only unambiguous catalog facts.</summary>
    [Fact]
    public void StateFromRoadmap_AmbiguousProvince_RemainsUnknown()
    {
        var a = AgentTestData.Dish(); var b = AgentTestData.Dish(2); b.Restaurant.ProvinceName = "Other";
        var roadmap = new ANGI.Domain.Entities.Roadmap { NumDays = 2, Title = "Trip", Days = [new RoadmapDay { Items =
            [new RoadmapItem { Dish = a }, new RoadmapItem { Dish = b }] }] };
        AgentContextBuilder.StateFromRoadmap(roadmap).Province.Should().BeNull();
    }
}
