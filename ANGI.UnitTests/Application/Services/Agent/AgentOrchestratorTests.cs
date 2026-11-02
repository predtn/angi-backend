using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Services.Agent;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
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
        error.Message.Should().Contain("điều chỉnh yêu cầu").And.NotContain("thử lại");
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedCalls));
    }

    // TEST-18: Structurally valid model state with invalid business bounds reports an Agent failure instead of blaming user input.
    /// <summary>Verifies invalid model-enriched state cannot escape as user validation or create ready state.</summary>
    [Fact]
    public async Task RunAsync_InvalidModelState_ProducesControlledUnavailable()
    {
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = AgentTestData.State() with { NumDays = 0 } }));
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
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
        (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("catalog ANGI").And.NotContain("Recovered");
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
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("catalog ANGI").And.NotContain("Recovered");
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
        result.Reply.Should().NotContain("thứ tự đánh giá");
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

    /// <summary>Builds orchestration from deterministic catalog boundaries and observable provider mocks.</summary>
    private static AgentOrchestrator Create(IAgentLlmService llm, AgentLimits? configuredLimits = null, IAgentTelemetry? telemetry = null,
        ANGI.Domain.Entities.Dish[]? catalog = null)
    {
        var fixture = AgentTestData.Validator(catalog ?? [AgentTestData.Dish()]);
        telemetry ??= Mock.Of<IAgentTelemetry>(); var limits = configuredLimits ?? new AgentLimits(); var state = new AgentStatePolicy();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), state,
            new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);
        return new(llm, tools, state, new(), fixture.Validator, limits, telemetry);
    }
}
