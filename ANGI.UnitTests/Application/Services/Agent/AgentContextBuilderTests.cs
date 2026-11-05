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
    // TEST-01: A legacy proposal snapshot stored out of order is mapped back in chronological day and meal order.
    /// <summary>Keeps proposals saved before ordering was enforced readable in the order users expect.</summary>
    [Fact]
    public void MapProposal_LegacyUnsortedSnapshot_ReturnsChronologicalItems()
    {
        // STEP-01: Get snapshot from AgentTestData.Snapshot and create proposal as AgentProposal.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item(3, "c", 2, "dinner"),
            AgentTestData.Item(2, "b", 1, "lunch"), AgentTestData.Item(1, "a", 1, "breakfast"));
        var proposal = new AgentProposal { Payload = AgentJson.Write(snapshot), ValidationResult = AgentJson.Write(new AgentValidation([], [], 0, [])) };
        // STEP-02: Assert MapProposal().Snapshot.Items.Select(filtered) equals "a".
        AgentContextBuilder.MapProposal(proposal).Snapshot.Items.Select(item => item.ItemKey).Should().Equal("a", "b", "c");
    }

    // TEST-02: After commit, context reloads the latest official roadmap revision and preserves the source item ID.
    /// <summary>Verifies future edits start from the latest saved aggregate.</summary>
    [Fact]
    public async Task BuildAsync_CommittedSource_LoadsLatestRoadmap()
    {
        // STEP-01: Mock IAgentRepository and IRoadmapRepository.
        var repository = new Mock<IAgentRepository>(); var roadmaps = new Mock<IRoadmapRepository>();
        // STEP-02: Create roadmap as ANGI.Domain.Entities.Roadmap and add a RoadmapDay to roadmap.Days.
        var roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88, UserId = 3, UpdatedAt = DateTime.UnixEpoch.AddSeconds(6), Title = "Latest", NumDays = 1 };
        roadmap.Days.Add(new RoadmapDay { DayNumber = 1, Items = [new RoadmapItem { Id = 9, DishId = 1, MealSlot = MealSlot.Lunch, Dish = AgentTestData.Dish() }] });
        // STEP-03: Stub roadmaps.GetOwnedDetailAsync and repository.GetCompletedHistoryAsync.
        roadmaps.Setup(x => x.GetOwnedDetailAsync(88, 3, It.IsAny<CancellationToken>())).ReturnsAsync(roadmap);
        repository.Setup(x => x.GetCompletedHistoryAsync(7, 20, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AgentMessage>());
        // STEP-04: Create conversation as AgentConversation and create result through new AgentContextBuilder().BuildAsync.
        var conversation = new AgentConversation { Id = 7, UserId = 3, RoadmapId = 88, State = AgentJson.Write(AgentTestData.State()) };
        var result = await new AgentContextBuilder(repository.Object, roadmaps.Object, new(), TimeProvider.System, AgentTestData.Validator().Tags.Object).BuildAsync(conversation, new(), CancellationToken.None);
        // STEP-05: Assert result.Source.BaseRoadmapUpdatedAt is DateTime.UnixEpoch.AddSeconds(), result.Source.Items.Single().SourceItemId is 9 and 3 more.
        result.Source!.BaseRoadmapUpdatedAt.Should().Be(DateTime.UnixEpoch.AddSeconds(6)); result.Source.Items.Single().SourceItemId.Should().Be(9);
        result.Source.Items.Single().ItemKey.Should().Be("r9");
        result.State.Title.Should().Be("Latest"); result.State.NumDays.Should().Be(1);
    }

    // TEST-03: A pending proposal based on an old roadmap revision is rejected rather than silently rebased.
    /// <summary>Checks context loading detects a stale edit before any LLM or tools.</summary>
    [Fact]
    public async Task BuildAsync_StaleReadyProposal_Rejects()
    {
        // STEP-01: Mock IAgentRepository and IRoadmapRepository; stub roadmaps.GetOwnedDetailAsync and repository.GetProposalSnapshotAsync.
        var repository = new Mock<IAgentRepository>(); var roadmaps = new Mock<IRoadmapRepository>();
        roadmaps.Setup(x => x.GetOwnedDetailAsync(88, 3, It.IsAny<CancellationToken>())).ReturnsAsync(new ANGI.Domain.Entities.Roadmap { Id = 88, UpdatedAt = DateTime.UnixEpoch.AddSeconds(6) });
        repository.Setup(x => x.GetProposalSnapshotAsync(7, 9, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentProposal
        {
            Id = 9, ConversationId = 7, Status = AgentProposalStatus.Ready, ExpiresAt = DateTime.MaxValue,
            Payload = AgentJson.Write(AgentTestData.Snapshot(AgentTestData.Item()) with { BaseRoadmapId = 88, BaseRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(5) })
        });
        // STEP-02: Create conversation as AgentConversation.
        var conversation = new AgentConversation { Id = 7, UserId = 3, RoadmapId = 88, CurrentProposalId = 9, State = AgentJson.Write(AgentTestData.State()) };
        // STEP-03: Wrap new AgentContextBuilder().BuildAsync with conversation and an empty SendAgentMessageRequestDto in act so its outcome can be asserted.
        var act = () => new AgentContextBuilder(repository.Object, roadmaps.Object, new(), TimeProvider.System, AgentTestData.Validator().Tags.Object).BuildAsync(conversation, new SendAgentMessageRequestDto(), CancellationToken.None);
        // STEP-04: Assert act throws ConflictException and the thrown exception.ErrorCode is "ROADMAP_UPDATED_AT_CONFLICT".
        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("ROADMAP_UPDATED_AT_CONFLICT");
    }

    // TEST-04: Unchanged official revisions preserve constraints accepted in chat and reset fences remove older context.
    /// <summary>Prevents a saved roadmap reload from reverting a confirmed chat budget or resurrecting reset history.</summary>
    [Fact]
    public async Task BuildAsync_UnchangedRoadmap_PreservesChatConstraintsAndHistoryFence()
    {
        // STEP-01: Mock IAgentRepository and IRoadmapRepository; stub roadmaps.GetOwnedDetailAsync and repository.GetCompletedHistoryAsync.
        var repository = new Mock<IAgentRepository>(); var roadmaps = new Mock<IRoadmapRepository>();
        roadmaps.Setup(repo => repo.GetOwnedDetailAsync(88, 3, It.IsAny<CancellationToken>())).ReturnsAsync(
            new ANGI.Domain.Entities.Roadmap { Id = 88, UpdatedAt = DateTime.UnixEpoch.AddSeconds(4), Title = "Official", NumDays = 2, BudgetAmount = 500000 });
        repository.Setup(repo => repo.GetCompletedHistoryAsync(7, 20, It.IsAny<CancellationToken>())).ReturnsAsync([
            new AgentMessage { Role = AgentMessageRole.User, SequenceNo = 1, Payload = AgentJson.Write(new AgentStoredMessage("Old trip")) },
            new AgentMessage { Role = AgentMessageRole.Assistant, SequenceNo = 9, Payload = AgentJson.Write(new AgentStoredMessage("Fresh context")) }]);
        // STEP-02: Derive state from a copy of AgentTestData.State(), create conversation as AgentConversation and 1 more.
        var state = AgentTestData.State() with { BudgetAmount = 700000, SourceRoadmapUpdatedAt = DateTime.UnixEpoch.AddSeconds(4), HistoryStartSequence = 8 };
        var conversation = new AgentConversation { Id = 7, UserId = 3, RoadmapId = 88, State = AgentJson.Write(state) };
        var result = await new AgentContextBuilder(repository.Object, roadmaps.Object, new(), TimeProvider.System, AgentTestData.Validator().Tags.Object).BuildAsync(conversation, new(), CancellationToken.None);
        // STEP-03: Assert result.State.BudgetAmount is 700000, result.History has exactly one matching item and result.History.Content is "Fresh context".
        result.State.BudgetAmount.Should().Be(700000); result.History.Should().ContainSingle().Which.Content.Should().Be("Fresh context");
    }

    // TEST-05: A roadmap with items from multiple provinces leaves the destination unknown instead of guessing.
    /// <summary>Verifies initial context records only unambiguous catalog facts.</summary>
    [Fact]
    public void StateFromRoadmap_AmbiguousProvince_RemainsUnknown()
    {
        // STEP-01: Get a from AgentTestData.Dish, get b from AgentTestData.Dish, set b.Restaurant.ProvinceName to "Other" and 1 more.
        var a = AgentTestData.Dish(); var b = AgentTestData.Dish(2); b.Restaurant.ProvinceName = "Other";
        var roadmap = new ANGI.Domain.Entities.Roadmap { NumDays = 2, Title = "Trip", Days = [new RoadmapDay { Items =
            [new RoadmapItem { Dish = a }, new RoadmapItem { Dish = b }] }] };
        // STEP-02: Assert AgentContextBuilder.StateFromRoadmap().Province is null.
        AgentContextBuilder.StateFromRoadmap(roadmap).Province.Should().BeNull();
    }
}
