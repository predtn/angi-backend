using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.UseCases.Agent;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Agent;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.Services.Agent;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using ANGI.IntegrationTests.Fixtures;
using ANGI.WebApi.Common.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANGI.IntegrationTests.WebApi.Controllers.Agent;

/// <summary>Exercises real HTTP, use cases, EF mappings and PostgreSQL with deterministic external AI/route boundaries.</summary>
public sealed class AgentConversationsHttpTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private const string Path = "/api/v1/agent/conversations";
    private static long _nextClientMessageId;

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
        first.RoadmapVersion.Should().Be(1);
        using var edit = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{first.RoadmapId}", new { title = "Manual edit" });
        edit.EnsureSuccessStatusCode();
        var second = await ConfirmAsync(client, conversation.ConversationId, confirm);
        second.Should().BeEquivalentTo(first);
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(1);
            (await db.AgentMessages.CountAsync(x => x.Role == AgentMessageRole.User)).Should().Be(1);
            (await db.AgentProposals.SingleAsync()).Status.Should().Be(AgentProposalStatus.Committed);
            (await db.Roadmaps.SingleAsync()).Version.Should().BeGreaterThan(first.RoadmapVersion);
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
        responses.Should().OnlyContain(x => x.RoadmapId == responses[0].RoadmapId && x.RoadmapVersion == responses[0].RoadmapVersion);
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.CountAsync()).Should().Be(1));
    }

    // TEST-07: Manual roadmap metadata edits invalidate an outstanding update proposal atomically.
    /// <summary>Verifies source revision checks prevent overwriting a newer manual roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task ManualEditBeforeConfirm_ReturnsRoadmapVersionConflict()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var conversation = await OpenAsync(client); var firstTurn = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = firstTurn.Proposal!.ProposalId, ProposalVersion = 1 });
        var current = await GetAsync(client, conversation.ConversationId);
        var next = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        using var manual = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{saved.RoadmapId}", new { title = "Keep this newer title" });
        manual.EnsureSuccessStatusCode();
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = next.Proposal!.ProposalId, proposalVersion = next.Proposal.Version });
        await ErrorAsync(confirm, HttpStatusCode.Conflict, "ROADMAP_VERSION_CONFLICT");
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.SingleAsync()).Title.Should().Be("Keep this newer title"));
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
            (await db.Roadmaps.SingleAsync()).Version.Should().Be(saved.RoadmapVersion);
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
        using var scope = host.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None)).Should().Be(1);
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.AgentConversations.CountAsync()).Should().Be(0); (await db.AgentMessages.CountAsync()).Should().Be(0);
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
        updated.RoadmapId.Should().Be(saved.RoadmapId); updated.RoadmapVersion.Should().Be(saved.RoadmapVersion + 1);
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

    // TEST-19: A second conversation that commits first invalidates another outstanding update proposal for the same roadmap.
    /// <summary>Verifies optimistic aggregate concurrency across independently owned conversations.</summary>
    [PostgresEndToEndFact]
    public async Task TwoConversations_ProtectSharedRoadmapRevision()
    {
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var firstConversation = await OpenAsync(client); var first = await SendAsync(client, firstConversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, firstConversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        var source = await GetAsync(client, firstConversation.ConversationId);
        var a = await SendAsync(client, source.ConversationId, Message(source.Version));
        using var opening = await client.PostAsJsonAsync(Path, new { roadmapId = saved.RoadmapId });
        var secondConversation = await DataAsync<AgentConversationDto>(opening);
        var b = await SendAsync(client, secondConversation.ConversationId, Message(secondConversation.Version));
        await ConfirmAsync(client, secondConversation.ConversationId, new() { ProposalId = b.Proposal!.ProposalId, ProposalVersion = 1 });
        using var conflict = await client.PostAsJsonAsync($"{Path}/{firstConversation.ConversationId}/confirm", new { proposalId = a.Proposal!.ProposalId, proposalVersion = 2 });
        await ErrorAsync(conflict, HttpStatusCode.Conflict, "ROADMAP_VERSION_CONFLICT");
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
        updated.RoadmapVersion.Should().Be(saved.RoadmapVersion + 1); updated.ItemIds.Should().BeEmpty();
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

    /// <summary>Resets the dedicated database, seeds candidates and authenticates the real Traveler HTTP pipeline.</summary>
    private async Task<Scenario> SetupAsync()
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

        /// <summary>Obtains real backend-ranked candidates before selecting a deterministic scoped replacement.</summary>
        public async Task<AgentLlmStep?> RunAsync(AgentContext context, bool repair, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Before is not null) await Before(context, ct);
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

    /// <summary>Provides deterministic estimated route metrics while exercising the real Agent route adapter.</summary>
    private sealed class EstimatedRoute : IRouteService
    {
        /// <summary>Returns one estimated leg per pair of supplied route points.</summary>
        public Task<RouteCalculationResult> CalculateRouteAsync(RouteCoordinate? origin, IReadOnlyList<RouteCoordinate> destinations, RouteProfile profile, CancellationToken ct) =>
            Task.FromResult(new RouteCalculationResult(Enumerable.Range(0, Math.Max(0, destinations.Count + (origin is null ? 0 : 1) - 1)).Select(_ => new RouteMetric(100, 60)).ToArray(), true));
    }
}
