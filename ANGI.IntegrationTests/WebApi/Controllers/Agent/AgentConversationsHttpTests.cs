using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using ANGI.Infrastructure.Services.Agent;
using ANGI.Infrastructure.Services.Ai;
using ANGI.Infrastructure.Services.Recommendation;
using ANGI.Infrastructure.Services.Outbox;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Moq;

namespace ANGI.IntegrationTests.WebApi.Controllers.Agent;

/// <summary>Exercises real HTTP, use cases, EF mappings and PostgreSQL with deterministic external AI/route boundaries.</summary>
public sealed class AgentConversationsHttpTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Path = "/api/v1/agent/conversations";
    private static long _nextClientMessageId;

    [PostgresEndToEndFact]
    public async Task ScopedPlanning_ThreeDaysKeepSeparateVectorsAndCommitProvenance()
    {
        var vectors = new List<double[]?>();
        var reco = ScopedReco(vectors);
        var scenario = await SetupAsync(reco.Object); using var host = scenario.Host; using var client = scenario.Client;
        var specialty = await factory.ExecuteDbAsync(async db =>
        {
            var tag = new Tag { Code = "scoped_specialty", Name = "Đặc sản khu vực", TagType = TagType.Category };
            var dish = await db.Dishes.OrderByDescending(x => x.Id).FirstAsync();
            dish.DishTags.Add(new() { Tag = tag }); await db.SaveChangesAsync(); return tag.Id;
        });
        var state = new AgentState { Province = "Ho Chi Minh City", NumDays = 3, BudgetAmount = 500000,
            MealSlots = ["lunch"], TasteOverrides = [new(1, null, [new("spicy", 1)])],
            MealRequests = [new(2, null, null, null, [specialty]), new(3, null, null, null, [], true)] };
        scenario.Llm.Override = context =>
        {
            if (context.Exchanges.Count == 0)
                return new(Enumerable.Range(1, 3).Select(day => new AgentToolCall($"day-{day}", "get_recommendation",
                    JsonSerializer.SerializeToElement(new AgentRecommendationInput(State: day == 1 ? state : null, Scope: "day", DayNumber: (short)day,
                        TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12), AgentJson.Options))).ToArray(), null);
            var items = new List<AgentItem>();
            for (short day = 1; day <= 3; day++)
            {
                var choices = context.Exchanges[0].Results[day - 1].GetProperty("items").EnumerateArray();
                var id = choices.Select(x => x.GetProperty("dishId").GetInt32()).First(id => items.All(x => x.DishId != id));
                items.Add(new($"d{day}", null, day, "lunch", 0, id));
            }
            return new([], new AgentOutput { Action = "create_proposal", Reply = "Quán bịa nổi tiếng mở 24/24", Items = items.ToArray() });
        };
        var conversation = await OpenAsync(client);
        var preview = await SendPromptAsync(client, conversation.ConversationId, "Lập lịch 3 ngày: ngày 1 cay, ngày 2 đặc sản, ngày 3 khác món trong lịch, chỉ bữa trưa.");
        preview.Proposal.Should().NotBeNull(); preview.Proposal!.Snapshot.Items.Should().HaveCount(3);
        preview.Reply.Should().NotContain("bịa").And.NotContain("24/24");
        vectors.Should().HaveCount(3); vectors[0]![5].Should().Be(1); vectors[1]![5].Should().Be(.25); vectors[2]![5].Should().Be(.25);
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.State.TasteOverrides.Should().ContainSingle(); restored.State.MealRequests.Should().HaveCount(2);
        (await factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded))).Should().Be(0);
        var commit = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = preview.Proposal.ProposalId, ProposalVersion = preview.Proposal.Version });
        (await factory.ExecuteDbAsync(db => db.RoadmapItems.CountAsync(x => x.RoadmapDay.RoadmapId == commit.RoadmapId && x.RecoRequestId != null))).Should().Be(3);
        (await factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded))).Should().Be(3);
    }

    [PostgresEndToEndFact]
    public async Task ScopedPlanning_MissingNamedDishAsksInsteadOfSubstituting()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        scenario.Llm.Override = context => context.Exchanges.Count == 0
            ? new([new("named", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                State: new() { Province = "Ho Chi Minh City", NumDays = 1, BudgetAmount = 500000,
                    MealRequests = [new(null, null, "Món tuyệt đối không có trong catalog", null, [])] },
                TagIds: [], ExcludeTagIds: [], ExcludeDishIds: []), AgentJson.Options))], null)
            : new([], new AgentOutput { Action = "clarify", Reply = "Tôi đã tự thêm món vào quán nổi tiếng.", Clarification = "catalog_choice" });
        var conversation = await OpenAsync(client);
        var result = await SendPromptAsync(client, conversation.ConversationId, "Thêm món tuyệt đối không có trong catalog vào lịch.");
        result.Proposal.Should().BeNull(); result.Reply.Should().Contain("Chưa tìm thấy").And.NotContain("tự thêm");
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    [PostgresEndToEndFact]
    public async Task ScopedPlanning_ReevaluatedSavedDishRetainsItemAndUpdatesRecoRequest()
    {
        var scenario = await SetupAsync(ScopedReco([]).Object); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var initial = await SendAsync(client, conversation.ConversationId, Message(1));
        var first = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = initial.Proposal!.ProposalId, ProposalVersion = initial.Proposal.Version });
        var oldItem = await factory.ExecuteDbAsync(db => db.RoadmapItems.AsNoTracking().SingleAsync());
        scenario.Llm.Override = context =>
        {
            var target = context.Source!.Items.Single();
            if (context.Exchanges.Count == 0)
                return new([new("refresh", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                    State: context.State with { Preferences = null, SourceRoadmapUpdatedAt = null, HistoryStartSequence = null,
                        TasteOverrides = [new(1, null, [new("spicy", 1)])] }, Scope: "slot", DayNumber: 1, MealSlot: "lunch",
                    TagIds: [], ExcludeTagIds: [], ExcludeDishIds: []), AgentJson.Options))], null);
            return new([], new AgentOutput { Action = "patch_proposal", Reply = "Đã đổi khẩu vị", BaseProposalVersion = context.ProposalVersion,
                Scope = new(1, "lunch"), Changes = [new("replace", target.ItemKey, target)] });
        };
        var updated = await SendPromptAsync(client, conversation.ConversationId, "Đánh giá lại ngày 1 theo khẩu vị cay, giữ món nếu vẫn phù hợp.");
        updated.Proposal.Should().NotBeNull(); updated.Proposal!.Snapshot.Items[0].RequestId.Should().NotBeNull();
        updated.Proposal.Snapshot.Items[0].RequestId!.Value.Should().NotBe(oldItem.RecoRequestId!.Value);
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = updated.Proposal.ProposalId, ProposalVersion = updated.Proposal.Version });
        saved.RoadmapId.Should().Be(first.RoadmapId);
        var current = await factory.ExecuteDbAsync(db => db.RoadmapItems.AsNoTracking().SingleAsync());
        current.Id.Should().Be(oldItem.Id); current.DishId.Should().Be(oldItem.DishId);
        current.RecoRequestId.Should().Be(updated.Proposal.Snapshot.Items[0].RequestId);
    }

    private static Mock<IRecommendationService> ScopedReco(List<double[]?> vectors)
    {
        var reco = new Mock<IRecommendationService>();
        reco.Setup(x => x.GetPreferencesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => new UserPreferenceSnapshot(id, 1, Enumerable.Repeat(.25, 12).ToArray(), null));
        reco.Setup(x => x.RecommendConversationAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<int>(), It.IsAny<RecommendationVector>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, IReadOnlyList<int> ids, int count, RecommendationVector vector, CancellationToken _) =>
            {
                vectors.Add(vector.UserVectorOverride?.ToArray()); var selected = ids.Take(count).ToArray();
                return new RecommendationResult(Guid.NewGuid(), selected, [], selected.Select(_ => .8).ToArray(), "conversation");
            });
        return reco;
    }

    // TEST-01: Create an unsaved preview, replay its message, confirm once, and replay the original commit after later edits.
    /// <summary>Verifies the complete Agent create lifecycle and both durable idempotency contracts.</summary>
    [PostgresEndToEndFact]
    public async Task PreviewReplayAndConfirm_ProducesOneRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var request = Message(conversation.Version);
        var turn = await SendAsync(client, conversation.ConversationId, request);
        turn.Proposal.Should().NotBeNull(); turn.Proposal!.Status.Should().Be("ready");
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.CountAsync()).Should().Be(0));
        var calls = scenario.Llm.Calls;
        var replay = await SendAsync(client, conversation.ConversationId, request);
        replay.Replayed.Should().BeTrue(); replay.Proposal!.ProposalId.Should().Be(turn.Proposal.ProposalId);
        scenario.Llm.Calls.Should().Be(calls);
        var confirm = new ConfirmAgentProposalRequestDto { ProposalId = turn.Proposal.ProposalId, ProposalVersion = turn.Proposal.Version };
        var first = await ConfirmAsync(client, conversation.ConversationId, confirm);
        first.RoadmapUpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        using var edit = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{first.RoadmapId}", new { title = "Manual edit" });
        edit.EnsureSuccessStatusCode();
        var second = await ConfirmAsync(client, conversation.ConversationId, confirm);
        second.Should().BeEquivalentTo(first);
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(1);
            (await db.AgentMessages.CountAsync(x => x.Role == AgentMessageRole.User)).Should().Be(1);
            (await db.AgentProposals.SingleAsync()).Status.Should().Be(AgentProposalStatus.Committed);
            (await db.Roadmaps.SingleAsync()).UpdatedAt.Should().BeAfter(first.RoadmapUpdatedAt);
        });
    }

    // TEST-02: Hide another traveler's conversation and roadmap from reads, tools and confirmation.
    /// <summary>Verifies ownership before any LLM or tool execution.</summary>
    [PostgresEndToEndFact]
    public async Task ForeignResources_AreConcealed()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var committed = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = 1 });
        var calls = scenario.Llm.Calls;
        using var stranger = factory.CreateClient();
        var auth = await DiscoverySeed.RegisterAndLoginAsync(factory, stranger, "TRAVELER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        using var get = await client.GetAsync($"{Path}/{conversation.ConversationId}");
        await ErrorAsync(get, HttpStatusCode.NotFound, "CONVERSATION_NOT_FOUND");
        using var send = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(1));
        await ErrorAsync(send, HttpStatusCode.NotFound, "CONVERSATION_NOT_FOUND");
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = turn.Proposal.ProposalId, proposalVersion = 1 });
        await ErrorAsync(confirm, HttpStatusCode.NotFound, "CONVERSATION_NOT_FOUND");
        using var open = await client.PostAsJsonAsync(Path, new { roadmapId = committed.RoadmapId });
        await ErrorAsync(open, HttpStatusCode.NotFound, "ROADMAP_NOT_FOUND");
        scenario.Llm.Calls.Should().Be(calls);
    }

    // TEST-03: Concurrent duplicate messages return processing while a distinct message receives a busy conflict.
    /// <summary>Verifies a single live attempt per conversation without holding a transaction across the LLM wait.</summary>
    [PostgresEndToEndFact]
    public async Task ConcurrentMessages_EnforceLeaseAndReplay()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var request = Message(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Llm.Before = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var firstTask = client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var duplicate = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
            duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted);
            using var other = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(2));
            await ErrorAsync(other, HttpStatusCode.Conflict, "CONVERSATION_BUSY");
        }
        finally { release.TrySetResult(); }
        using var first = await firstTask; first.EnsureSuccessStatusCode();
        scenario.Llm.Calls.Should().Be(2);
        (await SendAsync(client, conversation.ConversationId, request)).Replayed.Should().BeTrue();
    }

    // TEST-04: Reusing a client message ID for changed text or selections is rejected before provider work.
    /// <summary>Verifies that request hashing includes business choices and excludes expected transport version.</summary>
    [PostgresEndToEndFact]
    public async Task ReusedMessageIdentity_RejectsChangedBusinessContent()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var request = Message(1);
        await SendAsync(client, conversation.ConversationId, request);
        request.ExpectedConversationVersion = 999;
        (await SendAsync(client, conversation.ConversationId, request)).Replayed.Should().BeTrue();
        request.Content = "Different trip";
        using var changed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        await ErrorAsync(changed, HttpStatusCode.Conflict, "MESSAGE_ID_REUSED");
    }

    // TEST-05: An expired worker cannot save after a new retry acquires the same logical turn.
    /// <summary>Verifies attempt fencing against late results after lease takeover.</summary>
    [PostgresEndToEndFact]
    public async Task ExpiredAttempt_CannotOverwriteNewRetry()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var request = Message(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCall = 0;
        scenario.Llm.Before = async (_, ct) => { if (Interlocked.Increment(ref firstCall) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        var old = client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await factory.ExecuteDbAsync(async db => await db.AgentConversations.Where(x => x.Id == conversation.ConversationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcessingUntil, DateTime.UtcNow.AddMinutes(-1))));
            var current = await GetAsync(client, conversation.ConversationId); request.ExpectedConversationVersion = current.Version;
            var retried = await SendAsync(client, conversation.ConversationId, request);
            retried.Proposal.Should().NotBeNull();
        }
        finally { release.TrySetResult(); }
        using var stale = await old;
        await ErrorAsync(stale, HttpStatusCode.Conflict, "CONVERSATION_VERSION_CONFLICT");
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.AgentProposals.CountAsync()).Should().Be(1);
            (await db.AgentMessages.SingleAsync(x => x.Role == AgentMessageRole.User)).ProcessingStatus.Should().Be(AgentProcessingStatus.Completed);
        });
    }

    // TEST-06: Concurrent confirmation creates one roadmap and returns the same original committed result.
    /// <summary>Verifies database-serialized confirmation without duplicate itinerary items.</summary>
    [PostgresEndToEndFact]
    public async Task ConcurrentConfirm_HasOneBusinessEffect()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var request = new ConfirmAgentProposalRequestDto { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = turn.Proposal.Version };
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConfirmAsync(client, conversation.ConversationId, request)));
        responses.Should().OnlyContain(x => x.RoadmapId == responses[0].RoadmapId && x.RoadmapUpdatedAt == responses[0].RoadmapUpdatedAt);
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.CountAsync()).Should().Be(1));
    }

    // TEST-07: Manual roadmap metadata edits invalidate an outstanding update proposal atomically.
    /// <summary>Verifies source revision checks prevent overwriting a newer manual roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task ManualEditBeforeConfirm_ReturnsRoadmapUpdatedAtConflict()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var firstTurn = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = firstTurn.Proposal!.ProposalId, ProposalVersion = 1 });
        var current = await GetAsync(client, conversation.ConversationId);
        var next = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        using var manual = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{saved.RoadmapId}", new { title = "Keep this newer title" });
        manual.EnsureSuccessStatusCode();
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = next.Proposal!.ProposalId, proposalVersion = next.Proposal.Version });
        await ErrorAsync(confirm, HttpStatusCode.Conflict, "ROADMAP_UPDATED_AT_CONFLICT");
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.SingleAsync()).Title.Should().Be("Keep this newer title"));
    }

    [PostgresEndToEndFact]
    public async Task ItemDeletedAfterPreview_ConfirmRejectsStaleTimestamp()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        var current = await GetAsync(client, conversation.ConversationId);
        var preview = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        preview.Proposal!.Snapshot.BaseRoadmapUpdatedAt.Should().Be(saved.RoadmapUpdatedAt);
        using var deleted = await client.DeleteAsync($"/api/v1/roadmaps/{saved.RoadmapId}/items/{saved.ItemIds.Values.First()}");
        deleted.EnsureSuccessStatusCode();
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm",
            new { proposalId = preview.Proposal.ProposalId, proposalVersion = preview.Proposal.Version });
        await ErrorAsync(confirm, HttpStatusCode.Conflict, "ROADMAP_UPDATED_AT_CONFLICT");
        await factory.ExecuteDbAsync(async db => (await db.RoadmapItems.CountAsync()).Should().Be(0));
    }

    // TEST-08: Changed price and sold-out dishes block confirmation while preserving the ready proposal.
    /// <summary>Verifies current catalog data is rechecked rather than trusting preview prices.</summary>
    [PostgresEndToEndFact]
    public async Task CatalogChangesBeforeConfirm_BlockWithoutWritingRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var dishId = turn.Proposal!.Snapshot.Items[0].DishId;
        await factory.ExecuteDbAsync(async db => await db.Dishes.Where(x => x.Id == dishId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 900000m)));
        using var changed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = turn.Proposal.ProposalId, proposalVersion = 1 });
        await ErrorAsync(changed, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(0);
            (await db.AgentProposals.SingleAsync()).Status.Should().Be(AgentProposalStatus.Ready);
        });
    }

    // TEST-09: Discard an update without touching the official roadmap, then reset the draft safely.
    /// <summary>Verifies explicit non-destructive message commands and source-proposal lifecycle.</summary>
    [PostgresEndToEndFact]
    public async Task DiscardAndReset_KeepOfficialRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = 1 });
        var current = await GetAsync(client, conversation.ConversationId);
        var updated = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        var discard = Message(updated.ConversationVersion); discard.Command = "discard"; discard.ConfirmedState = null;
        var discarded = await SendAsync(client, conversation.ConversationId, discard); discarded.Proposal.Should().BeNull();
        var reset = Message(discarded.ConversationVersion); reset.Command = "reset"; reset.ConfirmedState = null;
        await SendAsync(client, conversation.ConversationId, reset);
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.SingleAsync()).UpdatedAt.Should().Be(saved.RoadmapUpdatedAt);
            (await db.AgentProposals.SingleAsync(x => x.Id == updated.Proposal!.ProposalId)).Status.Should().Be(AgentProposalStatus.Discarded);
            (await db.AgentProposals.SingleAsync(x => x.Id == turn.Proposal!.ProposalId)).Status.Should().Be(AgentProposalStatus.Committed);
        });
    }

    // TEST-10: Hallucinated model IDs and provider failures preserve the previous valid proposal.
    /// <summary>Verifies no invalid ready snapshot is stored on output or availability failure.</summary>
    [PostgresEndToEndFact]
    public async Task InvalidOutputAndProviderFailure_PreserveReadyProposal()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        scenario.Llm.Override = _ => new AgentLlmStep([], new AgentOutput { Action = "patch_proposal", Reply = "Fake", BaseProposalVersion = 1,
            Scope = new(1, "lunch"), Changes = [new("replace", "d1", new AgentItem("d1", null, 1, "lunch", 0, int.MaxValue))] });
        var invalid = await SendAsync(client, conversation.ConversationId, Message(turn.ConversationVersion));
        invalid.Validation!.Errors.Should().NotBeEmpty(); invalid.Proposal!.ProposalId.Should().Be(turn.Proposal!.ProposalId);
        scenario.Llm.Override = _ => null;
        using var unavailable = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(invalid.ConversationVersion));
        await ErrorAsync(unavailable, HttpStatusCode.ServiceUnavailable, "AGENT_UNAVAILABLE");
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.CurrentProposal!.ProposalId.Should().Be(turn.Proposal.ProposalId); restored.Processing.Should().BeFalse();
    }

    // TEST-11: Expiry is enforced on requests while completed replay remains durable and GET does not renew activity.
    /// <summary>Verifies retention policy independently of the background cleanup schedule.</summary>
    [PostgresEndToEndFact]
    public async Task ExpiryAndGet_RespectShortTermMemoryPolicy()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var request = Message(1); var turn = await SendAsync(client, conversation.ConversationId, request);
        var before = await factory.ExecuteDbAsync(async db => await db.AgentConversations.AsNoTracking().SingleAsync());
        await GetAsync(client, conversation.ConversationId);
        await factory.ExecuteDbAsync(async db => (await db.AgentConversations.SingleAsync()).UpdatedAt.Should().Be(before.UpdatedAt));
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))));
        using var expired = await client.GetAsync($"{Path}/{conversation.ConversationId}"); await ErrorAsync(expired, HttpStatusCode.Gone, "CONVERSATION_EXPIRED");
        (await SendAsync(client, conversation.ConversationId, request)).Replayed.Should().BeTrue();
        using var fresh = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(turn.ConversationVersion));
        await ErrorAsync(fresh, HttpStatusCode.Gone, "CONVERSATION_EXPIRED");
    }

    // TEST-12: Cleanup deletes retained Agent memory while the committed roadmap and items survive.
    /// <summary>Verifies restrictive current-proposal unlinking and non-cascading roadmap lifetime.</summary>
    [PostgresEndToEndFact]
    public async Task Cleanup_RemovesMemoryAndPreservesRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = 1 });
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow.AddDays(-31)).SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddDays(-24))));
        await factory.ExecuteDbAsync(async db =>
        {
            await db.AgentMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
            await db.AgentProposals.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
            await db.AgentMessages.Where(x => x.Role == AgentMessageRole.User).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{\"attempts\":[]}"));
        });
        using var scope = host.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None)).Should().Be(1);
        await factory.ExecuteDbAsync(async db =>
        {
            var retained = await db.AgentConversations.SingleAsync();
            retained.Id.Should().Be(conversation.ConversationId); retained.RoadmapId.Should().Be(saved.RoadmapId);
            retained.State.Should().Be("{}");
            (await db.AgentMessages.CountAsync()).Should().Be(0);
            (await db.AgentProposals.CountAsync()).Should().Be(0); (await db.Roadmaps.SingleAsync()).Id.Should().Be(saved.RoadmapId);
            (await db.RoadmapItems.CountAsync()).Should().BeGreaterThan(0);
        });
    }

    // TEST-13: A replacement creates a fresh roadmap item and detaches old feedback instead of transferring it.
    /// <summary>Verifies the RM-08 identity and feedback rule through a real Agent update transaction.</summary>
    [PostgresEndToEndFact]
    public async Task UpdateProposal_ReplacesItemWithoutTransferringFeedback()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        var itemId = saved.ItemIds["d1"];
        using var feedback = await client.PostAsJsonAsync($"/api/v1/roadmaps/{saved.RoadmapId}/items/{itemId}/feedback", new { value = "like" }); feedback.EnsureSuccessStatusCode();
        var current = await GetAsync(client, conversation.ConversationId);
        var next = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        var updated = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = next.Proposal!.ProposalId, ProposalVersion = 2 });
        updated.RoadmapId.Should().Be(saved.RoadmapId); updated.RoadmapUpdatedAt.Should().BeAfter(saved.RoadmapUpdatedAt);
        updated.ItemIds[$"r{itemId}"].Should().NotBe(itemId);
        await factory.ExecuteDbAsync(async db => (await db.DishFeedbacks.SingleAsync()).RoadmapItemId.Should().BeNull());
    }

    // TEST-14: Explicit alternatives avoid previous dishes rather than treating retry or recommender fallback as diversity.
    /// <summary>Verifies exclusion-before-ranking for an alternative proposal.</summary>
    [PostgresEndToEndFact]
    public async Task Alternative_ExcludesPreviousDish()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var request = Message(first.ConversationVersion); request.Command = "alternative";
        var second = await SendAsync(client, conversation.ConversationId, request);
        second.Proposal!.Snapshot.Items.Select(x => x.DishId).Should().NotIntersectWith(first.Proposal!.Snapshot.Items.Select(x => x.DishId));
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-15: Persistent quota admission is serialized across conversations and returns the documented retry header.
    /// <summary>Verifies per-user allowance enforcement without in-memory quota races.</summary>
    [PostgresEndToEndFact]
    public async Task QuotaAcrossConversations_Returns429WithRetryAfter()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        host.Services.GetRequiredService<AgentLimits>().MessagesPerMinute = 1;
        var first = await OpenAsync(client); var second = await OpenAsync(client);
        await SendAsync(client, first.ConversationId, Message(1));
        using var blocked = await client.PostAsJsonAsync($"{Path}/{second.ConversationId}/messages", Message(1));
        await ErrorAsync(blocked, HttpStatusCode.TooManyRequests, "AGENT_RATE_LIMITED");
        blocked.Headers.RetryAfter.Should().NotBeNull();
    }

    // TEST-16: JWT, role and request validators reject unauthorized, wrong-role and malformed Agent requests.
    /// <summary>Verifies canonical HTTP envelopes at the transport boundary.</summary>
    [PostgresEndToEndFact]
    public async Task HttpAdmission_EnforcesAuthAndValidation()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        using var invalid = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new { content = (string?)null });
        var invalidBody = await invalid.Content.ReadAsStringAsync();
        await ErrorAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var validation = AgentJson.Read<ApiResponse<object>>(invalidBody);
        validation!.Errors!["content"].Should().Contain("Vui lòng nhập nội dung tin nhắn.");
        client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await client.GetAsync($"{Path}/{conversation.ConversationId}"); await ErrorAsync(anonymous, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        using var ownerClient = factory.CreateClient(); var owner = await DiscoverySeed.RegisterAndLoginAsync(factory, ownerClient, "RESTAURANT_OWNER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var forbidden = await client.GetAsync($"{Path}/{conversation.ConversationId}"); await ErrorAsync(forbidden, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // TEST-17: Replacing day-two lunch preserves day-one items and leaves the official roadmap absent until confirmation.
    /// <summary>Verifies accumulated multi-turn scope preservation through the complete HTTP and database stack.</summary>
    [PostgresEndToEndFact]
    public async Task DayTwoLunchPatch_PreservesOtherDays()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var request = Message(1);
        request.ConfirmedState = request.ConfirmedState! with { NumDays = 2 };
        var first = await SendAsync(client, conversation.ConversationId, request);
        var next = Message(first.ConversationVersion); next.SelectedItemKey = "d2";
        next.ConfirmedState = request.ConfirmedState with { FocusDay = 2, FocusMeal = "lunch" };
        var second = await SendAsync(client, conversation.ConversationId, next);
        second.Proposal!.Snapshot.Items.Single(x => x.ItemKey == "d1").Should().Be(first.Proposal!.Snapshot.Items.Single(x => x.ItemKey == "d1"));
        second.Proposal.Snapshot.Items.Single(x => x.ItemKey == "d2").DishId.Should().NotBe(first.Proposal.Snapshot.Items.Single(x => x.ItemKey == "d2").DishId);
        second.Proposal.Snapshot.Changes!.Single().Type.Should().Be("replace");
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-18: A failed provider attempt can retry with the same business ID after the client reloads the current version.
    /// <summary>Verifies failed-turn retry appends admission metadata without creating a second logical user message.</summary>
    [PostgresEndToEndFact]
    public async Task FailedTurnRetry_UsesSameLogicalMessage()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var request = Message(1); scenario.Llm.Override = _ => null;
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        await ErrorAsync(failed, HttpStatusCode.ServiceUnavailable, "AGENT_UNAVAILABLE");
        request.ExpectedConversationVersion = (await GetAsync(client, conversation.ConversationId)).Version; scenario.Llm.Override = null;
        (await SendAsync(client, conversation.ConversationId, request)).Proposal.Should().NotBeNull();
        await factory.ExecuteDbAsync(async db =>
        {
            var userMessage = await db.AgentMessages.SingleAsync(x => x.Role == AgentMessageRole.User);
            AgentJson.Read<AgentStoredMessage>(userMessage.Payload).Attempts.Should().HaveCount(2);
            userMessage.ProcessingStatus.Should().Be(AgentProcessingStatus.Completed);
        });
    }

    // TEST-19: Opening a saved roadmap reuses its conversation and newer previews supersede older ones.
    /// <summary>Verifies the single-conversation contract through HTTP.</summary>
    [PostgresEndToEndFact]
    public async Task OpenRoadmap_ReusesConversationAndSupersedesOldProposal()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var firstConversation = await OpenAsync(client); var first = await SendAsync(client, firstConversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, firstConversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        var source = await GetAsync(client, firstConversation.ConversationId);
        var a = await SendAsync(client, source.ConversationId, Message(source.Version));
        using var opening = await client.PostAsJsonAsync(Path, new { roadmapId = saved.RoadmapId });
        opening.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondConversation = await DataAsync<AgentConversationDto>(opening);
        secondConversation.ConversationId.Should().Be(firstConversation.ConversationId);
        var b = await SendAsync(client, secondConversation.ConversationId, Message(secondConversation.Version));
        await ConfirmAsync(client, secondConversation.ConversationId, new() { ProposalId = b.Proposal!.ProposalId, ProposalVersion = b.Proposal.Version });
        using var conflict = await client.PostAsJsonAsync($"{Path}/{firstConversation.ConversationId}/confirm", new { proposalId = a.Proposal!.ProposalId, proposalVersion = 2 });
        await ErrorAsync(conflict, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
    }

    // TEST-20: Proposal-insert failure rolls back superseding the old snapshot and failure recovery never reflushes staged changes.
    /// <summary>Exercises real database rollback and the change-tracker isolation required by attempt recovery.</summary>
    [PostgresEndToEndFact]
    public async Task ProposalSaveFailure_RollsBackAndPreservesPreviousReadyState()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        await factory.ExecuteDbAsync(async db => await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION core.reject_agent_test_proposal() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected test proposal failure'; END $$;
            CREATE TRIGGER reject_agent_test_proposal BEFORE INSERT ON core.agent_proposals
                FOR EACH ROW EXECUTE FUNCTION core.reject_agent_test_proposal();
            """));
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(first.ConversationVersion));
        await ErrorAsync(failed, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.Processing.Should().BeFalse(); restored.CurrentProposal!.Status.Should().Be("ready");
        restored.CurrentProposal.ProposalId.Should().Be(first.Proposal!.ProposalId);
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.AgentProposals.CountAsync()).Should().Be(1);
            (await db.AgentMessages.CountAsync(x => x.ProcessingStatus == AgentProcessingStatus.Failed)).Should().Be(1);
        });
    }

    // TEST-21: Clarification always records a pending question and blocks confirmation of an older ready snapshot.
    /// <summary>Verifies ambiguous references cannot accidentally turn a conversational yes into a commit.</summary>
    [PostgresEndToEndFact]
    public async Task AmbiguousReference_RequiresClarificationBeforeCommit()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        scenario.Llm.Override = _ => new AgentLlmStep([], new AgentOutput { Action = "clarify", Reply = "Bạn muốn đổi món nào?" });
        var request = Message(first.ConversationVersion); request.Content = "Đổi món đó"; request.ConfirmedState = null;
        await SendAsync(client, conversation.ConversationId, request);
        (await GetAsync(client, conversation.ConversationId)).State.PendingQuestion.Should().NotBeNullOrWhiteSpace();
        using var blocked = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = first.Proposal!.ProposalId, proposalVersion = 1 });
        await ErrorAsync(blocked, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-22: Idle expiry preserves the last business timestamp and cleanup leaves an unexpired processing lease alone.
    /// <summary>Verifies both cleanup retention boundaries using real PostgreSQL row selection.</summary>
    [PostgresEndToEndFact]
    public async Task Cleanup_ExpiresWithoutRenewingActivityAndSkipsLiveLeases()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var lastActivity = DateTime.UtcNow.AddDays(-8);
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.UpdatedAt, lastActivity).SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddDays(-1))));
        using (var scope = host.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None);
        await factory.ExecuteDbAsync(async db =>
        {
            var expired = await db.AgentConversations.SingleAsync(); expired.Status.Should().Be(AgentConversationStatus.Expired);
            expired.UpdatedAt.Should().BeCloseTo(lastActivity, TimeSpan.FromMilliseconds(1));
        });
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow.AddDays(-31)).SetProperty(x => x.ProcessingTurnId, 1L)
            .SetProperty(x => x.ProcessingAttemptId, 2L).SetProperty(x => x.ProcessingUntil, DateTime.UtcNow.AddMinutes(1))));
        using (var scope = host.Services.CreateScope()) (await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None)).Should().Be(0);
        (await factory.ExecuteDbAsync(db => db.AgentConversations.CountAsync())).Should().Be(1);
    }

    // TEST-23: Manual metadata edits made before a new chat turn remain authoritative for a scoped item update.
    /// <summary>Prevents old conversation state from reverting a newer title or budget during a subsequent Agent edit.</summary>
    [PostgresEndToEndFact]
    public async Task ManualMetadataBeforeNewTurn_IsPreservedOnAgentUpdate()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        using var manual = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{saved.RoadmapId}", new { title = "Keep my manual title", budgetAmount = 250000 });
        manual.EnsureSuccessStatusCode();
        var request = Message((await GetAsync(client, conversation.ConversationId)).Version); request.ConfirmedState = null;
        var next = await SendAsync(client, conversation.ConversationId, request);
        next.Proposal!.Snapshot.State.Title.Should().Be("Keep my manual title");
        next.Proposal.Snapshot.State.BudgetAmount.Should().Be(250000);
        await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = next.Proposal.ProposalId, ProposalVersion = 2 });
        await factory.ExecuteDbAsync(async db =>
        {
            var roadmap = await db.Roadmaps.SingleAsync(); roadmap.Title.Should().Be("Keep my manual title"); roadmap.BudgetAmount.Should().Be(250000);
        });
    }

    // TEST-24: Removing the last official item keeps the roadmap and its days while applying exactly one revision.
    /// <summary>Verifies the zero-item update boundary without treating it as roadmap deletion or draft discard.</summary>
    [PostgresEndToEndFact]
    public async Task RemoveLastSavedItem_PreservesRoadmapAndDays()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        scenario.Llm.Override = context => new AgentLlmStep([], new AgentOutput { Action = "patch_proposal", Reply = "Removal preview", BaseProposalVersion = context.ProposalVersion,
            Changes = [new("remove", context.Source!.Items.Single().ItemKey, null)] });
        var request = Message((await GetAsync(client, conversation.ConversationId)).Version); request.ConfirmedState = null;
        var removal = await SendAsync(client, conversation.ConversationId, request);
        removal.Proposal!.Snapshot.Items.Should().BeEmpty();
        var updated = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = removal.Proposal.ProposalId, ProposalVersion = 2 });
        updated.RoadmapUpdatedAt.Should().BeAfter(saved.RoadmapUpdatedAt); updated.ItemIds.Should().BeEmpty();
        await factory.ExecuteDbAsync(async db => { (await db.Roadmaps.CountAsync()).Should().Be(1); (await db.RoadmapDays.CountAsync()).Should().Be(1); (await db.RoadmapItems.CountAsync()).Should().Be(0); });
    }

    // TEST-25: Numeric message IDs are scoped to a conversation and transcript turns retain server-issued sequence IDs.
    /// <summary>Verifies ordinary integer IDs preserve replay, cross-conversation isolation and transcript grouping.</summary>
    [PostgresEndToEndFact]
    public async Task NumericMessageIds_AreConversationScopedAndReplayable()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var first = await OpenAsync(client); var second = await OpenAsync(client);
        var request = Message(1); request.ClientMessageId = 1;
        var turn = await SendAsync(client, first.ConversationId, request);
        (await SendAsync(client, first.ConversationId, request)).Replayed.Should().BeTrue();
        (await SendAsync(client, second.ConversationId, request)).Replayed.Should().BeFalse();
        var discard = new SendAgentMessageRequestDto { ClientMessageId = 2,
            ExpectedConversationVersion = turn.ConversationVersion, Content = "Discard preview", Command = "discard" };
        await SendAsync(client, first.ConversationId, discard);
        await factory.ExecuteDbAsync(async db =>
        {
            var messages = await db.AgentMessages.OrderBy(x => x.SequenceNo).ToArrayAsync();
            var users = messages.Where(x => x.Role == AgentMessageRole.User).ToArray();
            users.Should().HaveCount(3);
            users.Should().OnlyContain(x => x.TurnId == x.SequenceNo && x.ClientMessageId > 0);
            messages.Should().OnlyContain(x => x.TurnId > 0 && users.Any(u =>
                u.ConversationId == x.ConversationId && u.TurnId == x.TurnId));
        });
    }

    // TEST-26: Invalid numeric identities and legacy UUID strings fail before provider execution.
    /// <summary>Checks positive integer JSON binding, signed bounds and rejection of the former UUID contract.</summary>
    [PostgresEndToEndFact]
    public async Task InvalidNumericMessageIds_AreRejectedBeforeProvider()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        foreach (var id in new[] { "0", "-1", "1.5", "9223372036854775808", "\"9348b4fd-82b7-4755-8b5c-f56e79a073d5\"" })
        {
            using var body = new StringContent("{\"clientMessageId\":" + id +
                ",\"expectedConversationVersion\":1,\"content\":\"Plan lunch\"}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"{Path}/{conversation.ConversationId}/messages", body);
            await ErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        }
        scenario.Llm.Calls.Should().Be(0);
        await factory.ExecuteDbAsync(async db => (await db.AgentMessages.CountAsync()).Should().Be(0));
    }

    // TEST-27: Chat-only requests create a preview, explicitly save it and replay the same committed response without LLM calls.
    /// <summary>Exercises the complete three-field request contract through real HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_PreviewSaveAndReplay_ProducesOneRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");
        preview.Proposal!.Status.Should().Be("ready");
        var calls = scenario.Llm.Calls;
        var saved = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này", 9001);
        saved.Commit.Should().NotBeNull(); saved.Proposal!.Status.Should().Be("committed");
        var replay = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này", 9001);
        replay.Replayed.Should().BeTrue(); replay.Commit.Should().BeEquivalentTo(saved.Commit);
        scenario.Llm.Calls.Should().Be(calls);
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(1);
            (await db.AgentMessages.CountAsync(message => message.Role == AgentMessageRole.User)).Should().Be(2);
            (await db.AgentProposals.SingleAsync()).CommitResult.Should().NotBeNull();
        });
    }

    // TEST-28: Bare yes, negation and quoted save text cannot commit, while saving without a preview gives a natural response.
    /// <summary>Verifies the backend save authorization boundary independently of provider interpretation.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_UnreviewedOrNegatedSave_NeverWritesRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        (await SendChatAsync(client, conversation.ConversationId, conversation.Version, "lưu lịch trình này")).Commit.Should().BeNull();
        scenario.Llm.Calls.Should().Be(0);
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Chưa lưu lịch." });
        foreach (var content in new[] { "đồng ý", "đừng lưu lịch trình này", "\"lưu lịch trình này\"" })
        {
            var current = await GetAsync(client, conversation.ConversationId);
            (await SendChatAsync(client, conversation.ConversationId, current.Version, content)).Commit.Should().BeNull();
        }
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-29: A model relaxation is staged durably and accepted or rejected only by a separate human chat turn.
    /// <summary>Checks chat consent across requests and blocks saving an older preview while consent is pending.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ConstraintConsent_DoesNotTreatYesAsSave()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        scenario.Llm.Override = context => new([], new AgentOutput { Action = "answer", Reply = "Budget request",
            ConstraintsChangeConfirmed = true, State = context.State with { BudgetAmount = 700000 } });
        var staged = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "Tăng ngân sách lên 700000 đồng");
        var state = (await GetAsync(client, conversation.ConversationId)).State;
        state.BudgetAmount.Should().Be(500000); state.PendingStateChange!.BudgetAmount.Should().Be(700000);
        var blocked = await SendChatAsync(client, conversation.ConversationId, staged.ConversationVersion, "lưu lịch trình này");
        blocked.Commit.Should().BeNull(); blocked.Reply.Should().Contain("đang chờ");
        EnableChatPlanning(scenario);
        var accepted = await SendChatAsync(client, conversation.ConversationId, blocked.ConversationVersion, "đồng ý");
        accepted.Commit.Should().BeNull(); accepted.Proposal.Should().NotBeNull();
        accepted.Proposal!.Snapshot.State.BudgetAmount.Should().Be(700000);
        accepted.Proposal.Snapshot.Items.Should().NotBeEmpty();
        var current = await GetAsync(client, conversation.ConversationId);
        current.State.BudgetAmount.Should().Be(700000); current.State.PendingStateChange.Should().BeNull();
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
        (await factory.ExecuteDbAsync(db => db.AgentProposals.SingleAsync(proposal => proposal.Id == preview.Proposal!.ProposalId))).Status.Should().Be(AgentProposalStatus.Superseded);
    }

    // TEST-30: Rejecting a staged change keeps the earlier preview and permits its later explicit save.
    /// <summary>Verifies chat rejection does not alter constraints or accidentally discard a still-valid preview.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_RejectConstraintChange_KeepsReviewedPreview()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        scenario.Llm.Override = context => new([], new AgentOutput { Action = "answer", Reply = "Budget request", State = context.State with { BudgetAmount = 700000 } });
        var staged = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "Tăng ngân sách");
        var rejected = await SendChatAsync(client, conversation.ConversationId, staged.ConversationVersion, "giữ nguyên");
        var saved = await SendChatAsync(client, conversation.ConversationId, rejected.ConversationVersion, "lưu lịch trình này");
        saved.Commit.Should().NotBeNull(); saved.Proposal!.ProposalId.Should().Be(preview.Proposal!.ProposalId);
        (await factory.ExecuteDbAsync(db => db.Roadmaps.SingleAsync())).BudgetAmount.Should().Be(500000);
    }

    // TEST-31: Discard and reset operate through text alone, preserve the official roadmap and fence old chat context.
    /// <summary>Verifies reset preserves its roadmap and excludes pre-reset user messages.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_DiscardAndReset_PreserveSavedRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Original old planning text");
        var saved = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này");
        var discarded = await SendChatAsync(client, conversation.ConversationId, saved.ConversationVersion, "bỏ bản nháp");
        discarded.Proposal.Should().BeNull();
        var reset = await SendChatAsync(client, conversation.ConversationId, discarded.ConversationVersion, "làm lại từ đầu");
        reset.Proposal.Should().BeNull();
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.State.Province.Should().Be("Ho Chi Minh City"); restored.State.HistoryStartSequence.Should().BeGreaterThan(0);
        scenario.Llm.Override = context =>
        {
            context.Roadmap.Should().NotBeNull(); context.Source.Should().NotBeNull();
            context.History.Should().NotContain(message => message.Content == "Original old planning text");
            return new([], new AgentOutput { Action = "clarify", Reply = "Bạn muốn đến đâu?" });
        };
        await SendChatAsync(client, conversation.ConversationId, reset.ConversationVersion, "Kế hoạch mới");
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
        (await factory.ExecuteDbAsync(db => db.AgentConversations.SingleAsync())).RoadmapId.Should().Be(saved.Commit!.RoadmapId);
    }

    // TEST-32: A late transcript persistence error rolls back the official save and proposal commit together.
    /// <summary>Proves chat acknowledgement and roadmap application share one transaction.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_FinalizeFailure_RollsBackRoadmapAndCommit()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        await factory.ExecuteDbAsync(async db => await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION core.reject_chat_save_completion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected chat finalize failure'; END $$;
            CREATE TRIGGER reject_chat_save_completion BEFORE UPDATE ON core.agent_messages
                FOR EACH ROW WHEN (NEW.role = 'user' AND NEW.processing_status = 'completed')
                EXECUTE FUNCTION core.reject_chat_save_completion();
            """));
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new
            { clientMessageId = 9001, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" });
        await ErrorAsync(failed, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(0);
            (await db.AgentProposals.SingleAsync()).Status.Should().Be(AgentProposalStatus.Ready);
            (await db.AgentMessages.CountAsync(message => message.ProcessingStatus == AgentProcessingStatus.Failed)).Should().Be(1);
        });
        (await GetAsync(client, conversation.ConversationId)).Processing.Should().BeFalse();
    }

    // TEST-33: Simultaneous save messages cannot apply one preview twice and stale versions fail before saving.
    /// <summary>Checks both turn leasing and the transactional proposal idempotency boundary.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ConcurrentSave_ProducesOneBusinessEffect()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        var uri = $"{Path}/{conversation.ConversationId}/messages";
        var responses = await Task.WhenAll(client.PostAsJsonAsync(uri, new { clientMessageId = 9001, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" }),
            client.PostAsJsonAsync(uri, new { clientMessageId = 9002, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" }));
        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
            (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    // TEST-34: Catalog price changes invalidate a chat save just as they invalidate the explicit confirm endpoint.
    /// <summary>Reuses deterministic confirmation validation rather than trusting an AI save decision.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ChangedPrice_BlocksSaveWithoutRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        var dishId = preview.Proposal!.Snapshot.Items.Single().DishId;
        await factory.ExecuteDbAsync(async db => await db.Dishes.Where(dish => dish.Id == dishId).ExecuteUpdateAsync(setters => setters.SetProperty(dish => dish.Price, dish => dish.Price + 10000)));
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new
            { clientMessageId = 9001, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" });
        await ErrorAsync(failed, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-35: Scalar documents a content-only request without fixed identity/version values that manual users might reuse.
    /// <summary>Checks the generated OpenAPI example consumed by the manual testing UI.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_OpenApi_PublishesMinimalRequestExample()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var example = document.RootElement.GetProperty("paths").GetProperty(Path + "/{id}/messages")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example");
        example.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("content");
        example.GetProperty("content").GetString().Should().Contain("Hà Nội");
    }

    // TEST-36: Chat alternatives exclude current scoped dishes before ranking without requiring a frontend command.
    /// <summary>Verifies inferred alternative scope is enforced by backend candidate filtering and proposal validation.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_Alternative_ExcludesPreviousDish()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        var old = preview.Proposal!.Snapshot.Items.Single();
        scenario.Llm.Override = context =>
        {
            if (context.Exchanges.Count == 0) return new([new("chat-alternative", "get_recommendation", JsonSerializer.SerializeToElement(
                new AgentRecommendationInput(Scope: "slot", DayNumber: 1, MealSlot: "lunch", TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], Alternative: true), AgentJson.Options))], null);
            var candidate = context.Exchanges[0].Results[0].GetProperty("items")[0].GetProperty("dishId").GetInt32();
            return new([], new AgentOutput { Action = "patch_proposal", Reply = "Phương án khác", BaseProposalVersion = context.ProposalVersion,
                Scope = new(1, "lunch"), Changes = [new("replace", old.ItemKey, old with { DishId = candidate, ItemKey = old.ItemKey + "_alt" })] });
        };
        var alternative = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "Cho tôi phương án bữa trưa khác");
        alternative.Proposal!.Snapshot.Items.Single().DishId.Should().NotBe(old.DishId);
        alternative.Proposal.Snapshot.Items.Single().ItemKey.Should().Be(old.ItemKey);
        alternative.Proposal.Validation.Errors.Should().BeEmpty();
    }

    // TEST-37: A manual roadmap edit after a preview prevents a later chat save from overwriting that edit.
    /// <summary>Checks source-revision fencing for the chat confirmation transport.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ManualRoadmapEdit_BlocksStaleSave()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        var saved = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này");
        scenario.Llm.Override = null;
        var edit = await SendChatAsync(client, conversation.ConversationId, saved.ConversationVersion, "Đổi món trưa");
        using var manual = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{saved.Commit!.RoadmapId}", new { title = "Keep manual title" });
        manual.EnsureSuccessStatusCode();
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new
            { clientMessageId = 9001, expectedConversationVersion = edit.ConversationVersion, content = "lưu lịch trình này" });
        await ErrorAsync(failed, HttpStatusCode.Conflict, "ROADMAP_UPDATED_AT_CONFLICT");
        (await factory.ExecuteDbAsync(db => db.Roadmaps.SingleAsync())).Title.Should().Be("Keep manual title");
    }

    // TEST-38: Prompt-only messages permit repeated text and skip existing explicit IDs while preserving retry/conflict semantics.
    /// <summary>Exercises omitted/null transport metadata and replays the returned server identity with a scripted provider.</summary>
    [PostgresEndToEndFact]
    public async Task PromptOnly_RepeatedContent_GetsDistinctIdsAndSupportsExplicitReplay()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Bạn muốn ăn ở tỉnh nào?" });
        var conversation = await OpenAsync(client);
        await SendAsync(client, conversation.ConversationId, new() { ClientMessageId = 3, Content = "Tin cũ" });
        const string content = "Tôi muốn ăn ở Việt Nam";
        var first = await SendPromptAsync(client, conversation.ConversationId, content);
        var second = await SendPromptAsync(client, conversation.ConversationId, content);
        using var nullResponse = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages",
            new { content = "Tôi muốn ăn ở Hà Nội", clientMessageId = (long?)null, expectedConversationVersion = (long?)null });
        var third = await DataAsync<AgentTurnDto>(nullResponse);
        first.ClientMessageId.Should().Be(4); // Next sequence 3 is already an explicit client ID.
        new[] { first.ClientMessageId, second.ClientMessageId, third.ClientMessageId }.Should().OnlyHaveUniqueItems();
        first.Replayed.Should().BeFalse(); second.Replayed.Should().BeFalse(); third.Replayed.Should().BeFalse();
        var calls = scenario.Llm.Calls;
        var replay = await SendAsync(client, conversation.ConversationId, new() { ClientMessageId = second.ClientMessageId,
            ExpectedConversationVersion = 1, Content = content });
        replay.Replayed.Should().BeTrue(); replay.ClientMessageId.Should().Be(second.ClientMessageId);
        using var reused = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages",
            new { clientMessageId = second.ClientMessageId, content = "Nội dung khác" });
        await ErrorAsync(reused, HttpStatusCode.Conflict, "MESSAGE_ID_REUSED");
        using var stale = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages",
            new { content, expectedConversationVersion = 1 });
        await ErrorAsync(stale, HttpStatusCode.Conflict, "CONVERSATION_VERSION_CONFLICT");
        scenario.Llm.Calls.Should().Be(calls);
        await factory.ExecuteDbAsync(async db =>
        {
            var messages = await db.AgentMessages.Where(message => message.Role == AgentMessageRole.User).ToArrayAsync();
            messages.Should().HaveCount(4); messages.Should().OnlyContain(message => message.ClientMessageId > 0);
            messages.Select(message => message.ClientMessageId).Should().OnlyHaveUniqueItems();
        });
    }

    // TEST-39: Content-only planning and explicit chat save retain the same atomic, idempotent roadmap writer.
    /// <summary>Verifies saving and retrying the returned message ID do not call the provider or create a second roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task PromptOnly_PreviewAndSave_ProducesOneRoadmap()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        var conversation = await OpenAsync(client);
        var preview = await SendPromptAsync(client, conversation.ConversationId, "Lên lịch ăn trưa");
        preview.Proposal!.Status.Should().Be("ready");
        var calls = scenario.Llm.Calls;
        var saved = await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này");
        saved.Commit.Should().NotBeNull(); saved.ClientMessageId.Should().NotBe(preview.ClientMessageId);
        var replay = await SendAsync(client, conversation.ConversationId, new() { ClientMessageId = saved.ClientMessageId, Content = "lưu lịch trình này" });
        replay.Replayed.Should().BeTrue(); replay.Commit.Should().BeEquivalentTo(saved.Commit);
        var again = await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này");
        again.ClientMessageId.Should().NotBe(saved.ClientMessageId); again.Commit.Should().BeEquivalentTo(saved.Commit);
        scenario.Llm.Calls.Should().Be(calls);
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
    }

    // TEST-40: Omitting identity/version does not admit a second concurrent message while the first owns its processing lease.
    /// <summary>Checks prompt-only busy fencing with a blocked scripted provider, then completes the original turn.</summary>
    [PostgresEndToEndFact]
    public async Task PromptOnly_ConcurrentMessage_IsBusyBeforeProvider()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Bạn muốn ăn ở tỉnh nào?" });
        scenario.Llm.Before = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var conversation = await OpenAsync(client);
        var first = SendPromptAsync(client, conversation.ConversationId, "Tôi muốn ăn ở Việt Nam");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var busy = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new { content = "Tôi muốn ăn ở Việt Nam" });
            await ErrorAsync(busy, HttpStatusCode.Conflict, "CONVERSATION_BUSY");
        }
        finally { release.TrySetResult(); }
        (await first).Status.Should().Be("completed"); scenario.Llm.Calls.Should().Be(1);
        (await factory.ExecuteDbAsync(db => db.AgentMessages.CountAsync(message => message.Role == AgentMessageRole.User))).Should().Be(1);
    }

    // TEST-41: Real adapter failures use one fake HTTP request and preserve the existing ready preview through failed-turn recovery.
    /// <summary>Exercises rate limits, authentication, provider unavailability and HTTP timeout through the complete backend stack.</summary>
    [PostgresEndToEndFact]
    public async Task ProviderFailures_DoNotRepairOrReplacePreview()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var preview = await SendAsync(client, conversation.ConversationId, Message(1));
        foreach (var mode in new[] { "429", "401", "503", "timeout" })
        {
            using var handler = new FailureProviderHandler(mode);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/"), Timeout = TimeSpan.FromMilliseconds(100) };
            scenario.Llm.Provider = new OpenAiAgentLlmService(http,
                Options.Create(new AiSettings { ApiKey = "fake-test-key", Model = "fake-test-model" }), new(), TimeProvider.System);
            using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new { content = "Đổi món trong lịch" });
            var failureJson = await failed.Content.ReadAsStringAsync();
            await ErrorAsync(failed, mode == "429" ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable, "AGENT_UNAVAILABLE");
            var message = JsonSerializer.Deserialize<ApiResponse<object>>(failureJson, AgentJson.Options)!.Message!;
            message.Should().Contain(mode switch { "429" => "giới hạn", "401" => "cấu hình", "503" => "không khả dụng", _ => "quá lâu" });
            message.Should().NotContain("sensitive-provider-body"); handler.Calls.Should().Be(1); handler.RepairCalls.Should().Be(0);
            TimeSpan? retryDelay = mode is "429" or "503" ? TimeSpan.FromSeconds(45) : null;
            (failed.Headers.RetryAfter?.Delta).Should().Be(retryDelay);
            var restored = await GetAsync(client, conversation.ConversationId);
            restored.Processing.Should().BeFalse(); restored.CurrentProposal!.ProposalId.Should().Be(preview.Proposal!.ProposalId);
            restored.CurrentProposal.Status.Should().Be("ready");
        }
        scenario.Llm.Provider = null;
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Bản nháp vẫn còn nguyên." });
        (await SendPromptAsync(client, conversation.ConversationId, "Giải thích bản nháp")).Status.Should().Be("completed");
        (await factory.ExecuteDbAsync(db => db.AgentMessages.CountAsync(message => message.ProcessingStatus == AgentProcessingStatus.Failed))).Should().Be(4);
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-42: Only a received schema-invalid model response permits one repair through the real adapter.
    /// <summary>Verifies bounded repair preserves the current proposal and never writes an official roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task ProviderSchemaFailure_RepairsOnceAndPreservesPreview()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var preview = await SendAsync(client, conversation.ConversationId, Message(1));
        using var handler = new FailureProviderHandler("schema"); using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/") };
        scenario.Llm.Provider = new OpenAiAgentLlmService(http,
            Options.Create(new AiSettings { ApiKey = "fake-test-key", Model = "fake-test-model" }), new(), TimeProvider.System);
        var answered = await SendPromptAsync(client, conversation.ConversationId, "Giải thích bản nháp");
        answered.Reply.Should().Contain("Bản đề xuất hiện tại").And.NotContain("Đã giải thích lịch."); answered.Proposal!.ProposalId.Should().Be(preview.Proposal!.ProposalId);
        handler.Calls.Should().Be(2); handler.RepairCalls.Should().Be(1);
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    [PostgresEndToEndFact]
    public async Task ConcurrentFirstOpen_CreatesOnlyOneConversation()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var roadmapId = await factory.ExecuteDbAsync(async db =>
        {
            var userId = await db.Users.Where(x => x.Role.Code == "TRAVELER").Select(x => x.Id).SingleAsync();
            var roadmap = new ANGI.Domain.Entities.Roadmap { UserId = userId, Title = "Existing trip", NumDays = 1 };
            db.Roadmaps.Add(roadmap); await db.SaveChangesAsync(); return roadmap.Id;
        });
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync(Path, new { roadmapId })));
        try
        {
            responses.Count(x => x.StatusCode == HttpStatusCode.Created).Should().Be(1);
            responses.Count(x => x.StatusCode == HttpStatusCode.OK).Should().Be(3);
            var opened = await Task.WhenAll(responses.Select(DataAsync<AgentConversationDto>));
            opened.Select(x => x.ConversationId).Distinct().Should().ContainSingle();
            (await factory.ExecuteDbAsync(db => db.AgentConversations.CountAsync())).Should().Be(1);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [PostgresEndToEndFact]
    public async Task ConcurrentOpenAndExpiredReopen_KeepIdentityAndResetContext()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var request = Message(1); request.ConfirmedState = request.ConfirmedState! with { TasteTargets = [new("spicy", .75)] };
        var turn = await SendAsync(client, conversation.ConversationId, request);
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = turn.Proposal.Version });
        var opens = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync(Path, new { roadmapId = saved.RoadmapId })));
        foreach (var response in opens)
        {
            using (response)
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                (await DataAsync<AgentConversationDto>(response)).ConversationId.Should().Be(conversation.ConversationId);
            }
        }
        var previous = await factory.ExecuteDbAsync(db => db.AgentConversations.AsNoTracking().SingleAsync());
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))));
        using var read = await client.GetAsync($"{Path}/{conversation.ConversationId}");
        await ErrorAsync(read, HttpStatusCode.Gone, "CONVERSATION_EXPIRED");
        using var reopen = await client.PostAsJsonAsync(Path, new { roadmapId = saved.RoadmapId });
        reopen.StatusCode.Should().Be(HttpStatusCode.OK);
        var fresh = await DataAsync<AgentConversationDto>(reopen);
        fresh.ConversationId.Should().Be(conversation.ConversationId); fresh.CurrentProposal.Should().BeNull();
        fresh.State.TasteTargets.Should().BeEmpty(); fresh.State.Preferences.Should().BeNull();
        fresh.State.HistoryStartSequence.Should().Be(previous.LastMessageSequence + 1); fresh.Messages.Should().BeEmpty();
        var next = await SendAsync(client, fresh.ConversationId, Message(fresh.Version));
        next.Proposal!.Version.Should().BeGreaterThan(turn.Proposal.Version);
        (await factory.ExecuteDbAsync(db => db.AgentConversations.CountAsync())).Should().Be(1);
    }

    [PostgresEndToEndFact]
    public async Task ActiveCleanup_PreservesReplayAndCountersWithoutRenewingActivity()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client);
        var oldRequest = Message(1); var first = await SendAsync(client, conversation.ConversationId, oldRequest);
        var recentRequest = Message(first.ConversationVersion); var second = await SendAsync(client, conversation.ConversationId, recentRequest);
        await factory.ExecuteDbAsync(async db =>
        {
            await db.AgentMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
            await db.AgentMessages.Where(x => x.ClientMessageId == oldRequest.ClientMessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{\"attempts\":[]}"));
            await db.AgentProposals.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
        });
        var before = await factory.ExecuteDbAsync(db => db.AgentConversations.AsNoTracking().SingleAsync());
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None);
        var after = await factory.ExecuteDbAsync(db => db.AgentConversations.AsNoTracking().SingleAsync());
        after.UpdatedAt.Should().Be(before.UpdatedAt); after.LastMessageSequence.Should().Be(before.LastMessageSequence);
        after.PurgedClientMessageId.Should().Be(oldRequest.ClientMessageId);
        (await SendAsync(client, conversation.ConversationId, recentRequest)).Replayed.Should().BeTrue();
        using var gone = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", oldRequest);
        await ErrorAsync(gone, HttpStatusCode.Gone, "MESSAGE_EXPIRED");
        var third = await SendAsync(client, conversation.ConversationId, Message(second.ConversationVersion));
        third.Proposal!.Version.Should().BeGreaterThan(second.Proposal!.Version);
        (await factory.ExecuteDbAsync(db => db.AgentConversations.SingleAsync())).LastMessageSequence.Should().BeGreaterThan(before.LastMessageSequence);
    }

    [RecoEndToEndTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealReco_UsesConversationVectorAndCountsOnlyCommittedDishes(bool saveViaChat)
    {
        var settings = new RecommendationSettings { BaseUrl = Environment.GetEnvironmentVariable("ANGI_TEST_RECO_URL")!,
            ApiKey = Environment.GetEnvironmentVariable("ANGI_TEST_RECO_API_KEY")!, TimeoutMilliseconds = 10000 };
        using var recoHttp = new HttpClient { BaseAddress = new Uri(settings.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        var recommendation = new RecommendationService(recoHttp, Options.Create(settings), NullLogger<RecommendationService>.Instance);
        var scenario = await SetupAsync(recommendation); using var host = scenario.Host; using var client = scenario.Client;
        var userId = await factory.ExecuteDbAsync(db => db.Users.Where(x => x.Role.Code == "TRAVELER").Select(x => x.Id).SingleAsync());
        var dishes = await factory.ExecuteDbAsync(db => db.Dishes.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync());
        await using var sql = new NpgsqlConnection(factory.ConnectionString); await sql.OpenAsync();
        await using (var seed = new NpgsqlCommand("""
            DELETE FROM recommendation.recommendation_logs WHERE user_id=@u;
            DELETE FROM recommendation.user_dish_scores WHERE user_id=@u;
            DELETE FROM recommendation.user_preference_vectors WHERE user_id=@u;
            INSERT INTO recommendation.user_preference_vectors(user_id,vec,survey_answer_count) VALUES (@u,'[1,0,0,0,0,0,0,0,0,0,0,0]',6);
            INSERT INTO recommendation.dish_vectors(dish_id,vec,source_hash,status,is_active)
            SELECT d,'[0,0,0,0,0,1,0,0,0,0,0,0]'::vector,repeat('0',64),'ready',true FROM unnest(@d) d
            ON CONFLICT(dish_id) DO UPDATE SET vec=EXCLUDED.vec,status='ready',is_active=true;
            """, sql))
        { seed.Parameters.AddWithValue("u", userId); seed.Parameters.AddWithValue("d", dishes); await seed.ExecuteNonQueryAsync(); }
        var conversation = await OpenAsync(client);
        var request = Message(1); request.ConfirmedState = request.ConfirmedState! with { NumDays = 2,
            TasteTargets = [new("sweet", 0), new("spicy", 1)], Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.5, 12).ToArray() } };
        var preview = await SendAsync(client, conversation.ConversationId, request);
        var snapshot = preview.Proposal!.Snapshot; var requestId = snapshot.Items.First().RequestId;
        requestId.Should().NotBeNull(); snapshot.State.Preferences!.BaselineVector![0].Should().Be(1);
        snapshot.State.Preferences.EffectiveVector![5].Should().Be(1); snapshot.State.Preferences.EffectiveVector[0].Should().Be(0);
        (await factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded))).Should().Be(0);
        using var publicPreview = await client.PostAsJsonAsync("/api/v1/impressions", new { requestId, dishIds = snapshot.Items.Select(x => x.DishId).ToArray(), agentProposalId = preview.Proposal.ProposalId });
        publicPreview.StatusCode.Should().BeOneOf(HttpStatusCode.Accepted, HttpStatusCode.BadRequest);
        await using (var notShown = new NpgsqlCommand("SELECT count(*) FROM recommendation.recommendation_items WHERE request_id=@r AND shown_at IS NOT NULL", sql))
        { notShown.Parameters.AddWithValue("r", requestId!.Value); Convert.ToInt64(await notShown.ExecuteScalarAsync()).Should().Be(0); }
        await factory.ExecuteDbAsync(async db => await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION core.reject_agent_commit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected commit failure'; END $$;
            CREATE TRIGGER reject_agent_commit_test BEFORE UPDATE ON core.agent_proposals
                FOR EACH ROW WHEN (NEW.status = 'committed') EXECUTE FUNCTION core.reject_agent_commit_test();
            """));
        using (var rolledBack = saveViaChat
            ? await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new { content = "lưu lịch trình này" })
            : await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = preview.Proposal.ProposalId, proposalVersion = preview.Proposal.Version }))
            await ErrorAsync(rolledBack, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(0);
            (await db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded)).Should().Be(0);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_agent_commit_test ON core.agent_proposals; DROP FUNCTION core.reject_agent_commit_test()");
        });
        AgentCommitDto commit;
        if (saveViaChat)
        {
            var saved = await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này"); commit = saved.Commit!;
            (await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này")).Commit.Should().BeEquivalentTo(commit);
        }
        else
        {
            var confirm = new ConfirmAgentProposalRequestDto { ProposalId = preview.Proposal.ProposalId, ProposalVersion = preview.Proposal.Version };
            commit = await ConfirmAsync(client, conversation.ConversationId, confirm);
            (await ConfirmAsync(client, conversation.ConversationId, confirm)).Should().BeEquivalentTo(commit);
        }
        var outbox = await factory.ExecuteDbAsync(db => db.OutboxMessages.Where(x => x.EventType == OutboxEventType.AgentImpressionRecorded).ToArrayAsync());
        outbox.Should().ContainSingle(); commit.RoadmapId.Should().BeGreaterThan(0);
        using (var unavailable = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1/"), Timeout = TimeSpan.FromSeconds(1) })
        {
            var failed = await new RecoOutboxSender(unavailable, Options.Create(settings)).SendAsync(outbox[0], CancellationToken.None);
            failed.Outcome.Should().Be(OutboxDeliveryOutcome.Retry);
            (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
        }
        var sender = new RecoOutboxSender(recoHttp, Options.Create(settings));
        (await sender.SendAsync(outbox[0], CancellationToken.None)).Outcome.Should().Be(OutboxDeliveryOutcome.Delivered);
        (await sender.SendAsync(outbox[0], CancellationToken.None)).Outcome.Should().Be(OutboxDeliveryOutcome.Delivered);
        await using var check = new NpgsqlCommand("""
            SELECT p.total_shown,p.total_interactions,p.feedback_count,p.vec::text,
              (SELECT count(*) FROM recommendation.recommendation_items WHERE request_id=@r AND shown_at IS NOT NULL AND agent_proposal_id=@p),
              (SELECT vector_source FROM recommendation.recommendation_logs WHERE request_id=@r),
              (SELECT effective_vector::text FROM recommendation.recommendation_logs WHERE request_id=@r)
            FROM recommendation.user_preference_vectors p WHERE user_id=@u
            """, sql);
        check.Parameters.AddWithValue("r", requestId!.Value); check.Parameters.AddWithValue("p", preview.Proposal.ProposalId); check.Parameters.AddWithValue("u", userId);
        await using var row = await check.ExecuteReaderAsync(); (await row.ReadAsync()).Should().BeTrue();
        row.GetInt64(0).Should().Be(snapshot.Items.Select(x => x.DishId).Distinct().Count()); row.GetInt32(1).Should().Be(6);
        row.GetInt32(2).Should().Be(0); row.GetString(3).Should().Be("[1,0,0,0,0,0,0,0,0,0,0,0]");
        row.GetInt64(4).Should().Be(snapshot.Items.Select(x => x.DishId).Distinct().Count()); row.GetString(5).Should().Be("conversation");
        JsonSerializer.Deserialize<double[]>(row.GetString(6))!.Should().Equal(snapshot.State.Preferences.EffectiveVector);
    }

    /// <summary>Sends only the prompt; no live provider is used by the scripted HTTP test host.</summary>
    private static async Task<AgentTurnDto> SendPromptAsync(HttpClient client, long id, string content)
    {
        using var response = await client.PostAsJsonAsync($"{Path}/{id}/messages", new { content });
        return await DataAsync<AgentTurnDto>(response);
    }

    /// <summary>Models a normal chat plan with tool-proven candidates and no frontend-provided state or command.</summary>
    private static void EnableChatPlanning(Scenario scenario) => scenario.Llm.Override = context =>
    {
        if (context.Exchanges.Count == 0)
            return new([new("chat-candidates", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                State: new AgentState { Province = "Ho Chi Minh City", NumDays = 1, BudgetAmount = 500000 }, TagIds: [], ExcludeTagIds: [], ExcludeDishIds: []), AgentJson.Options))], null);
        var candidates = context.Exchanges[0].Results[0].GetProperty("items");
        return new([], new AgentOutput { Action = "create_proposal", Reply = "Bản xem trước bữa trưa", Items =
            [new AgentItem("chat-lunch", null, 1, "lunch", 0, candidates[0].GetProperty("dishId").GetInt32())] });
    };

    /// <summary>Sends exactly the three fields required by the chat-only frontend contract.</summary>
    private static async Task<AgentTurnDto> SendChatAsync(HttpClient client, long id, long version, string content, long? messageId = null)
    {
        using var response = await client.PostAsJsonAsync($"{Path}/{id}/messages", new
            { clientMessageId = messageId ?? Interlocked.Increment(ref _nextClientMessageId), expectedConversationVersion = version, content });
        return await DataAsync<AgentTurnDto>(response);
    }

    /// <summary>Resets the dedicated database, seeds candidates and authenticates the real Traveler HTTP pipeline.</summary>
    private async Task<Scenario> SetupAsync(IRecommendationService? recommendation = null)
    {
        await factory.ResetDatabaseAsync();
        var seed = await DiscoverySeed.PublicDishAsync(factory);
        await factory.ExecuteDbAsync(async db =>
        {
            for (var index = 0; index < 10; index++) db.Dishes.Add(new Dish { RestaurantId = seed.RestaurantId, Name = $"Agent candidate {index}", Price = 30000 + index * 1000 });
            await db.SaveChangesAsync();
        });
        using var authentication = factory.CreateClient(); var auth = await DiscoverySeed.RegisterAndLoginAsync(factory, authentication, "TRAVELER");
        var llm = new ScriptedLlm();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentLlmService>(); services.AddSingleton<IAgentLlmService>(llm);
            services.RemoveAll<IRouteService>(); services.AddSingleton<IRouteService, EstimatedRoute>();
            if (recommendation is not null)
            {
                services.RemoveAll<IRecommendationService>(); services.AddSingleton(recommendation);
            }
        }));
        var client = host.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new(host, client, llm);
    }

    /// <summary>Creates a fresh, explicit budgeted user message with independently generated idempotency identity.</summary>
    private static SendAgentMessageRequestDto Message(long version) => new()
    {
        ClientMessageId = Interlocked.Increment(ref _nextClientMessageId), ExpectedConversationVersion = version, Content = "Create or replace lunch",
        ConfirmedState = new AgentState { Province = "Ho Chi Minh City", NumDays = 1, BudgetAmount = 500000 }
    };

    /// <summary>Opens a conversation through its actual HTTP contract.</summary>
    private static async Task<AgentConversationDto> OpenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(Path, new CreateAgentConversationRequestDto());
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await DataAsync<AgentConversationDto>(response);
    }
    /// <summary>Reads conversation state through the real HTTP pipeline.</summary>
    private static async Task<AgentConversationDto> GetAsync(HttpClient client, long id)
    { using var response = await client.GetAsync($"{Path}/{id}"); return await DataAsync<AgentConversationDto>(response); }
    /// <summary>Sends one user turn and verifies the canonical success envelope.</summary>
    private static async Task<AgentTurnDto> SendAsync(HttpClient client, long id, SendAgentMessageRequestDto request)
    { using var response = await client.PostAsJsonAsync($"{Path}/{id}/messages", request); return await DataAsync<AgentTurnDto>(response); }
    /// <summary>Confirms a displayed proposal and reads the durable commit result.</summary>
    private static async Task<AgentCommitDto> ConfirmAsync(HttpClient client, long id, ConfirmAgentProposalRequestDto request)
    { using var response = await client.PostAsJsonAsync($"{Path}/{id}/confirm", request); return await DataAsync<AgentCommitDto>(response); }
    /// <summary>Reports failure response details before decoding a successful envelope.</summary>
    private static async Task<T> DataAsync<T>(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(); envelope!.Success.Should().BeTrue(); return envelope.Data!;
    }
    /// <summary>Checks a documented HTTP error code without relying on translated message text.</summary>
    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode.Should().Be(code);
    }

    /// <summary>Owns the deterministic provider boundary and real HTTP host used by one scenario.</summary>
    private sealed record Scenario(WebApplicationFactory<Program> Host, HttpClient Client, ScriptedLlm Llm);

    /// <summary>Produces deterministic tool calls and source-aware plans while leaving validation and persistence real.</summary>
    private sealed class ScriptedLlm : IAgentLlmService
    {
        public int Calls;
        public Func<AgentContext, CancellationToken, Task>? Before { get; set; }
        public Func<AgentContext, AgentLlmStep?>? Override { get; set; }
        public IAgentLlmService? Provider { get; set; }

        /// <summary>Obtains real backend-ranked candidates before selecting a deterministic scoped replacement.</summary>
        public async Task<AgentLlmStep?> RunAsync(AgentContext context, bool repair, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Before is not null) await Before(context, ct);
            if (Provider is not null) return await Provider.RunAsync(context, repair, ct);
            if (Override is not null) return Override(context);
            var target = context.Source?.Items.FirstOrDefault(item => context.Request.SelectedItemKey is { } selected ? item.ItemKey == selected :
                (!context.State.FocusDay.HasValue || item.DayNumber == context.State.FocusDay) && (context.State.FocusMeal is null || item.MealSlot == context.State.FocusMeal));
            if (context.Exchanges.Count == 0)
            {
                var args = JsonSerializer.SerializeToElement(new AgentRecommendationInput(null, context.State.Province,
                    context.Request.Command == "alternative" || target is null ? "full" : "slot",
                    context.Request.Command == "alternative" ? null : target?.DayNumber,
                    context.Request.Command == "alternative" ? null : target?.MealSlot,
                    [], [], target is null ? [] : [target.DishId]), AgentJson.Options);
                return new([new("call-1", "get_recommendation", args)], null);
            }
            var candidates = context.Exchanges[0].Results[0].GetProperty("items");
            if (candidates.GetArrayLength() == 0) return new([], new AgentOutput { Action = "clarify", Reply = "Please relax the filters explicitly" });
            var id = candidates[0].GetProperty("dishId").GetInt32();
            if (target is null || context.Request.Command == "alternative")
                return new([], new AgentOutput { Action = "create_proposal", Reply = "Preview only", Items = Enumerable.Range(1, context.State.NumDays ?? 1)
                    .Select(day => new AgentItem($"d{day}", null, (short)day, "lunch", 0, candidates[(day - 1) % candidates.GetArrayLength()].GetProperty("dishId").GetInt32())).ToArray() });
            return new([], new AgentOutput { Action = "patch_proposal", Reply = "Update preview only", BaseProposalVersion = context.ProposalVersion,
                Scope = new(target.DayNumber, target.MealSlot), Changes = [new("replace", target.ItemKey, target with { DishId = id })] });
        }
    }

    /// <summary>Simulates provider status, timeout and malformed model output without using any network connection.</summary>
    private sealed class FailureProviderHandler(string mode) : HttpMessageHandler
    {
        public int Calls;
        public int RepairCalls;

        /// <summary>Counts real adapter requests and supplies bounded deterministic provider responses.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var last = body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString();
            if (last?.Contains("previous output was invalid", StringComparison.Ordinal) == true) RepairCalls++;
            if (mode == "timeout") await Task.Delay(Timeout.Infinite, ct);
            if (mode != "schema")
            {
                var response = new HttpResponseMessage((HttpStatusCode)int.Parse(mode, System.Globalization.CultureInfo.InvariantCulture))
                    { Content = new StringContent("sensitive-provider-body") };
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(45)); return response;
            }
            var content = Calls == 1 ? "{}" : AgentJson.Write(new AgentOutput { Action = "answer", Reply = "Đã giải thích lịch." });
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } }) };
        }
    }

    /// <summary>Provides deterministic estimated route metrics while exercising the real Agent route adapter.</summary>
    private sealed class EstimatedRoute : IRouteService
    {
        /// <summary>Returns one estimated leg per pair of supplied route points.</summary>
        public Task<RouteCalculationResult> CalculateRouteAsync(RouteCoordinate? origin, IReadOnlyList<RouteCoordinate> destinations, RouteProfile profile, CancellationToken ct) =>
            Task.FromResult(new RouteCalculationResult(Enumerable.Range(0, Math.Max(0, destinations.Count + (origin is null ? 0 : 1) - 1)).Select(_ => new RouteMetric(100, 60)).ToArray(), true));
    }
}
