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
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
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
    private static readonly DateOnly TripStart = new(2099, 1, 1);
    /// <summary>The reply the scripted model writes when it reads a wish to save; the backend shows it as written.</summary>
    private const string SaveReply = "Mình lưu lịch này cho bạn nhé.";
    /// <summary>The reply the scripted model writes with its reading of an answer to an open question.</summary>
    private const string AnswerReply = "Mình hiểu rồi nhé.";
    /// <summary>The reply the scripted model writes for a save wish when it is told the plan no longer matches the trip.</summary>
    private const string StalePlanReply = "Lịch hiện tại chưa theo ngân sách mới. Bạn muốn mình cập nhật lịch trước rồi mới lưu không?";
    private static long _nextClientMessageId;

    // TEST-01: A trip prompt with an unscoped taste gets the model's question and no ranking, with no target saved; covers 3-day sour and 5-day spicy prompts.
    /// <summary>Replays conversation 10 prompts through HTTP and persists the question before any planning call.</summary>
    [PostgresEndToEndFact]
    public async Task UnscopedTaste_Conversation10PromptsAskBeforePlanning()
    {
        // STEP-01: Build the prompts collection.
        string[] prompts = [
            "Tôi muốn lên lịch ăn uống Ho Chi Minh City 3 ngày, ngân sách 500000 đồng 1 ngày, tôi thích ăn chua",
            "Tôi muốn lên lịch ăn uống Ho Chi Minh City 5 ngày, ngân sách 500000 đồng 1 ngày, tôi thích ăn cay"
        ];
        // STEP-02: Assert for each prompt, OpenAsync succeeds, SendPromptAsync succeeds, result.Reply contains the expected text, result.Proposal is null and 8 more.
        foreach (var prompt in prompts)
        {
            var vectors = new List<double[]?>();
            var scenario = await SetupAsync(ScopedReco(vectors).Object); using var host = scenario.Host; using var client = scenario.Client;
            var conversation = await OpenAsync(client);
            scenario.Llm.Override = _ => new([], new AgentOutput { Action = "clarify", Clarification = "taste_intensity", Reply = "Bạn thích mức nào, nhẹ hay vừa, cho cả chuyến nhé?" });
            var result = await SendPromptAsync(client, conversation.ConversationId, prompt);
            result.Reply.Should().Be("Bạn thích mức nào, nhẹ hay vừa, cho cả chuyến nhé?");
            result.Proposal.Should().BeNull(); result.Commit.Should().BeNull();
            scenario.Llm.Calls.Should().Be(1); vectors.Should().BeEmpty();
            var restored = await GetAsync(client, conversation.ConversationId);
            restored.State.PendingQuestion.Should().Be(result.Reply);
            restored.State.TasteTargets.Should().BeEmpty(); restored.State.TasteOverrides.Should().BeEmpty();
            (await factory.ExecuteDbAsync(db => db.AgentProposals.CountAsync())).Should().Be(0);
            (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
        }
    }

    // TEST-02: Partial taste answers reach the model turn by turn, each question stays open between turns, and the whole exchange reaches it through history.
    /// <summary>The model keeps asking only what is missing because every earlier answer is in the stored history.</summary>
    [PostgresEndToEndFact]
    public async Task UnscopedTaste_PartialAnswersReachTheModelWithHistory()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and set scenario.Llm.Override to the model's question.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "clarify", Clarification = "taste_scope", Reply = "Bạn muốn cay cho cả chuyến hay ngày nào?" });
        // STEP-02: Obtain conversation through OpenAsync and first through SendPromptAsync.
        var conversation = await OpenAsync(client);
        var first = await SendPromptAsync(client, conversation.ConversationId, "Đi chơi 3 ngày, tôi thích ăn cay");
        // STEP-03: Obtain partial through SendPromptAsync; assert first.Proposal is null.
        first.Proposal.Should().BeNull();
        var partial = await SendPromptAsync(client, conversation.ConversationId, "Vừa");
        // STEP-04: Assert partial.Reply is the model's and partial.Proposal is null.
        partial.Reply.Should().Be("Bạn muốn cay cho cả chuyến hay ngày nào?");
        // STEP-05: Assert scenario.Llm.Calls is 2, GetAsync succeeds and GetAsync().State.PendingQuestion is partial.Reply.
        partial.Proposal.Should().BeNull(); scenario.Llm.Calls.Should().Be(2);
        (await GetAsync(client, conversation.ConversationId)).State.PendingQuestion.Should().Be(partial.Reply);
        // STEP-06: Declare resumed to capture a call argument and set scenario.Llm.Override to a test condition.
        AgentContext? resumed = null;
        scenario.Llm.Override = context =>
        {
            resumed = context;
            return new([], new AgentOutput { Action = "clarify", Reply = "Cần thông tin chuyến đi", Clarification = "trip_details" });
        };
        // STEP-07: Assert SendPromptAsync succeeds, scenario.Llm.Calls is 3, resumed is not null and 2 more.
        await SendPromptAsync(client, conversation.ConversationId, "Toàn chuyến");
        scenario.Llm.Calls.Should().Be(3); resumed.Should().NotBeNull();
        resumed!.History.Where(turn => turn.Role == "user").Select(turn => turn.Content)
            .Should().Contain("Đi chơi 3 ngày, tôi thích ăn cay").And.Contain("Vừa");
        resumed.Request.Content.Should().Be("Toàn chuyến");
    }

    // TEST-03: A stated taste with no trip in sight reaches the model and opens no planning question, in the stored conversation too.
    /// <summary>"Tôi thích ngọt" used to be asked for its scope, duration and budget though no trip was ever mentioned.</summary>
    [PostgresEndToEndFact]
    public async Task StatedTaste_WithNoTrip_GoesToTheModelWithoutPlanningQuestions()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and set scenario.Llm.Override to a social reply.
        var conversation = await OpenAsync(client);
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "chat", Reply = "Bạn thích ngọt à, hay ghê!" });

        // STEP-03: Obtain reply through SendPromptAsync.
        var reply = await SendPromptAsync(client, conversation.ConversationId, "Tôi thích ngọt");

        // STEP-04: Assert reply.Reply is the model's and scenario.Llm.Calls is 1.
        reply.Reply.Should().Be("Bạn thích ngọt à, hay ghê!");
        scenario.Llm.Calls.Should().Be(1);
        // STEP-05: Assert the stored state holds no pending request, question or intake stage.
        var state = (await GetAsync(client, conversation.ConversationId)).State;
        state.PendingPlanningRequest.Should().BeNull();
        state.PendingQuestion.Should().BeNull();
        state.PlanningIntake.Should().BeNull();
    }

    // TEST-04: An unresolved taste keeps the previous preview as current but blocks confirming or chat-saving it.
    /// <summary>An unresolved taste preserves the durable preview without attaching it as a fresh turn result.</summary>
    [PostgresEndToEndFact]
    public async Task UnscopedTaste_KeepsPreviousPreviewButBlocksSaving()
    {
        // STEP-01: Create vectors as List<double[]?>, get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var vectors = new List<double[]?>();
        var scenario = await SetupAsync(ScopedReco(vectors).Object); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and preview through SendAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Derive calls from scenario.Llm.Calls, derive rankingCalls from vectors.Count and set scenario.Llm.Override to the model's question.
        var calls = scenario.Llm.Calls; var rankingCalls = vectors.Count;
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "clarify", Clarification = "taste_scope", Reply = "Bạn muốn cay cho toàn chuyến hay ngày nào?" });
        // STEP-04: Obtain result through SendPromptAsync; assert result.Reply contains the expected text and result.Proposal is null.
        var result = await SendPromptAsync(client, conversation.ConversationId,
            "Tôi muốn lên lịch ăn uống Hà Nội 5 ngày, ngân sách 500000 đồng 1 ngày, tôi thích ăn cay");
        result.Reply.Should().Contain("toàn chuyến");
        result.Proposal.Should().BeNull();
        // STEP-05: Assert GetAsync succeeds, GetAsync().CurrentProposal.ProposalId is preview.Proposal.ProposalId and scenario.Llm.Calls is one more.
        (await GetAsync(client, conversation.ConversationId)).CurrentProposal!.ProposalId.Should().Be(preview.Proposal!.ProposalId);
        // STEP-06: Assert vectors has rankingCalls item(s).
        scenario.Llm.Calls.Should().Be(calls + 1); vectors.Should().HaveCount(rankingCalls);
        // STEP-07: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
        (await factory.ExecuteDbAsync(db => db.AgentProposals.CountAsync())).Should().Be(1);
        // STEP-08: Send POST {Path}/{conversation.ConversationId}/confirm with a body of proposalId and proposalVersion and keep blocked.
        using var blocked = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm",
            new { proposalId = preview.Proposal.ProposalId, proposalVersion = preview.Proposal.Version });
        // STEP-09: Assert ErrorAsync confirms PROPOSAL_NOT_COMMITTABLE, SendPromptAsync succeeds and SendPromptAsync().Commit is null.
        await ErrorAsync(blocked, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
        (await SendPromptAsync(client, conversation.ConversationId, "Lưu lịch trình này")).Commit.Should().BeNull();
        // STEP-10: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
        (await factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded))).Should().Be(0);
    }

    // TEST-05: The trip stays unchanged while taste questions are open, and the four-day daily-budget request the model read across the answers is applied as written, with no staged confirmation and no ranking.
    /// <summary>Verifies over HTTP that open questions keep the trip, and the model's final reading is the new trip with nothing left waiting.</summary>
    [PostgresEndToEndFact]
    public async Task TasteClarification_AppliesFourDayRequestReadAcrossPartialAnswers()
    {
        // STEP-01: Create vectors as List<double[]?>, get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and 2 more.
        var vectors = new List<double[]?>();
        var scenario = await SetupAsync(ScopedReco(vectors).Object); using var host = scenario.Host; using var client = scenario.Client;
        var initial = new AgentState { Province = "Ho Chi Minh City", NumDays = 3, StartDate = TripStart, BudgetAmount = 1500000, BudgetQuote = "1500000 đồng", MealSlots = ["lunch"] };
        scenario.Llm.Override = context => context.Exchanges.Count == 0
            ? new([new("initial", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                State: initial, TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12), AgentJson.Options))], null)
            : new([], new AgentOutput { Action = "create_proposal", Reply = "Preview", Items = Enumerable.Range(1, 3)
                .Select(day => new AgentItem($"day-{day}", null, (short)day, "lunch", 0,
                    context.Exchanges[0].Results[0].GetProperty("items")[0].GetProperty("dishId").GetInt32())).ToArray() });
        // STEP-02: Obtain conversation through OpenAsync and first through SendPromptAsync; assert SendPromptAsync succeeds and first.Proposal is not null.
        var conversation = await OpenAsync(client);
        await SendPromptAsync(client, conversation.ConversationId, "Lập lịch 3 ngày tại Ho Chi Minh City, tổng ngân sách 1500000 đồng.");
        var first = await SendPromptAsync(client, conversation.ConversationId, "không");
        first.Proposal.Should().NotBeNull();
        // STEP-03: Derive modelCalls from scenario.Llm.Calls and derive rankingCalls from vectors.Count.
        var modelCalls = scenario.Llm.Calls; var rankingCalls = vectors.Count;

        // STEP-04: Set scenario.Llm.Override to the model's taste question, obtain waiting through GetAsync; assert SendPromptAsync succeeds.
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "clarify", Clarification = "taste_intensity", Reply = "Bạn thích chua mức nào, cho cả chuyến hay ngày nào?" });
        await SendPromptAsync(client, conversation.ConversationId,
            "Tôi muốn lên lịch ăn uống Ho Chi Minh City 4 ngày, ngân sách 500000 đồng 1 ngày, tôi muốn ăn chua");
        var waiting = await GetAsync(client, conversation.ConversationId);
        // STEP-05: Assert the trip and the preview are unchanged while the question stands.
        waiting.State.NumDays.Should().Be(3);
        waiting.State.BudgetAmount.Should().Be(1500000);
        waiting.CurrentProposal!.ProposalId.Should().Be(first.Proposal!.ProposalId);
        // STEP-06: Assert SendPromptAsync succeeds and the trip is still unchanged.
        await SendPromptAsync(client, conversation.ConversationId, "Nhẹ thôi");
        (await GetAsync(client, conversation.ConversationId)).State.NumDays.Should().Be(3);
        // STEP-07: Assert scenario.Llm.Calls is two more and vectors has rankingCalls item(s).
        scenario.Llm.Calls.Should().Be(modelCalls + 2); vectors.Should().HaveCount(rankingCalls);

        // STEP-08: Set scenario.Llm.Override to the model's reading of the whole request, quoting the words that state the budget.
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Updated",
            State = initial with { NumDays = 4, BudgetAmount = 2000000, BudgetQuote = "500000 đồng 1 ngày", FocusDay = 2, TasteOverrides = [new(2, null, [new("sour", .25, "Nhẹ")])] } });
        // STEP-09: Obtain resolved through SendPromptAsync; assert resolved.Proposal is null and resolved.Reply is the model's reply as written.
        var resolved = await SendPromptAsync(client, conversation.ConversationId, "Chỉ áp dụng ngày 2 thôi");
        resolved.Proposal.Should().BeNull(); resolved.Reply.Should().Be("Updated");
        // STEP-10: Obtain applied through GetAsync; assert the four days, the daily budget and the day-2 taste are applied with nothing staged.
        var applied = await GetAsync(client, conversation.ConversationId);
        applied.State.NumDays.Should().Be(4); applied.State.BudgetAmount.Should().Be(2000000);
        applied.State.PendingStateChange.Should().BeNull(); applied.State.PendingPlanningRequest.Should().BeNull();
        applied.State.TasteOverrides.Single().Targets.Single().Target.Should().Be(.25);
        // STEP-11: Assert vectors has rankingCalls item(s), so reading the answer ranked nothing.
        vectors.Should().HaveCount(rankingCalls);
        // STEP-12: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-06: Day-specific taste and catalog requests rank with separate vectors and commit per-item reco provenance and impressions.
    /// <summary>Checks scoped ranking end to end, including impression outbox rows only after commit.</summary>
    [PostgresEndToEndFact]
    public async Task ScopedPlanning_ThreeDaysKeepSeparateVectorsAndCommitProvenance()
    {
        // STEP-01: Create vectors as List<double[]?>, get reco from ScopedReco, get scenario from SetupAsync, derive host from scenario.Host and 4 more.
        var vectors = new List<double[]?>();
        var reco = ScopedReco(vectors);
        var scenario = await SetupAsync(reco.Object); using var host = scenario.Host; using var client = scenario.Client;
        var specialty = await factory.ExecuteDbAsync(async db =>
        {
            var tag = new Tag { Code = "scoped_specialty", Name = "Đặc sản khu vực", TagType = TagType.Category };
            var dish = await db.Dishes.OrderByDescending(x => x.Id).FirstAsync();
            dish.DishTags.Add(new() { Tag = tag }); await db.SaveChangesAsync(); return tag.Id;
        });
        var state = new AgentState { Province = "Ho Chi Minh City", NumDays = 3, StartDate = TripStart, BudgetAmount = 500000, BudgetQuote = "500000 đồng",
            MealSlots = ["lunch"], TasteOverrides = [new(1, null, [new("spicy", 1, "cay rất nhiều")])],
            MealRequests = [new(2, null, null, null, [specialty]), new(3, null, null, null, [], true)] };
        scenario.Llm.Override = context =>
        {
            // Ngân sách phải có nguyên văn người dùng: tin nhắn trước đó của kịch bản đã nêu nó.
            context.History.Add(new("user", "ngân sách 500000 đồng"));
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
            return new([], new AgentOutput { Action = "create_proposal", Reply = "Lịch 3 ngày của bạn đây nhé.", Items = items.ToArray() });
        };
        // STEP-02: Obtain conversation through OpenAsync and preview through SendPromptAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendPromptAsync(client, conversation.ConversationId, "Lập lịch 3 ngày tại Ho Chi Minh City, ngân sách 500000 đồng: ngày 1 cay rất nhiều, ngày 2 đặc sản, ngày 3 khác món trong lịch, chỉ bữa trưa.");
        // STEP-03: Assert preview.Proposal is not null, preview.Proposal.Snapshot.Items has 3 item(s) and preview.Reply is the model's reply as written.
        preview.Proposal.Should().NotBeNull(); preview.Proposal!.Snapshot.Items.Should().HaveCount(3);
        preview.Reply.Should().Be("Lịch 3 ngày của bạn đây nhé.");
        // STEP-04: Assert vectors has 3 item(s), vectors[0][5] is 1, vectors[1][5] is .25 and vectors[2][5] is .25.
        vectors.Should().HaveCount(3); vectors[0]![5].Should().Be(1); vectors[1]![5].Should().Be(.25); vectors[2]![5].Should().Be(.25);
        // STEP-05: Obtain restored through GetAsync; assert restored.State.TasteOverrides has exactly one matching item and restored.State.MealRequests has 2 item(s).
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.State.TasteOverrides.Should().ContainSingle(); restored.State.MealRequests.Should().HaveCount(2);
        // STEP-06: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded))).Should().Be(0);
        // STEP-07: Obtain commit through ConfirmAsync.
        var commit = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = preview.Proposal.ProposalId, ProposalVersion = preview.Proposal.Version });
        // STEP-08: Assert in the database factory.ExecuteDbAsync(filtered) is 3.
        (await factory.ExecuteDbAsync(db => db.RoadmapItems.CountAsync(x => x.RoadmapDay.RoadmapId == commit.RoadmapId && x.RecoRequestId != null))).Should().Be(3);
        (await factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded))).Should().Be(3);
    }

    // TEST-07: A full replan replaces a twelve-item fallback preview with nine personalized items that keep the old stable keys.
    /// <summary>Verifies per-meal candidates and builder key reuse across a full-scope update.</summary>
    [PostgresEndToEndFact]
    public async Task FullSelection_UpdatesOldTwelveItemFallbackToNineRecoItems()
    {
        // STEP-01: Define recovering as false and get reco from ScopedReco.
        var recovering = false;
        var reco = ScopedReco([]);
        // STEP-02: Stub reco.GetPreferencesAsync.
        reco.Setup(x => x.GetPreferencesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, CancellationToken _) => recovering ? new UserPreferenceSnapshot(id, 1, null, null) : null);
        // STEP-03: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client, get drinkTag from factory.ExecuteDbAsync and 2 more.
        var scenario = await SetupAsync(reco.Object); using var host = scenario.Host; using var client = scenario.Client;
        var drinkTag = await factory.ExecuteDbAsync(async db =>
        {
            var tag = new Tag { Code = "submission_drink", Name = "Đồ uống", TagType = TagType.Category };
            foreach (var dish in await db.Dishes.ToArrayAsync()) dish.DishTags.Add(new() { Tag = tag });
            await db.SaveChangesAsync(); return tag.Id;
        });
        var initialState = new AgentState { Province = "Ho Chi Minh City", NumDays = 3, StartDate = TripStart, BudgetAmount = 1500000, BudgetQuote = "1500000 đồng",
            MealSlots = ["breakfast", "lunch", "dinner"] };
        scenario.Llm.Override = context =>
        {
            if (context.Exchanges.Count == 0)
            {
                // Ngân sách phải có nguyên văn người dùng: tin nhắn trước đó của kịch bản đã nêu nó.
                context.History.Add(new("user", "ngân sách 1500000 đồng"));
                var state = recovering ? initialState with { TasteOverrides = [new(1, null, [new("sweet", .5, "ngọt vừa")])],
                    MealRequests = [new(2, null, null, null, [drinkTag])] } : initialState;
                return new(Enumerable.Range(1, 3).Select(day => new AgentToolCall($"day-{day}", "get_recommendation",
                    JsonSerializer.SerializeToElement(new AgentRecommendationInput(State: day == 1 ? state : null,
                        Scope: "day", DayNumber: (short)day, TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 12), AgentJson.Options))).ToArray(), null);
            }
            var items = new List<AgentItem>();
            for (short day = 1; day <= 3; day++)
            {
                // A day search ranks each declared meal separately; use only candidates issued for that exact meal.
                var candidates = context.Exchanges[0].Results[day - 1].GetProperty("items");
                for (var meal = 0; meal < 3; meal++)
                    items.Add(new($"{(recovering ? "new" : "old")}-{day}-{meal}", null, day, initialState.MealSlots[meal], 0,
                        PickForMeal(candidates, initialState.MealSlots[meal], 0)));
                if (!recovering) items.Add(new($"old-side-{day}", null, day, "lunch", 1, PickForMeal(candidates, "lunch", 1)));
            }
            return new([], new AgentOutput { Action = "create_proposal", Reply = "Selected items", Items = items.ToArray() });
        };
        // STEP-04: Obtain conversation through OpenAsync and first through SendPromptAsync; assert SendPromptAsync succeeds, first.Proposal is not null and 1 more.
        var conversation = await OpenAsync(client);
        await SendPromptAsync(client, conversation.ConversationId, "Lập lịch 3 ngày tại Ho Chi Minh City, ngân sách 1500000 đồng.");
        var first = await SendPromptAsync(client, conversation.ConversationId, "không");
        first.Proposal.Should().NotBeNull(AgentJson.Write(new { first.Reply, first.Validation }));
        first.Proposal!.Snapshot.Items.Should().HaveCount(12).And.OnlyContain(item => item.RequestId == null);
        // STEP-05: Set recovering to true.
        recovering = true;

        // STEP-06: Obtain updated through SendPromptAsync; assert updated.Proposal is not null, updated.Proposal.Version is first.Proposal.Version + 1 and 1 more.
        var updated = await SendPromptAsync(client, conversation.ConversationId,
            "tôi muốn ngày 1 ăn đồ ngọt vừa, ngày 2 chỉ có đồ uống thôi, ngày 3 thì theo bạn quyết định");

        updated.Proposal.Should().NotBeNull();
        updated.Proposal!.Version.Should().Be(first.Proposal.Version + 1);
        updated.Proposal.Snapshot.Items.Should().HaveCount(9).And.OnlyContain(item => item.RequestId != null);
        // STEP-07: Assert updated.Proposal.Snapshot.Items.Select(filtered) only holds items matching the condition and updated.Proposal.Validation.Errors is empty.
        updated.Proposal.Snapshot.Items.Select(item => item.ItemKey).Should().OnlyContain(key => key.StartsWith("old-"));
        updated.Proposal.Validation.Errors.Should().BeEmpty();
        // STEP-08: Assert updated.Proposal.Validation.Warnings holds no matching item and updated.Reply does not contain AgentReplyBuilder.RatingFallbackNote.
        updated.Proposal.Validation.Warnings.Should().NotContain(warning => warning.Contains("đánh giá"));
        updated.Reply.Should().NotContain(AgentReplyBuilder.RatingFallbackNote).And.NotContain("chưa hoàn tất");
        // STEP-09: Assert GetAsync succeeds and GetAsync().CurrentProposal.ProposalId is updated.Proposal.ProposalId.
        (await GetAsync(client, conversation.ConversationId)).CurrentProposal!.ProposalId.Should().Be(updated.Proposal.ProposalId);
        // STEP-10: Assert in the database factory.ExecuteDbAsync(filtered).Status is AgentProposalStatus.Superseded and 1 more.
        (await factory.ExecuteDbAsync(db => db.AgentProposals.SingleAsync(p => p.Id == first.Proposal.ProposalId)))
            .Status.Should().Be(AgentProposalStatus.Superseded);
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-08: A named dish missing from the catalog yields no proposal and no roadmap write; the model's not-found question is shown as written.
    /// <summary>Prevents silent substitution for explicit dish requests.</summary>
    [PostgresEndToEndFact]
    public async Task ScopedPlanning_MissingNamedDishAsksInsteadOfSubstituting()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and set scenario.Llm.Override to a test condition.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        scenario.Llm.Override = context => context.Exchanges.Count == 0
            ? new([new("named", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                State: new() { Province = "Ho Chi Minh City", NumDays = 1, StartDate = TripStart, BudgetAmount = 500000, BudgetQuote = "500000 đồng",
                    MealRequests = [new(null, null, "Món tuyệt đối không có trong catalog", null, [])] },
                TagIds: [], ExcludeTagIds: [], ExcludeDishIds: []), AgentJson.Options))], null)
            : new([], new AgentOutput { Action = "clarify", Reply = "Mình chưa tìm thấy món này trên ANGI. Bạn muốn chọn món khác không?", Clarification = "catalog_choice" });
        // STEP-02: Obtain conversation through OpenAsync and result through SendPromptAsync; assert result.Proposal is null and result.Reply is the model's question as written.
        var conversation = await OpenAsync(client);
        var result = await SendPromptAsync(client, conversation.ConversationId, "Thêm món tuyệt đối không có trong catalog vào lịch, ngân sách 500000 đồng.");
        result.Proposal.Should().BeNull(); result.Reply.Should().Be("Mình chưa tìm thấy món này trên ANGI. Bạn muốn chọn món khác không?");
        // STEP-03: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-09: Re-evaluating a saved dish under a new taste keeps the roadmap item and replaces its reco request ID on save.
    /// <summary>Ensures reranked unchanged dishes refresh provenance without recreating roadmap items.</summary>
    [PostgresEndToEndFact]
    public async Task ScopedPlanning_ReevaluatedSavedDishRetainsItemAndUpdatesRecoRequest()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(ScopedReco([]).Object); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, initial through SendAsync and first through ConfirmAsync.
        var conversation = await OpenAsync(client);
        var initial = await SendAsync(client, conversation.ConversationId, Message(1));
        var first = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = initial.Proposal!.ProposalId, ProposalVersion = initial.Proposal.Version });
        // STEP-03: Get oldItem from factory.ExecuteDbAsync and set scenario.Llm.Override to a test condition.
        var oldItem = await factory.ExecuteDbAsync(db => db.RoadmapItems.AsNoTracking().SingleAsync());
        scenario.Llm.Override = context =>
        {
            var target = context.Source!.Items.Single();
            if (context.Exchanges.Count == 0)
                return new([new("refresh", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                    State: context.State with { Preferences = null, SourceRoadmapUpdatedAt = null, HistoryStartSequence = null,
                        TasteOverrides = [new(1, null, [new("spicy", 1, "cay rất nhiều")])] }, Scope: "slot", DayNumber: 1, MealSlot: "lunch",
                    TagIds: [], ExcludeTagIds: [], ExcludeDishIds: []), AgentJson.Options))], null);
            return new([], new AgentOutput { Action = "patch_proposal", Reply = "Đã đổi khẩu vị", BaseProposalVersion = context.ProposalVersion,
                Scope = new(1, "lunch"), Changes = [new("replace", target.ItemKey, target)] });
        };
        // STEP-04: Obtain updated through SendPromptAsync and 1 more; assert updated.Proposal is not null and 3 more.
        var updated = await SendPromptAsync(client, conversation.ConversationId, "Đánh giá lại ngày 1 theo khẩu vị cay rất nhiều, giữ món nếu vẫn phù hợp.");
        updated.Proposal.Should().NotBeNull(); updated.Proposal!.Snapshot.Items[0].RequestId.Should().NotBeNull();
        updated.Proposal.Snapshot.Items[0].RequestId!.Value.Should().NotBe(oldItem.RecoRequestId!.Value);
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = updated.Proposal.ProposalId, ProposalVersion = updated.Proposal.Version });
        saved.RoadmapId.Should().Be(first.RoadmapId);
        // STEP-05: Get current from factory.ExecuteDbAsync.
        var current = await factory.ExecuteDbAsync(db => db.RoadmapItems.AsNoTracking().SingleAsync());
        // STEP-06: Assert current.Id is oldItem.Id, current.DishId is oldItem.DishId and current.RecoRequestId is Proposal.Snapshot.Items[0].RequestId.
        current.Id.Should().Be(oldItem.Id); current.DishId.Should().Be(oldItem.DishId);
        current.RecoRequestId.Should().Be(updated.Proposal.Snapshot.Items[0].RequestId);
    }

    /// <summary>Returns the n-th candidate authorized for a meal via allowedMealSlots, mirroring how a model must pick per meal.</summary>
    private static int PickForMeal(JsonElement candidates, string meal, int skip) => candidates.EnumerateArray()
        .Where(candidate => candidate.GetProperty("allowedMealSlots").EnumerateArray().Any(slot => slot.GetString() == meal))
        .Skip(skip).First().GetProperty("dishId").GetInt32();

    /// <summary>Fakes conversation ranking in candidate order and records each request vector so scoped tastes can be asserted.</summary>
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

    // TEST-10: Create an unsaved preview, replay its message, confirm once, and replay the original commit after later edits.
    /// <summary>Verifies the complete Agent create lifecycle and both durable idempotency contracts.</summary>
    [PostgresEndToEndFact]
    public async Task PreviewReplayAndConfirm_ProducesOneRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        var conversation = await OpenAsync(client);
        // STEP-03: Get request from Message.
        var request = Message(conversation.Version);
        // STEP-04: Obtain turn through SendAsync; assert turn.Proposal is not null and turn.Proposal.Status is "ready".
        var turn = await SendAsync(client, conversation.ConversationId, request);
        turn.Proposal.Should().NotBeNull(); turn.Proposal!.Status.Should().Be("ready");
        // STEP-05: Assert in the database db.Roadmaps.CountAsync() is 0.
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.CountAsync()).Should().Be(0));
        // STEP-06: Derive calls from scenario.Llm.Calls.
        var calls = scenario.Llm.Calls;
        // STEP-07: Obtain replay through SendAsync; assert replay.Replayed is true, replay.Proposal.ProposalId is turn.Proposal.ProposalId and 1 more.
        var replay = await SendAsync(client, conversation.ConversationId, request);
        replay.Replayed.Should().BeTrue(); replay.Proposal!.ProposalId.Should().Be(turn.Proposal.ProposalId);
        scenario.Llm.Calls.Should().Be(calls);
        // STEP-08: Create confirm as ConfirmAgentProposalRequestDto.
        var confirm = new ConfirmAgentProposalRequestDto { ProposalId = turn.Proposal.ProposalId, ProposalVersion = turn.Proposal.Version };
        // STEP-09: Obtain first through ConfirmAsync; assert first.RoadmapUpdatedAt.Kind is DateTimeKind.Utc.
        var first = await ConfirmAsync(client, conversation.ConversationId, confirm);
        first.RoadmapUpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        // STEP-10: Send PATCH /api/v1/roadmaps/{first.RoadmapId} with a body of title and keep edit.
        using var edit = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{first.RoadmapId}", new { title = "Manual edit" });
        // STEP-11: Ensure edit succeeded.
        edit.EnsureSuccessStatusCode();
        // STEP-12: Obtain second through ConfirmAsync; assert second is equivalent to first.
        var second = await ConfirmAsync(client, conversation.ConversationId, confirm);
        second.Should().BeEquivalentTo(first);
        // STEP-13: Assert in the database db.Roadmaps.CountAsync() is 1, db.AgentMessages.CountAsync(filtered) is 1 and 2 more.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(1);
            (await db.AgentMessages.CountAsync(x => x.Role == AgentMessageRole.User)).Should().Be(1);
            (await db.AgentProposals.SingleAsync()).Status.Should().Be(AgentProposalStatus.Committed);
            (await db.Roadmaps.SingleAsync()).UpdatedAt.Should().BeAfter(first.RoadmapUpdatedAt);
            // The province settled in the conversation became the roadmap's destination.
            (await db.Roadmaps.SingleAsync()).DestinationProvince.Should().Be("Ho Chi Minh City");
        });
    }

    // TEST-11: Hide another traveler's conversation and roadmap from reads, tools and confirmation.
    /// <summary>Verifies ownership before any LLM or tool execution.</summary>
    [PostgresEndToEndFact]
    public async Task ForeignResources_AreConcealed()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, turn through SendAsync and committed through ConfirmAsync.
        var conversation = await OpenAsync(client);
        var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var committed = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = 1 });
        // STEP-03: Derive calls from scenario.Llm.Calls, create stranger through factory.CreateClient, create auth through DiscoverySeed.RegisterAndLoginAsync and 1 more.
        var calls = scenario.Llm.Calls;
        using var stranger = factory.CreateClient();
        var auth = await DiscoverySeed.RegisterAndLoginAsync(factory, stranger, "TRAVELER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        // STEP-04: Send GET {Path}/{conversation.ConversationId} and keep get.
        using var get = await client.GetAsync($"{Path}/{conversation.ConversationId}");
        // STEP-05: Assert ErrorAsync confirms CONVERSATION_NOT_FOUND.
        await ErrorAsync(get, HttpStatusCode.NotFound, "CONVERSATION_NOT_FOUND");
        // STEP-06: Send POST {Path}/{conversation.ConversationId}/messages with a request body and keep send.
        using var send = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(1));
        // STEP-07: Assert ErrorAsync confirms CONVERSATION_NOT_FOUND.
        await ErrorAsync(send, HttpStatusCode.NotFound, "CONVERSATION_NOT_FOUND");
        // STEP-08: Send POST {Path}/{conversation.ConversationId}/confirm with a body of proposalId and proposalVersion and keep confirm.
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = turn.Proposal.ProposalId, proposalVersion = 1 });
        // STEP-09: Assert ErrorAsync confirms CONVERSATION_NOT_FOUND.
        await ErrorAsync(confirm, HttpStatusCode.NotFound, "CONVERSATION_NOT_FOUND");
        // STEP-10: Send POST Path with a body of roadmapId and keep open.
        using var open = await client.PostAsJsonAsync(Path, new { roadmapId = committed.RoadmapId });
        // STEP-11: Assert ErrorAsync confirms ROADMAP_NOT_FOUND and scenario.Llm.Calls is calls.
        await ErrorAsync(open, HttpStatusCode.NotFound, "ROADMAP_NOT_FOUND");
        scenario.Llm.Calls.Should().Be(calls);
    }

    // TEST-12: Concurrent duplicate messages return processing while a distinct message receives a busy conflict.
    /// <summary>Verifies a single live attempt per conversation without holding a transaction across the LLM wait.</summary>
    [PostgresEndToEndFact]
    public async Task ConcurrentMessages_EnforceLeaseAndReplay()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        // STEP-03: Get request from Message, create entered as TaskCompletionSource, create release as TaskCompletionSource and 1 more.
        var conversation = await OpenAsync(client); var request = Message(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Llm.Before = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        // STEP-04: Send POST {Path}/{conversation.ConversationId}/messages with request and keep firstTask.
        var firstTask = client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        // STEP-05: Call entered.Task.WaitAsync.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // STEP-06: Send POST {Path}/{conversation.ConversationId}/messages with request and keep duplicate.
            using var duplicate = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
            // STEP-07: Assert duplicate.StatusCode is HttpStatusCode.Accepted.
            duplicate.StatusCode.Should().Be(HttpStatusCode.Accepted);
            // STEP-08: Send POST {Path}/{conversation.ConversationId}/messages with a request body and keep other.
            using var other = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(2));
            // STEP-09: Assert ErrorAsync confirms CONVERSATION_BUSY.
            await ErrorAsync(other, HttpStatusCode.Conflict, "CONVERSATION_BUSY");
        }
        // STEP-10: Clean up: call release.TrySetResult.
        finally { release.TrySetResult(); }
        // STEP-11: Derive first from firstTask and ensure first succeeded.
        using var first = await firstTask; first.EnsureSuccessStatusCode();
        // STEP-12: Assert scenario.Llm.Calls is 2, SendAsync succeeds and SendAsync().Replayed is true.
        scenario.Llm.Calls.Should().Be(2);
        (await SendAsync(client, conversation.ConversationId, request)).Replayed.Should().BeTrue();
    }

    // TEST-13: Reusing a client message ID for changed text or selections is rejected before provider work.
    /// <summary>Verifies that request hashing includes business choices and excludes expected transport version.</summary>
    [PostgresEndToEndFact]
    public async Task ReusedMessageIdentity_RejectsChangedBusinessContent()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        // STEP-03: Get request from Message.
        var conversation = await OpenAsync(client); var request = Message(1);
        // STEP-04: Assert SendAsync succeeds.
        await SendAsync(client, conversation.ConversationId, request);
        // STEP-05: Set request.ExpectedConversationVersion to 999.
        request.ExpectedConversationVersion = 999;
        // STEP-06: Assert SendAsync succeeds and SendAsync().Replayed is true.
        (await SendAsync(client, conversation.ConversationId, request)).Replayed.Should().BeTrue();
        // STEP-07: Set request.Content to "Different trip".
        request.Content = "Different trip";
        // STEP-08: Send POST {Path}/{conversation.ConversationId}/messages with request and keep changed.
        using var changed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        // STEP-09: Assert ErrorAsync confirms MESSAGE_ID_REUSED.
        await ErrorAsync(changed, HttpStatusCode.Conflict, "MESSAGE_ID_REUSED");
    }

    // TEST-14: An expired worker cannot save after a new retry acquires the same logical turn.
    /// <summary>Verifies attempt fencing against late results after lease takeover.</summary>
    [PostgresEndToEndFact]
    public async Task ExpiredAttempt_CannotOverwriteNewRetry()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        // STEP-03: Get request from Message, create entered as TaskCompletionSource, create release as TaskCompletionSource, define firstCall as 0 and 1 more.
        var conversation = await OpenAsync(client); var request = Message(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCall = 0;
        scenario.Llm.Before = async (_, ct) => { if (Interlocked.Increment(ref firstCall) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        // STEP-04: Send POST {Path}/{conversation.ConversationId}/messages with request and keep old.
        var old = client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        // STEP-05: Call entered.Task.WaitAsync and seed or update rows directly in the test database.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await factory.ExecuteDbAsync(async db => await db.AgentConversations.Where(x => x.Id == conversation.ConversationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProcessingUntil, DateTime.UtcNow.AddMinutes(-1))));
            // STEP-06: Obtain current through GetAsync.
            // STEP-07: Set request.ExpectedConversationVersion to current.Version.
            var current = await GetAsync(client, conversation.ConversationId); request.ExpectedConversationVersion = current.Version;
            // STEP-08: Obtain retried through SendAsync; assert retried.Proposal is not null.
            var retried = await SendAsync(client, conversation.ConversationId, request);
            retried.Proposal.Should().NotBeNull();
        }
        // STEP-09: Clean up: call release.TrySetResult.
        finally { release.TrySetResult(); }
        // STEP-10: Derive stale from old.
        using var stale = await old;
        // STEP-11: Assert ErrorAsync confirms CONVERSATION_VERSION_CONFLICT.
        await ErrorAsync(stale, HttpStatusCode.Conflict, "CONVERSATION_VERSION_CONFLICT");
        // STEP-12: Assert in the database db.AgentProposals.CountAsync() is 1 and SingleAsync(filtered).ProcessingStatus is AgentProcessingStatus.Completed.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.AgentProposals.CountAsync()).Should().Be(1);
            (await db.AgentMessages.SingleAsync(x => x.Role == AgentMessageRole.User)).ProcessingStatus.Should().Be(AgentProcessingStatus.Completed);
        });
    }

    // TEST-15: Concurrent confirmation creates one roadmap and returns the same original committed result.
    /// <summary>Verifies database-serialized confirmation without duplicate itinerary items.</summary>
    [PostgresEndToEndFact]
    public async Task ConcurrentConfirm_HasOneBusinessEffect()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and turn through SendAsync.
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Create request as ConfirmAgentProposalRequestDto.
        var request = new ConfirmAgentProposalRequestDto { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = turn.Proposal.Version };
        // STEP-04: Assert ConfirmAsync succeeds and responses only holds items matching the condition.
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConfirmAsync(client, conversation.ConversationId, request)));
        responses.Should().OnlyContain(x => x.RoadmapId == responses[0].RoadmapId && x.RoadmapUpdatedAt == responses[0].RoadmapUpdatedAt);
        // STEP-05: Assert in the database db.Roadmaps.CountAsync() is 1.
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.CountAsync()).Should().Be(1));
    }

    // TEST-16: Manual roadmap metadata edits invalidate an outstanding update proposal atomically.
    /// <summary>Verifies source revision checks prevent overwriting a newer manual roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task ManualEditBeforeConfirm_ReturnsRoadmapUpdatedAtConflict()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, firstTurn through SendAsync, saved through ConfirmAsync, current through GetAsync and 1 more.
        var conversation = await OpenAsync(client); var firstTurn = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = firstTurn.Proposal!.ProposalId, ProposalVersion = 1 });
        var current = await GetAsync(client, conversation.ConversationId);
        var next = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        // STEP-03: Send PATCH /api/v1/roadmaps/{saved.RoadmapId} with a body of title and keep manual.
        using var manual = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{saved.RoadmapId}", new { title = "Keep this newer title" });
        // STEP-04: Ensure manual succeeded.
        manual.EnsureSuccessStatusCode();
        // STEP-05: Send POST {Path}/{conversation.ConversationId}/confirm with a body of proposalId and proposalVersion and keep confirm.
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = next.Proposal!.ProposalId, proposalVersion = next.Proposal.Version });
        // STEP-06: Assert ErrorAsync confirms ROADMAP_UPDATED_AT_CONFLICT.
        await ErrorAsync(confirm, HttpStatusCode.Conflict, "ROADMAP_UPDATED_AT_CONFLICT");
        // STEP-07: Assert in the database db.Roadmaps.SingleAsync().Title is "Keep this newer title".
        await factory.ExecuteDbAsync(async db => (await db.Roadmaps.SingleAsync()).Title.Should().Be("Keep this newer title"));
    }

    // TEST-17: Deleting a roadmap item after a preview makes confirmation fail with ROADMAP_UPDATED_AT_CONFLICT and writes nothing.
    /// <summary>Protects saved roadmaps from being overwritten by a preview built on stale data.</summary>
    [PostgresEndToEndFact]
    public async Task ItemDeletedAfterPreview_ConfirmRejectsStaleTimestamp()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and 4 more; assert preview.Proposal.Snapshot.BaseRoadmapUpdatedAt is saved.RoadmapUpdatedAt.
        var conversation = await OpenAsync(client);
        var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        var current = await GetAsync(client, conversation.ConversationId);
        var preview = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        preview.Proposal!.Snapshot.BaseRoadmapUpdatedAt.Should().Be(saved.RoadmapUpdatedAt);
        // STEP-03: Send DELETE /api/v1/roadmaps/{saved.RoadmapId}/items/{saved.ItemIds.Values.First()} and keep deleted.
        using var deleted = await client.DeleteAsync($"/api/v1/roadmaps/{saved.RoadmapId}/items/{saved.ItemIds.Values.First()}");
        // STEP-04: Ensure deleted succeeded.
        deleted.EnsureSuccessStatusCode();
        // STEP-05: Send POST {Path}/{conversation.ConversationId}/confirm with a body of proposalId and proposalVersion and keep confirm.
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm",
            new { proposalId = preview.Proposal.ProposalId, proposalVersion = preview.Proposal.Version });
        // STEP-06: Assert ErrorAsync confirms ROADMAP_UPDATED_AT_CONFLICT.
        await ErrorAsync(confirm, HttpStatusCode.Conflict, "ROADMAP_UPDATED_AT_CONFLICT");
        // STEP-07: Assert in the database db.RoadmapItems.CountAsync() is 0.
        await factory.ExecuteDbAsync(async db => (await db.RoadmapItems.CountAsync()).Should().Be(0));
    }

    // TEST-18: Changed price and sold-out dishes block confirmation while preserving the ready proposal.
    /// <summary>Verifies current catalog data is rechecked rather than trusting preview prices.</summary>
    [PostgresEndToEndFact]
    public async Task CatalogChangesBeforeConfirm_BlockWithoutWritingRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and turn through SendAsync.
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Derive dishId from Proposal.Snapshot.Items[0].DishId and seed or update rows directly in the test database.
        var dishId = turn.Proposal!.Snapshot.Items[0].DishId;
        await factory.ExecuteDbAsync(async db => await db.Dishes.Where(x => x.Id == dishId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Price, 900000m)));
        // STEP-04: Send POST {Path}/{conversation.ConversationId}/confirm with a body of proposalId and proposalVersion and keep changed.
        using var changed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = turn.Proposal.ProposalId, proposalVersion = 1 });
        // STEP-05: Assert ErrorAsync confirms PROPOSAL_NOT_COMMITTABLE.
        await ErrorAsync(changed, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
        // STEP-06: Assert in the database db.Roadmaps.CountAsync() is 0 and db.AgentProposals.SingleAsync().Status is AgentProposalStatus.Ready.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(0);
            (await db.AgentProposals.SingleAsync()).Status.Should().Be(AgentProposalStatus.Ready);
        });
    }

    // TEST-19: Discard an update without touching the official roadmap, then reset the draft safely.
    /// <summary>Verifies explicit non-destructive message commands and source-proposal lifecycle.</summary>
    [PostgresEndToEndFact]
    public async Task DiscardAndReset_KeepOfficialRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, turn through SendAsync, saved through ConfirmAsync, current through GetAsync and 1 more.
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = 1 });
        var current = await GetAsync(client, conversation.ConversationId);
        var updated = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        // STEP-03: Get discard from Message, set discard.Command to "discard" and set discard.ConfirmedState to null.
        var discard = Message(updated.ConversationVersion); discard.Command = "discard"; discard.ConfirmedState = null;
        // STEP-04: Obtain discarded through SendAsync; assert discarded.Proposal is null.
        var discarded = await SendAsync(client, conversation.ConversationId, discard); discarded.Proposal.Should().BeNull();
        // STEP-05: Get reset from Message, set reset.Command to "reset" and set reset.ConfirmedState to null.
        var reset = Message(discarded.ConversationVersion); reset.Command = "reset"; reset.ConfirmedState = null;
        // STEP-06: Assert SendAsync succeeds.
        await SendAsync(client, conversation.ConversationId, reset);
        // STEP-07: Assert in the database db.Roadmaps.SingleAsync().UpdatedAt is saved.RoadmapUpdatedAt and 2 more.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.SingleAsync()).UpdatedAt.Should().Be(saved.RoadmapUpdatedAt);
            (await db.AgentProposals.SingleAsync(x => x.Id == updated.Proposal!.ProposalId)).Status.Should().Be(AgentProposalStatus.Discarded);
            (await db.AgentProposals.SingleAsync(x => x.Id == turn.Proposal!.ProposalId)).Status.Should().Be(AgentProposalStatus.Committed);
        });
    }

    // TEST-20: A hallucinated dish ID never reaches the draft: after one repair the meal keeps its dish from the current plan and nothing is saved; a provider failure keeps the current proposal.
    /// <summary>Verifies the chat turn keeps an unknown dish out of the draft (user decision 2026-11-12), and an availability failure stores nothing.</summary>
    [PostgresEndToEndFact]
    public async Task InvalidOutputAndProviderFailure_NeverReachRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and turn through SendAsync, and keep the dish the first draft put on lunch of day 1.
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var lunch = turn.Proposal!.Snapshot.Items.Single(item => item.ItemKey == "d1").DishId;
        // STEP-03: Set scenario.Llm.Override to a patch that swaps in a dish ID the catalog does not have, on every call including the repair.
        scenario.Llm.Override = _ => new AgentLlmStep([], new AgentOutput { Action = "patch_proposal", Reply = "Fake", BaseProposalVersion = 1,
            Scope = new(1, "lunch"), Changes = [new("replace", "d1", new AgentItem("d1", null, 1, "lunch", 0, int.MaxValue))] });
        // STEP-04: Obtain invalid through SendAsync; assert the draft never holds the unknown dish ID and lunch keeps its dish.
        var invalid = await SendAsync(client, conversation.ConversationId, Message(turn.ConversationVersion));
        invalid.Proposal!.Snapshot.Items.Should().NotContain(item => item.DishId == int.MaxValue);
        invalid.Proposal.Snapshot.Items.Single(item => item.ItemKey == "d1").DishId.Should().Be(lunch);
        // STEP-05: Assert no roadmap was written by the chat turn.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
        // STEP-06: Set scenario.Llm.Override to return no step, as a failed provider does.
        scenario.Llm.Override = _ => null;
        // STEP-07: Send POST {Path}/{conversation.ConversationId}/messages with a request body and keep unavailable.
        using var unavailable = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(invalid.ConversationVersion));
        // STEP-08: Obtain restored through GetAsync; assert ErrorAsync confirms AGENT_UNAVAILABLE, the current proposal is unchanged and nothing is processing.
        await ErrorAsync(unavailable, HttpStatusCode.ServiceUnavailable, "AGENT_UNAVAILABLE");
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.CurrentProposal!.ProposalId.Should().Be(invalid.Proposal.ProposalId); restored.Processing.Should().BeFalse();
    }

    // TEST-21: Expiry is enforced on requests while completed replay remains durable and GET does not renew activity.
    /// <summary>Verifies retention policy independently of the background cleanup schedule.</summary>
    [PostgresEndToEndFact]
    public async Task ExpiryAndGet_RespectShortTermMemoryPolicy()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        // STEP-03: Get request from Message.
        // STEP-04: Obtain turn through SendAsync.
        var conversation = await OpenAsync(client); var request = Message(1); var turn = await SendAsync(client, conversation.ConversationId, request);
        // STEP-05: Get before from factory.ExecuteDbAsync.
        var before = await factory.ExecuteDbAsync(async db => await db.AgentConversations.AsNoTracking().SingleAsync());
        // STEP-06: Assert GetAsync succeeds.
        await GetAsync(client, conversation.ConversationId);
        // STEP-07: Assert in the database db.AgentConversations.SingleAsync().UpdatedAt is before.UpdatedAt.
        await factory.ExecuteDbAsync(async db => (await db.AgentConversations.SingleAsync()).UpdatedAt.Should().Be(before.UpdatedAt));
        // STEP-08: Seed or update rows directly in the test database.
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))));
        // STEP-09: Send GET {Path}/{conversation.ConversationId} and keep expired.
        // STEP-10: Assert ErrorAsync confirms CONVERSATION_EXPIRED, SendAsync succeeds and SendAsync().Replayed is true.
        using var expired = await client.GetAsync($"{Path}/{conversation.ConversationId}"); await ErrorAsync(expired, HttpStatusCode.Gone, "CONVERSATION_EXPIRED");
        (await SendAsync(client, conversation.ConversationId, request)).Replayed.Should().BeTrue();
        // STEP-11: Send POST {Path}/{conversation.ConversationId}/messages with a request body and keep fresh.
        using var fresh = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(turn.ConversationVersion));
        // STEP-12: Assert ErrorAsync confirms CONVERSATION_EXPIRED.
        await ErrorAsync(fresh, HttpStatusCode.Gone, "CONVERSATION_EXPIRED");
    }

    // TEST-22: Cleanup deletes retained Agent memory while the committed roadmap and items survive.
    /// <summary>Verifies restrictive current-proposal unlinking and non-cascading roadmap lifetime.</summary>
    [PostgresEndToEndFact]
    public async Task Cleanup_RemovesMemoryAndPreservesRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, turn through SendAsync and saved through ConfirmAsync.
        var conversation = await OpenAsync(client); var turn = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = 1 });
        // STEP-03: Seed or update rows directly in the test database and create scope through host.Services.CreateScope.
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow.AddDays(-31)).SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddDays(-24))));
        await factory.ExecuteDbAsync(async db =>
        {
            await db.AgentMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
            await db.AgentProposals.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
            await db.AgentMessages.Where(x => x.Role == AgentMessageRole.User).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{\"attempts\":[]}"));
        });
        using var scope = host.Services.CreateScope();
        // STEP-04: Assert GetRequiredService().ExecuteAsync() is 1.
        (await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None)).Should().Be(1);
        // STEP-05: Assert in the database retained.Id is conversation.ConversationId, retained.RoadmapId is saved.RoadmapId, retained.State is "{}" and 4 more.
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

    // TEST-23: A replacement creates a fresh roadmap item and detaches old feedback instead of transferring it.
    /// <summary>Verifies the RM-08 identity and feedback rule through a real Agent update transaction.</summary>
    [PostgresEndToEndFact]
    public async Task UpdateProposal_ReplacesItemWithoutTransferringFeedback()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, first through SendAsync and saved through ConfirmAsync.
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        // STEP-03: Derive itemId from saved.ItemIds["d1"].
        var itemId = saved.ItemIds["d1"];
        // STEP-04: Send POST /api/v1/roadmaps/{saved.RoadmapId}/items/{itemId}/feedback with a body of value and keep feedback.
        // STEP-05: Ensure feedback succeeded.
        using var feedback = await client.PostAsJsonAsync($"/api/v1/roadmaps/{saved.RoadmapId}/items/{itemId}/feedback", new { value = "like" }); feedback.EnsureSuccessStatusCode();
        // STEP-06: Obtain current through GetAsync, next through SendAsync and updated through ConfirmAsync.
        var current = await GetAsync(client, conversation.ConversationId);
        var next = await SendAsync(client, conversation.ConversationId, Message(current.Version));
        var updated = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = next.Proposal!.ProposalId, ProposalVersion = 2 });
        // STEP-07: Assert updated.RoadmapId is saved.RoadmapId, updated.RoadmapUpdatedAt is after saved.RoadmapUpdatedAt and updated.ItemIds[$"r{itemId}"] is not itemId.
        updated.RoadmapId.Should().Be(saved.RoadmapId); updated.RoadmapUpdatedAt.Should().BeAfter(saved.RoadmapUpdatedAt);
        updated.ItemIds[$"r{itemId}"].Should().NotBe(itemId);
        // STEP-08: Assert in the database db.DishFeedbacks.SingleAsync().RoadmapItemId is null.
        await factory.ExecuteDbAsync(async db => (await db.DishFeedbacks.SingleAsync()).RoadmapItemId.Should().BeNull());
    }

    // TEST-24: Explicit alternatives avoid previous dishes rather than treating retry or recommender fallback as diversity.
    /// <summary>Verifies exclusion-before-ranking for an alternative proposal.</summary>
    [PostgresEndToEndFact]
    public async Task Alternative_ExcludesPreviousDish()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and first through SendAsync.
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Get request from Message and set request.Command to "alternative".
        var request = Message(first.ConversationVersion); request.Command = "alternative";
        // STEP-04: Obtain second through SendAsync; assert second.Proposal.Snapshot.Items.Select(filtered) not intersect with Snapshot.Items.Select(filtered).
        var second = await SendAsync(client, conversation.ConversationId, request);
        second.Proposal!.Snapshot.Items.Select(x => x.DishId).Should().NotIntersectWith(first.Proposal!.Snapshot.Items.Select(x => x.DishId));
        // STEP-05: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-25: Persistent quota admission is serialized across conversations and returns the documented retry header.
    /// <summary>Verifies per-user allowance enforcement without in-memory quota races.</summary>
    [PostgresEndToEndFact]
    public async Task QuotaAcrossConversations_Returns429WithRetryAfter()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and set GetRequiredService().MessagesPerMinute to 1.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        host.Services.GetRequiredService<AgentLimits>().MessagesPerMinute = 1;
        // STEP-02: Obtain first through OpenAsync and second through OpenAsync; assert SendAsync succeeds.
        var first = await OpenAsync(client); var second = await OpenAsync(client);
        await SendAsync(client, first.ConversationId, Message(1));
        // STEP-03: Send POST {Path}/{second.ConversationId}/messages with a request body and keep blocked.
        using var blocked = await client.PostAsJsonAsync($"{Path}/{second.ConversationId}/messages", Message(1));
        // STEP-04: Assert ErrorAsync confirms AGENT_RATE_LIMITED and blocked.Headers.RetryAfter is not null.
        await ErrorAsync(blocked, HttpStatusCode.TooManyRequests, "AGENT_RATE_LIMITED");
        blocked.Headers.RetryAfter.Should().NotBeNull();
    }

    // TEST-26: JWT, role and request validators reject unauthorized, wrong-role and malformed Agent requests.
    /// <summary>Verifies canonical HTTP envelopes at the transport boundary.</summary>
    [PostgresEndToEndFact]
    public async Task HttpAdmission_EnforcesAuthAndValidation()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        var conversation = await OpenAsync(client);
        // STEP-03: Send POST {Path}/{conversation.ConversationId}/messages with a body of content and keep invalid.
        using var invalid = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new { content = (string?)null });
        // STEP-04: Read invalidBody from the response and 1 more; assert ErrorAsync confirms VALIDATION_FAILED and 1 more.
        var invalidBody = await invalid.Content.ReadAsStringAsync();
        await ErrorAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var validation = AgentJson.Read<ApiResponse<object>>(invalidBody);
        validation!.Errors!["content"].Should().Contain("Vui lòng nhập nội dung tin nhắn.");
        // STEP-05: Attach the bearer token to the HTTP client.
        client.DefaultRequestHeaders.Authorization = null;
        // STEP-06: Send GET {Path}/{conversation.ConversationId} and keep anonymous.
        // STEP-07: Assert ErrorAsync confirms UNAUTHORIZED.
        using var anonymous = await client.GetAsync($"{Path}/{conversation.ConversationId}"); await ErrorAsync(anonymous, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        // STEP-08: Create the HTTP client.
        // STEP-09: Create owner through DiscoverySeed.RegisterAndLoginAsync and attach the bearer token to the HTTP client.
        using var ownerClient = factory.CreateClient(); var owner = await DiscoverySeed.RegisterAndLoginAsync(factory, ownerClient, "RESTAURANT_OWNER");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        // STEP-10: Send GET {Path}/{conversation.ConversationId} and keep forbidden.
        // STEP-11: Assert ErrorAsync confirms FORBIDDEN.
        using var forbidden = await client.GetAsync($"{Path}/{conversation.ConversationId}"); await ErrorAsync(forbidden, HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // TEST-27: Replacing day-two lunch preserves day-one items and leaves the official roadmap absent until confirmation.
    /// <summary>Verifies accumulated multi-turn scope preservation through the complete HTTP and database stack.</summary>
    [PostgresEndToEndFact]
    public async Task DayTwoLunchPatch_PreservesOtherDays()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        // STEP-03: Get request from Message and set request.ConfirmedState to a copy of request.ConfirmedState.
        var conversation = await OpenAsync(client); var request = Message(1);
        request.ConfirmedState = request.ConfirmedState! with { NumDays = 2 };
        // STEP-04: Obtain first through SendAsync.
        var first = await SendAsync(client, conversation.ConversationId, request);
        // STEP-05: Get next from Message, set next.SelectedItemKey to "d2" and set next.ConfirmedState to a copy of request.ConfirmedState.
        var next = Message(first.ConversationVersion); next.SelectedItemKey = "d2";
        next.ConfirmedState = request.ConfirmedState with { FocusDay = 2, FocusMeal = "lunch" };
        // STEP-06: Obtain second through SendAsync; assert second.Proposal.Snapshot.Items.Single(filtered) is Snapshot.Items.Single(filtered) and 2 more.
        var second = await SendAsync(client, conversation.ConversationId, next);
        second.Proposal!.Snapshot.Items.Single(x => x.ItemKey == "d1").Should().Be(first.Proposal!.Snapshot.Items.Single(x => x.ItemKey == "d1"));
        second.Proposal.Snapshot.Items.Single(x => x.ItemKey == "d2").DishId.Should().NotBe(first.Proposal.Snapshot.Items.Single(x => x.ItemKey == "d2").DishId);
        second.Proposal.Snapshot.Changes!.Single().Type.Should().Be("replace");
        // STEP-07: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-28: A failed provider attempt can retry with the same business ID after the client reloads the current version.
    /// <summary>Verifies failed-turn retry appends admission metadata without creating a second logical user message.</summary>
    [PostgresEndToEndFact]
    public async Task FailedTurnRetry_UsesSameLogicalMessage()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        // STEP-03: Get request from Message and set scenario.Llm.Override to a test condition.
        var conversation = await OpenAsync(client); var request = Message(1); scenario.Llm.Override = _ => null;
        // STEP-04: Send POST {Path}/{conversation.ConversationId}/messages with request and keep failed.
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", request);
        // STEP-05: Assert ErrorAsync confirms AGENT_UNAVAILABLE and GetAsync succeeds.
        await ErrorAsync(failed, HttpStatusCode.ServiceUnavailable, "AGENT_UNAVAILABLE");
        // STEP-06: Set scenario.Llm.Override to null.
        request.ExpectedConversationVersion = (await GetAsync(client, conversation.ConversationId)).Version; scenario.Llm.Override = null;
        // STEP-07: Assert SendAsync succeeds and SendAsync().Proposal is not null.
        (await SendAsync(client, conversation.ConversationId, request)).Proposal.Should().NotBeNull();
        // STEP-08: Assert in the database AgentJson.Read().Attempts has 2 item(s) and userMessage.ProcessingStatus is AgentProcessingStatus.Completed.
        await factory.ExecuteDbAsync(async db =>
        {
            var userMessage = await db.AgentMessages.SingleAsync(x => x.Role == AgentMessageRole.User);
            AgentJson.Read<AgentStoredMessage>(userMessage.Payload).Attempts.Should().HaveCount(2);
            userMessage.ProcessingStatus.Should().Be(AgentProcessingStatus.Completed);
        });
    }

    // TEST-29: Opening a saved roadmap reuses its conversation and newer previews supersede older ones.
    /// <summary>Verifies the single-conversation contract through HTTP.</summary>
    [PostgresEndToEndFact]
    public async Task OpenRoadmap_ReusesConversationAndSupersedesOldProposal()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain firstConversation through OpenAsync, first through SendAsync, saved through ConfirmAsync, source through GetAsync and 1 more.
        var firstConversation = await OpenAsync(client); var first = await SendAsync(client, firstConversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, firstConversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        var source = await GetAsync(client, firstConversation.ConversationId);
        var a = await SendAsync(client, source.ConversationId, Message(source.Version));
        // STEP-03: Send POST Path with a body of roadmapId and keep opening.
        using var opening = await client.PostAsJsonAsync(Path, new { roadmapId = saved.RoadmapId });
        // STEP-04: Obtain secondConversation through DataAsync and 1 more; assert opening.StatusCode is HttpStatusCode.OK and 2 more.
        opening.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondConversation = await DataAsync<AgentConversationDto>(opening);
        secondConversation.ConversationId.Should().Be(firstConversation.ConversationId);
        var b = await SendAsync(client, secondConversation.ConversationId, Message(secondConversation.Version));
        await ConfirmAsync(client, secondConversation.ConversationId, new() { ProposalId = b.Proposal!.ProposalId, ProposalVersion = b.Proposal.Version });
        // STEP-05: Send POST {Path}/{firstConversation.ConversationId}/confirm with a body of proposalId and proposalVersion and keep conflict.
        using var conflict = await client.PostAsJsonAsync($"{Path}/{firstConversation.ConversationId}/confirm", new { proposalId = a.Proposal!.ProposalId, proposalVersion = 2 });
        // STEP-06: Assert ErrorAsync confirms PROPOSAL_NOT_COMMITTABLE.
        await ErrorAsync(conflict, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
    }

    // TEST-30: Proposal-insert failure rolls back superseding the old snapshot and failure recovery never reflushes staged changes.
    /// <summary>Exercises real database rollback and the change-tracker isolation required by attempt recovery.</summary>
    [PostgresEndToEndFact]
    public async Task ProposalSaveFailure_RollsBackAndPreservesPreviousReadyState()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and first through SendAsync.
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Seed or update rows directly in the test database.
        await factory.ExecuteDbAsync(async db => await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION core.reject_agent_test_proposal() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected test proposal failure'; END $$;
            CREATE TRIGGER reject_agent_test_proposal BEFORE INSERT ON core.agent_proposals
                FOR EACH ROW EXECUTE FUNCTION core.reject_agent_test_proposal();
            """));
        // STEP-04: Send POST {Path}/{conversation.ConversationId}/messages with a request body and keep failed.
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", Message(first.ConversationVersion));
        // STEP-05: Obtain restored through GetAsync; assert ErrorAsync confirms INTERNAL_ERROR, restored.Processing is false and 2 more.
        await ErrorAsync(failed, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.Processing.Should().BeFalse(); restored.CurrentProposal!.Status.Should().Be("ready");
        restored.CurrentProposal.ProposalId.Should().Be(first.Proposal!.ProposalId);
        // STEP-06: Assert in the database db.AgentProposals.CountAsync() is 1 and db.AgentMessages.CountAsync(filtered) is 1.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.AgentProposals.CountAsync()).Should().Be(1);
            (await db.AgentMessages.CountAsync(x => x.ProcessingStatus == AgentProcessingStatus.Failed)).Should().Be(1);
        });
    }

    // TEST-31: Clarification always records a pending question and blocks confirmation of an older ready snapshot.
    /// <summary>Verifies ambiguous references cannot accidentally turn a conversational yes into a commit.</summary>
    [PostgresEndToEndFact]
    public async Task AmbiguousReference_RequiresClarificationBeforeCommit()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and first through SendAsync.
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Set scenario.Llm.Override to a test condition, get request from Message, set request.Content to a test text and set request.ConfirmedState to null.
        scenario.Llm.Override = _ => new AgentLlmStep([], new AgentOutput { Action = "clarify", Reply = "Bạn muốn đổi món nào?" });
        var request = Message(first.ConversationVersion); request.Content = "Đổi món đó"; request.ConfirmedState = null;
        // STEP-04: Assert SendAsync succeeds, GetAsync succeeds and GetAsync().State.PendingQuestion has a value.
        await SendAsync(client, conversation.ConversationId, request);
        (await GetAsync(client, conversation.ConversationId)).State.PendingQuestion.Should().NotBeNullOrWhiteSpace();
        // STEP-05: Send POST {Path}/{conversation.ConversationId}/confirm with a body of proposalId and proposalVersion and keep blocked.
        using var blocked = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = first.Proposal!.ProposalId, proposalVersion = 1 });
        // STEP-06: Assert ErrorAsync confirms PROPOSAL_NOT_COMMITTABLE.
        await ErrorAsync(blocked, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
        // STEP-07: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-32: Idle expiry preserves the last business timestamp and cleanup leaves an unexpired processing lease alone.
    /// <summary>Verifies both cleanup retention boundaries using real PostgreSQL row selection.</summary>
    [PostgresEndToEndFact]
    public async Task Cleanup_ExpiresWithoutRenewingActivityAndSkipsLiveLeases()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        var conversation = await OpenAsync(client);
        // STEP-03: Create lastActivity through DateTime.UtcNow.AddDays and seed or update rows directly in the test database.
        var lastActivity = DateTime.UtcNow.AddDays(-8);
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.UpdatedAt, lastActivity).SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddDays(-1))));
        // STEP-04: Run host.Services.CreateScope.
        using (var scope = host.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None);
        // STEP-05: Assert in the database expired.Status is AgentConversationStatus.Expired and expired.UpdatedAt is close to lastActivity.
        await factory.ExecuteDbAsync(async db =>
        {
            var expired = await db.AgentConversations.SingleAsync(); expired.Status.Should().Be(AgentConversationStatus.Expired);
            expired.UpdatedAt.Should().BeCloseTo(lastActivity, TimeSpan.FromMilliseconds(1));
        });
        // STEP-06: Seed or update rows directly in the test database.
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.UpdatedAt, DateTime.UtcNow.AddDays(-31)).SetProperty(x => x.ProcessingTurnId, 1L)
            .SetProperty(x => x.ProcessingAttemptId, 2L).SetProperty(x => x.ProcessingUntil, DateTime.UtcNow.AddMinutes(1))));
        // STEP-07: Assert GetRequiredService().ExecuteAsync() is 0.
        using (var scope = host.Services.CreateScope()) (await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None)).Should().Be(0);
        // STEP-08: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
        (await factory.ExecuteDbAsync(db => db.AgentConversations.CountAsync())).Should().Be(1);
    }

    // TEST-33: Manual metadata edits made before a new chat turn remain authoritative for a scoped item update.
    /// <summary>Prevents old conversation state from reverting a newer title or budget during a subsequent Agent edit.</summary>
    [PostgresEndToEndFact]
    public async Task ManualMetadataBeforeNewTurn_IsPreservedOnAgentUpdate()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, first through SendAsync and saved through ConfirmAsync.
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        // STEP-03: Send PATCH /api/v1/roadmaps/{saved.RoadmapId} with a body of title and budgetAmount and keep manual.
        using var manual = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{saved.RoadmapId}", new { title = "Keep my manual title", budgetAmount = 250000 });
        // STEP-04: Ensure manual succeeded.
        manual.EnsureSuccessStatusCode();
        // STEP-05: Assert GetAsync succeeds.
        // STEP-06: Set request.ConfirmedState to null.
        var request = Message((await GetAsync(client, conversation.ConversationId)).Version); request.ConfirmedState = null;
        // STEP-07: Obtain next through SendAsync; assert next.Proposal.Snapshot.State.Title is "Keep my manual title" and 2 more.
        var next = await SendAsync(client, conversation.ConversationId, request);
        next.Proposal!.Snapshot.State.Title.Should().Be("Keep my manual title");
        next.Proposal.Snapshot.State.BudgetAmount.Should().Be(250000);
        await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = next.Proposal.ProposalId, ProposalVersion = 2 });
        // STEP-08: Assert in the database roadmap.Title is "Keep my manual title" and roadmap.BudgetAmount is 250000.
        await factory.ExecuteDbAsync(async db =>
        {
            var roadmap = await db.Roadmaps.SingleAsync(); roadmap.Title.Should().Be("Keep my manual title"); roadmap.BudgetAmount.Should().Be(250000);
        });
    }

    // TEST-34: Removing the last official item keeps the roadmap and its days while applying exactly one revision.
    /// <summary>Verifies the zero-item update boundary without treating it as roadmap deletion or draft discard.</summary>
    [PostgresEndToEndFact]
    public async Task RemoveLastSavedItem_PreservesRoadmapAndDays()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync, first through SendAsync and saved through ConfirmAsync.
        var conversation = await OpenAsync(client); var first = await SendAsync(client, conversation.ConversationId, Message(1));
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = first.Proposal!.ProposalId, ProposalVersion = 1 });
        // STEP-03: Set scenario.Llm.Override to a test condition.
        scenario.Llm.Override = context => new AgentLlmStep([], new AgentOutput { Action = "patch_proposal", Reply = "Removal preview", BaseProposalVersion = context.ProposalVersion,
            Changes = [new("remove", context.Source!.Items.Single().ItemKey, null)] });
        // STEP-04: Assert GetAsync succeeds.
        // STEP-05: Set request.ConfirmedState to null.
        var request = Message((await GetAsync(client, conversation.ConversationId)).Version); request.ConfirmedState = null;
        // STEP-06: Obtain removal through SendAsync and 1 more; assert removal.Proposal.Snapshot.Items is empty and 2 more.
        var removal = await SendAsync(client, conversation.ConversationId, request);
        removal.Proposal!.Snapshot.Items.Should().BeEmpty();
        var updated = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = removal.Proposal.ProposalId, ProposalVersion = 2 });
        updated.RoadmapUpdatedAt.Should().BeAfter(saved.RoadmapUpdatedAt); updated.ItemIds.Should().BeEmpty();
        // STEP-07: Assert in the database db.Roadmaps.CountAsync() is 1, db.RoadmapDays.CountAsync() is 1 and db.RoadmapItems.CountAsync() is 0.
        await factory.ExecuteDbAsync(async db => { (await db.Roadmaps.CountAsync()).Should().Be(1); (await db.RoadmapDays.CountAsync()).Should().Be(1); (await db.RoadmapItems.CountAsync()).Should().Be(0); });
    }

    // TEST-35: Numeric message IDs are scoped to a conversation and transcript turns retain server-issued sequence IDs.
    /// <summary>Verifies ordinary integer IDs preserve replay, cross-conversation isolation and transcript grouping.</summary>
    [PostgresEndToEndFact]
    public async Task NumericMessageIds_AreConversationScopedAndReplayable()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain first through OpenAsync and second through OpenAsync.
        var first = await OpenAsync(client); var second = await OpenAsync(client);
        // STEP-03: Get request from Message and set request.ClientMessageId to 1.
        var request = Message(1); request.ClientMessageId = 1;
        // STEP-04: Obtain turn through SendAsync; assert SendAsync succeeds, SendAsync().Replayed is true and SendAsync().Replayed is false.
        var turn = await SendAsync(client, first.ConversationId, request);
        (await SendAsync(client, first.ConversationId, request)).Replayed.Should().BeTrue();
        (await SendAsync(client, second.ConversationId, request)).Replayed.Should().BeFalse();
        // STEP-05: Create discard as SendAgentMessageRequestDto.
        var discard = new SendAgentMessageRequestDto { ClientMessageId = 2,
            ExpectedConversationVersion = turn.ConversationVersion, Content = "Discard preview", Command = "discard" };
        // STEP-06: Assert SendAsync succeeds.
        await SendAsync(client, first.ConversationId, discard);
        // STEP-07: Assert in the database users has 3 item(s), users only holds items matching the condition and messages only holds items matching the condition.
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

    // TEST-36: Invalid numeric identities and legacy UUID strings fail before provider execution.
    /// <summary>Checks positive integer JSON binding, signed bounds and rejection of the former UUID contract.</summary>
    [PostgresEndToEndFact]
    public async Task InvalidNumericMessageIds_AreRejectedBeforeProvider()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync; assert for each id, ErrorAsync confirms VALIDATION_FAILED and scenario.Llm.Calls is 0.
        var conversation = await OpenAsync(client);
        foreach (var id in new[] { "0", "-1", "1.5", "9223372036854775808", "\"9348b4fd-82b7-4755-8b5c-f56e79a073d5\"" })
        {
            using var body = new StringContent("{\"clientMessageId\":" + id +
                ",\"expectedConversationVersion\":1,\"content\":\"Plan lunch\"}", System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"{Path}/{conversation.ConversationId}/messages", body);
            await ErrorAsync(response, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        }
        scenario.Llm.Calls.Should().Be(0);
        // STEP-03: Assert in the database db.AgentMessages.CountAsync() is 0.
        await factory.ExecuteDbAsync(async db => (await db.AgentMessages.CountAsync()).Should().Be(0));
    }

    // TEST-37: Chat-only requests create a preview, save it on the save wish the model read and replay the same committed response without another LLM call.
    /// <summary>Exercises the complete three-field request contract through real HTTP and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_PreviewSaveAndReplay_ProducesOneRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync; assert preview.Proposal.Status is "ready".
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");
        preview.Proposal!.Status.Should().Be("ready");
        // STEP-03: Derive calls from scenario.Llm.Calls.
        var calls = scenario.Llm.Calls;
        // STEP-04: Obtain saved through SendChatAsync; assert saved.Commit is not null and saved.Proposal.Status is "committed".
        var saved = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này", 9001);
        saved.Commit.Should().NotBeNull(); saved.Proposal!.Status.Should().Be("committed");
        // STEP-05: Obtain replay through SendChatAsync; assert replay.Replayed is true and replay.Commit is equivalent to saved.Commit.
        var replay = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này", 9001);
        replay.Replayed.Should().BeTrue(); replay.Commit.Should().BeEquivalentTo(saved.Commit);
        // STEP-06: Assert scenario.Llm.Calls is one more, for reading the save wish, and saved.Reply is the model's; the replay calls nothing.
        scenario.Llm.Calls.Should().Be(calls + 1); saved.Reply.Should().Be(SaveReply);
        // STEP-07: Assert in the database db.Roadmaps.CountAsync() is 1, db.AgentMessages.CountAsync(filtered) is 2 and 1 more.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(1);
            (await db.AgentMessages.CountAsync(message => message.Role == AgentMessageRole.User)).Should().Be(2);
            (await db.AgentProposals.SingleAsync()).CommitResult.Should().NotBeNull();
        });
    }

    // TEST-38: A save wish with no preview saves nothing, and replies the model reads as anything but a save wish never commit.
    /// <summary>Verifies the backend save authorization boundary independently of provider interpretation.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_UnreviewedOrNegatedSave_NeverWritesRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync; assert SendChatAsync succeeds, SendChatAsync().Commit is null and the model read the message once.
        var conversation = await OpenAsync(client);
        (await SendChatAsync(client, conversation.ConversationId, conversation.Version, "lưu lịch trình này")).Commit.Should().BeNull();
        scenario.Llm.Calls.Should().Be(1);
        // STEP-03: Set scenario.Llm.Override to a test condition.
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Chưa lưu lịch." });
        // STEP-04: Assert for each content, GetAsync succeeds, SendChatAsync succeeds and SendChatAsync().Commit is null.
        foreach (var content in new[] { "đồng ý", "đừng lưu lịch trình này", "\"lưu lịch trình này\"" })
        {
            var current = await GetAsync(client, conversation.ConversationId);
            (await SendChatAsync(client, conversation.ConversationId, current.Version, content)).Commit.Should().BeNull();
        }
        // STEP-05: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-39: A budget change the model reads is applied at once with nothing staged; a save wish over the plan made for the old budget then gets the model's
    // own reply instead of a 409, and no roadmap is written.
    /// <summary>Checks over HTTP and PostgreSQL that a constraint the model read is trusted as written, and that a plan which no longer matches the trip is never saved.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ConstraintChange_AppliesAndStalePlanIsNotSaved()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        // STEP-03: Set scenario.Llm.Override to the model reading a raised budget.
        scenario.Llm.Override = context => new([], new AgentOutput { Action = "answer", Reply = "Mình tăng ngân sách lên 700000 đồng nhé.",
            ConstraintsChangeConfirmed = true, State = context.State with { BudgetAmount = 700000, BudgetQuote = "700000 đồng" } });
        // STEP-04: Obtain changed through SendChatAsync and state through GetAsync.
        var changed = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "Tăng ngân sách lên 700000 đồng");
        var state = (await GetAsync(client, conversation.ConversationId)).State;
        // STEP-05: Assert changed.Reply is the model's reply, changed.Commit is null, state.BudgetAmount is 700000 and nothing is staged.
        changed.Reply.Should().Be("Mình tăng ngân sách lên 700000 đồng nhé."); changed.Commit.Should().BeNull();
        state.BudgetAmount.Should().Be(700000); state.PendingStateChange.Should().BeNull();
        // STEP-06: Set scenario.Llm.Override to a save wish whose reply follows planMatchesTrip, the value the real model is sent, and record that value.
        bool? matched = null;
        scenario.Llm.Override = context =>
        {
            matched = AgentConfirmationPolicy.PlanMatchesTrip(context.State, context.CurrentProposal);
            return new([], new AgentOutput { Action = "request_save", Reply = matched.Value ? SaveReply : StalePlanReply });
        };
        // STEP-07: Obtain stale through SendChatAsync; assert it succeeds with the model's reply, no commit, and the model was told the plan no longer matches.
        var stale = await SendChatAsync(client, conversation.ConversationId, changed.ConversationVersion, "ok lưu lại giúp mình");
        stale.Reply.Should().Be(StalePlanReply); stale.Commit.Should().BeNull(); matched.Should().BeFalse();
        // STEP-08: Assert in the database no roadmap exists.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-40: Discard and reset operate through text alone, preserve the official roadmap and fence old chat context from the model but not from the chat.
    /// <summary>Verifies reset preserves its roadmap and excludes pre-reset user messages.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_DiscardAndReset_PreserveSavedRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync, preview through SendChatAsync, saved through SendChatAsync and discarded through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Original old planning text");
        var saved = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này");
        var discarded = await SendChatAsync(client, conversation.ConversationId, saved.ConversationVersion, "bỏ bản nháp");
        // STEP-03: Obtain reset through SendChatAsync; assert discarded.Proposal is null.
        discarded.Proposal.Should().BeNull();
        var reset = await SendChatAsync(client, conversation.ConversationId, discarded.ConversationVersion, "làm lại từ đầu");
        // STEP-04: Obtain restored through GetAsync; assert reset.Proposal is null.
        reset.Proposal.Should().BeNull();
        var restored = await GetAsync(client, conversation.ConversationId);
        // STEP-05: Assert restored.State.Province is "Ho Chi Minh City", restored.State.HistoryStartSequence is greater than 0,
        // and the chat still shows the pre-reset messages (conversation 232 lost every message after a reset).
        restored.State.Province.Should().Be("Ho Chi Minh City"); restored.State.HistoryStartSequence.Should().BeGreaterThan(0);
        restored.Messages.Should().Contain(message => message.Content == "Original old planning text");
        // STEP-06: Assert context.Roadmap is not null, context.Source is not null, context.History holds no matching item and SendChatAsync succeeds.
        scenario.Llm.Override = context =>
        {
            context.Roadmap.Should().NotBeNull(); context.Source.Should().NotBeNull();
            context.History.Should().NotContain(message => message.Content == "Original old planning text");
            return new([], new AgentOutput { Action = "clarify", Reply = "Bạn muốn đến đâu?" });
        };
        await SendChatAsync(client, conversation.ConversationId, reset.ConversationVersion, "Kế hoạch mới");
        // STEP-07: Assert in the database factory.ExecuteDbAsync(filtered) is 1 and in the database factory.ExecuteDbAsync(filtered).RoadmapId is saved.Commit.RoadmapId.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
        (await factory.ExecuteDbAsync(db => db.AgentConversations.SingleAsync())).RoadmapId.Should().Be(saved.Commit!.RoadmapId);
    }

    // TEST-41: A late transcript persistence error rolls back the official save and proposal commit together.
    /// <summary>Proves chat acknowledgement and roadmap application share one transaction.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_FinalizeFailure_RollsBackRoadmapAndCommit()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        // STEP-03: Seed or update rows directly in the test database.
        await factory.ExecuteDbAsync(async db => await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION core.reject_chat_save_completion() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected chat finalize failure'; END $$;
            CREATE TRIGGER reject_chat_save_completion BEFORE UPDATE ON core.agent_messages
                FOR EACH ROW WHEN (NEW.role = 'user' AND NEW.processing_status = 'completed')
                EXECUTE FUNCTION core.reject_chat_save_completion();
            """));
        // STEP-04: Send POST {Path}/{conversation.ConversationId}/messages with a body of clientMessageId, expectedConversationVersion and content and keep failed.
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new
            { clientMessageId = 9001, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" });
        // STEP-05: Assert ErrorAsync confirms INTERNAL_ERROR.
        await ErrorAsync(failed, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        // STEP-06: Assert in the database db.Roadmaps.CountAsync() is 0, db.AgentProposals.SingleAsync().Status is AgentProposalStatus.Ready and 1 more.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(0);
            (await db.AgentProposals.SingleAsync()).Status.Should().Be(AgentProposalStatus.Ready);
            (await db.AgentMessages.CountAsync(message => message.ProcessingStatus == AgentProcessingStatus.Failed)).Should().Be(1);
        });
        // STEP-07: Assert GetAsync succeeds and GetAsync().Processing is false.
        (await GetAsync(client, conversation.ConversationId)).Processing.Should().BeFalse();
    }

    // TEST-42: Simultaneous save messages cannot apply one preview twice and stale versions fail before saving.
    /// <summary>Checks both turn leasing and the transactional proposal idempotency boundary.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ConcurrentSave_ProducesOneBusinessEffect()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        // STEP-03: Define uri as the expected text.
        var uri = $"{Path}/{conversation.ConversationId}/messages";
        // STEP-04: Send POST uri with a body of clientMessageId, expectedConversationVersion and content from 2 concurrent calls and keep responses.
        var responses = await Task.WhenAll(client.PostAsJsonAsync(uri, new { clientMessageId = 9001, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" }),
            client.PostAsJsonAsync(uri, new { clientMessageId = 9002, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" }));
        try
        {
            // STEP-05: Assert responses.Count(filtered) is 1.
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
            // STEP-06: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
            (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
        }
        // STEP-07: Clean up: for each response in responses, call response.Dispose.
        finally { foreach (var response in responses) response.Dispose(); }
    }

    // TEST-43: Catalog price changes invalidate a chat save just as they invalidate the explicit confirm endpoint.
    /// <summary>Reuses deterministic confirmation validation rather than trusting an AI save decision.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ChangedPrice_BlocksSaveWithoutRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        // STEP-03: Derive dishId from Snapshot.Items.Single().DishId and seed or update rows directly in the test database.
        var dishId = preview.Proposal!.Snapshot.Items.Single().DishId;
        await factory.ExecuteDbAsync(async db => await db.Dishes.Where(dish => dish.Id == dishId).ExecuteUpdateAsync(setters => setters.SetProperty(dish => dish.Price, dish => dish.Price + 10000)));
        // STEP-04: Send POST {Path}/{conversation.ConversationId}/messages with a body of clientMessageId, expectedConversationVersion and content and keep failed.
        using var failed = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new
            { clientMessageId = 9001, expectedConversationVersion = preview.ConversationVersion, content = "lưu lịch trình này" });
        // STEP-05: Assert ErrorAsync confirms PROPOSAL_NOT_COMMITTABLE.
        await ErrorAsync(failed, HttpStatusCode.Conflict, "PROPOSAL_NOT_COMMITTABLE");
        // STEP-06: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-44: A preview over the trip budget, shown by the chat despite that rule, is saved when the user asks, instead of failing with 409 (conversation 223).
    /// <summary>Checks over HTTP and PostgreSQL that plan rules such as the budget no longer block the roadmap writer, while integrity checks still do.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_OverBudgetPreview_IsSavedOnRequest()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Script the model to plan one lunch under a 20000 budget that every seeded dish exceeds.
        scenario.Llm.Override = context =>
        {
            if (context.Exchanges.Count == 0)
            {
                context.History.Add(new("user", "ngân sách 20000 đồng"));
                return new([new("over-budget", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                    State: new AgentState { Province = "Ho Chi Minh City", NumDays = 1, StartDate = TripStart, BudgetAmount = 20000, BudgetQuote = "20000 đồng" },
                    TagIds: [], ExcludeTagIds: [], ExcludeDishIds: []), AgentJson.Options))], null);
            }
            return new([], new AgentOutput { Action = "create_proposal", Reply = "Bản xem trước bữa trưa", Items =
                [new AgentItem("over-lunch", null, 1, "lunch", 0, context.Exchanges[0].Results[0].GetProperty("items")[0].GetProperty("dishId").GetInt32())] });
        };
        // STEP-03: Obtain conversation through OpenAsync and preview through SendChatAsync; assert the ready preview costs more than the budget.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");
        preview.Proposal!.Status.Should().Be("ready");
        preview.Proposal.Snapshot.Items.Sum(item => item.Price).Should().BeGreaterThan(20000);
        // STEP-04: Obtain saved through SendChatAsync with the model reading a save wish; assert saved.Commit is not null.
        scenario.Llm.Override = null;
        var saved = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này");
        saved.Commit.Should().NotBeNull(); saved.Reply.Should().Be(SaveReply);
        // STEP-05: Assert in the database one roadmap holds the saved lunch.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
        (await factory.ExecuteDbAsync(db => db.RoadmapItems.CountAsync())).Should().Be(1);
    }

    // TEST-45: Scalar documents a content-only request without fixed identity/version values that manual users might reuse.
    /// <summary>Checks the generated OpenAPI example consumed by the manual testing UI.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_OpenApi_PublishesMinimalRequestExample()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Send GET /openapi/v1.json and keep response.
        using var response = await client.GetAsync("/openapi/v1.json");
        // STEP-03: Ensure response succeeded.
        response.EnsureSuccessStatusCode();
        // STEP-04: Read document from the response.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // STEP-05: Get example from content.application/json.GetProperty.
        var example = document.RootElement.GetProperty("paths").GetProperty(Path + "/{id}/messages")
            .GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example");
        // STEP-06: Assert example.EnumerateObject().Select(filtered) is equivalent to "content" and example.content contains the expected text.
        example.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("content");
        example.GetProperty("content").GetString().Should().Contain("Hà Nội");
    }

    // TEST-46: Chat alternatives exclude current scoped dishes before ranking without requiring a frontend command.
    /// <summary>Verifies inferred alternative scope is enforced by backend candidate filtering and proposal validation.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_Alternative_ExcludesPreviousDish()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        // STEP-03: Get old from preview.Proposal.Snapshot.Items.Single and set scenario.Llm.Override to a test condition.
        var old = preview.Proposal!.Snapshot.Items.Single();
        scenario.Llm.Override = context =>
        {
            if (context.Exchanges.Count == 0) return new([new("chat-alternative", "get_recommendation", JsonSerializer.SerializeToElement(
                new AgentRecommendationInput(Scope: "slot", DayNumber: 1, MealSlot: "lunch", TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], Alternative: true), AgentJson.Options))], null);
            var candidate = context.Exchanges[0].Results[0].GetProperty("items")[0].GetProperty("dishId").GetInt32();
            return new([], new AgentOutput { Action = "patch_proposal", Reply = "Phương án khác", BaseProposalVersion = context.ProposalVersion,
                Scope = new(1, "lunch"), Changes = [new("replace", old.ItemKey, old with { DishId = candidate, ItemKey = old.ItemKey + "_alt" })] });
        };
        // STEP-04: Obtain alternative through SendChatAsync; assert Proposal.Snapshot.Items.Single().DishId is not old.DishId and 2 more.
        var alternative = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "Cho tôi phương án bữa trưa khác");
        alternative.Proposal!.Snapshot.Items.Single().DishId.Should().NotBe(old.DishId);
        alternative.Proposal.Snapshot.Items.Single().ItemKey.Should().Be(old.ItemKey);
        alternative.Proposal.Validation.Errors.Should().BeEmpty();
    }

    // TEST-47: A manual roadmap edit after a preview drops that preview: the next message still reaches the model, which is told so,
    // the stale draft is discarded and a save wish writes nothing over the manual edit.
    /// <summary>Checks source-revision fencing for the chat save without locking the chat, since the UI has no discard button.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_ManualRoadmapEdit_DropsStaleDraft()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync, preview through SendChatAsync and saved through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan lunch");
        var saved = await SendChatAsync(client, conversation.ConversationId, preview.ConversationVersion, "lưu lịch trình này");
        // STEP-03: Set scenario.Llm.Override to null.
        scenario.Llm.Override = null;
        // STEP-04: Obtain edit through SendChatAsync.
        var edit = await SendChatAsync(client, conversation.ConversationId, saved.ConversationVersion, "Đổi món trưa");
        // STEP-05: Send PATCH /api/v1/roadmaps/{saved.Commit.RoadmapId} with a body of title and keep manual.
        using var manual = await client.PatchAsJsonAsync($"/api/v1/roadmaps/{saved.Commit!.RoadmapId}", new { title = "Keep manual title" });
        // STEP-06: Ensure manual succeeded.
        manual.EnsureSuccessStatusCode();
        // STEP-07: Keep what the model is told, then obtain save through SendChatAsync.
        var told = new List<bool>();
        scenario.Llm.Before = (context, _) => { told.Add(context.OutdatedProposalDropped); return Task.CompletedTask; };
        var save = await SendChatAsync(client, conversation.ConversationId, edit.ConversationVersion, "lưu lịch trình này");
        // STEP-08: Assert the model was told, nothing was committed or shown, and the stale draft is discarded and detached.
        told.Should().Equal(true);
        edit.Proposal.Should().NotBeNull(); save.Commit.Should().BeNull(); save.Proposal.Should().BeNull();
        (await factory.ExecuteDbAsync(db => db.AgentProposals.SingleAsync(p => p.Id == edit.Proposal!.ProposalId))).Status.Should().Be(AgentProposalStatus.Discarded);
        (await factory.ExecuteDbAsync(db => db.AgentConversations.SingleAsync(x => x.Id == conversation.ConversationId))).CurrentProposalId.Should().BeNull();
        // STEP-09: Assert in the database factory.ExecuteDbAsync(filtered).Title is "Keep manual title".
        (await factory.ExecuteDbAsync(db => db.Roadmaps.SingleAsync())).Title.Should().Be("Keep manual title");
    }

    // TEST-48: Prompt-only messages permit repeated text and skip existing explicit IDs while preserving retry/conflict semantics.
    /// <summary>Exercises omitted/null transport metadata and replays the returned server identity with a scripted provider.</summary>
    [PostgresEndToEndFact]
    public async Task PromptOnly_RepeatedContent_GetsDistinctIdsAndSupportsExplicitReplay()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and set scenario.Llm.Override to a test condition.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Bạn muốn ăn ở tỉnh nào?" });
        // STEP-02: Obtain conversation through OpenAsync; assert SendAsync succeeds.
        var conversation = await OpenAsync(client);
        await SendAsync(client, conversation.ConversationId, new() { ClientMessageId = 3, Content = "Tin cũ" });
        // STEP-03: Define content as the expected text.
        const string content = "Tôi muốn ăn ở Việt Nam";
        // STEP-04: Obtain first through SendPromptAsync and second through SendPromptAsync.
        var first = await SendPromptAsync(client, conversation.ConversationId, content);
        var second = await SendPromptAsync(client, conversation.ConversationId, content);
        // STEP-05: Send POST {Path}/{conversation.ConversationId}/messages with a body of content, clientMessageId and expectedConversationVersion and keep nullResponse.
        using var nullResponse = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages",
            new { content = "Tôi muốn ăn ở Hà Nội", clientMessageId = (long?)null, expectedConversationVersion = (long?)null });
        // STEP-06: Obtain third through DataAsync; assert first.ClientMessageId is 4, the computed value has no duplicates, first.Replayed is false and 2 more.
        var third = await DataAsync<AgentTurnDto>(nullResponse);
        first.ClientMessageId.Should().Be(4); // Next sequence 3 is already an explicit client ID.
        new[] { first.ClientMessageId, second.ClientMessageId, third.ClientMessageId }.Should().OnlyHaveUniqueItems();
        first.Replayed.Should().BeFalse(); second.Replayed.Should().BeFalse(); third.Replayed.Should().BeFalse();
        // STEP-07: Derive calls from scenario.Llm.Calls.
        var calls = scenario.Llm.Calls;
        // STEP-08: Obtain replay through SendAsync; assert replay.Replayed is true and replay.ClientMessageId is second.ClientMessageId.
        var replay = await SendAsync(client, conversation.ConversationId, new() { ClientMessageId = second.ClientMessageId,
            ExpectedConversationVersion = 1, Content = content });
        replay.Replayed.Should().BeTrue(); replay.ClientMessageId.Should().Be(second.ClientMessageId);
        // STEP-09: Send POST {Path}/{conversation.ConversationId}/messages with a body of clientMessageId and content and keep reused.
        using var reused = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages",
            new { clientMessageId = second.ClientMessageId, content = "Nội dung khác" });
        // STEP-10: Assert ErrorAsync confirms MESSAGE_ID_REUSED.
        await ErrorAsync(reused, HttpStatusCode.Conflict, "MESSAGE_ID_REUSED");
        // STEP-11: Send POST {Path}/{conversation.ConversationId}/messages with a body of content and expectedConversationVersion and keep stale.
        using var stale = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages",
            new { content, expectedConversationVersion = 1 });
        // STEP-12: Assert ErrorAsync confirms CONVERSATION_VERSION_CONFLICT and scenario.Llm.Calls is calls.
        await ErrorAsync(stale, HttpStatusCode.Conflict, "CONVERSATION_VERSION_CONFLICT");
        scenario.Llm.Calls.Should().Be(calls);
        // STEP-13: Assert in the database messages has 4 item(s), messages only holds items matching the condition and messages.Select(filtered) has no duplicates.
        await factory.ExecuteDbAsync(async db =>
        {
            var messages = await db.AgentMessages.Where(message => message.Role == AgentMessageRole.User).ToArrayAsync();
            messages.Should().HaveCount(4); messages.Should().OnlyContain(message => message.ClientMessageId > 0);
            messages.Select(message => message.ClientMessageId).Should().OnlyHaveUniqueItems();
        });
    }

    // TEST-49: Content-only planning and a chat save keep the same atomic, idempotent roadmap writer: a retry replays the commit and a second save wish writes nothing more.
    /// <summary>Verifies retrying the returned message ID calls no provider, and neither the retry nor a later save wish creates a second roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task PromptOnly_PreviewAndSave_ProducesOneRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendPromptAsync; assert preview.Proposal.Status is "ready".
        var conversation = await OpenAsync(client);
        var preview = await SendPromptAsync(client, conversation.ConversationId, "Gợi ý bữa ăn trưa");
        preview.Proposal!.Status.Should().Be("ready");
        // STEP-03: Derive calls from scenario.Llm.Calls.
        var calls = scenario.Llm.Calls;
        // STEP-04: Obtain saved through SendPromptAsync; assert saved.Commit is not null and saved.ClientMessageId is not preview.ClientMessageId.
        var saved = await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này");
        saved.Commit.Should().NotBeNull(); saved.ClientMessageId.Should().NotBe(preview.ClientMessageId);
        // STEP-05: Obtain replay through SendAsync; assert replay.Replayed is true and replay.Commit is equivalent to saved.Commit.
        var replay = await SendAsync(client, conversation.ConversationId, new() { ClientMessageId = saved.ClientMessageId, Content = "lưu lịch trình này" });
        replay.Replayed.Should().BeTrue(); replay.Commit.Should().BeEquivalentTo(saved.Commit);
        // STEP-06: Obtain again through SendPromptAsync; assert again.ClientMessageId is not saved.ClientMessageId and again.Commit is null, as the proposal is already committed.
        var again = await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này");
        again.ClientMessageId.Should().NotBe(saved.ClientMessageId); again.Commit.Should().BeNull();
        // STEP-07: Assert scenario.Llm.Calls is two more, for the two save wishes; the replay calls nothing.
        scenario.Llm.Calls.Should().Be(calls + 2);
        // STEP-08: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
    }

    // TEST-50: Omitting identity/version does not admit a second concurrent message while the first owns its processing lease.
    /// <summary>Checks prompt-only busy fencing with a blocked scripted provider, then completes the original turn.</summary>
    [PostgresEndToEndFact]
    public async Task PromptOnly_ConcurrentMessage_IsBusyBeforeProvider()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client, create entered as TaskCompletionSource and 3 more.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Bạn muốn ăn ở tỉnh nào?" });
        scenario.Llm.Before = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        // STEP-02: Obtain conversation through OpenAsync and first through SendPromptAsync.
        var conversation = await OpenAsync(client);
        var first = SendPromptAsync(client, conversation.ConversationId, "Tôi muốn ăn ở Việt Nam");
        try
        {
            // STEP-03: Call entered.Task.WaitAsync.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // STEP-04: Send POST {Path}/{conversation.ConversationId}/messages with a body of content and keep busy.
            using var busy = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new { content = "Tôi muốn ăn ở Việt Nam" });
            // STEP-05: Assert ErrorAsync confirms CONVERSATION_BUSY.
            await ErrorAsync(busy, HttpStatusCode.Conflict, "CONVERSATION_BUSY");
        }
        // STEP-06: Clean up: call release.TrySetResult.
        finally { release.TrySetResult(); }
        // STEP-07: Assert first.Status is "completed" and scenario.Llm.Calls is 1.
        (await first).Status.Should().Be("completed"); scenario.Llm.Calls.Should().Be(1);
        // STEP-08: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
        (await factory.ExecuteDbAsync(db => db.AgentMessages.CountAsync(message => message.Role == AgentMessageRole.User))).Should().Be(1);
    }

    // TEST-51: Real adapter failures use one fake HTTP request and preserve the existing ready preview through failed-turn recovery.
    /// <summary>Exercises rate limits, authentication, provider unavailability and HTTP timeout through the complete backend stack.</summary>
    [PostgresEndToEndFact]
    public async Task ProviderFailures_DoNotRepairOrReplacePreview()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and 1 more; assert for each mode, ErrorAsync confirms AGENT_UNAVAILABLE and 9 more.
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
        // STEP-03: Set scenario.Llm.Provider to null and set scenario.Llm.Override to a test condition.
        scenario.Llm.Provider = null;
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Bản nháp vẫn còn nguyên." });
        // STEP-04: Assert SendPromptAsync succeeds and SendPromptAsync().Status is "completed".
        (await SendPromptAsync(client, conversation.ConversationId, "Giải thích bản nháp")).Status.Should().Be("completed");
        // STEP-05: Assert in the database factory.ExecuteDbAsync(filtered) is 4 and in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.AgentMessages.CountAsync(message => message.ProcessingStatus == AgentProcessingStatus.Failed))).Should().Be(4);
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-52: Only a received schema-invalid model response permits one repair through the real adapter.
    /// <summary>Verifies bounded repair preserves the current proposal and never writes an official roadmap.</summary>
    [PostgresEndToEndFact]
    public async Task ProviderSchemaFailure_RepairsOnceAndPreservesPreview()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and preview through SendAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Create the FailureProviderHandler under test as handler.
        // STEP-04: Create http as HttpClient and set scenario.Llm.Provider to a new OpenAiAgentLlmService.
        using var handler = new FailureProviderHandler("schema"); using var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/") };
        scenario.Llm.Provider = new OpenAiAgentLlmService(http,
            Options.Create(new AiSettings { ApiKey = "fake-test-key", Model = "fake-test-model" }), new(), TimeProvider.System);
        // STEP-05: Obtain answered through SendPromptAsync; assert answered.Reply is the expected text and 3 more.
        var answered = await SendPromptAsync(client, conversation.ConversationId, "Giải thích bản nháp");
        answered.Reply.Should().Be("Đã giải thích lịch."); answered.Proposal!.ProposalId.Should().Be(preview.Proposal!.ProposalId);
        handler.Calls.Should().Be(2); handler.RepairCalls.Should().Be(1);
        // STEP-06: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);
    }

    // TEST-53: Four concurrent first opens for one roadmap create exactly one conversation (one 201, three 200).
    /// <summary>Verifies the unique open path under concurrency.</summary>
    [PostgresEndToEndFact]
    public async Task ConcurrentFirstOpen_CreatesOnlyOneConversation()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client, get roadmapId from factory.ExecuteDbAsync and 1 more.
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
            // STEP-02: Assert responses.Count(filtered) is 1 and responses.Count(filtered) is 3.
            responses.Count(x => x.StatusCode == HttpStatusCode.Created).Should().Be(1);
            responses.Count(x => x.StatusCode == HttpStatusCode.OK).Should().Be(3);
            // STEP-03: Get opened from Task.WhenAll.
            var opened = await Task.WhenAll(responses.Select(DataAsync<AgentConversationDto>));
            // STEP-04: Assert opened.Select(filtered).Distinct() has exactly one matching item.
            opened.Select(x => x.ConversationId).Distinct().Should().ContainSingle();
            // STEP-05: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
            (await factory.ExecuteDbAsync(db => db.AgentConversations.CountAsync())).Should().Be(1);
        }
        // STEP-06: Clean up: for each response in responses, call response.Dispose.
        finally { foreach (var response in responses) response.Dispose(); }
    }

    // TEST-54: Concurrent reopens return the same conversation; after expiry the read is 410 and a reopen keeps the identity but resets context.
    /// <summary>Checks conversation identity and context reset across expiry.</summary>
    [PostgresEndToEndFact]
    public async Task ConcurrentOpenAndExpiredReopen_KeepIdentityAndResetContext()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        var conversation = await OpenAsync(client);
        // STEP-03: Get request from Message and set request.ConfirmedState to a copy of request.ConfirmedState.
        var request = Message(1); request.ConfirmedState = request.ConfirmedState! with { TasteTargets = [new("spicy", .75)] };
        // STEP-04: Obtain turn through SendAsync and saved through ConfirmAsync.
        var turn = await SendAsync(client, conversation.ConversationId, request);
        var saved = await ConfirmAsync(client, conversation.ConversationId, new() { ProposalId = turn.Proposal!.ProposalId, ProposalVersion = turn.Proposal.Version });
        // STEP-05: Get opens from Task.WhenAll.
        var opens = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => client.PostAsJsonAsync(Path, new { roadmapId = saved.RoadmapId })));
        // STEP-06: Assert for each response, response.StatusCode is HttpStatusCode.OK, DataAsync succeeds and DataAsync().ConversationId is conversation.ConversationId.
        foreach (var response in opens)
        {
            using (response)
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                (await DataAsync<AgentConversationDto>(response)).ConversationId.Should().Be(conversation.ConversationId);
            }
        }
        // STEP-07: Get previous from factory.ExecuteDbAsync and seed or update rows directly in the test database.
        var previous = await factory.ExecuteDbAsync(db => db.AgentConversations.AsNoTracking().SingleAsync());
        await factory.ExecuteDbAsync(async db => await db.AgentConversations.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddSeconds(-1))));
        // STEP-08: Send GET {Path}/{conversation.ConversationId} and keep read.
        using var read = await client.GetAsync($"{Path}/{conversation.ConversationId}");
        // STEP-09: Assert ErrorAsync confirms CONVERSATION_EXPIRED.
        await ErrorAsync(read, HttpStatusCode.Gone, "CONVERSATION_EXPIRED");
        // STEP-10: Send POST Path with a body of roadmapId and keep reopen.
        using var reopen = await client.PostAsJsonAsync(Path, new { roadmapId = saved.RoadmapId });
        // STEP-11: Obtain fresh through DataAsync; assert reopen.StatusCode is HttpStatusCode.OK.
        reopen.StatusCode.Should().Be(HttpStatusCode.OK);
        var fresh = await DataAsync<AgentConversationDto>(reopen);
        // STEP-12: Assert fresh.ConversationId is conversation.ConversationId, fresh.CurrentProposal is null, fresh.State.TasteTargets is empty and 3 more.
        fresh.ConversationId.Should().Be(conversation.ConversationId); fresh.CurrentProposal.Should().BeNull();
        fresh.State.TasteTargets.Should().BeEmpty(); fresh.State.Preferences.Should().BeNull();
        fresh.State.HistoryStartSequence.Should().Be(previous.LastMessageSequence + 1); fresh.Messages.Should().BeEmpty();
        // STEP-13: Obtain next through SendAsync; assert next.Proposal.Version is greater than turn.Proposal.Version.
        var next = await SendAsync(client, fresh.ConversationId, Message(fresh.Version));
        next.Proposal!.Version.Should().BeGreaterThan(turn.Proposal.Version);
        // STEP-14: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
        (await factory.ExecuteDbAsync(db => db.AgentConversations.CountAsync())).Should().Be(1);
    }

    // TEST-55: Cleanup purges old messages without renewing activity, keeps recent replay, rejects the purged message with 410 and allows new turns.
    /// <summary>Verifies retention cleanup preserves counters and idempotent replay.</summary>
    [PostgresEndToEndFact]
    public async Task ActiveCleanup_PreservesReplayAndCountersWithoutRenewingActivity()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync.
        var conversation = await OpenAsync(client);
        // STEP-03: Get oldRequest from Message.
        // STEP-04: Obtain first through SendAsync.
        var oldRequest = Message(1); var first = await SendAsync(client, conversation.ConversationId, oldRequest);
        // STEP-05: Get recentRequest from Message.
        // STEP-06: Obtain second through SendAsync.
        var recentRequest = Message(first.ConversationVersion); var second = await SendAsync(client, conversation.ConversationId, recentRequest);
        // STEP-07: Seed or update rows directly in the test database and get before from factory.ExecuteDbAsync.
        await factory.ExecuteDbAsync(async db =>
        {
            await db.AgentMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
            await db.AgentMessages.Where(x => x.ClientMessageId == oldRequest.ClientMessageId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Payload, "{\"attempts\":[]}"));
            await db.AgentProposals.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-31)));
        });
        var before = await factory.ExecuteDbAsync(db => db.AgentConversations.AsNoTracking().SingleAsync());
        // STEP-08: Run host.Services.CreateScope.
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ICleanupAgentConversationsUseCase>().ExecuteAsync(CancellationToken.None);
        // STEP-09: Get after from factory.ExecuteDbAsync.
        var after = await factory.ExecuteDbAsync(db => db.AgentConversations.AsNoTracking().SingleAsync());
        // STEP-10: Assert after.UpdatedAt is before.UpdatedAt, after.LastMessageSequence is before.LastMessageSequence and 3 more.
        after.UpdatedAt.Should().Be(before.UpdatedAt); after.LastMessageSequence.Should().Be(before.LastMessageSequence);
        after.PurgedClientMessageId.Should().Be(oldRequest.ClientMessageId);
        (await SendAsync(client, conversation.ConversationId, recentRequest)).Replayed.Should().BeTrue();
        // STEP-11: Send POST {Path}/{conversation.ConversationId}/messages with oldRequest and keep gone.
        using var gone = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", oldRequest);
        // STEP-12: Obtain third through SendAsync; assert ErrorAsync confirms MESSAGE_EXPIRED and third.Proposal.Version is greater than second.Proposal.Version.
        await ErrorAsync(gone, HttpStatusCode.Gone, "MESSAGE_EXPIRED");
        var third = await SendAsync(client, conversation.ConversationId, Message(second.ConversationVersion));
        third.Proposal!.Version.Should().BeGreaterThan(second.Proposal!.Version);
        // STEP-13: Assert in the database ExecuteDbAsync(filtered).LastMessageSequence is greater than before.LastMessageSequence.
        (await factory.ExecuteDbAsync(db => db.AgentConversations.SingleAsync())).LastMessageSequence.Should().BeGreaterThan(before.LastMessageSequence);
    }

    // TEST-56: The real reco service ranks with the conversation vector and counts impressions only for committed dishes; covers confirm endpoint and chat save.
    /// <summary>Exercises the live reco contract end to end when a reco URL and key are configured.</summary>
    [RecoEndToEndTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealReco_UsesConversationVectorAndCountsOnlyCommittedDishes(bool saveViaChat)
    {
        // STEP-01: Create settings as RecommendationSettings and create recoHttp as HttpClient.
        var settings = new RecommendationSettings { BaseUrl = Environment.GetEnvironmentVariable("ANGI_TEST_RECO_URL")!,
            ApiKey = Environment.GetEnvironmentVariable("ANGI_TEST_RECO_API_KEY")!, TimeoutMilliseconds = 10000 };
        using var recoHttp = new HttpClient { BaseAddress = new Uri(settings.BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
        // STEP-02: Create the RecommendationService under test as recommendation.
        var recommendation = new RecommendationService(recoHttp, Options.Create(settings), NullLogger<RecommendationService>.Instance);
        // STEP-03: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client, get userId from factory.ExecuteDbAsync and 4 more.
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
        // STEP-04: Obtain conversation through OpenAsync.
        var conversation = await OpenAsync(client);
        // STEP-05: Get request from Message and set request.ConfirmedState to a copy of request.ConfirmedState.
        var request = Message(1); request.ConfirmedState = request.ConfirmedState! with { NumDays = 2,
            TasteTargets = [new("sweet", 0), new("spicy", 1)], Preferences = new() { Initialized = true, BaselineVector = Enumerable.Repeat(.5, 12).ToArray() } };
        // STEP-06: Obtain preview through SendAsync.
        var preview = await SendAsync(client, conversation.ConversationId, request);
        // STEP-07: Derive snapshot from preview.Proposal.Snapshot and derive requestId from snapshot.Items.First().RequestId.
        var snapshot = preview.Proposal!.Snapshot; var requestId = snapshot.Items.First().RequestId;
        // STEP-08: Assert requestId is not null, snapshot.State.Preferences.BaselineVector[0] is 1, snapshot.State.Preferences.EffectiveVector[5] is 1 and 1 more.
        requestId.Should().NotBeNull(); snapshot.State.Preferences!.BaselineVector![0].Should().Be(1);
        snapshot.State.Preferences.EffectiveVector![5].Should().Be(1); snapshot.State.Preferences.EffectiveVector[0].Should().Be(0);
        // STEP-09: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded))).Should().Be(0);
        // STEP-10: Send POST /api/v1/impressions with a body of requestId, dishIds and agentProposalId and keep publicPreview.
        using var publicPreview = await client.PostAsJsonAsync("/api/v1/impressions", new { requestId, dishIds = snapshot.Items.Select(x => x.DishId).ToArray(), agentProposalId = preview.Proposal.ProposalId });
        // STEP-11: Assert publicPreview.StatusCode is one of the allowed values and Convert.ToInt64() is 0.
        publicPreview.StatusCode.Should().BeOneOf(HttpStatusCode.Accepted, HttpStatusCode.BadRequest);
        await using (var notShown = new NpgsqlCommand("SELECT count(*) FROM recommendation.recommendation_items WHERE request_id=@r AND shown_at IS NOT NULL", sql))
        { notShown.Parameters.AddWithValue("r", requestId!.Value); Convert.ToInt64(await notShown.ExecuteScalarAsync()).Should().Be(0); }
        // STEP-12: Seed or update rows directly in the test database.
        await factory.ExecuteDbAsync(async db => await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION core.reject_agent_commit_test() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Injected commit failure'; END $$;
            CREATE TRIGGER reject_agent_commit_test BEFORE UPDATE ON core.agent_proposals
                FOR EACH ROW WHEN (NEW.status = 'committed') EXECUTE FUNCTION core.reject_agent_commit_test();
            """));
        // STEP-13: Assert ErrorAsync confirms INTERNAL_ERROR.
        using (var rolledBack = saveViaChat
            ? await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/messages", new { content = "lưu lịch trình này" })
            : await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm", new { proposalId = preview.Proposal.ProposalId, proposalVersion = preview.Proposal.Version }))
            await ErrorAsync(rolledBack, HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        // STEP-14: Assert in the database db.Roadmaps.CountAsync() is 0 and db.OutboxMessages.CountAsync(filtered) is 0.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(0);
            (await db.OutboxMessages.CountAsync(x => x.EventType == OutboxEventType.AgentImpressionRecorded)).Should().Be(0);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_agent_commit_test ON core.agent_proposals; DROP FUNCTION core.reject_agent_commit_test()");
        });
        // STEP-15: Declare commit to capture a call argument.
        AgentCommitDto commit;
        // STEP-16: Assert depending on saveViaChat, SendPromptAsync succeeds, SendPromptAsync succeeds, SendPromptAsync().Commit is equivalent to commit and 2 more.
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
        // STEP-17: Get outbox from factory.ExecuteDbAsync.
        var outbox = await factory.ExecuteDbAsync(db => db.OutboxMessages.Where(x => x.EventType == OutboxEventType.AgentImpressionRecorded).ToArrayAsync());
        // STEP-18: Assert outbox has exactly one matching item and commit.RoadmapId is greater than 0.
        outbox.Should().ContainSingle(); commit.RoadmapId.Should().BeGreaterThan(0);
        // STEP-19: Assert in the database failed.Outcome is OutboxDeliveryOutcome.Retry and factory.ExecuteDbAsync(filtered) is 1.
        using (var unavailable = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1/"), Timeout = TimeSpan.FromSeconds(1) })
        {
            var failed = await new RecoOutboxSender(unavailable, Options.Create(settings)).SendAsync(outbox[0], CancellationToken.None);
            failed.Outcome.Should().Be(OutboxDeliveryOutcome.Retry);
            (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
        }
        // STEP-20: Create sender as RecoOutboxSender.
        var sender = new RecoOutboxSender(recoHttp, Options.Create(settings));
        // STEP-21: Assert sender.SendAsync().Outcome is OutboxDeliveryOutcome.Delivered.
        (await sender.SendAsync(outbox[0], CancellationToken.None)).Outcome.Should().Be(OutboxDeliveryOutcome.Delivered);
        (await sender.SendAsync(outbox[0], CancellationToken.None)).Outcome.Should().Be(OutboxDeliveryOutcome.Delivered);
        // STEP-22: Create check as NpgsqlCommand, call check.Parameters.AddWithValue and get row from check.ExecuteReaderAsync.
        await using var check = new NpgsqlCommand("""
            SELECT p.total_shown,p.total_interactions,p.feedback_count,p.vec::text,
              (SELECT count(*) FROM recommendation.recommendation_items WHERE request_id=@r AND shown_at IS NOT NULL AND agent_proposal_id=@p),
              (SELECT vector_source FROM recommendation.recommendation_logs WHERE request_id=@r),
              (SELECT effective_vector::text FROM recommendation.recommendation_logs WHERE request_id=@r)
            FROM recommendation.user_preference_vectors p WHERE user_id=@u
            """, sql);
        check.Parameters.AddWithValue("r", requestId!.Value); check.Parameters.AddWithValue("p", preview.Proposal.ProposalId); check.Parameters.AddWithValue("u", userId);
        // STEP-23: Assert row.ReadAsync() is true and row is Select(filtered).Distinct().Count().
        await using var row = await check.ExecuteReaderAsync(); (await row.ReadAsync()).Should().BeTrue();
        // STEP-24: Assert row is 6, row is 0 and row is "[1,0,0,0,0,0,0,0,0,0,0,0]".
        row.GetInt64(0).Should().Be(snapshot.Items.Select(x => x.DishId).Distinct().Count()); row.GetInt32(1).Should().Be(6);
        row.GetInt32(2).Should().Be(0); row.GetString(3).Should().Be("[1,0,0,0,0,0,0,0,0,0,0,0]");
        // STEP-25: Assert row is Select(filtered).Distinct().Count(), row is "conversation" and JsonSerializer.Deserialize() equals State.Preferences.EffectiveVector.
        row.GetInt64(4).Should().Be(snapshot.Items.Select(x => x.DishId).Distinct().Count()); row.GetString(5).Should().Be("conversation");
        JsonSerializer.Deserialize<double[]>(row.GetString(6))!.Should().Equal(snapshot.State.Preferences.EffectiveVector);
    }

    // TEST-57: An edit naming nothing gets the model's question and keeps the preview; a save wish the model reads next saves that same preview at once,
    // since wanting to save means the open question is no longer needed, and its reply is shown as written.
    /// <summary>Verifies through HTTP and PostgreSQL that a save intent is never turned into a new plan and saves exactly the reviewed preview.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_SaveWishOverOpenQuestion_SavesReviewedPreview()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");
        // STEP-03: Derive calls from scenario.Llm.Calls.
        var calls = scenario.Llm.Calls;

        // STEP-04: Obtain edit through SendPromptAsync, current through GetAsync and saved through SendPromptAsync with the model reading "ưng rồi đó" as a save wish.
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "clarify", Clarification = "edit_scope", Reply = "Bạn muốn đổi món nào, hay cả lịch?" });
        var edit = await SendPromptAsync(client, conversation.ConversationId, "đổi cái đó đi");
        var current = await GetAsync(client, conversation.ConversationId);
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "request_save", Reply = SaveReply });
        var saved = await SendPromptAsync(client, conversation.ConversationId, "ưng rồi đó");

        // STEP-05: Assert edit.Reply is the model's question and the preview stays current and ready while it is open.
        edit.Reply.Should().Be("Bạn muốn đổi món nào, hay cả lịch?");
        current.CurrentProposal!.ProposalId.Should().Be(preview.Proposal!.ProposalId); current.CurrentProposal.Status.Should().Be("ready");
        // STEP-06: Assert saved commits the reviewed preview with the model's reply.
        saved.Commit!.CommittedProposalId.Should().Be(preview.Proposal.ProposalId); saved.Reply.Should().Be(SaveReply);
        // STEP-07: Assert scenario.Llm.Calls is two more, for the edit and the save wish.
        scenario.Llm.Calls.Should().Be(calls + 2);
        // STEP-08: Assert in the database db.Roadmaps.CountAsync() is 1 and db.AgentProposals.CountAsync() is 1.
        await factory.ExecuteDbAsync(async db =>
        {
            (await db.Roadmaps.CountAsync()).Should().Be(1);
            (await db.AgentProposals.CountAsync()).Should().Be(1);
        });
    }

    // TEST-58: A refusal the model read in an open clarification closes it, so the same preview is saved by the next save message.
    /// <summary>Verifies through HTTP and PostgreSQL that a question the user declines no longer blocks saving.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_DeclineQuestion_ThenSaveCommitsPreview()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");
        // STEP-03: Derive calls from scenario.Llm.Calls.
        var calls = scenario.Llm.Calls;

        // STEP-04: Set scenario.Llm.Override to the model's question, obtain kept through SendPromptAsync and saved through SendPromptAsync; assert SendPromptAsync succeeds.
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "clarify", Clarification = "edit_scope", Reply = "Bạn muốn đổi món nào, hay cả lịch?" });
        await SendPromptAsync(client, conversation.ConversationId, "đổi cái đó đi");
        ReadsAnswer(scenario, "decline");
        var kept = await SendPromptAsync(client, conversation.ConversationId, "thôi dẹp đi");
        var saved = await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này");

        // STEP-05: Assert kept.Reply is the model's reply as written and saved.Commit.CommittedProposalId is preview.Proposal.ProposalId.
        kept.Reply.Should().Be(AnswerReply);
        saved.Commit!.CommittedProposalId.Should().Be(preview.Proposal!.ProposalId);
        // STEP-06: Assert scenario.Llm.Calls is three more, for the edit, reading the refusal and reading the save wish.
        scenario.Llm.Calls.Should().Be(calls + 3);
        // STEP-07: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
    }

    // TEST-59: After the attached roadmap is deleted, messages still reach the model, which is told so, and a reset it reads detaches the roadmap and starts empty.
    /// <summary>Verifies through HTTP and PostgreSQL that a conversation whose roadmap was deleted is not left permanently failing.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_RoadmapDeleted_ResetDetachesConversation()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync and saved through SendPromptAsync; assert SendChatAsync confirms Plan one lunch.
        var conversation = await OpenAsync(client);
        await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");
        var saved = await SendPromptAsync(client, conversation.ConversationId, "lưu lịch trình này");
        // STEP-03: Send DELETE /api/v1/roadmaps/{saved.Commit.RoadmapId} and keep deleted.
        using var deleted = await client.DeleteAsync($"/api/v1/roadmaps/{saved.Commit!.RoadmapId}");
        // STEP-04: Ensure deleted succeeded.
        deleted.EnsureSuccessStatusCode();

        // STEP-05: Keep what the model is told, then send an edit and the reset sentence through SendPromptAsync and read current through GetAsync.
        var told = new List<bool>();
        scenario.Llm.Before = (context, _) => { told.Add(context.AttachedRoadmapDeleted); return Task.CompletedTask; };
        await SendPromptAsync(client, conversation.ConversationId, "đổi bữa trưa ngày 1");
        var reset = await SendPromptAsync(client, conversation.ConversationId, "làm lại từ đầu");
        var current = await GetAsync(client, conversation.ConversationId);

        // STEP-06: Assert the first message reached the model with attachedRoadmapDeleted set and the reset after it did not:
        // the turn that told the user detaches the roadmap, so the notice is not repeated on every reply (Round 3, AG-60).
        told.Should().NotBeEmpty();
        told.First().Should().BeTrue(); told.Last().Should().BeFalse();
        // STEP-07: Assert reset.Reply is the model's reply, current.State.Province is null and current.CurrentProposal is null.
        reset.Reply.Should().Be("Mình làm lại từ đầu nhé.");
        current.State.Province.Should().BeNull(); current.CurrentProposal.Should().BeNull();
        // STEP-08: Assert in the database factory.ExecuteDbAsync(filtered).RoadmapId is null.
        (await factory.ExecuteDbAsync(db => db.AgentConversations.SingleAsync(x => x.Id == conversation.ConversationId))).RoadmapId.Should().BeNull();
    }

    // TEST-60: Over a ready preview, the model's greeting and its refusal of an out-of-scope request are shown as written, and the same preview can still be saved.
    /// <summary>Verifies through HTTP and PostgreSQL that social and refused turns are stored without touching the plan.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_GreetingAndOutOfScope_KeepPreviewSavable()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        // STEP-02: Obtain conversation through OpenAsync and preview through SendAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendAsync(client, conversation.ConversationId, Message(1));
        // STEP-03: Set scenario.Llm.Override to the model's greeting.
        scenario.Llm.Override = _ => new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = "Chào bạn nha!" });

        // STEP-04: Obtain greeting through SendPromptAsync.
        var greeting = await SendPromptAsync(client, conversation.ConversationId, "Xin chào");
        // STEP-05: Set scenario.Llm.Override to the model's own refusal.
        scenario.Llm.Override = _ => new AgentLlmStep([], new AgentOutput { Action = "out_of_scope", Reply = "Việc này ngoài phạm vi của mình, mình chỉ giúp về món ăn, quán và lịch ăn uống thôi nha." });
        // STEP-06: Obtain refusal through SendPromptAsync.
        var refusal = await SendPromptAsync(client, conversation.ConversationId, "Hướng dẫn bóc cam");
        // STEP-07: Set scenario.Llm.Override to null.
        scenario.Llm.Override = null;
        // STEP-08: Obtain current through GetAsync; assert greeting.Reply is the model's.
        var current = await GetAsync(client, conversation.ConversationId);

        greeting.Reply.Should().Be("Chào bạn nha!");
        // STEP-09: Assert refusal.Reply is the model's refusal as written and every turn kept the preview as the current proposal.
        refusal.Reply.Should().Be("Việc này ngoài phạm vi của mình, mình chỉ giúp về món ăn, quán và lịch ăn uống thôi nha.");
        new[] { greeting.Proposal!.ProposalId, refusal.Proposal!.ProposalId, current.CurrentProposal!.ProposalId }.Should().OnlyContain(id => id == preview.Proposal!.ProposalId);
        // STEP-10: Obtain saved through SendPromptAsync; assert current.Messages.Select(filtered) contains greeting.Reply.
        current.Messages.Select(message => message.Content).Should().Contain(greeting.Reply).And.Contain(refusal.Reply);
        var saved = await SendPromptAsync(client, conversation.ConversationId, "chốt lịch này");
        // STEP-11: Assert saved.Commit is not null.
        saved.Commit.Should().NotBeNull();
        // STEP-12: Assert in the database factory.ExecuteDbAsync(filtered) is 1.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(1);
    }

    // TEST-61: A save wish over an undated preview writes nothing and shows the model's date question; the start date the model reads from the answer is then saved on the roadmap.
    /// <summary>Verifies through HTTP and PostgreSQL that a chat save asks for the start date first, as the roadmap's date step requires, with every reply worded by the model.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_SaveWithoutDate_AsksThenStoresTheDate()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario, dated: false);
        // STEP-02: Obtain conversation through OpenAsync; assert SendChatAsync confirms Plan one lunch.
        var conversation = await OpenAsync(client);
        await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");

        // STEP-03: Script the save wish with the model's date question; obtain asked through SendPromptAsync.
        const string question = "Bạn muốn bắt đầu lịch từ ngày nào? Chưa biết thì nhắn “để sau” nhé.";
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "request_save", Reply = question });
        var asked = await SendPromptAsync(client, conversation.ConversationId, "chốt đi");
        // STEP-04: Assert asked shows the model's question, commits nothing, and the preview is still ready with no roadmap.
        asked.Reply.Should().Be(question); asked.Commit.Should().BeNull();
        (await GetAsync(client, conversation.ConversationId)).CurrentProposal!.Status.Should().Be("ready");
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);

        // STEP-05: Script the model reading the awaited date answer as 2099-06-15; obtain saved through SendPromptAsync.
        string? awaited = null;
        scenario.Llm.Override = context =>
        {
            awaited = context.AwaitedAnswer;
            return new([], new AgentOutput { Action = "answer", Reply = "Mình lưu lịch bắt đầu ngày 15/06/2099 nhé.", PendingAnswer = "accept", AnswerStartDate = "2099-06-15" });
        };
        var saved = await SendPromptAsync(client, conversation.ConversationId, "15/6/2099");
        // STEP-06: Assert the model was waited on for save_date, saved.Commit is not null and the reply is the model's.
        awaited.Should().Be(AgentConfirmationPolicy.AwaitingSaveDate);
        saved.Commit.Should().NotBeNull(); saved.Reply.Should().Be("Mình lưu lịch bắt đầu ngày 15/06/2099 nhé.");
        // STEP-07: Assert in the database the single roadmap starts on 2099-06-15.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.SingleAsync())).StartDate.Should().Be(new DateOnly(2099, 6, 15));
    }

    // TEST-62: An agreement the model reads while no question is open saves nothing, and a later save wish saves the dated preview at once with its start date.
    /// <summary>Verifies a short "ok" never writes on its own, while the model's save wish does.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_AcceptWithoutQuestion_SavesNothingUntilSaveWish()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario);
        // STEP-02: Obtain conversation through OpenAsync; assert SendChatAsync confirms Plan one lunch, SendPromptAsync confirms ok and SendPromptAsync().Commit is null.
        var conversation = await OpenAsync(client);
        await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");

        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "answer", Reply = "Chưa lưu nhé.", PendingAnswer = "accept" });
        (await SendPromptAsync(client, conversation.ConversationId, "ok")).Commit.Should().BeNull();
        // STEP-03: Assert in the database factory.ExecuteDbAsync(filtered) is 0.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.CountAsync())).Should().Be(0);

        // STEP-04: Obtain saved through SendPromptAsync with the model reading "chốt đi" as a save wish; assert saved.Commit is not null.
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "request_save", Reply = SaveReply });
        var saved = await SendPromptAsync(client, conversation.ConversationId, "chốt đi");
        saved.Commit.Should().NotBeNull();
        // STEP-05: Assert in the database factory.ExecuteDbAsync(filtered).StartDate is TripStart.
        (await factory.ExecuteDbAsync(db => db.Roadmaps.SingleAsync())).StartDate.Should().Be(TripStart);
    }

    // TEST-63: AGENT-04 stores a start date sent by the save dialog, and the proposal stays reusable for its replay.
    /// <summary>Verifies the save button path, which never goes through chat, can also set the date.</summary>
    [PostgresEndToEndFact]
    public async Task Confirm_WithStartDate_StoresItOnTheRoadmap()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and call EnableChatPlanning.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        EnableChatPlanning(scenario, dated: false);
        // STEP-02: Obtain conversation through OpenAsync and preview through SendChatAsync.
        var conversation = await OpenAsync(client);
        var preview = await SendChatAsync(client, conversation.ConversationId, conversation.Version, "Plan one lunch");

        // STEP-03: Send POST {Path}/{conversation.ConversationId}/confirm with a body of proposalId, proposalVersion and startDate and keep confirm.
        using var confirm = await client.PostAsJsonAsync($"{Path}/{conversation.ConversationId}/confirm",
            new { proposalId = preview.Proposal!.ProposalId, proposalVersion = preview.Proposal.Version, startDate = "2099-06-15" });

        // STEP-04: Obtain commit through DataAsync.
        var commit = await DataAsync<AgentCommitDto>(confirm);
        // STEP-05: Get roadmap from factory.ExecuteDbAsync.
        var roadmap = await factory.ExecuteDbAsync(db => db.Roadmaps.SingleAsync());
        // STEP-06: Assert roadmap.Id is commit.RoadmapId, roadmap.StartDate is a new DateOnly, GetAsync succeeds and GetAsync().State.StartDate is a new DateOnly.
        roadmap.Id.Should().Be(commit.RoadmapId);
        roadmap.StartDate.Should().Be(new DateOnly(2099, 6, 15));
        (await GetAsync(client, conversation.ConversationId)).State.StartDate.Should().Be(new DateOnly(2099, 6, 15));
    }

    // TEST-64: A new plan goes through the place, scope, duration and budget, then preferences, worded by the model with the order and each stage kept by the backend; an unknown province is answered with where ANGI has data, then "không" plans once.
    /// <summary>Verifies the reported conversation end to end over the real HTTP contract and PostgreSQL.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_NewPlan_ModelAsksInOrderThenPlans()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and script the model by message.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var trip = new AgentState { Province = "Ho Chi Minh City" };
        static AgentLlmStep Asks(string kind, string reply, AgentState? state = null) => new([], new AgentOutput { Action = "clarify", Clarification = kind, Reply = reply, State = state });
        scenario.Llm.Override = context => context.Request.Content switch
        {
            "lap lich cho toi" => Asks("trip_details", "Bạn muốn ăn ở đâu nè?"),
            "Đà Nẵng" => Asks("trip_details", "Mình chưa có dữ liệu quán ăn ở Đà Nẵng, hiện có Ho Chi Minh City. Bạn muốn ăn ở đó không?"),
            "ho chi minh city" => Asks("place_scope", "Bạn muốn tìm trong toàn thành phố hay quanh một khu?", trip),
            "toàn thành phố" => Asks("trip_details", "Bạn đi mấy ngày, ngân sách bao nhiêu?", trip),
            "1 ngày, ngân sách 500000 đồng" => Asks("preferences", "Bạn có khẩu vị riêng gì không?", trip with { NumDays = 1, BudgetAmount = 500000, BudgetQuote = "500000 đồng", StartDate = TripStart }),
            _ when context.Exchanges.Count == 0 => new([new("candidates", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(null, null, "full", null, null, [], [], [], null, null, 30, false), AgentJson.Options))], null),
            _ => new([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi nhé!", Items = [new AgentItem("chat-lunch", null, 1, "lunch", 0, context.Exchanges[0].Results[0].GetProperty("items")[0].GetProperty("dishId").GetInt32())] })
        };
        // STEP-02: Obtain conversation through OpenAsync and send the first message.
        var conversation = await OpenAsync(client);
        var place = await SendPromptAsync(client, conversation.ConversationId, "lap lich cho toi");
        // STEP-03: Assert the model's question is shown and the details stage is stored.
        place.Reply.Should().Be("Bạn muốn ăn ở đâu nè?");
        (await GetAsync(client, conversation.ConversationId)).State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        // STEP-04: Obtain unknown through SendPromptAsync; assert the model's reply naming where ANGI has data is shown as written and no province is kept.
        var unknown = await SendPromptAsync(client, conversation.ConversationId, "Đà Nẵng");
        unknown.Reply.Should().Be("Mình chưa có dữ liệu quán ăn ở Đà Nẵng, hiện có Ho Chi Minh City. Bạn muốn ăn ở đó không?");
        (await GetAsync(client, conversation.ConversationId)).State.Province.Should().BeNull();
        // STEP-05: Obtain scope through SendPromptAsync; assert the scope stage is stored, which shows the location button.
        await SendPromptAsync(client, conversation.ConversationId, "ho chi minh city");
        (await GetAsync(client, conversation.ConversationId)).State.PlanningIntake.Should().Be(AgentIntakePolicy.Scope);
        // STEP-06: Obtain details and preferences through SendPromptAsync; assert the preferences stage is stored, which shows the plan-now button.
        await SendPromptAsync(client, conversation.ConversationId, "toàn thành phố");
        var preferences = await SendPromptAsync(client, conversation.ConversationId, "1 ngày, ngân sách 500000 đồng");
        preferences.Reply.Should().Be("Bạn có khẩu vị riêng gì không?");
        (await GetAsync(client, conversation.ConversationId)).State.PlanningIntake.Should().Be(AgentIntakePolicy.Preferences);

        // STEP-07: Obtain plan through SendPromptAsync; assert plan.Proposal.Status is "ready".
        var plan = await SendPromptAsync(client, conversation.ConversationId, "không");
        plan.Proposal!.Status.Should().Be("ready");
        // STEP-08: Obtain restored through GetAsync; assert the trip is kept and no question stays open.
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.State.Province.Should().Be("Ho Chi Minh City");
        restored.State.PendingQuestion.Should().BeNull();
        restored.State.PlanningIntake.Should().BeNull();
    }

    // TEST-65: A long conversation comes back whole after a reload while the model still sees its bounded window, and tool rows count against neither.
    /// <summary>Replays the shape of conversation 158, where tool rows filled the window and a reload showed 12 of 50 messages.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_LongConversation_ReloadRestoresEveryMessage()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host, derive client from scenario.Client and script a suggestion on every turn.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        var historySizes = new List<int>();
        scenario.Llm.Before = (context, _) => { if (context.Exchanges.Count == 0) historySizes.Add(context.History.Count); return Task.CompletedTask; };
        scenario.Llm.Override = context => context.Exchanges.Count == 0
            ? new([new("suggest", "suggest_dish", JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], TasteTargets: []), AgentJson.Options))], null)
            : new([], new AgentOutput { Action = "answer", Reply = "Chưa có món nào hợp trên ANGI, bạn đổi điều kiện nhé." });
        // STEP-02: Obtain conversation through OpenAsync and send 15 messages, which store 45 rows with their tool rows.
        var conversation = await OpenAsync(client);
        for (var turn = 1; turn <= 15; turn++) await SendPromptAsync(client, conversation.ConversationId, $"gợi ý món {turn}");
        // STEP-03: Assert the database holds the tool rows that used to fill the window.
        await factory.ExecuteDbAsync(async db => (await db.AgentMessages.CountAsync(message => message.Role == AgentMessageRole.Tool)).Should().Be(15));

        // STEP-04: Obtain restored through GetAsync; assert all 30 chat messages come back in order.
        var restored = await GetAsync(client, conversation.ConversationId);
        restored.Messages.Should().HaveCount(30);
        restored.Messages[0].Content.Should().Be("gợi ý món 1");
        restored.Messages[^2].Content.Should().Be("gợi ý món 15");
        // STEP-05: Assert the model's window filled up to its limit with chat messages only.
        historySizes[^1].Should().Be(20);
    }

    // TEST-66: Current location sent as confirmed state resolves the nearest catalog province and default radius; a far location sets no province and goes to the model like any message.
    /// <summary>Verifies geographic planning over the real HTTP contract and PostgreSQL coordinates, without any map provider.</summary>
    [PostgresEndToEndFact]
    public async Task ChatOnly_CurrentLocation_ResolvesProvinceFromCoordinates()
    {
        // STEP-01: Get scenario from SetupAsync, derive host from scenario.Host and derive client from scenario.Client.
        var scenario = await SetupAsync(); using var host = scenario.Host; using var client = scenario.Client;
        scenario.Llm.Override = _ => new([], new AgentOutput { Action = "clarify", Clarification = "trip_details", Reply = "Bạn đi mấy ngày nè?" });
        // STEP-02: Obtain conversation through OpenAsync.
        var conversation = await OpenAsync(client);
        // STEP-03: Get far from Message, set far.Content to a test text and set far.ConfirmedState to a new AgentState.
        var far = Message(1);
        far.Content = "Dùng vị trí hiện tại của mình";
        far.ConfirmedState = new AgentState { OriginMode = "gps", OriginLatitude = 21.03m, OriginLongitude = 105.85m, OriginLabel = "Vị trí hiện tại" };
        // STEP-04: Obtain farTurn through SendAsync; assert farTurn.Reply is the model's question and GetAsync().State.Province is null.
        var farTurn = await SendAsync(client, conversation.ConversationId, far);
        farTurn.Reply.Should().Be("Bạn đi mấy ngày nè?");
        (await GetAsync(client, conversation.ConversationId)).State.Province.Should().BeNull();

        // STEP-05: Get near from Message, set near.Content to far.Content and set near.ConfirmedState to a copy of far.ConfirmedState.
        var near = Message(farTurn.ConversationVersion);
        near.Content = far.Content;
        near.ConfirmedState = far.ConfirmedState with { OriginLatitude = 10.775m, OriginLongitude = 106.7m };
        // STEP-06: Obtain located through SendAsync; assert located.Reply is the model's question and the state holds the nearest province and the default radius.
        var located = await SendAsync(client, conversation.ConversationId, near);

        located.Reply.Should().Be("Bạn đi mấy ngày nè?");
        var state = (await GetAsync(client, conversation.ConversationId)).State;
        state.Province.Should().Be("Ho Chi Minh City");
        state.RadiusKm.Should().Be(AgentIntakePolicy.DefaultRadiusKm);
        // STEP-07: Assert scenario.Llm.Calls is 2, one per message: the backend writes no reply of its own for either location.
        scenario.Llm.Calls.Should().Be(2);
    }

    /// <summary>Sends only the prompt; no live provider is used by the scripted HTTP test host.</summary>
    private static async Task<AgentTurnDto> SendPromptAsync(HttpClient client, long id, string content)
    {
        using var response = await client.PostAsJsonAsync($"{Path}/{id}/messages", new { content });
        return await DataAsync<AgentTurnDto>(response);
    }

    /// <summary>Models a normal chat plan with tool-proven candidates and no frontend-provided state or command.</summary>
    private static void EnableChatPlanning(Scenario scenario, bool dated = true) => scenario.Llm.Override = context =>
    {
        if (context.Exchanges.Count == 0)
        {
            // Ngân sách phải có nguyên văn người dùng: tin nhắn trước đó của kịch bản đã nêu nó.
            context.History.Add(new("user", "ngân sách 500000 đồng"));
            return new([new("chat-candidates", "get_recommendation", JsonSerializer.SerializeToElement(new AgentRecommendationInput(
                // Mức giá đang là yêu cầu người dùng vừa nêu, nên câu hỏi khẩu vị được tính là đã trả lời và lịch được lập ngay.
                State: new AgentState { Province = "Ho Chi Minh City", NumDays = 1, StartDate = dated ? TripStart : null, BudgetAmount = 500000, BudgetQuote = "500000 đồng", MaxPricePerMeal = 100000 }, TagIds: [], ExcludeTagIds: [], ExcludeDishIds: []), AgentJson.Options))], null);
        }
        var candidates = context.Exchanges[0].Results[0].GetProperty("items");
        return new([], new AgentOutput { Action = "create_proposal", Reply = "Bản xem trước bữa trưa", Items =
            [new AgentItem("chat-lunch", null, 1, "lunch", 0, candidates[0].GetProperty("dishId").GetInt32())] });
    };

    /// <summary>Makes the model read the reply to an open question this way, whatever it says; other turns keep the current script.</summary>
    private static void ReadsAnswer(Scenario scenario, string reading)
    {
        var script = scenario.Llm.Override;
        scenario.Llm.Override = context => context.AwaitedAnswer is not null
            ? new([], new AgentOutput { Action = "answer", Reply = AnswerReply, PendingAnswer = reading }) : script?.Invoke(context);
    }

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
        ConfirmedState = new AgentState { Province = "Ho Chi Minh City", NumDays = 1, StartDate = TripStart, BudgetAmount = 500000 }
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
        /// <summary>Whole messages the scripted model reads as a save, discard or reset, with the reply it writes for each.</summary>
        private static readonly Dictionary<string, AgentOutput> Controls = new(StringComparer.Ordinal)
        {
            ["lưu lịch trình này"] = new() { Action = "request_save", Reply = SaveReply },
            ["chốt lịch này"] = new() { Action = "request_save", Reply = SaveReply },
            ["bỏ bản nháp"] = new() { Action = "discard_proposal", Reply = "Mình bỏ bản nháp này nhé." },
            ["làm lại từ đầu"] = new() { Action = "reset_conversation", Reply = "Mình làm lại từ đầu nhé." }
        };
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
            // Câu lưu, bỏ nháp và làm lại do model đọc; backend chỉ thực hiện hành động model chọn, nên kịch bản đọc chúng như model thật.
            if (context.Exchanges.Count == 0 && Controls.TryGetValue(context.Request.Content, out var control)) return new([], control);
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
