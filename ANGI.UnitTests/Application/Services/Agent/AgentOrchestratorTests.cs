using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using ANGI.Application.Services.Agent.Constraints;
using ANGI.Application.Services.Agent.Conversation;
using ANGI.Application.Services.Agent.Intake;
using ANGI.Application.Services.Agent.Places;
using ANGI.Application.Services.Agent.Proposals;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies bounded LLM execution and proposal preservation on all controlled failure paths.</summary>
public sealed class AgentOrchestratorTests
{
    // TEST-01: A slot candidate copied outside its issued day is replaced by the best authorized dish for that position without another LLM call (with and without a prior schema repair).
    /// <summary>A day-1/day-2 slot candidate copied into day 3 is swapped for an already issued day-3 candidate without another LLM call.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CandidateOutsideIssuedScope_BackendSubstitutesWithoutExtraLlmCall(bool schemaFirst)
    {
        var state = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000,
            MealSlots = ["breakfast", "lunch", "dinner"], TasteTargets = [new("sour", .25)] };
        var items = Enumerable.Range(0, 9).Select(index => AgentTestData.Item(index + 1, $"item{index}",
            (short)(index / 3 + 1), state.MealSlots[index % 3])).ToArray();
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, null, Enumerable.Range(1, 9).ToArray(), Guid.NewGuid(), state));
        context.Grants.Add(new(1, "breakfast", [221], Guid.NewGuid(), state));
        context.Grants.Add(new(2, "breakfast", [221], Guid.NewGuid(), state));
        var invalid = new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = items.Select(item =>
            item.DayNumber == 3 && item.MealSlot == "breakfast" ? item with { DishId = 221 } : item).ToArray() };
        var llm = new Mock<IAgentLlmService>();
        var sequence = llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (schemaFirst) sequence.ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        sequence.ReturnsAsync(new AgentLlmStep([], invalid));
        var catalog = Enumerable.Range(1, 9).Append(221).Select(id => AgentTestData.Dish(id)).ToArray();

        var result = await Create(llm.Object, catalog: catalog).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Should().HaveCount(9).And.NotContain(item => item.DishId == 221);
        result.Snapshot.Items.Single(item => item.DayNumber == 3 && item.MealSlot == "breakfast").DishId.Should().Be(7);
        result.Snapshot.Grants.Should().Contain(grant => grant.DishIds.Contains(7));
        result.Validation.TotalCost.Should().Be(450000);
        result.Reply.Should().Contain("Có 1 món được hệ thống chọn lại");
        context.RepairFeedback.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(schemaFirst ? 2 : 1));
    }

    // TEST-02: Without a distinct authorized substitute, the model gets one proposal repair and one coverage repair; covers schema-first/no schema failure and recovered/unrecovered outcomes.
    /// <summary>Without a distinct, authorized substitute the backend keeps the bounded model repair and preserves the error details.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task RunAsync_NoSafeSubstituteThenFalseCoverage_RepairsIndependentlyAndPreservesErrors(bool schemaFirst, bool recover)
    {
        var state = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000,
            MealSlots = ["breakfast", "lunch", "dinner"], TasteTargets = [new("sour", .25)] };
        var items = Enumerable.Range(0, 9).Select(index => AgentTestData.Item(index + 1, $"item{index}",
            (short)(index / 3 + 1), state.MealSlots[index % 3])).ToArray();
        var context = AgentTestData.Context(); context.State = state;
        // Món 7 chưa được cấp, mọi món còn lại đã nằm trong lịch: backend không có món thay khác món.
        context.Grants.Add(new(null, null, [1, 2, 3, 4, 5, 6, 8, 9], Guid.NewGuid(), state));
        context.Grants.Add(new(1, "breakfast", [221], Guid.NewGuid(), state));
        context.Grants.Add(new(2, "breakfast", [221], Guid.NewGuid(), state));
        var badItems = items.Select(item => item.DayNumber == 3 && item.MealSlot == "breakfast"
            ? item with { DishId = 221 } : item).ToArray();
        var invalid = new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = badItems };
        var unsupported = new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Thiếu món" };
        var valid = invalid with { Items = badItems.Select(item => item.DishId == 221 ? item with { DishId = 1 } : item).ToArray() };
        var llm = new Mock<IAgentLlmService>();
        var sequence = llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (schemaFirst) sequence.ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        sequence.ReturnsAsync(new AgentLlmStep([], invalid))
            .ReturnsAsync(new AgentLlmStep([], unsupported))
            .ReturnsAsync(new AgentLlmStep([], recover ? valid : unsupported));
        var catalog = Enumerable.Range(1, 9).Append(221).Select(id => AgentTestData.Dish(id)).ToArray();

        var result = await Create(llm.Object, catalog: catalog).RunAsync(context, default);

        context.RepairFeedback.Should().Contain("Món 221").And.Contain("ngày 3, bữa breakfast")
            .And.Contain("ngày 1, bữa breakfast").And.Contain("ngày 2, bữa breakfast").And.Contain("Chưa có bằng chứng");
        if (recover)
        {
            result.Validation!.Errors.Should().BeEmpty();
            result.Snapshot!.Items.Should().HaveCount(9).And.NotContain(item => item.DishId == 221);
            result.Validation.TotalCost.Should().Be(450000);
        }
        else
        {
            result.Snapshot.Should().BeNull();
            result.Validation!.Errors.Should().Contain(error => error.Contains("Món 221"));
            result.Reply.Should().Contain("Món 221").And.NotContain("Thiếu món");
        }
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(schemaFirst ? 4 : 3));
    }

    // TEST-03: An identical tool request in the same turn returns a duplicate notice, is not executed again and closes tools for the final call.
    /// <summary>A repeated identical tool request is answered from the turn, not re-executed, and closes tools for the final call.</summary>
    [Fact]
    public async Task RunAsync_RepeatedIdenticalTool_IsNotReexecutedAndClosesTools()
    {
        var llm = new Mock<IAgentLlmService>();
        var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", arguments)], null))
            .ReturnsAsync(new AgentLlmStep([new("b", "get_roadmap", arguments)], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        context.ToolsClosed.Should().BeTrue();
        context.Exchanges.Should().HaveCount(2);
        context.Exchanges[1].Results.Single().GetProperty("status").GetString().Should().Be("duplicate_request");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-04: The invalid-output notice and schema path are sent only on the call after a schema failure, not after a successful tool round.
    /// <summary>The invalid-output notice applies to the call right after a schema failure, not to every later tool round.</summary>
    [Fact]
    public async Task RunAsync_SchemaRepairThenTool_StopsSendingRepairNotice()
    {
        var llm = new Mock<IAgentLlmService>();
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.InvalidSchema, 200, Reason: "output:$.state.preferences(unexpected)")))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 }))], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        llm.Invocations.Select(call => call.Arguments[1] is true).Should().Equal(false, true, false);
        context.RepairFeedback.Should().Contain("$.state.preferences");
    }

    // TEST-05: After tools are closed by a duplicate request, a further tool request fails the turn without executing it.
    /// <summary>Enforces the closed-tools state even if a provider ignores tool_choice=none.</summary>
    [Fact]
    public async Task RunAsync_ToolRequestAfterToolsClosed_FailsWithoutExecuting()
    {
        var llm = new Mock<IAgentLlmService>();
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        var same = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([new("b", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([new("c", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 89 }))], null));

        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(context, CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();

        context.Exchanges.Should().HaveCount(2);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-06: Two identical calls in one provider step execute once; the second gets the duplicate notice and tools close.
    /// <summary>Covers duplicates inside a single step, not only across rounds.</summary>
    [Fact]
    public async Task RunAsync_IdenticalCallsInOneStep_ExecuteOnce()
    {
        var llm = new Mock<IAgentLlmService>();
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        var same = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", same), new("b", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        var results = context.Exchanges.Single().Results;
        results[0].TryGetProperty("status", out _).Should().BeFalse();
        results[1].GetProperty("status").GetString().Should().Be("duplicate_request");
        context.ToolsClosed.Should().BeTrue();
    }

    // TEST-07: A substitute that fails full revalidation is discarded and the bounded model repair keeps the original error details.
    /// <summary>Ensures backend substitution can never bypass validation; telemetry records the failed attempt.</summary>
    [Fact]
    public async Task RunAsync_SubstituteFailsRevalidation_FallsBackToModelRepair()
    {
        var state = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000, MealSlots = ["breakfast", "lunch", "dinner"] };
        var items = Enumerable.Range(0, 9).Select(index => AgentTestData.Item(index == 6 ? 221 : index + 1, $"item{index}",
            (short)(index / 3 + 1), state.MealSlots[index % 3])).ToArray();
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, null, Enumerable.Range(1, 9).ToArray(), Guid.NewGuid(), state));
        context.Grants.Add(new(1, "breakfast", [221], Guid.NewGuid(), state));
        var catalog = Enumerable.Range(1, 9).Append(221).Select(id => AgentTestData.Dish(id)).ToArray();
        // Món 7 qua được bước chọn món thay nhưng nằm ngoài tỉnh, nên bước kiểm tra lại toàn bộ phải từ chối.
        catalog.Single(dish => dish.Id == 7).Restaurant.ProvinceName = "Other";
        var unsupported = new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Thiếu món" };
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = items }))
            .ReturnsAsync(new AgentLlmStep([], unsupported))
            .ReturnsAsync(new AgentLlmStep([], unsupported));
        var telemetry = new Mock<IAgentTelemetry>();

        var result = await Create(llm.Object, telemetry: telemetry.Object, catalog: catalog).RunAsync(context, CancellationToken.None);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("Món 221").And.NotContain("Thiếu món");
        telemetry.Verify(t => t.Record("scope_autorepair", 7, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object?>()), Times.Once);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-08: A duration and daily budget captured during taste clarification override a stale model state and are staged as one complete change.
    /// <summary>Protects explicit pending trip facts from being dropped when the model copies the old duration.</summary>
    [Fact]
    public async Task RunAsync_DurationChangeDuringTasteClarification_OverridesStaleModelStateAndStagesCompleteChange()
    {
        var state = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000 };
        var source = new AgentSnapshot(state, [AgentTestData.Item()], []);
        var context = AgentTestData.Context(source); context.State = state;
        context.Request.Content = "Tôi muốn lên lịch ăn uống Hà Nội 4 ngày, ngân sách 500000 đồng 1 ngày, tôi muốn ăn chua";
        var llm = new Mock<IAgentLlmService>();
        var first = await Create(llm.Object).RunAsync(context, default);
        first.State.NumDays.Should().Be(3); first.State.BudgetAmount.Should().Be(1500000);
        first.State.PendingPlanningRequest!.NumDays.Should().Be(4);
        llm.VerifyNoOtherCalls();
        context.State = AgentJson.Read<AgentState>(AgentJson.Write(first.State));
        context.History.Add(new("user", context.Request.Content));
        context.Request.Content = "Tôi muốn ăn chua nhẹ, tôi muốn áp dụng cho ngày 2 thôi";
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = state with { FocusDay = 2, TasteOverrides = [new(2, null, [new("sour", .25)])] } }));

        var resumed = await Create(llm.Object).RunAsync(context, default);

        resumed.State.NumDays.Should().Be(3);
        resumed.State.PendingStateChange!.NumDays.Should().Be(4);
        resumed.State.PendingStateChange.BudgetAmount.Should().Be(2000000);
        resumed.State.PendingStateChange.FocusDay.Should().BeNull();
        resumed.State.PendingStateChange.TasteOverrides.Single().Targets.Single().Target.Should().Be(.25);
        resumed.State.PendingPlanningRequest.Should().BeNull();
        resumed.State.PendingStateChange.PendingPlanningRequest.Should().BeNull();
        resumed.Reply.Should().Contain("số ngày: 4").And.Contain("2000000");
        resumed.Snapshot.Should().BeNull(); context.Source.Should().BeSameAs(source);
    }

    // TEST-09: A coverage claim without an observed empty meal triggers one repair, and the repaired patch becomes the proposal without the false claim.
    /// <summary>Ensures a model's coverage intent is not treated as evidence of missing catalog choices.</summary>
    [Fact]
    public async Task RunAsync_UnsupportedCoverage_RepairsAndBuildsUpdatedProposal()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.MealCoverage.Add(new(1, "lunch", 12, AgentScopedPlanning.Fingerprint(context.State, 1, "lunch"), []));
        context.MealCoverage.Add(new(2, "lunch", 12, AgentScopedPlanning.Fingerprint(context.State, 2, "lunch"), ["Đồ uống"]));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Thiếu món, hãy tăng ngân sách" }));
        llm.Setup(x => x.RunAsync(context, true, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "patch_proposal", Reply = "Moved", Changes = [new("move", "a", AgentTestData.Item() with { DayNumber = 2 })] }));

        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);

        result.Snapshot!.Items.Single().DayNumber.Should().Be(2);
        result.Reply.Should().NotContain("Thiếu món").And.NotContain("tăng ngân sách");
        context.RepairFeedback.Should().Contain("Chưa có bằng chứng");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-10: Repeated unsupported coverage claims end in a planning-incomplete reply that keeps the source and never blames the catalog; covers a one-call and a two-call budget.
    /// <summary>Bounds coverage repairs by the LLM call limit and keeps the previous proposal intact.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunAsync_RepeatedUnsupportedCoverage_ReportsPlanningFailureWithoutBlamingCatalog(int maxCalls)
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var context = AgentTestData.Context(source);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Không có đủ dữ liệu" }));

        var result = await Create(llm.Object, new() { MaxLlmCalls = maxCalls }).RunAsync(context, CancellationToken.None);

        result.Snapshot.Should().BeNull();
        context.Source.Should().BeSameAs(source);
        result.Reply.Should().Contain("chưa hoàn tất việc sắp xếp lịch").And.NotContain("Chưa đủ lựa chọn").And.NotContain("Không có đủ dữ liệu");
        result.State.PendingQuestion.Should().Be(result.Reply);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(maxCalls));
    }

    // TEST-11: An observed empty meal produces a question naming that day/meal and its required tags, without the model's invented cause.
    /// <summary>Allows coverage clarification only when backend tool results actually observed a missing meal.</summary>
    [Fact]
    public async Task RunAsync_ObservedMissingMeal_AsksAboutThatMealWithoutInventedCause()
    {
        var context = AgentTestData.Context();
        context.MealCoverage.Add(new(2, "breakfast", 0, AgentScopedPlanning.Fingerprint(context.State, 2, "breakfast"), ["Đồ uống"]));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Không có nhà hàng trong toàn thành phố" }));

        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("kết quả gợi ý vừa lấy").And.Contain("ngày 2, bữa sáng").And.Contain("Đồ uống")
            .And.NotContain("toàn thành phố");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-12: Explanation and clarification use existing context without calling ranking or routes.
    /// <summary>Checks the no-reranking path for ordinary conversational answers.</summary>
    [Theory]
    [InlineData("answer")][InlineData("clarify")]
    public async Task RunAsync_Answer_ReturnsWithoutTools(string action)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { Action = action }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.Snapshot.Should().BeNull(); result.Reply.Should().NotBeEmpty().And.NotContain("Please clarify");
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-13: Repair one invalid schema response and reject a second invalid response.
    /// <summary>Checks both successful repair and the single-repair ceiling.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_SchemaRepair_IsBounded(bool recover)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recover ? new AgentLlmStep([], AgentTestData.Answer()) : AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Snapshot.Should().BeNull();
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-14: A hallucinated dish never produces a new ready proposal or overwrites the source snapshot.
    /// <summary>Verifies deterministic validation after a structurally valid model output.</summary>
    [Fact]
    public async Task RunAsync_HallucinatedDish_ReturnsBlockingValidation()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Preview", Items = [AgentTestData.Item(999)] }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.Snapshot.Should().BeNull(); result.Validation!.Errors.Should().NotBeEmpty();
    }

    // TEST-15: Context size and cancellation prevent uncontrolled provider work.
    /// <summary>Checks resource caps before an LLM request and preserves caller cancellation.</summary>
    [Fact]
    public async Task RunAsync_OversizedContextAndCancellation_StopBeforeProvider()
    {
        var llm = new Mock<IAgentLlmService>();
        var context = AgentTestData.Context(); context.Request.Content = new('x', 130000);
        var act = () => Create(llm.Object).RunAsync(context, CancellationToken.None);
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token))).Should().ThrowAsync<OperationCanceledException>();
        llm.VerifyNoOtherCalls();
    }

    // TEST-16: The total backend timeout cancels a slow provider and reports a controlled unavailable result.
    /// <summary>Verifies processing is bounded independently of provider HTTP timeouts.</summary>
    [Fact]
    public async Task RunAsync_TotalTimeout_CancelsSlowProvider()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        var act = () => Create(llm.Object, new AgentLimits { TimeoutSeconds = 1 }).RunAsync(AgentTestData.Context(), CancellationToken.None);
        (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("AGENT_UNAVAILABLE");
    }

    // TEST-17: Repeated tools stop at the round, aggregate-call and total-LLM-call caps.
    /// <summary>Verifies backend execution limits against a provider that never returns a final answer.</summary>
    [Theory]
    [InlineData("rounds", 2)][InlineData("calls", 1)][InlineData("llm", 2)]
    public async Task RunAsync_RepeatedTools_EnforcesEveryCallCap(string limit, int expectedCalls)
    {
        var limits = new AgentLimits { MaxToolRounds = limit == "rounds" ? 1 : 10,
            MaxToolCalls = limit == "calls" ? 1 : 20, MaxLlmCalls = limit == "llm" ? 2 : 5 };
        var llm = new Mock<IAgentLlmService>();
        var tool = new AgentToolCall("a", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 }));
        var tools = limit == "calls" ? new[] { tool, tool with { Id = "b" } } : [tool];
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep(tools, null));
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        var error = (await ((Func<Task>)(async () => await Create(llm.Object, limits).RunAsync(context, CancellationToken.None)))
            .Should().ThrowAsync<ServiceUnavailableException>()).Which;
        // A limit failure repeats if resent unchanged, so the message asks for clarification instead of a retry.
        error.Message.Should().Contain("điều chỉnh ngày, bữa, món").And.NotContain("thử lại");
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedCalls));
    }

    // TEST-18: Structurally valid model state with invalid business bounds asks the user again, keeping the turn's state, instead of a service error.
    /// <summary>Verifies invalid model-enriched state cannot escape as user validation, a 503 or a ready state.</summary>
    [Fact]
    public async Task RunAsync_InvalidModelState_AsksAgainWithoutChangingState()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = AgentTestData.State() with { NumDays = 0 } }));

        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);

        result.Reply.Should().Be(AgentSmallTalkPolicy.NotUnderstood);
        result.State.NumDays.Should().Be(2);
        result.Snapshot.Should().BeNull();
    }

    // TEST-19: A model may request draft controls but cannot use those actions to also mutate state or items.
    /// <summary>Checks the chat intent allowlist and keeps backend lifecycle effects separate from proposal deltas.</summary>
    [Theory]
    [InlineData("discard_proposal", "discard")]
    [InlineData("reset_conversation", "reset")]
    public async Task RunAsync_DraftControl_ReturnsBackendAction(string action, string expected)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(service => service.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "Draft control" }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.Action.Should().Be(expected); result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-20: A model cannot authorize its own relaxed constraints, even when it sets its consent flag.
    /// <summary>Turns relaxation into a persisted confirmation question rather than a provider error or automatic change.</summary>
    [Fact]
    public async Task RunAsync_RelaxedState_StagesHumanConsent()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(service => service.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Already confirmed",
                ConstraintsChangeConfirmed = true, State = AgentTestData.State() with { BudgetAmount = 700000 } }));
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        result.State.BudgetAmount.Should().Be(500000); result.State.PendingStateChange!.BudgetAmount.Should().Be(700000);
        result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-21: Non-schema failures never enter repair, including quota, transport, refusal and truncated output.
    /// <summary>Protects free provider quota while retaining a reason-specific API failure.</summary>
    [Theory]
    [InlineData(AgentLlmFailureKind.Configuration)][InlineData(AgentLlmFailureKind.Authentication)]
    [InlineData(AgentLlmFailureKind.Timeout)][InlineData(AgentLlmFailureKind.TurnTimeout)]
    [InlineData(AgentLlmFailureKind.Network)][InlineData(AgentLlmFailureKind.RateLimited)]
    [InlineData(AgentLlmFailureKind.RejectedRequest)][InlineData(AgentLlmFailureKind.ProviderUnavailable)]
    [InlineData(AgentLlmFailureKind.InvalidResponse)][InlineData(AgentLlmFailureKind.Refused)][InlineData(AgentLlmFailureKind.Truncated)]
    public async Task RunAsync_ProviderFailure_DoesNotRepair(AgentLlmFailureKind kind)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(kind, 429, 45));
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        var error = (await act.Should().ThrowAsync<AgentProviderException>()).Which;
        error.ErrorCode.Should().Be("AGENT_UNAVAILABLE"); error.Failure.Should().Be(new AgentLlmFailure(kind, 429, 45));
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Once);
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-22: An unclassified missing response cannot be assumed to contain repairable model output.
    /// <summary>Fails closed without spending another provider request on an unknown failure.</summary>
    [Fact]
    public async Task RunAsync_NullResult_DoesNotRepair()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((AgentLlmStep?)null);
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        (await act.Should().ThrowAsync<AgentProviderException>()).Which.Failure.Kind.Should().Be(AgentLlmFailureKind.InvalidResponse);
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-23: One transient provider 503 is retried once and the recovered answer is rebuilt by the backend.
    /// <summary>Verifies classified temporary provider failures are retried within the turn deadline.</summary>
    [Fact]
    public async Task RunAsync_Transient503_RetriesWithoutRepeatingTools()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")));
        (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("có trên ANGI").And.NotContain("Recovered");
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-24: Persistent provider 503 responses stop after two retries and surface a provider error.
    /// <summary>Caps transient retries so an outage cannot consume the LLM call budget.</summary>
    [Fact]
    public async Task RunAsync_Repeated503_StopsAfterTwoRetries()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503));
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)))
            .Should().ThrowAsync<AgentProviderException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-25: A short per-minute 429 is retried once while a daily quota 429 fails immediately; covers rate_limit and daily_quota_exceeded.
    /// <summary>Distinguishes recoverable rate limits from exhausted quota before spending another provider call.</summary>
    [Theory]
    [InlineData("rate_limit", true)]
    [InlineData("daily_quota_exceeded", false)]
    public async Task RunAsync_Gemini429_RetriesOnlyShortTemporaryLimits(string reason, bool recover)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.RateLimited, 429, 1, "RESOURCE_EXHAUSTED", reason)))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")));
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("có trên ANGI").And.NotContain("Recovered");
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<AgentProviderException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(recover ? 2 : 1));
    }

    // TEST-26: A complete create_proposal selection over an existing proposal updates it with fresh ranking provenance and no repair call.
    /// <summary>Accepts create_proposal in update mode as a scoped snapshot instead of forcing a patch repair.</summary>
    [Fact]
    public async Task RunAsync_CompleteSelectionOverExistingProposal_UpdatesWithoutActionRepair()
    {
        var llm = new Mock<IAgentLlmService>();
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        var grant = AgentTestData.Grant([1]);
        context.Grants.Add(grant);
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Selection", Items = [AgentTestData.Item()] }));
        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);
        result.Snapshot!.Items.Single().RequestId.Should().Be(grant.RequestId);
        result.Reply.Should().NotContain(AgentReplyBuilder.RatingFallbackNote);
        context.RepairFeedback.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-27: Caller cancellation during a provider call propagates without repair or an availability fallback.
    /// <summary>Keeps the UI Stop action distinct from server or provider timeouts.</summary>
    [Fact]
    public async Task RunAsync_CallerCancellationDuringProvider_Propagates()
    {
        var llm = new Mock<IAgentLlmService>(); using var cancellation = new CancellationTokenSource();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-28: Failure telemetry includes safe diagnostic categories and timing without leaking prompt content.
    /// <summary>Makes future provider failures distinguishable without logging raw requests or upstream bodies.</summary>
    [Fact]
    public async Task RunAsync_ProviderFailure_RecordsSafeDiagnostics()
    {
        var llm = new Mock<IAgentLlmService>(); var telemetry = new Mock<IAgentTelemetry>(); object? captured = null;
        telemetry.Setup(t => t.Record("llm", It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object>()))
            .Callback<string, long, long?, long?, object?>((_, _, _, _, data) => captured = data);
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.RateLimited, 429, 45));
        var context = AgentTestData.Context(); context.Request.Content = "private-prompt-must-not-leak";
        var act = () => Create(llm.Object, telemetry: telemetry.Object).RunAsync(context, CancellationToken.None);
        await act.Should().ThrowAsync<AgentProviderException>();
        using var diagnostic = System.Text.Json.JsonDocument.Parse(AgentJson.Write(captured));
        diagnostic.RootElement.GetProperty("failureKind").GetString().Should().Be("RateLimited");
        diagnostic.RootElement.GetProperty("httpStatus").GetInt32().Should().Be(429);
        diagnostic.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(45);
        diagnostic.RootElement.GetProperty("callNumber").GetInt32().Should().Be(1);
        diagnostic.RootElement.GetProperty("repair").GetBoolean().Should().BeFalse();
        diagnostic.RootElement.GetProperty("elapsedMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        AgentJson.Write(captured).Should().NotContain("private-prompt");
    }

    // TEST-29: A dish repeated on day 2 is swapped for an unused authorized dish without another LLM call, and a repeat with no unused alternative stays and is disclosed by the repeat note; covers both cases.
    /// <summary>Keeps multi-day plans varied for free while never blocking a plan whose ranked candidates cannot avoid a repeat.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_RepeatedDish_IsReplacedOnlyWhenAnUnusedCandidateExists(bool spare)
    {
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", spare ? [1, 2] : [1], Guid.NewGuid(), state));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2)] }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(spare ? [1, 2] : [1, 1]);
        if (spare) result.Reply.Should().Contain("Có 1 món được hệ thống chọn lại").And.NotContain(AgentReplyBuilder.RepeatNote);
        else result.Reply.Should().Contain(AgentReplyBuilder.RepeatNote).And.NotContain("được hệ thống chọn lại");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-30: A proposal rejected only for exceeding the trip budget ends in a question about the budget with the observed figures
    // when the repaired output gives up (empty proposal), still exceeds the budget, or asks a budget clarification; covers all three.
    /// <summary>Turns "the dishes do not fit the budget" into a question the user can answer instead of a generic invalid-proposal error.</summary>
    [Theory]
    [InlineData("empty")][InlineData("still-over")][InlineData("clarify")]
    public async Task RunAsync_OverBudgetThenNoFittingProposal_AsksAboutBudget(string repair)
    {
        var state = AgentTestData.State() with { BudgetAmount = 80000, MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2], Guid.NewGuid(), state));
        var plan = new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2)] };
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], plan))
            .ReturnsAsync(new AgentLlmStep([], repair switch
            {
                "empty" => new AgentOutput { Action = "create_proposal", Reply = "Không đủ ngân sách" },
                "still-over" => plan,
                _ => new AgentOutput { Action = "clarify", Clarification = "budget", Reply = "Tăng ngân sách nhé" }
            }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("tổng 100.000 đồng/người").And.Contain("vượt ngân sách 80.000 đồng")
            .And.Contain("khoảng 40.000 đồng/bữa cho 2 bữa").And.Contain("tăng ngân sách").And.NotContain("không hợp lệ");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.Validation!.Errors.Should().Equal(AgentProposalValidator.BudgetExceeded);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-31: A budget clarification without an observed over-budget proposal is repaired once, and the repaired proposal is built.
    /// <summary>Keeps the model from asking the user to raise the budget before the backend has seen a proposal exceed it.</summary>
    [Fact]
    public async Task RunAsync_UnsupportedBudgetClarification_RepairsAndBuildsProposal()
    {
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2], Guid.NewGuid(), state));
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "budget", Reply = "Tăng ngân sách nhé" }))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft",
                Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2)] }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        result.Snapshot!.Items.Should().HaveCount(2);
        context.RepairFeedback.Should().Contain("Chưa có bằng chứng vượt ngân sách");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-32: An empty proposal before any validation is repaired once without blaming the budget; a second empty proposal ends in a question, not an invalid-proposal error.
    /// <summary>Keeps a model that gives up twice from surfacing "Lịch đề xuất không hợp lệ" to the user.</summary>
    [Fact]
    public async Task RunAsync_EmptyProposalTwiceBeforeValidation_AsksInsteadOfFailing()
    {
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2], Guid.NewGuid(), state));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Không lập được" }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        result.State.PendingQuestion.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        context.RepairFeedback.Should().Contain("ứng viên đã được cấp").And.NotContain("budgetPerMeal").And.NotContain("get_recommendation");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-33: An empty proposal with no issued candidates asks for recommendations while tools remain, and ends without another LLM call once tools are closed; covers both.
    /// <summary>Points the repair at the real gap (no candidates) and never spends a paid call that cannot fetch them.</summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RunAsync_EmptyProposalWithoutCandidates_RequestsRecommendationsOnlyWhenToolsRemain(bool toolsClosed)
    {
        var context = AgentTestData.Context(); context.State = AgentTestData.State() with { MealSlots = ["lunch"] };
        context.ToolsClosed = toolsClosed;
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Không lập được" }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        if (toolsClosed) context.RepairFeedback.Should().BeNull();
        else context.RepairFeedback.Should().Contain("get_recommendation");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(toolsClosed ? 1 : 2));
    }

    // TEST-34: After an empty-proposal repair, a full proposal rejected by validation still gets its own repair and the corrected proposal is built.
    /// <summary>Keeps the empty-proposal repair from consuming the repair reserved for validation errors.</summary>
    [Fact]
    public async Task RunAsync_EmptyProposalRepair_KeepsValidationRepairAvailable()
    {
        var state = AgentTestData.State() with { BudgetAmount = 80000, MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2, 3], Guid.NewGuid(), state));
        var cheap = AgentTestData.Dish(3); cheap.Price = 30000;
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Không lập được" }))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft",
                Items = [AgentTestData.Item(4, "a", 1), AgentTestData.Item(2, "b", 2)] }))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft",
                Items = [AgentTestData.Item(3, "a", 1), AgentTestData.Item(1, "b", 2)] }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2), cheap]).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(3, 1);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-35: The first taste question also asks the duration/budget still missing from the wording, never the province, without an LLM call;
    // covers missing budget, missing duration and budget, and a fully stated trip.
    /// <summary>Ensures the user is asked every missing required trip value together with the taste question.</summary>
    [Theory]
    [InlineData("Tôi muốn ăn ở Hà Nội 4 ngày, tôi thích ăn chua", false, true)]
    [InlineData("Tôi thích ăn chua", true, true)]
    [InlineData("Tôi muốn ăn ở Hà Nội 4 ngày, ngân sách 500k 1 ngày, tôi thích ăn chua", false, false)]
    public async Task RunAsync_TasteQuestionWithMissingTripValues_AsksThemTogether(string content, bool asksDays, bool asksBudget)
    {
        var context = AgentTestData.Context(); context.State = new AgentState(); context.Request.Content = content;
        var llm = new Mock<IAgentLlmService>();

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().StartWith("Bạn muốn mức chua").And.NotContain("tỉnh/thành phố");
        result.Reply.Contains("số ngày").Should().Be(asksDays);
        result.Reply.Contains("ngân sách cho một người").Should().Be(asksBudget);
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.PendingPlanningRequest!.Messages.Should().Equal(content);
        llm.VerifyNoOtherCalls();
    }

    // TEST-36: A recommendation requested before the budget is known ends the turn with the fixed budget question, keeps the accepted taste
    // and makes no further LLM call instead of failing with AGENT_UNAVAILABLE.
    /// <summary>Protects the user's taste answer and the turn itself when the model asks for recommendations too early.</summary>
    [Fact]
    public async Task RunAsync_RecommendationBeforeBudget_AsksBudgetAndKeepsTaste()
    {
        var context = AgentTestData.Context(); context.State = AgentTestData.State() with { BudgetAmount = null };
        var input = new AgentRecommendationInput(context.State with { TasteTargets = [new("sour", .5)] }, "Da Nang", "full", null, null, [], [], []);
        var call = new AgentToolCall("a", "get_recommendation", System.Text.Json.JsonSerializer.SerializeToElement(input, AgentJson.Options));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([call], null));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be("Bạn cho mình biết ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.TasteTargets.Should().Equal(new AgentTasteTarget("sour", .5));
        result.Snapshot.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-37: A whole-day taste proposed for "bữa 2 ngày 1" is stored as lunch of day 1, while an override already in state is not
    // narrowed by later edit wording such as "bữa 1 ngày 1"; covers both.
    /// <summary>Checks final-output narrowing applies only to the taste scope the model proposes from the user's taste wording this turn.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WholeDayTasteForNamedMeal_IsNarrowedOnlyWhenNewlyProposed(bool newTaste)
    {
        var context = AgentTestData.Context();
        var dayTaste = new AgentTasteOverride(1, null, [new("sour", .5)]);
        var state = AgentTestData.State() with { MealSlots = ["breakfast", "lunch", "dinner"] };
        context.State = newTaste
            ? state with { PendingPlanningRequest = new(["Tôi muốn ăn ở Đà Nẵng 2 ngày, tôi thích ăn chua"]) }
            : state with { TasteOverrides = [dayTaste] };
        context.Request.Content = newTaste ? "Vừa, áp dụng cho bữa 2 ngày 1" : "Đổi món khác cho bữa 1 ngày 1";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = state with { TasteOverrides = [new(1, null, [new("sour", .5)])] } }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.State.TasteOverrides.Select(x => (x.DayNumber, x.MealSlot))
            .Should().Equal(((short)1, newTaste ? "lunch" : null));
    }

    // TEST-38: A meal number beyond the meals planned per day ("bữa 5" with breakfast, lunch and dinner) is asked about without an LLM call.
    /// <summary>Ensures the backend asks which meal is meant instead of letting the model guess an out-of-range meal.</summary>
    [Fact]
    public async Task RunAsync_MealNumberBeyondPlannedMeals_AsksWhichMeal()
    {
        var context = AgentTestData.Context(); context.Request.Content = "Đổi bữa 5 ngày 1 giúp tôi";
        var llm = new Mock<IAgentLlmService>();

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be("Mỗi ngày trong lịch có 3 bữa (bữa sáng, bữa trưa, bữa tối). Bạn muốn chọn bữa nào?");
        result.State.PendingQuestion.Should().Be(result.Reply);
        llm.VerifyNoOtherCalls();
    }

    // TEST-39: "Đổi cái đó đi" over an existing plan returns the fixed edit-scope question without an LLM call.
    /// <summary>Ensures an edit that names nothing is clarified instead of being guessed as a whole-plan rewrite.</summary>
    [Fact]
    public async Task RunAsync_EditWithoutTarget_AsksWhichItem()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Content = "đổi cái đó đi";
        var llm = new Mock<IAgentLlmService>();

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().StartWith("Bạn muốn đổi món nào, hay cả lịch?");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.Snapshot.Should().BeNull();
        llm.VerifyNoOtherCalls();
    }

    // TEST-40: A repeat whose own grant has no unused dish is replaced from the backend-held reserve of the same ranked request without
    // another LLM call, and the reserve dish keeps its provenance in the snapshot.
    /// <summary>Prevents a small per-day grant from forcing a repeat while the ranked list still holds unused dishes.</summary>
    [Fact]
    public async Task RunAsync_RepeatWithOnlyReserveAlternative_IsReplacedFromReserve()
    {
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        var requestId = Guid.NewGuid();
        context.Grants.Add(new(1, "lunch", [1], requestId, state));
        context.Grants.Add(new(2, "lunch", [1], requestId, state));
        context.ReserveGrants.Add(new(2, "lunch", [2], requestId, state));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2)] }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(1, 2);
        result.Snapshot.Items.Should().OnlyContain(item => item.RequestId == requestId);
        result.Snapshot.Grants.Should().Contain(grant => grant.DayNumber == 2 && grant.DishIds.SequenceEqual(new[] { 2 }));
        result.Reply.Should().Contain("Có 1 món được hệ thống chọn lại").And.NotContain(AgentReplyBuilder.RepeatNote);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-41: A reply that ignores the open taste question clears it and lets the model answer, so the user is never stuck in a loop.
    /// <summary>Checks the abandoned taste question no longer blocks the conversation or survives into the next turn.</summary>
    [Fact]
    public async Task RunAsync_ReplyIgnoringTasteQuestion_ClearsItAndCallsModel()
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Tôi thích ăn chua"));
        context.State = context.State with { PendingQuestion = "Bạn muốn mức chua như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?" };
        context.Request.Content = "Tôi đâu có nói vậy, cho tôi xem lịch trước đã";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().NotStartWith("Bạn muốn mức");
        result.State.PendingQuestion.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-42: "Món chưa ăn bao giờ" gets the novelty question and choosing "never eaten" gets the no-history reply, both without an LLM call
    // and without any dish-name lookup.
    /// <summary>Checks novelty is clarified honestly instead of being searched as a dish called "món chưa ăn bao giờ".</summary>
    [Fact]
    public async Task RunAsync_NoveltyRequest_IsClarifiedWithoutModel()
    {
        var context = AgentTestData.Context(); context.Request.Content = "Hà Nội 3 ngày, 1 triệu. Tôi muốn món chưa ăn bao giờ";
        var llm = new Mock<IAgentLlmService>();

        var question = await Create(llm.Object).RunAsync(context, default);
        context.State = question.State; context.Request.Content = "Món chưa từng ăn";
        var answer = await Create(llm.Object).RunAsync(context, default);

        question.Reply.Should().Be(AgentReplyBuilder.NoveltyQuestion);
        answer.Reply.Should().Be(AgentReplyBuilder.NoveltyWithoutHistoryOrPlan);
        answer.State.PendingQuestion.Should().Be(AgentReplyBuilder.NoveltyWithoutHistoryOrPlan);
        context.Grants.Should().BeEmpty();
        llm.VerifyNoOtherCalls();
    }

    // TEST-43: A real answer to the taste question ("Ít, toàn chuyến đi") keeps that question in the state the model receives, while
    // a reply that ignores it is sent without it; covers both.
    /// <summary>Prevents the model from losing which question a short answer refers to, which made it ask the scope again.</summary>
    [Theory]
    [InlineData("Ít, toàn chuyến đi", true)]
    [InlineData("Tôi đâu có nói vậy, cho tôi xem lịch trước đã", false)]
    public async Task RunAsync_TasteAnswer_KeepsQuestionForModelOnlyWhenAnswered(string content, bool keepsQuestion)
    {
        const string question = "Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều? Bạn muốn áp dụng khẩu vị cay cho toàn chuyến hay ngày/bữa cụ thể nào?";
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Tôi muốn ăn ở Đà Nẵng 2 ngày, tôi thích ăn cay"));
        context.State = context.State with { PendingQuestion = question };
        context.Request.Content = content;
        string? seen = "not called";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<AgentContext, bool, CancellationToken>((ctx, _, _) => seen = ctx.State.PendingQuestion)
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object).RunAsync(context, default);

        seen.Should().Be(keepsQuestion ? question : null);
    }

    // TEST-44: Over an existing plan, a proposal answering "ăn vui vui" is not accepted: the plan and the turn-start state stay unchanged,
    // the general question is asked and no pending question blocks saving the current plan.
    /// <summary>Prevents a vague message from silently replacing meals the user never asked to change.</summary>
    [Fact]
    public async Task RunAsync_UnrequestedChangeOverPlan_KeepsPlanAndAsks()
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2));
        var context = AgentTestData.Context(source); context.Request.Content = "ăn vui vui";
        var start = context.State;
        context.Grants.Add(new(null, "lunch", [3, 4], Guid.NewGuid(), start));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", State = start with { Note = "ăn vui vẻ" },
                Items = [AgentTestData.Item(3, "a", 1), AgentTestData.Item(4, "b", 2)] }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2), AgentTestData.Dish(3), AgentTestData.Dish(4)])
            .RunAsync(context, default);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().Be("Bạn muốn xem lại lịch hiện tại hay thay đổi ngày/bữa hoặc món nào?");
        result.State.Should().Be(start);
        result.State.PendingQuestion.Should().BeNull();
        context.Source.Should().BeSameAs(source);
    }

    // TEST-45: When the model asks the taste level again after a complete answer, it gets one repair carrying the resolved value instead of
    // the user being asked again; a second repeated question is then returned as usual.
    /// <summary>Removes the flaky re-ask of "Ít, toàn chuyến đi" without letting the backend loop on a model that keeps asking.</summary>
    [Fact]
    public async Task RunAsync_ModelReasksAnsweredTaste_GetsOneRepairWithResolvedValue()
    {
        var context = AgentTestData.Context();
        context.History.Add(new("user", "Tôi muốn ăn ở Đà Nẵng 2 ngày, tôi thích ăn cay"));
        context.State = context.State with { PendingQuestion = "Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều? Bạn muốn áp dụng khẩu vị cay cho toàn chuyến hay ngày/bữa cụ thể nào?" };
        context.Request.Content = "Ít, toàn chuyến đi";
        var reask = new AgentOutput { Action = "clarify", Reply = "Mức nào?", Clarification = "taste_intensity" };
        string? feedback = null;
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], reask))
            .ReturnsAsync(() => { feedback = context.RepairFeedback; return new AgentLlmStep([], reask); });

        await Create(llm.Object).RunAsync(context, default);

        feedback.Should().Contain("spicy=0.25").And.Contain("tasteTargets");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-46: A restaurant the catalog cannot find is asked about without being kept in mealRequests, so naming another restaurant
    // next turn is a new request rather than a constraint change; a tag the turn added for that meal is kept.
    /// <summary>Stops an unfound name ("Hương Sen Phố Cổ") from turning the user's next choice into a "xác nhận thay đổi" question.</summary>
    [Fact]
    public async Task RunAsync_UnfoundRestaurant_IsAskedWithoutKeepingTheName()
    {
        var context = AgentTestData.Context(); context.Request.Content = "Bữa tối ngày 1 tôi muốn ăn ở nhà hàng Hương Sen Phố Cổ";
        var start = context.State;
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Hương Sen Phố Cổ", It.IsAny<string?>(), 21, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var search = System.Text.Json.JsonSerializer.SerializeToElement(new AgentCatalogInput("restaurant", "Hương Sen Phố Cổ", "Da Nang"), AgentJson.Options);
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "search_catalog", search)], null))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Reply = "?", Clarification = "catalog_support",
                State = start with { MealRequests = [new(1, "dinner", null, "Hương Sen Phố Cổ", [53])] } }));

        var result = await Create(llm.Object, catalogSearch: catalog.Object).RunAsync(context, default);

        result.Reply.Should().StartWith("Chưa tìm thấy món/quán theo tên yêu cầu");
        result.State.MealRequests.Should().ContainSingle().Which.Should().Match<AgentMealRequest>(request =>
            request.RestaurantName == null && request.DayNumber == 1 && request.MealSlot == "dinner" && request.TagIds.Single() == 53);
    }

    /// <summary>Builds orchestration from deterministic catalog boundaries and observable provider mocks.</summary>
    // TEST-47: An unrequested change also closes an older clarification question, so the current plan can be saved.
    /// <summary>Fixes a stale question that kept blocking "chốt lịch này" after the user moved on.</summary>
    [Fact]
    public async Task RunAsync_UnrequestedChangeWithOldQuestion_ClearsQuestion()
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2));
        var context = AgentTestData.Context(source); context.Request.Content = "ăn vui vui";
        context.State = context.State with { PendingQuestion = "Bạn có muốn xem lựa chọn khác không?" };
        context.Grants.Add(new(null, "lunch", [3, 4], Guid.NewGuid(), context.State));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(3, "a", 1), AgentTestData.Item(4, "b", 2)] }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2), AgentTestData.Dish(3), AgentTestData.Dish(4)]).RunAsync(context, default);

        result.Snapshot.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
    }

    // TEST-48: An answer closes an older clarification question; a taste question stays because it belongs to the request being clarified.
    /// <summary>Ensures only questions the user has really moved past are dropped.</summary>
    [Theory]
    [InlineData("Bạn có muốn xem lựa chọn khác không?", false)]
    [InlineData("Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?", true)]
    public async Task RunAsync_AnswerWithOldQuestion_ClearsOnlyNonTasteQuestion(string question, bool kept)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1)));
        context.Request.Content = "Có những món gì?";
        context.State = context.State with { PendingQuestion = question };
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        result.State.PendingQuestion.Should().Be(kept ? question : null);
    }

    // TEST-49: A bare acknowledgement over a plan is answered with the fixed question without a provider call and leaves the plan savable.
    /// <summary>Saves the calls an "ok" used to spend on a proposal that was then rejected.</summary>
    [Fact]
    public async Task RunAsync_BareAcknowledgement_AnswersWithoutProvider()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1)));
        context.Request.Content = "ok";
        var llm = new Mock<IAgentLlmService>();

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("bảo mình chốt");
        result.State.PendingQuestion.Should().BeNull();
        llm.VerifyNoOtherCalls();
    }

    // TEST-50: Removing a meal builds a valid preview without a provider call, records the skipped meal and remembers the dish.
    /// <summary>Fixes "xóa bữa tối ngày 2", which used to spend five calls and end in a validation error.</summary>
    [Fact]
    public async Task RunAsync_RemoveMeal_BuildsPreviewWithoutProvider()
    {
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch", "dinner"] };
        var lunch = AgentTestData.Item(1, "a", 1, "lunch") with { DishName = "Dish 1", Price = 50000 };
        var dinner = AgentTestData.Item(2, "b", 1, "dinner") with { DishName = "Dish 2", Price = 50000 };
        var context = AgentTestData.Context(new AgentSnapshot(state, [lunch, dinner], [AgentTestData.Grant()]));
        context.State = state; context.Request.Content = "Xóa bữa tối ngày 1";
        var llm = new Mock<IAgentLlmService>();

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.ItemKey).Should().Equal("a");
        result.State.SkippedMeals.Should().Equal(new AgentScope(1, "dinner"));
        result.State.ReplacedDishNames.Should().Equal("Dish 2");
        result.Reply.Should().StartWith("Xong rồi! Mình đã bỏ Dish 2. Các món còn lại giữ nguyên.").And.NotContain("Dish 1");
        llm.VerifyNoOtherCalls();
    }

    // TEST-51: Removing every item of an unsaved preview points to discarding it instead of producing an invalid empty plan.
    /// <summary>Keeps the empty-create rule while telling the user the command that does what they want.</summary>
    [Fact]
    public async Task RunAsync_RemoveEverythingFromDraft_PointsToDiscard()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1) with { DishName = "Dish 1" }));
        context.Request.Content = "bỏ món Dish 1";
        var llm = new Mock<IAgentLlmService>();

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("bỏ đề xuất");
        llm.VerifyNoOtherCalls();
    }

    // TEST-52: A proposal only over budget is repaired from cheaper ranked dishes without another provider call, and the reply discloses the swap.
    /// <summary>Fixes a low budget ending in "raise your budget" although cheaper dishes had been ranked.</summary>
    [Fact]
    public async Task RunAsync_OverBudgetWithCheaperDish_RepairsWithoutProvider()
    {
        var state = AgentTestData.State() with { BudgetAmount = 80000, MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        var cheap = AgentTestData.Dish(3); cheap.Price = 30000;
        context.Grants.Add(new(null, "lunch", [1, 2, 3], Guid.NewGuid(), state));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2)] }));

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2), cheap]).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Contain(3);
        result.Reply.Should().Contain(AgentReplyBuilder.SubstitutionNote);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-53: A daily budget the model misread is corrected from the user's wording before validation.
    /// <summary>Prevents the 1,500 đồng budget the model produced for "500k 1 ngày".</summary>
    [Fact]
    public async Task RunAsync_ModelMisreadsDailyBudget_UsesUserWording()
    {
        var context = AgentTestData.Context();
        context.State = new AgentState { MealSlots = [] };
        context.Request.Content = "Lập lịch 2 ngày ở Da Nang, ngân sách 500k 1 ngày";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "clarify", Clarification = "general", Reply = "?", State = new AgentState { Province = "Da Nang", NumDays = 2, BudgetAmount = 1000 } }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.State.BudgetAmount.Should().Be(1000000);
    }

    // TEST-54: "Xin chào" over a plan answers with the self-introduction and never calls the provider, changes the plan or clears an open planning question.
    /// <summary>Fixes the greeting that used to print the whole itinerary and spend a paid call.</summary>
    [Fact]
    public async Task RunAsync_Greeting_AnswersWithoutProviderOrPlanChange()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng" }));
        context.Request.Content = "Xin chào";
        var pending = new AgentPendingPlanningRequest(["Lập lịch 3 ngày"]);
        context.State = context.State with { PendingQuestion = "Bạn muốn mức cay như thế nào?", PendingPlanningRequest = pending };
        var llm = new Mock<IAgentLlmService>();

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().StartWith(AgentSmallTalkPolicy.GreetingFor(context.State)).And.Contain("Bạn muốn mức cay như thế nào?").And.NotContain("Mì Quảng");
        result.Action.Should().Be("chat");
        result.Snapshot.Should().BeNull(); result.Validation.Should().BeNull();
        result.State.PendingQuestion.Should().Be("Bạn muốn mức cay như thế nào?");
        result.State.PendingPlanningRequest.Should().Be(pending);
        llm.VerifyNoOtherCalls();
    }

    // TEST-55: A model-written social reply is shown, while its state, items and changes are ignored; covers a safe reply and one falling back for stating a price.
    /// <summary>Ensures a social turn can sound natural but can never alter the trip or the plan.</summary>
    [Theory]
    [InlineData("Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!", "Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!")]
    [InlineData("Phở chỉ 50000 thôi", AgentSmallTalkPolicy.ChatFallback)]
    public async Task RunAsync_ChatAction_ShowsSafeReplyAndKeepsState(string draft, string expected)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "Hôm nay bạn thế nào?";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "chat", Reply = draft, State = new AgentState { Province = "Hue", NumDays = 9 }, Items = [AgentTestData.Item(9, "z")] }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be(expected);
        result.Action.Should().Be("chat");
        result.Snapshot.Should().BeNull();
        result.State.Province.Should().Be("Da Nang"); result.State.NumDays.Should().Be(2);
        llm.Verify(x => x.RunAsync(context, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-56: "Hướng dẫn bóc cam" gets the owner's exact refusal in one call, whether or not a plan exists, ignoring the model's draft, state and items.
    /// <summary>Guarantees out-of-scope requests are refused instead of printing the plan or asking for trip details.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_OutOfScope_RefusesWithExactSentence(bool hasPlan)
    {
        var context = AgentTestData.Context(hasPlan ? AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng" }) : null);
        context.State = new AgentState();
        context.Request.Content = "Hướng dẫn bóc cam";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "out_of_scope", Reply = "Trước tiên hãy khía vỏ cam", State = new AgentState { Province = "Hue" }, Items = [AgentTestData.Item(9, "z")] }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be("Việc này nằm ngoài khả năng của mình rồi. Mình chỉ giúp được về món ăn, quán và lịch ăn uống thôi.");
        result.Action.Should().Be("out_of_scope");
        result.Snapshot.Should().BeNull();
        result.State.Province.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-57: A general food question is refused with an offer to find the dish the user named; covers a dish in the message and one the model invented.
    /// <summary>Gives a useful next step while keeping the offer tied to the user's own words.</summary>
    [Theory]
    [InlineData("phở", " Bạn có muốn mình tìm quán phở không?")]
    [InlineData("bún chả", "")]
    public async Task RunAsync_OutOfScopeFoodQuestion_OffersOnlyTheNamedDish(string dish, string offer)
    {
        var context = AgentTestData.Context();
        context.Request.Content = "Cách nấu phở";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "out_of_scope", Reply = "Draft", SuggestedDish = dish }));

        (await Create(llm.Object).RunAsync(context, default)).Reply.Should().Be(AgentSmallTalkPolicy.OutOfScope + offer);
    }

    // TEST-58: An off-topic reply to an open taste question keeps that question and does not join the request being planned.
    /// <summary>Prevents "bóc cam" from clearing the taste question or being read as part of the trip request.</summary>
    [Fact]
    public async Task RunAsync_OutOfScopeDuringTasteQuestion_KeepsQuestionAndPlanningRequest()
    {
        var context = AgentTestData.Context();
        const string question = "Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?";
        var pending = new AgentPendingPlanningRequest(["Lập lịch, tôi thích ăn cay"]);
        context.State = context.State with { PendingQuestion = question, PendingPlanningRequest = pending };
        context.History.Add(new("user", "Lập lịch, tôi thích ăn cay"));
        context.Request.Content = "Hướng dẫn bóc cam";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "out_of_scope", Reply = "Draft" }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be(AgentSmallTalkPolicy.OutOfScope);
        result.State.PendingQuestion.Should().Be(question);
        result.State.PendingPlanningRequest.Should().Be(pending);
    }

    // TEST-59: A question answered by the model no longer dumps the plan; a request to see it over a saved plan still lists it.
    /// <summary>Verifies the end-to-end behaviour of the answer action for both kinds of message.</summary>
    [Theory]
    [InlineData("Vì sao chọn món này?", false)]
    [InlineData("Xem lại lịch giúp mình", true)]
    public async Task RunAsync_AnswerAction_ListsPlanOnlyWhenAsked(string content, bool listed)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng" }));
        context.Request.Content = content;
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Contains("Mì Quảng").Should().Be(listed);
        if (!listed) result.Reply.Should().Be(AgentReplyBuilder.AnswerUnavailable);
    }

    // TEST-60: An empty reply is not a failure: refusals and social messages use the backend's fixed text in one call; this was the "hướng dẫn tôi" 503.
    /// <summary>Reproduces conversation 88: the model left reply empty because the backend owns the refusal, which cost repairs and ended in a 503.</summary>
    [Theory]
    [InlineData("out_of_scope", AgentSmallTalkPolicy.OutOfScope)]
    [InlineData("chat", AgentSmallTalkPolicy.ChatFallback)]
    public async Task RunAsync_EmptyReply_UsesFixedReplyInOneCall(string action, string expected)
    {
        var context = AgentTestData.Context();
        context.Request.Content = "hướng dẫn tôi gọt vỏ cam";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "" }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be(expected);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-61: An empty reply on a clarify or answer turn also falls back to the fixed wording without a repair call.
    /// <summary>Ensures no paid repair call is spent on wording the backend can supply itself.</summary>
    [Theory]
    [InlineData("clarify")][InlineData("answer")]
    public async Task RunAsync_EmptyReplyOnQuestion_UsesFixedWordingWithoutRepair(string action)
    {
        var context = AgentTestData.Context();
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "   " }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Trim().Should().NotBeEmpty();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-62: Output still unusable after its one repair asks the user again; the state is unchanged and telemetry never holds the model's text.
    /// <summary>Bounds paid calls at two and replaces the old 503 with a question the user can answer.</summary>
    [Fact]
    public async Task RunAsync_UnusableOutputAfterRepair_AsksAgainWithoutLeakingText()
    {
        var context = AgentTestData.Context();
        var secret = new string('x', new AgentLimits().MaxMessageLength + 1);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = secret }));
        var telemetry = new Mock<IAgentTelemetry>();

        var result = await Create(llm.Object, telemetry: telemetry.Object).RunAsync(context, default);

        result.Reply.Should().Be(AgentSmallTalkPolicy.NotUnderstood);
        result.State.Should().Be(context.State);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        telemetry.Verify(x => x.Record("invalid_output", 7, It.IsAny<long?>(), It.IsAny<long?>(), It.Is<object?>(data => !System.Text.Json.JsonSerializer.Serialize(data, (System.Text.Json.JsonSerializerOptions?)null).Contains("xxx"))), Times.Exactly(2));
    }

    // TEST-63: Items attached to a question are dropped rather than failing the turn, and never reach a proposal.
    /// <summary>A clarify or answer can never edit a plan, so its stray items are simply ignored.</summary>
    [Fact]
    public async Task RunAsync_QuestionWithItems_IgnoresItems()
    {
        var context = AgentTestData.Context();
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { Items = [AgentTestData.Item()] }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Snapshot.Should().BeNull();
        result.Reply.Should().NotBeNullOrWhiteSpace();
    }

    // TEST-64: Discard or reset carrying a state or items is refused without a service error and without discarding anything.
    /// <summary>Keeps draft controls from also mutating the plan, while the user still gets an answer.</summary>
    [Fact]
    public async Task RunAsync_ControlWithPayload_AsksAgain()
    {
        var context = AgentTestData.Context();
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "reset_conversation", Reply = "ok", Items = [AgentTestData.Item()] }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Action.Should().BeNull();
        result.Reply.Should().Be(AgentSmallTalkPolicy.NotUnderstood);
    }

    // TEST-65: The reported flow: "lap lich cho toi" asks the place first, then budget, then preferences, with no provider call; a title holding a place does not answer it.
    /// <summary>Walks the whole intake over an empty three-day roadmap whose title names Hà Nội.</summary>
    [Fact]
    public async Task RunAsync_NewPlan_AsksPlaceThenBudgetThenPreferences()
    {
        var llm = new Mock<IAgentLlmService>(MockBehavior.Strict);
        var sut = Create(llm.Object, intakeCatalog: IntakeCatalog());
        var state = new AgentState { NumDays = 3, Title = "3 ngày ăn ngon Hà Nội" };

        var place = await sut.RunAsync(IntakeContext("lap lich cho toi", state), default);
        place.Reply.Should().Be(AgentIntakePolicy.PlaceQuestion);
        place.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        place.State.Province.Should().BeNull();

        var budget = await sut.RunAsync(IntakeContext("Hà Nội", place.State), default);
        budget.Reply.Should().StartWith("Mình ghi nhận Hà Nội.").And.Contain("ngân sách").And.NotContain("số ngày");
        budget.State.Province.Should().Be("Hà Nội");

        var preferences = await sut.RunAsync(IntakeContext("ngân sách 500k 1 ngày", budget.State), default);
        preferences.Reply.Should().Be(AgentIntakePolicy.PreferencesQuestion);
        preferences.State.BudgetAmount.Should().Be(1500000);
        preferences.State.PlanningIntake.Should().Be(AgentIntakePolicy.Preferences);
        preferences.State.PendingQuestion.Should().Be(AgentIntakePolicy.PreferencesQuestion);
    }

    // TEST-66: Answering the preference question closes the intake and plans in the same turn; "không" reaches the model marked as adding no requirement.
    /// <summary>Ensures "không" is not read as a dismissal and that the plan is not blocked as an unrequested change on an empty roadmap.</summary>
    [Fact]
    public async Task RunAsync_PreferenceAnswer_ClosesIntakeAndPlans()
    {
        var state = new AgentState { Province = "Da Nang", NumDays = 1, BudgetAmount = 500000, MealSlots = ["lunch"], PlanningIntake = AgentIntakePolicy.Preferences,
            PendingQuestion = AgentIntakePolicy.PreferencesQuestion, PendingPlanningRequest = new(["lap lich cho toi"]) };
        var context = IntakeContext("không", state, AgentTestData.Snapshot());
        context.Grants.Add(new(null, null, [1], Guid.NewGuid(), state));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Xong rồi nhé!", Items = [AgentTestData.Item()] }));

        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        context.IntakeComplete.Should().BeTrue();
        result.Snapshot.Should().NotBeNull();
        result.State.PlanningIntake.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
    }

    // TEST-67: A model that asks again right after the intake gets one repair and then plans; a second question is shown without a third call.
    /// <summary>Bounds the extra paid call to one while protecting the user from being asked what they just answered.</summary>
    [Fact]
    public async Task RunAsync_ModelAsksAgainAfterIntake_RepairsOnce()
    {
        var state = new AgentState { Province = "Da Nang", NumDays = 1, BudgetAmount = 500000, PlanningIntake = AgentIntakePolicy.Preferences, PendingPlanningRequest = new(["lap lich"]) };
        var context = IntakeContext("không", state);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "general", Reply = "Bạn muốn gì?" }));

        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        result.Reply.Should().NotBeNullOrWhiteSpace();
        context.RepairFeedback.Should().Contain("Không hỏi lại");
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-68: Current location resolves the province from the nearest restaurant and limits candidates to 10 km; covers a covered and an uncovered location.
    /// <summary>Keeps recommendations geographic without a map provider call, and never keeps coordinates the trip cannot use.</summary>
    [Theory]
    [InlineData("Hà Nội")]
    [InlineData(null)]
    public async Task RunAsync_CurrentLocation_ResolvesNearestProvince(string? nearest)
    {
        var state = new AgentState { NumDays = 3, OriginMode = "gps", OriginLatitude = 21.03m, OriginLongitude = 105.85m, OriginLabel = "Vị trí hiện tại" };
        var llm = new Mock<IAgentLlmService>(MockBehavior.Strict);

        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog(nearest)).RunAsync(IntakeContext("Dùng vị trí hiện tại của mình", state), default);

        if (nearest is null)
        {
            result.Reply.Should().Contain("chưa có quán nào").And.Contain("Hà Nội").And.EndWith(AgentIntakePolicy.PlaceQuestion);
            result.State.Province.Should().BeNull();
            result.State.OriginMode.Should().Be("none");
            result.State.OriginLatitude.Should().BeNull();
            result.State.RadiusKm.Should().BeNull();
        }
        else
        {
            result.Reply.Should().StartWith("Bạn đang ở gần Hà Nội").And.Contain("10 km");
            result.State.Province.Should().Be("Hà Nội");
            result.State.RadiusKm.Should().Be(AgentIntakePolicy.DefaultRadiusKm);
        }
    }

    // TEST-69: Asking for "current location" as plain text, without coordinates from the browser, says how to share it instead of guessing a place.
    /// <summary>Covers a denied browser permission, where the client falls back to sending only the text.</summary>
    [Theory]
    [InlineData("vị trí hiện tại")][InlineData("gần đây thôi")][InlineData("quanh day")]
    public async Task RunAsync_HereWordsWithoutCoordinates_ExplainsHow(string content)
    {
        var state = new AgentState { PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion };

        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog()).RunAsync(IntakeContext(content, state), default);

        result.Reply.Should().Be(AgentIntakePolicy.LocationNeeded);
        result.State.Province.Should().BeNull();
    }

    // TEST-70: A short unknown place asks again naming the covered provinces; a long unrelated sentence closes the intake and goes to the model.
    /// <summary>Ensures the questions never trap a user who changed the subject.</summary>
    [Fact]
    public async Task RunAsync_UnansweredPlace_RepeatsOrStepsAside()
    {
        var state = new AgentState { PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion };
        var strict = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog());

        var unknown = await strict.RunAsync(IntakeContext("Đà Nẵng", state), default);
        unknown.Reply.Should().Contain("Hiện ANGI có dữ liệu ở Hà Nội").And.EndWith(AgentIntakePolicy.PlaceQuestion);

        var aside = IntakeContext("vì sao bạn lại hỏi mình nhiều câu như vậy", state);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(aside, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Mình cần biết địa điểm.")));

        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(aside, default);

        result.State.PlanningIntake.Should().BeNull();
        llm.Verify(x => x.RunAsync(aside, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-71: Facts in one message are all taken, and only what is missing is asked; covers everything at once and a stated preference skipping the preference question.
    /// <summary>Ensures nobody is asked again for what they already wrote in one sentence.</summary>
    [Fact]
    public async Task RunAsync_AllFactsInOneMessage_AsksOnlyWhatIsMissing()
    {
        var strict = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog());

        var all = await strict.RunAsync(IntakeContext("lập lịch Hà Nội 3 ngày 1 triệu", new AgentState()), default);
        all.Reply.Should().EndWith(AgentIntakePolicy.PreferencesQuestion);
        all.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.NumDays == 3 && x.BudgetAmount == 1000000);

        var noBudget = await strict.RunAsync(IntakeContext("lập lịch Hà Nội 3 ngày", new AgentState()), default);
        noBudget.Reply.Should().Contain("ngân sách").And.NotContain("số ngày");

        var taste = await strict.RunAsync(IntakeContext("lập lịch Hà Nội 3 ngày 1 triệu, thích ăn cay", new AgentState()), default);
        taste.Reply.Should().StartWith("Bạn muốn mức cay");
        taste.State.PlanningIntake.Should().BeNull();
    }

    // TEST-72: With no plan to disturb, the user's own answer is applied without asking them to confirm what they just said, and no paid call is made.
    /// <summary>Answering the questions with a new duration or "no limit", including slang, moves straight on.</summary>
    [Theory]
    [InlineData("lập lịch Hà Nội 5 ngày", 5, false)]
    [InlineData("Hà Nội, không giới hạn", 3, true)]
    [InlineData("Hà Nội, xả láng", 3, true)]
    public async Task RunAsync_AnswerWithoutAPlan_AppliesWithoutConfirmation(string content, short numDays, bool unrestricted)
    {
        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext(content, new AgentState { NumDays = 3, PlanningIntake = AgentIntakePolicy.Details }), default);

        result.State.PendingStateChange.Should().BeNull();
        result.State.NumDays.Should().Be(numDays);
        result.State.BudgetUnrestricted.Should().Be(unrestricted);
        result.State.Province.Should().Be("Hà Nội");
    }

    // TEST-73: An invalid duration in an answer repeats the validator's message and keeps the question open.
    /// <summary>Ensures a bad answer is explained to the user instead of failing the turn.</summary>
    [Fact]
    public async Task RunAsync_InvalidAnswer_ExplainsLimit()
    {
        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext("lập lịch Hà Nội 145 ngày", new AgentState()), default);

        result.Reply.Should().StartWith("Số ngày phải từ 1 đến 100.");
        result.State.NumDays.Should().BeNull();
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
    }

    // TEST-74: A plan already on screen skips the questions without querying the catalog.
    /// <summary>Ensures edits keep their existing path and cost nothing extra.</summary>
    [Fact]
    public async Task RunAsync_ExistingPlan_SkipsIntake()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "lập lịch lại";
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>(MockBehavior.Strict);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object, intakeCatalog: catalog.Object).RunAsync(context, default);

        llm.Verify(x => x.RunAsync(context, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-75: A bare request for help gets the fixed guide with no provider call, and repeats an open question; this was the other half of the "hướng dẫn tôi" failure.
    /// <summary>Answers the reported short message for free, whatever stage the conversation is in.</summary>
    [Fact]
    public async Task RunAsync_BareHelp_AnswersWithoutProvider()
    {
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog());

        (await sut.RunAsync(IntakeContext("huong dan toi", new AgentState()), default)).Reply.Should().Be(AgentSmallTalkPolicy.Help);
        var open = new AgentState { PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion };
        var result = await sut.RunAsync(IntakeContext("hướng dẫn tôi", open), default);
        result.Reply.Should().Be($"{AgentSmallTalkPolicy.Help}\n{AgentIntakePolicy.PlaceQuestion}");
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
    }

    // TEST-76: The reported itinerary message reaches the budget question with every day placed, with no provider call and without searching places as restaurants.
    /// <summary>Replays the start of conversation 94, which used to cost two LLM calls and end in "chưa tìm thấy món/quán".</summary>
    [Fact]
    public async Task RunAsync_ReportedItinerary_AsksBudgetWithoutProvider()
    {
        var context = IntakeContext("Ngày 1 tôi ở Hoàn kiếm\nNgày 2 tôi đi lăng bác\nNgày 3 tôi ở Hoàng Mai\nHãy lập lịch đi ăn", new AgentState());
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(context, default);

        result.Reply.Should().StartWith("Mình ghi nhận hành trình ở Hà Nội: ngày 1 quanh Hoàn Kiếm").And.Contain("ngày 2 quanh Lăng Bác").And.Contain("ngày 3 quanh Hoàng Mai").And.Contain("ngân sách");
        result.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.NumDays == 3 && x.PlanningIntake == AgentIntakePolicy.Details);
        result.State.DayAreas.Should().HaveCount(3);
    }

    // TEST-77: A longer trip keeps its duration and says what the unnamed days will do, which is how a four-day roadmap frame met a three-day itinerary.
    /// <summary>Ensures the mismatch of conversation 94 is stated to the user instead of silently planning a day they did not mention.</summary>
    [Fact]
    public async Task RunAsync_ItineraryShorterThanTrip_StatesTheRemainingDays()
    {
        var context = IntakeContext("Ngày 1 tôi ở Hoàn Kiếm, ngày 2 đi lăng bác", new AgentState { NumDays = 4, SourceRoadmapUpdatedAt = DateTime.UtcNow });
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(context, default);

        result.State.NumDays.Should().Be(4);
        result.Reply.Should().Contain("Những ngày còn lại trong 4 ngày, mình chọn trong toàn Hà Nội.");
    }

    // TEST-78: A bare area answers the place question for free; an unknown one keeps the old question and shows no province.
    /// <summary>Replays "Hai bà trưng", which used to end in "chưa nhận ra địa điểm", and an unknown name beside it.</summary>
    [Theory]
    [InlineData("Hai bà trưng", "khu vực Hai Bà Trưng")]
    [InlineData("Vậy thì ở Cầu Giấy", "khu vực Cầu Giấy")]
    [InlineData("xyzxyz", "Mình chưa nhận ra địa điểm")]
    public async Task RunAsync_AreaAnswersPlaceQuestion(string content, string expected)
    {
        var open = new AgentState { PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion };
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        (await sut.RunAsync(IntakeContext(content, open), default)).Reply.Should().Contain(expected);
    }

    // TEST-79: A model that sends an unusable proposal twice on a new plan no longer fails the turn with PROPOSAL_NOT_COMMITTABLE: the backend assembles the plan for no further call.
    /// <summary>Reproduces the "bản nháp không đủ điều kiện" error of conversation 94's failed turn, which forced a second paid attempt.</summary>
    [Fact]
    public async Task RunAsync_UnusableProposalTwiceOnNewPlan_BackendAssemblesPlan()
    {
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"] };
        context.Grants.Add(new(1, "lunch", [1], null, context.State));
        var unusable = new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Items = [AgentTestData.Item()], Changes = [new("add", null, AgentTestData.Item())] };
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], unusable));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Snapshot!.Items.Should().ContainSingle(item => item.DishId == 1 && item.DayNumber == 1 && item.MealSlot == "lunch");
        result.Validation!.Errors.Should().BeEmpty();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-80: The same failure on an edit keeps the plan on screen, savable and unchanged, instead of an error.
    /// <summary>Ensures a model failure never destroys or blocks the plan the user already reviewed.</summary>
    [Fact]
    public async Task RunAsync_UnusableProposalTwiceOnExistingPlan_KeepsPlan()
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var context = AgentTestData.Context(source);
        context.Request.Content = "đổi bữa trưa ngày 1 sang món khác";
        var unusable = new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Items = [AgentTestData.Item(2, "b")], Changes = [new("add", null, AgentTestData.Item(2, "b"))] };
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], unusable));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        result.Snapshot.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-81: Moving a day of an existing plan updates the state before the model runs, and its proposal is not discarded as "unrequested" although the wording has no edit verb.
    /// <summary>Ensures "Ngày 1 tôi ở Hoàn Kiếm" changes the plan, where it used to be searched as a restaurant.</summary>
    [Fact]
    public async Task RunAsync_ItineraryOnExistingPlan_MovesDayAndAcceptsProposal()
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var context = AgentTestData.Context(source);
        context.State = context.State with { Province = "Hà Nội", NumDays = 1, MealSlots = ["lunch"] };
        context.Request.Content = "Ngày 1 tôi ở Hoàn Kiếm";
        var dish = AgentTestData.Dish(2);
        dish.Restaurant.ProvinceName = "Hà Nội"; dish.Restaurant.Latitude = 21.03m; dish.Restaurant.Longitude = 105.85m;
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [2], null, context.State));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Scope = new(1, "lunch"), Items = [AgentTestData.Item(2, "a")] }))!;
        });

        var result = await Create(llm.Object, catalog: [dish], intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        context.ItineraryChanged.Should().BeTrue();
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.State.DayAreas!.Single().Label.Should().Be("Hoàn Kiếm");
        result.Snapshot.Items.Single().DishId.Should().Be(2);
    }

    // TEST-82: A day past the trip, or naming the same place again, changes nothing and is not flagged as a move.
    /// <summary>Ensures an unchanged or impossible itinerary does not force a replan, and the duration still goes through confirmation.</summary>
    [Theory]
    [InlineData("Ngày 9 tôi ở Hoàn Kiếm", false)]
    [InlineData("Ngày 1 tôi ở Hoàn Kiếm", true)]
    public async Task RunAsync_ItineraryWithoutChange_IsNotAMove(string content, bool alreadyThere)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội", DayAreas = alreadyThere ? [new(1, "Hoàn Kiếm", 21.03m, 105.85m, 3m)] : null };
        context.Request.Content = content;
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        context.ItineraryChanged.Should().BeFalse();
    }

    // TEST-83: A place with no day on an existing multi-day plan asks which day, free of charge, keeps the plan and holds the place for the answer.
    /// <summary>Implements the user's rule: "đổi sang khu Cầu Giấy" must not guess days, in any of the short wordings.</summary>
    [Theory]
    [InlineData("đổi sang khu Cầu Giấy")]
    [InlineData("ở Cầu Giấy thôi")]
    [InlineData("đổi hành trình sang Cầu Giấy")]
    public async Task RunAsync_AreaWithoutDayOnExistingPlan_AsksWhichDay(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội" };
        context.Request.Content = content;
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(context, default);

        result.Reply.Should().StartWith("Bạn muốn đổi ngày nào sang khu vực Cầu Giấy?");
        result.Snapshot.Should().BeNull();
        result.State.PendingDayArea!.Label.Should().Be("Cầu Giấy");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.DayAreas.Should().BeNull();
    }

    // TEST-84: The answer moves exactly those days and then goes to the model for new dishes; covers one day, several days and the whole trip.
    /// <summary>Ensures a short "ngày 2" finishes the change the previous message started, and clears the question so the plan can be saved afterwards.</summary>
    [Theory]
    [InlineData("ngày 2", new short[] { 2 })]
    [InlineData("ngày 1 và 2", new short[] { 1, 2 })]
    [InlineData("cả chuyến", new short[] { 1, 2 })]
    public async Task RunAsync_AnswerToWhichDay_MovesThoseDays(string content, short[] days)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội", PendingQuestion = "Bạn muốn đổi ngày nào?", PendingDayArea = new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m) };
        context.Request.Content = content;
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        context.MovedDays.Should().Equal(days);
        context.State.DayAreas!.Select(area => area.DayNumber).Should().Equal(days);
        context.State.DayAreas!.Should().OnlyContain(area => area.Label == "Cầu Giấy");
        context.State.PendingDayArea.Should().BeNull();
    }

    // TEST-85: Any other reply ends the wait and goes on normally, so the user is never stuck; nothing moves.
    /// <summary>Ensures "đổi bữa tối sang phở" after the question is treated as what it says, and that the place does not linger for a later "ngày 2".</summary>
    [Fact]
    public async Task RunAsync_OtherReplyToWhichDay_DropsThePlace()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội", PendingQuestion = "Bạn muốn đổi ngày nào?", PendingDayArea = new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m) };
        context.Request.Content = "đổi bữa trưa ngày 1 sang món khác";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        context.ItineraryChanged.Should().BeFalse();
        context.State.DayAreas.Should().BeNull();
        context.State.PendingDayArea.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-86: Answering with a day already there changes nothing and costs no model call.
    /// <summary>Ensures choosing the current place cannot trigger a paid replan.</summary>
    [Fact]
    public async Task RunAsync_AnswerToWhichDayWithSamePlace_KeepsPlanWithoutProvider()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội", PendingQuestion = "Bạn muốn đổi ngày nào?", PendingDayArea = new("Hà Nội", "Cầu Giấy", 21.03m, 105.79m, 3m),
            DayAreas = [new(2, "Cầu Giấy", 21.03m, 105.79m, 3m)] };
        context.Request.Content = "ngày 2";
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(context, default);

        result.Reply.Should().Contain("giữ nguyên lịch");
        result.State.PendingQuestion.Should().BeNull();
        result.State.PendingDayArea.Should().BeNull();
    }

    // TEST-87: A one-day trip has nothing to ask: the place applies to that day and goes to the model.
    /// <summary>Ensures the question is not asked when there is only one possible answer.</summary>
    [Fact]
    public async Task RunAsync_AreaWithoutDayOnOneDayTrip_MovesThatDay()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội", NumDays = 1 };
        context.Request.Content = "đổi sang khu Cầu Giấy";
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        context.MovedDays.Should().Equal((short)1);
    }

    // TEST-88: With no plan yet, only naming a place starts the questions around it for free, whether a district or a landmark.
    /// <summary>Answers "ở Hoàn Kiếm" and "đi Lăng Bác" as the first message, which used to reach the model.</summary>
    [Theory]
    [InlineData("ở Hoàn Kiếm", "khu vực Hoàn Kiếm")]
    [InlineData("tôi đi Lăng Bác", "khu vực Lăng Bác")]
    public async Task RunAsync_AreaWithoutPlan_StartsQuestionsWithoutProvider(string content, string expected)
    {
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(IntakeContext(content, new AgentState()), default);

        result.Reply.Should().Contain(expected).And.Contain("số ngày");
        result.State.Should().Match<AgentState>(x => x.OriginMode == "pinned" && x.Province == "Hà Nội" && x.RadiusKm != null);
    }

    // TEST-89: A trip only to a place with no restaurants says so, says where ANGI has data and asks again, free of charge and without inventing a province.
    /// <summary>Ensures "Ngày 1 đi Đà Nẵng" is answered honestly instead of planning in Hà Nội or asking as if nothing was said.</summary>
    [Fact]
    public async Task RunAsync_OnlyPlaceOutsideCatalog_SaysWhereDataExistsAndAsks()
    {
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(IntakeContext("Ngày 1 đi Đà Nẵng\nlập lịch giúp mình", new AgentState()), default);

        result.Reply.Should().StartWith("Mình chưa tìm được quán quanh “Đà Nẵng”.").And.Contain("Hiện ANGI có dữ liệu ở Hà Nội").And.EndWith(AgentIntakePolicy.PlaceQuestion);
        result.State.Province.Should().BeNull();
        result.State.DayAreas.Should().BeNull();
    }

    // TEST-90: The itinerary of conversation 121 reads Hồ Gươm at the lake, keeps a free day for the whole province and still asks about preferences, for free.
    /// <summary>Replays the message that put day 1 in Hà Đông, made day 3 a place called "Tự Do", and skipped the preferences question because of "tham quan".</summary>
    [Fact]
    public async Task RunAsync_Conversation121Itinerary_ReadsCorrectly()
    {
        var context = IntakeContext("Hiện tại tôi có hành trình sau : \nNgày 1 : Tôi đi tham quan Hồ Gươm \nNgày 2 : Tôi đi Lăng Bác \nNgày 3 : Tôi đi tự do \nHãy lập lịch ăn uống theo hành trình này",
            new AgentState { BudgetAmount = 900000 });
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(context, default);

        result.State.NumDays.Should().Be(3);
        result.State.DayAreas!.Select(area => (area.DayNumber, area.Label)).Should().Equal(((short)1, "Hồ Gươm"), ((short)2, "Lăng Bác"));
        result.State.DayAreas![0].Latitude.Should().Be(21.0287m);
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Preferences);
        result.Reply.Should().Contain("Những ngày còn lại trong 3 ngày, mình chọn trong toàn Hà Nội.").And.Contain(AgentIntakePolicy.PreferencesQuestion);
    }

    // TEST-91: Both complaints of conversation 121 move day 1 to the right place and reach the model for new dishes; these were the turns that repeated the wrong area and gave up.
    /// <summary>Ensures saying "ngày 1 sai" with the right place fixes the plan, whichever way the day is named.</summary>
    [Theory]
    [InlineData("Ngày 1 đang đề xuất sai, tôi đi Hồ Gươm nhưng sao roadmap lại chỉ sang Hà Đông nhỉ")]
    [InlineData("ý tôi là Hồ Gươm ở Hoàn Kiếm mà sao lại đề xuất ở Hà Đông")]
    public async Task RunAsync_ComplaintWithRightPlace_MovesTheDay(string content)
    {
        var context = WrongDayOneContext(content);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        context.MovedDays.Should().Equal((short)1);
        var day1 = context.State.DayAreas!.Single(area => area.DayNumber == 1);
        day1.Latitude.Should().Be(21.0287m);
        day1.Longitude.Should().Be(105.8524m);
        context.State.DayAreas!.Single(area => area.DayNumber == 2).Label.Should().Be("Lăng Bác");
    }

    // TEST-92: A complaint with no place explains where the plan searches and how to fix it, with no provider call and without blocking the save.
    /// <summary>Covers the bare "ngày 1 sai" that used to confuse the model; the reply names the place it uses and its district.</summary>
    [Fact]
    public async Task RunAsync_ComplaintWithoutPlace_ExplainsWithoutProvider()
    {
        var context = WrongDayOneContext("ngày 1 sai rồi");
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(context, default);

        result.Reply.Should().Contain("ngày 1 quanh Hồ Gươm (khu Hà Đông)").And.Contain("“Ngày 1 ở Hoàn Kiếm”").And.Contain("đổi bữa trưa ngày 1 sang món khác");
        result.Reply.Should().NotContain("Lăng Bác");
        result.State.PendingQuestion.Should().BeNull();
        result.Snapshot.Should().BeNull();
    }

    // TEST-93: A right place with no day to bind asks which day, and a place that cannot be located says so; neither reaches the model.
    /// <summary>Ensures a correction never guesses a day or claims to have fixed something it could not find.</summary>
    [Theory]
    [InlineData("sai rồi, tôi ở Cầu Giấy", "Bạn muốn đổi ngày nào sang khu vực Cầu Giấy?")]
    [InlineData("ngày 1 sai, tôi đi Xyzxyz", "Mình chưa định vị được “Xyzxyz”.")]
    public async Task RunAsync_ComplaintWithoutDayOrUnknownPlace_AsksWithoutProvider(string content, string expected)
    {
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        (await sut.RunAsync(WrongDayOneContext(content), default)).Reply.Should().StartWith(expected);
    }

    // TEST-94: Complaints about a dish, and any plan without itinerary places, keep the model's normal edit flow.
    /// <summary>Guards the rule that the new correction case does not take over edits handled before.</summary>
    [Theory]
    [InlineData("bữa trưa ngày 1 sai vị rồi", true)]
    [InlineData("món này sai rồi", true)]
    [InlineData("ngày 1 sai rồi", false)]
    public async Task RunAsync_ComplaintAboutDishOrPlanWithoutPlaces_KeepsModelFlow(string content, bool withPlaces)
    {
        var context = withPlaces ? WrongDayOneContext(content) : IntakeContext(content, AgentTestData.State() with { Province = "Hà Nội" }, AgentTestData.Snapshot(AgentTestData.Item()));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        context.ItineraryChanged.Should().BeFalse();
    }

    // TEST-95: Changing taste after a proposal replans with the new taste, keeps the itinerary places although the model's state cannot carry them, and accepts the new dishes.
    /// <summary>Covers the question "người dùng bỗng đổi khẩu vị khi đã có đề xuất": every changed position needs a fresh search under the new taste.</summary>
    [Fact]
    public async Task RunAsync_TasteChangedOnProposalWithItinerary_ReplansAndKeepsPlaces()
    {
        var before = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"], TasteTargets = [new("spicy", .25)], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)] };
        var source = new AgentSnapshot(before, [AgentTestData.Item() with { DishName = "Dish 1" }], []);
        var context = AgentTestData.Context(source);
        context.State = before;
        context.Request.Content = "đổi khẩu vị sang ngọt nhẹ cho cả chuyến";
        var fresh = AgentTestData.Dish(2);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            var changed = context.State with { TasteTargets = [new("sweet", .25)], DayAreas = null };
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [2], Guid.NewGuid(), context.State with { TasteTargets = [new("sweet", .25)] }));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", State = changed, Scope = new(1, null), Items = [AgentTestData.Item(2, "a")] }))!;
        });

        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(), fresh], intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Single().DishId.Should().Be(2);
        result.Snapshot.State.TasteTargets.Should().ContainSingle(target => target.Dimension == "sweet" && target.Target == .25);
        result.Snapshot.State.DayAreas!.Single().Label.Should().Be("Hoàn Kiếm");
        result.Reply.Should().Contain("ngọt mức nhẹ");
    }

    // TEST-96: A bare province now asks the scope once, for free; covers planning wording and the province alone.
    /// <summary>Implements the decision that "ở Hà Nội" asks: whole city, an area, an itinerary or the current location.</summary>
    [Theory]
    [InlineData("lập lịch ở Hà Nội")]
    [InlineData("Tôi muốn lập lịch đi ăn ở Hà Nội")]
    [InlineData("Hà Nội")]
    public async Task RunAsync_BareProvince_AsksScopeOnce(string content)
    {
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(IntakeContext(content, new AgentState()), default);

        result.Reply.Should().StartWith("Mình ghi nhận Hà Nội. Bạn muốn mình chọn quán trong toàn Hà Nội").And.Contain("hành trình từng ngày").And.Contain("vị trí hiện tại");
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Scope);
        result.State.Province.Should().Be("Hà Nội");
    }

    // TEST-97: A message that already gives days, a budget or a taste keeps the previous flow and does not ask the scope; these are the cases the question must not take over.
    /// <summary>Guards the rule that the scope question is only for a message naming nothing but the province.</summary>
    [Theory]
    [InlineData("Hà Nội 3 ngày 1 triệu")]
    [InlineData("Lập lịch 3 ngày tại Hà Nội, tổng ngân sách 1500000 đồng.")]
    [InlineData("Tôi muốn lên lịch ăn uống Hà Nội 3 ngày, ngân sách 500000 đồng 1 ngày, tôi thích ăn chua")]
    public async Task RunAsync_ProvinceWithTripFacts_DoesNotAskScope(string content)
    {
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(IntakeContext(content, new AgentState()), default);

        result.Reply.Should().NotContain("Bạn muốn mình chọn quán");
        result.State.PlanningIntake.Should().NotBe(AgentIntakePolicy.Scope);
        result.State.Province.Should().Be("Hà Nội");
    }

    // TEST-98: Each answer to the scope question leads to the next question; covers the whole city, no preference, an area, an itinerary, trip facts and an unknown name.
    /// <summary>Ensures every reply moves on, so the question can never trap the user.</summary>
    [Theory]
    [InlineData("toàn thành phố", "Mình sẽ chọn quán trong toàn Hà Nội.")]
    [InlineData("toàn Hà Nội nhé", "Mình sẽ chọn quán trong toàn Hà Nội.")]
    [InlineData("không", "Mình sẽ chọn quán trong toàn Hà Nội.")]
    [InlineData("3 ngày 1 triệu", "Mình sẽ chọn quán trong toàn Hà Nội.")]
    [InlineData("ở Cầu Giấy", "khu vực Cầu Giấy")]
    [InlineData("Cầu Giấy", "khu vực Cầu Giấy")]
    [InlineData("Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác", "ngày 1 quanh Hoàn Kiếm")]
    [InlineData("xyzxyz", "Mình chưa nhận ra khu này")]
    public async Task RunAsync_ScopeAnswer_MovesOn(string content, string expected)
    {
        var open = new AgentState { Province = "Hà Nội", PlanningIntake = AgentIntakePolicy.Scope, PendingQuestion = "phạm vi" };
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(IntakeContext(content, open), default);

        result.Reply.Should().Contain(expected);
        result.State.PlanningIntake.Should().NotBe(AgentIntakePolicy.Scope);
    }

    // TEST-99: The current location answers the scope question with its coordinates and radius, without a map search or provider call.
    /// <summary>Ensures the location button works at this new question, where the province is already known.</summary>
    [Fact]
    public async Task RunAsync_ScopeAnsweredByLocation_KeepsCoordinates()
    {
        var located = new AgentState { Province = "Hà Nội", PlanningIntake = AgentIntakePolicy.Scope, OriginMode = "gps", OriginLatitude = 21.03m, OriginLongitude = 105.85m, OriginLabel = "Vị trí hiện tại", RadiusKm = 10m };
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(IntakeContext("Dùng vị trí hiện tại của mình", located), default);

        result.Reply.Should().StartWith("Mình sẽ ưu tiên quán trong khoảng 10 km quanh vị trí của bạn.");
        result.State.Should().Match<AgentState>(x => x.OriginMode == "gps" && x.RadiusKm == 10m && x.PlanningIntake == AgentIntakePolicy.Details);
    }

    // TEST-100: A message that already says where the trip goes is not asked again; covers an area, an itinerary and a province with an area.
    /// <summary>Ensures the new question only fires when the scope is really unknown.</summary>
    [Theory]
    [InlineData("lập lịch ở Cầu Giấy")]
    [InlineData("Ngày 1 ở Hoàn Kiếm, ngày 2 đi Lăng Bác")]
    [InlineData("lập lịch ở Hà Nội khu Cầu Giấy")]
    public async Task RunAsync_ScopeAlreadyStated_DoesNotAsk(string content)
    {
        var sut = Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places());

        var result = await sut.RunAsync(IntakeContext(content, new AgentState()), default);

        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        result.Reply.Should().NotContain("Bạn muốn mình chọn quán");
    }

    // TEST-101: The roadmap of conversation 138 has a pin and no province: the pin answers "ở đâu", gives the search circle and only the budget is asked, for free.
    /// <summary>This message used to ask for a place the user had already chosen on the map.</summary>
    [Fact]
    public async Task RunAsync_PinnedRoadmapWithoutProvince_UsesPinInsteadOfAskingPlace()
    {
        var state = new AgentState { NumDays = 7, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };
        var context = IntakeContext("lập lịch cho tôi", state, new AgentSnapshot(state, [], []));

        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        result.Reply.Should().Contain("quanh điểm bạn đã chấm").And.Contain("ngân sách").And.NotContain("ở đâu");
        result.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.RadiusKm == 10m && x.OriginMode == "pinned" && x.OriginLatitude == 21.03m);
    }

    // TEST-102: A pin far from every restaurant says so and asks for a place, keeping the pin; covers the only case where the place question remains.
    /// <summary>Ensures the question is honest instead of looking as if the pin was never seen.</summary>
    [Fact]
    public async Task RunAsync_PinOutsideCatalog_SaysSoAndAsksPlace()
    {
        var state = new AgentState { NumDays = 7, OriginMode = "pinned", OriginLatitude = 10m, OriginLongitude = 106m };

        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places())
            .RunAsync(IntakeContext("lập lịch cho tôi", state), default);

        result.Reply.Should().StartWith("Quanh điểm bạn chấm trên bản đồ chưa có quán nào trên ANGI.").And.Contain("ở đâu");
        result.State.Should().Match<AgentState>(x => x.Province == null && x.OriginLatitude == 10m);
    }

    // TEST-103: A trip that already has a province keeps its search as it is, so a user who widened the search is not narrowed again.
    /// <summary>Only the first contact with a pin fills the circle.</summary>
    [Fact]
    public async Task RunAsync_PinWithProvince_KeepsSearch()
    {
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };

        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places())
            .RunAsync(IntakeContext("lập lịch cho tôi", state), default);

        result.State.RadiusKm.Should().BeNull();
    }

    // TEST-104: A model state that leaves the trip values empty neither erases them nor asks to confirm "số ngày: ; ngân sách:  đồng"; this is what wiped conversation 138.
    /// <summary>Only a real change reaches the confirmation question.</summary>
    [Fact]
    public async Task RunAsync_ModelLeavesTripValuesEmpty_KeepsThemWithoutConfirmation()
    {
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, BudgetAmount = 3500000, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };
        var context = IntakeContext("thử xem sao", state);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "answer", Reply = "Bạn muốn món gì?", State = new AgentState { Province = "Hà Nội" } }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().NotContain("số ngày:");
        result.State.Should().Match<AgentState>(x => x.PendingStateChange == null && x.NumDays == 7 && x.BudgetAmount == 3500000 && x.OriginMode == "pinned" && x.OriginLatitude == 21.03m);
    }

    // TEST-105: Removing a pin the user names still asks to confirm, so the guard against empty values does not take that choice away.
    /// <summary>Keeps the explicit way out of a pinned search.</summary>
    [Fact]
    public async Task RunAsync_UserRemovesPin_StillAsksToConfirm()
    {
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, BudgetAmount = 3500000, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };
        var context = IntakeContext("bỏ điểm đã chấm trên bản đồ đi", state);
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "answer", Reply = "Được", State = state with { OriginMode = "none", OriginLatitude = null, OriginLongitude = null } }));

        var result = await Create(llm.Object).RunAsync(context, default);

        result.Reply.Should().Contain("điểm xuất phát hoặc bán kính tìm món");
        result.State.PendingStateChange!.OriginMode.Should().Be("none");
    }

    // TEST-106: "ok" with no plan yet never invites saving a plan that does not exist; turn 26 of conversation 138 said "Ưng rồi thì cứ bảo mình chốt".
    /// <summary>Keeps the acknowledgement honest about what exists.</summary>
    [Fact]
    public async Task RunAsync_BareAcknowledgementWithoutPlan_DoesNotInviteSaving()
    {
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, BudgetAmount = 3500000 };

        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object).RunAsync(IntakeContext("ok", state, new AgentSnapshot(state, [], [])), default);

        result.Reply.Should().NotContain("chốt");
    }

    // TEST-107: A long trip is assembled by the backend after the one search, however long: one provider call and no paid route requests; covers just past the threshold, 30 days and the maximum.
    /// <summary>A model reply cannot hold hundreds of items, so these trips would otherwise end in a truncated output.</summary>
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(100)]
    public async Task RunAsync_LongTrip_BackendAssemblesAfterOneSearch(int days)
    {
        var state = new AgentState { Province = "Da Nang", NumDays = (short)days, BudgetAmount = 99999999, MealSlots = ["breakfast", "lunch", "dinner"] };
        var context = IntakeContext("lập lịch đi", state);
        context.IntakeComplete = true;
        var search = new AgentRecommendationInput(Scope: "full", TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 30);
        var call = new AgentToolCall("r", "get_recommendation", System.Text.Json.JsonSerializer.SerializeToElement(search, AgentJson.Options));
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([call], null));
        var catalog = Enumerable.Range(1, 40).Select(id => AgentTestData.Dish(id)).ToArray();
        var fixture = AgentTestData.Validator(catalog);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<ANGI.Application.Common.Models.Discovery.DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog.Select(dish => dish.Id).ToArray());
        var policy = new AgentStatePolicy();
        var limits = new AgentLimits();
        var telemetry = Mock.Of<IAgentTelemetry>();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), policy, new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);

        var result = await new AgentOrchestrator(llm.Object, tools, policy, new(), fixture.Validator, limits, telemetry).RunAsync(context, default);

        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Should().HaveCount(days * 3);
        result.Snapshot.Items.Select(item => (item.DayNumber, item.MealSlot)).Distinct().Should().HaveCount(days * 3);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Routes.Verify(x => x.CalculateRouteAsync(It.IsAny<ANGI.Application.Common.Models.Roadmap.RouteCoordinate?>(), It.IsAny<IReadOnlyList<ANGI.Application.Common.Models.Roadmap.RouteCoordinate>>(),
            It.IsAny<ANGI.Application.Common.Models.Roadmap.RouteProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-108: The duration limit reads up to 100 days in a message and refuses just above it.
    /// <summary>"30 ngày" used to be refused by the form and "100 ngày" could not even be read.</summary>
    [Theory]
    [InlineData("lập lịch Hà Nội 30 ngày", 30)]
    [InlineData("lập lịch Hà Nội 100 ngày", 100)]
    public async Task RunAsync_DurationUpToMaximum_IsAccepted(string content, int days)
    {
        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog()).RunAsync(IntakeContext(content, new AgentState()), default);

        result.State.NumDays.Should().Be((short)days);
        result.Reply.Should().NotContain("Số ngày phải");
    }

    /// <summary>Builds the plan of conversation 121: day 1 wrongly located in Hà Đông and day 2 at Lăng Bác.</summary>
    private static AgentContext WrongDayOneContext(string content)
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.State = context.State with { Province = "Hà Nội", NumDays = 3,
            DayAreas = [new(1, "Hồ Gươm", 20.978894m, 105.785555m, 3m, "Hà Đông"), new(2, "Lăng Bác", 21.0368m, 105.8347m, 3m, "Ba Đình")] };
        context.Source = context.Source! with { State = context.State };
        context.Request.Content = content;
        return context;
    }

    /// <summary>Returns a resolver over Hà Nội districts with enough restaurants each, and no map service.</summary>
    private static AgentPlaceResolver Places()
    {
        var addresses = new[] { "Hoàn Kiếm", "Ba Đình", "Hoàng Mai", "Cầu Giấy" }.SelectMany((district, index) => Enumerable.Range(0, 25)
            .Select(_ => new AgentCatalogPlace("Hà Nội", $"1 Phố Hai Bà Trưng, P. Cửa Nam, {district}, Hà Nội", 21.03m + index / 100m, 105.85m)));
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.GetPublicPlacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(addresses.ToArray());
        return new(catalog.Object);
    }

    /// <summary>Builds a turn with the given message over the given state.</summary>
    private static AgentContext IntakeContext(string content, AgentState state, AgentSnapshot? source = null)
    {
        var context = AgentTestData.Context(source);
        context.State = state;
        context.Request.Content = content;
        return context;
    }

    /// <summary>Returns a catalog covering only Hà Nội, like the current local data, with a fixed nearest-province answer.</summary>
    private static ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository IntakeCatalog(string? nearest = "Hà Nội")
    {
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.GetProvincesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["Hà Nội"]);
        catalog.Setup(x => x.NearestProvinceAsync(It.IsAny<decimal>(), It.IsAny<decimal>(), AgentIntakePolicy.MaxNearbyKm, It.IsAny<CancellationToken>())).ReturnsAsync(nearest);
        return catalog.Object;
    }

    private static AgentOrchestrator Create(IAgentLlmService llm, AgentLimits? configuredLimits = null, IAgentTelemetry? telemetry = null,
        ANGI.Domain.Entities.Dish[]? catalog = null, ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? catalogSearch = null,
        ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? intakeCatalog = null, AgentPlaceResolver? places = null)
    {
        var fixture = AgentTestData.Validator(catalog ?? [AgentTestData.Dish()]);
        telemetry ??= Mock.Of<IAgentTelemetry>(); var limits = configuredLimits ?? new AgentLimits(); var state = new AgentStatePolicy();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), state,
            new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry, catalogSearch: catalogSearch);
        return new(llm, tools, state, new(), fixture.Validator, limits, telemetry, intakeCatalog, places);
    }
}
