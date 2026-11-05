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
        // STEP-01: Derive state from a copy of AgentTestData.State(), get items from Range().Select(filtered).ToArray, get context from AgentTestData.Context and 3 more.
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
        // STEP-02: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-03: Get sequence from llm.SetupSequence, when schemaFirst, call sequence.ReturnsAsync, call sequence.ReturnsAsync and 2 more.
        var sequence = llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (schemaFirst) sequence.ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        sequence.ReturnsAsync(new AgentLlmStep([], invalid));
        var catalog = Enumerable.Range(1, 9).Append(221).Select(id => AgentTestData.Dish(id)).ToArray();

        var result = await Create(llm.Object, catalog: catalog).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty, result.Snapshot.Items has 9 item(s) and result.Snapshot.Items.Single(filtered).DishId is 7.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Should().HaveCount(9).And.NotContain(item => item.DishId == 221);
        result.Snapshot.Items.Single(item => item.DayNumber == 3 && item.MealSlot == "breakfast").DishId.Should().Be(7);
        // STEP-05: Assert result.Snapshot.Grants contains a matching item, result.Validation.TotalCost is 450000 and result.Reply contains the expected text.
        result.Snapshot.Grants.Should().Contain(grant => grant.DishIds.Contains(7));
        result.Validation.TotalCost.Should().Be(450000);
        result.Reply.Should().NotContain("chọn lại");
        // STEP-06: Assert context.RepairFeedback is null.
        context.RepairFeedback.Should().BeNull();
        // STEP-07: Verify RunAsync is called 2 times.
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
        // STEP-01: Derive state from a copy of AgentTestData.State(), get items from Range().Select(filtered).ToArray, get context from AgentTestData.Context and 6 more.
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
        // STEP-02: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-03: Get sequence from llm.SetupSequence, when schemaFirst, call sequence.ReturnsAsync, call ReturnsAsync().ReturnsAsync().ReturnsAsync and 2 more.
        var sequence = llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        if (schemaFirst) sequence.ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        sequence.ReturnsAsync(new AgentLlmStep([], invalid))
            .ReturnsAsync(new AgentLlmStep([], unsupported))
            .ReturnsAsync(new AgentLlmStep([], recover ? valid : unsupported));
        var catalog = Enumerable.Range(1, 9).Append(221).Select(id => AgentTestData.Dish(id)).ToArray();

        var result = await Create(llm.Object, catalog: catalog).RunAsync(context, default);

        // STEP-04: Assert context.RepairFeedback contains the expected text, depending on recover, result.Validation.Errors is empty and 5 more.
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
        // STEP-05: Verify RunAsync is called 4 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(schemaFirst ? 4 : 3));
    }

    // TEST-03: An identical tool request in the same turn returns a duplicate notice, is not executed again and closes tools for the final call.
    /// <summary>A repeated identical tool request is answered from the turn, not re-executed, and closes tools for the final call.</summary>
    [Fact]
    public async Task RunAsync_RepeatedIdenticalTool_IsNotReexecutedAndClosesTools()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Get arguments from System.Text.Json.JsonSerializer.SerializeToElement and create context as AgentContext.
        var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        // STEP-03: Stub llm.RunAsync.
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", arguments)], null))
            .ReturnsAsync(new AgentLlmStep([new("b", "get_roadmap", arguments)], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-04: Call Create().RunAsync.
        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        // STEP-05: Assert context.ToolsClosed is true, context.Exchanges has 2 item(s) and context.Exchanges[1].Results.Single().status is "duplicate_request".
        context.ToolsClosed.Should().BeTrue();
        context.Exchanges.Should().HaveCount(2);
        context.Exchanges[1].Results.Single().GetProperty("status").GetString().Should().Be("duplicate_request");
        // STEP-06: Verify RunAsync is called 3 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-04: The invalid-output notice and schema path are sent only on the call after a schema failure, not after a successful tool round.
    /// <summary>The invalid-output notice applies to the call right after a schema failure, not to every later tool round.</summary>
    [Fact]
    public async Task RunAsync_SchemaRepairThenTool_StopsSendingRepairNotice()
    {
        // STEP-01: Create context as AgentContext; mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.InvalidSchema, 200, Reason: "output:$.state.preferences(unexpected)")))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 }))], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        // STEP-03: Assert llm.Invocations.Select(filtered) equals false and context.RepairFeedback contains "$.state.preferences".
        llm.Invocations.Select(call => call.Arguments[1] is true).Should().Equal(false, true, false);
        context.RepairFeedback.Should().Contain("$.state.preferences");
    }

    // TEST-05: After tools are closed by a duplicate request, a further tool request fails the turn without executing it.
    /// <summary>Enforces the closed-tools state even if a provider ignores tool_choice=none.</summary>
    [Fact]
    public async Task RunAsync_ToolRequestAfterToolsClosed_FailsWithoutExecuting()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Create context as AgentContext and get same from System.Text.Json.JsonSerializer.SerializeToElement.
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        var same = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        // STEP-03: Stub llm.RunAsync.
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([new("b", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([new("c", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 89 }))], null));

        // STEP-04: Assert Create().RunAsync() throws ServiceUnavailableException and context.Exchanges has 2 item(s).
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(context, CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();

        context.Exchanges.Should().HaveCount(2);
        // STEP-05: Verify RunAsync is called 3 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-06: Two identical calls in one provider step execute once; the second gets the duplicate notice and tools close.
    /// <summary>Covers duplicates inside a single step, not only across rounds.</summary>
    [Fact]
    public async Task RunAsync_IdenticalCallsInOneStep_ExecuteOnce()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Create context as AgentContext and get same from System.Text.Json.JsonSerializer.SerializeToElement.
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        var same = System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 });
        // STEP-03: Stub llm.RunAsync.
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "get_roadmap", same), new("b", "get_roadmap", same)], null))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-04: Call Create().RunAsync and derive results from context.Exchanges.Single().Results.
        await Create(llm.Object).RunAsync(context, CancellationToken.None);

        var results = context.Exchanges.Single().Results;
        // STEP-05: Assert results[0].TryGetProperty() is false, results[1].status is "duplicate_request" and context.ToolsClosed is true.
        results[0].TryGetProperty("status", out _).Should().BeFalse();
        results[1].GetProperty("status").GetString().Should().Be("duplicate_request");
        context.ToolsClosed.Should().BeTrue();
    }

    // TEST-07: A substitute that fails full revalidation is discarded and the bounded model repair keeps the original error details.
    /// <summary>Ensures backend substitution can never bypass validation; telemetry records the failed attempt.</summary>
    [Fact]
    public async Task RunAsync_SubstituteFailsRevalidation_FallsBackToModelRepair()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get items from Range().Select(filtered).ToArray, get context from AgentTestData.Context and 5 more.
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
        // STEP-02: Mock IAgentLlmService and IAgentTelemetry; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = items }))
            .ReturnsAsync(new AgentLlmStep([], unsupported))
            .ReturnsAsync(new AgentLlmStep([], unsupported));
        var telemetry = new Mock<IAgentTelemetry>();

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, telemetry: telemetry.Object, catalog: catalog).RunAsync(context, CancellationToken.None);

        // STEP-04: Assert result.Snapshot is null and result.Reply contains the expected text.
        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("Món 221").And.NotContain("Thiếu món");
        // STEP-05: Verify Record is called once and RunAsync is called 3 times.
        telemetry.Verify(t => t.Record("scope_autorepair", 7, It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object?>()), Times.Once);
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-08: A coverage claim without an observed empty meal triggers one repair, and the repaired patch becomes the proposal without the false claim.
    /// <summary>Ensures a model's coverage intent is not treated as evidence of missing catalog choices.</summary>
    [Fact]
    public async Task RunAsync_UnsupportedCoverage_RepairsAndBuildsUpdatedProposal()
    {
        // STEP-01: Get context from AgentTestData.Context and add the computed value to context.MealCoverage.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.MealCoverage.Add(new(1, "lunch", 12, AgentScopedPlanning.Fingerprint(context.State, 1, "lunch"), []));
        context.MealCoverage.Add(new(2, "lunch", 12, AgentScopedPlanning.Fingerprint(context.State, 2, "lunch"), ["Đồ uống"]));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Thiếu món, hãy tăng ngân sách" }));
        llm.Setup(x => x.RunAsync(context, true, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "patch_proposal", Reply = "Moved", Changes = [new("move", "a", AgentTestData.Item() with { DayNumber = 2 })] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);

        // STEP-04: Assert result.Snapshot.Items.Single().DayNumber is 2, result.Reply does not contain the expected text and 1 more.
        result.Snapshot!.Items.Single().DayNumber.Should().Be(2);
        result.Reply.Should().NotContain("Thiếu món").And.NotContain("tăng ngân sách");
        context.RepairFeedback.Should().Contain("Chưa có bằng chứng");
        // STEP-05: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-09: Repeated unsupported coverage claims end in a planning-incomplete reply that keeps the source and never blames the catalog; covers a one-call and a two-call budget.
    /// <summary>Bounds coverage repairs by the LLM call limit and keeps the previous proposal intact.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunAsync_RepeatedUnsupportedCoverage_ReportsPlanningFailureWithoutBlamingCatalog(int maxCalls)
    {
        // STEP-01: Get source from AgentTestData.Snapshot and get context from AgentTestData.Context.
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var context = AgentTestData.Context(source);
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Không có đủ dữ liệu" }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, new() { MaxLlmCalls = maxCalls }).RunAsync(context, CancellationToken.None);

        // STEP-04: Assert result.Snapshot is null, context.Source is the same instance as source, result.Reply contains the expected text and 1 more.
        result.Snapshot.Should().BeNull();
        context.Source.Should().BeSameAs(source);
        result.Reply.Should().Contain("chưa hoàn tất việc sắp xếp lịch").And.NotContain("Chưa đủ lựa chọn").And.NotContain("Không có đủ dữ liệu");
        result.State.PendingQuestion.Should().Be(result.Reply);
        // STEP-05: Verify RunAsync is called times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(maxCalls));
    }

    // TEST-10: An observed empty meal produces a question naming that day/meal and its required tags, without the model's invented cause.
    /// <summary>Allows coverage clarification only when backend tool results actually observed a missing meal.</summary>
    [Fact]
    public async Task RunAsync_ObservedMissingMeal_AsksAboutThatMealWithoutInventedCause()
    {
        // STEP-01: Get context from AgentTestData.Context and add the computed value to context.MealCoverage.
        var context = AgentTestData.Context();
        context.MealCoverage.Add(new(2, "breakfast", 0, AgentScopedPlanning.Fingerprint(context.State, 2, "breakfast"), ["Đồ uống"]));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "clarify", Clarification = "coverage", Reply = "Không có nhà hàng trong toàn thành phố" }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);

        // STEP-04: Assert result.Snapshot is null and result.Reply contains the expected text.
        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("kết quả gợi ý vừa lấy").And.Contain("ngày 2, bữa sáng").And.Contain("Đồ uống")
            .And.NotContain("toàn thành phố");
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-11: Explanation and clarification use existing context without calling ranking or routes.
    /// <summary>Checks the no-reranking path for ordinary conversational answers.</summary>
    [Theory]
    [InlineData("answer")][InlineData("clarify")]
    public async Task RunAsync_Answer_ReturnsWithoutTools(string action)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { Action = action }));
        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert result.Snapshot is null and result.Reply is not empty.
        result.Snapshot.Should().BeNull(); result.Reply.Should().NotBeEmpty().And.NotContain("Please clarify");
        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-12: Repair one invalid schema response and reject a second invalid response.
    /// <summary>Checks both successful repair and the single-repair ceiling.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_SchemaRepair_IsBounded(bool recover)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>())).ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(recover ? new AgentLlmStep([], AgentTestData.Answer()) : AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema));
        // STEP-02: Assert depending on recover, Create().RunAsync().Snapshot is null and Create().RunAsync() throws ServiceUnavailableException.
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Snapshot.Should().BeNull();
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-03: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-13: A hallucinated dish never produces a new ready proposal or overwrites the source snapshot.
    /// <summary>Verifies deterministic validation after a structurally valid model output.</summary>
    [Fact]
    public async Task RunAsync_HallucinatedDish_ReturnsBlockingValidation()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Preview", Items = [AgentTestData.Item(999)] }));
        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert result.Snapshot is null and result.Validation.Errors is not empty.
        result.Snapshot.Should().BeNull(); result.Validation!.Errors.Should().NotBeEmpty();
    }

    // TEST-14: Context size and cancellation prevent uncontrolled provider work.
    /// <summary>Checks resource caps before an LLM request and preserves caller cancellation.</summary>
    [Fact]
    public async Task RunAsync_OversizedContextAndCancellation_StopBeforeProvider()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Get context from AgentTestData.Context and set context.Request.Content to a test value.
        var context = AgentTestData.Context(); context.Request.Content = new('x', 130000);
        // STEP-03: Wrap Create().RunAsync with context in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(context, CancellationToken.None);
        // STEP-04: Assert act throws ServiceUnavailableException.
        await act.Should().ThrowAsync<ServiceUnavailableException>();
        // STEP-05: Create cancellation as CancellationTokenSource and cancel cancellation.
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        // STEP-06: Assert Create().RunAsync() throws OperationCanceledException.
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token))).Should().ThrowAsync<OperationCanceledException>();
        // STEP-07: Verify llm receives no other calls.
        llm.VerifyNoOtherCalls();
    }

    // TEST-15: The total backend timeout cancels a slow provider and reports a controlled unavailable result.
    /// <summary>Verifies processing is bounded independently of provider HTTP timeouts.</summary>
    [Fact]
    public async Task RunAsync_TotalTimeout_CancelsSlowProvider()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object, new AgentLimits { TimeoutSeconds = 1 }).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert act throws ServiceUnavailableException and the thrown exception.ErrorCode is "AGENT_UNAVAILABLE".
        (await act.Should().ThrowAsync<ServiceUnavailableException>()).Which.ErrorCode.Should().Be("AGENT_UNAVAILABLE");
    }

    // TEST-16: Repeated tools stop at the round, aggregate-call and total-LLM-call caps.
    /// <summary>Verifies backend execution limits against a provider that never returns a final answer.</summary>
    [Theory]
    [InlineData("rounds", 2)][InlineData("calls", 1)][InlineData("llm", 2)]
    public async Task RunAsync_RepeatedTools_EnforcesEveryCallCap(string limit, int expectedCalls)
    {
        // STEP-01: Create limits as AgentLimits.
        var limits = new AgentLimits { MaxToolRounds = limit == "rounds" ? 1 : 10,
            MaxToolCalls = limit == "calls" ? 1 : 20, MaxLlmCalls = limit == "llm" ? 2 : 5 };
        // STEP-02: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-03: Create tool as AgentToolCall and derive tools from the expected value.
        var tool = new AgentToolCall("a", "get_roadmap", System.Text.Json.JsonSerializer.SerializeToElement(new { roadmapId = 88 }));
        var tools = limit == "calls" ? new[] { tool, tool with { Id = "b" } } : [tool];
        // STEP-04: Stub llm.RunAsync.
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep(tools, null));
        // STEP-05: Create context as AgentContext.
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = AgentTestData.State(), Request = new() { Content = "Explain" },
            Roadmap = new ANGI.Domain.Entities.Roadmap { Id = 88 } };
        // STEP-06: Assert Create().RunAsync() throws ServiceUnavailableException and error.Message contains the expected text.
        var error = (await ((Func<Task>)(async () => await Create(llm.Object, limits).RunAsync(context, CancellationToken.None)))
            .Should().ThrowAsync<ServiceUnavailableException>()).Which;
        // A limit failure repeats if resent unchanged, so the message asks for clarification instead of a retry.
        error.Message.Should().Contain("điều chỉnh ngày, bữa, món").And.NotContain("thử lại");
        // STEP-07: Verify RunAsync is called times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedCalls));
    }

    // TEST-17: Structurally valid model state with invalid business bounds asks the user again, keeping the turn's state, instead of a service error.
    /// <summary>Verifies invalid model-enriched state cannot escape as user validation, a 503 or a ready state.</summary>
    [Fact]
    public async Task RunAsync_InvalidModelState_AsksAgainWithoutChangingState()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = AgentTestData.State() with { NumDays = 0 } }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);

        // STEP-03: Assert result.Reply is AgentReplyGuard.NotUnderstood, result.State.NumDays is 2 and result.Snapshot is null.
        result.Reply.Should().Be(AgentReplyGuard.NotUnderstood);
        result.State.NumDays.Should().Be(2);
        result.Snapshot.Should().BeNull();
    }

    // TEST-18: A model may request draft controls but cannot use those actions to also mutate state or items.
    /// <summary>Checks the chat intent allowlist and keeps backend lifecycle effects separate from proposal deltas.</summary>
    [Theory]
    [InlineData("discard_proposal", "discard")]
    [InlineData("reset_conversation", "reset")]
    public async Task RunAsync_DraftControl_ReturnsBackendAction(string action, string expected)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(service => service.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "Draft control" }));
        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert result.Action is expected, result.Snapshot is null and result.Commit is null.
        result.Action.Should().Be(expected); result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-19: A model cannot authorize its own relaxed constraints, even when it sets its consent flag.
    /// <summary>Turns relaxation into a persisted confirmation question rather than a provider error or automatic change.</summary>
    [Fact]
    public async Task RunAsync_RelaxedState_StagesHumanConsent()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(service => service.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Already confirmed",
                ConstraintsChangeConfirmed = true, State = AgentTestData.State() with { BudgetAmount = 700000, BudgetQuote = "700k" } }));
        var context = AgentTestData.Context();
        context.Request.Content = "tăng ngân sách lên 700k";
        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);
        // STEP-03: Assert result.State.BudgetAmount is 500000, result.State.PendingStateChange.BudgetAmount is 700000, result.Snapshot is null and result.Commit is null.
        result.State.BudgetAmount.Should().Be(500000); result.State.PendingStateChange!.BudgetAmount.Should().Be(700000);
        result.Snapshot.Should().BeNull(); result.Commit.Should().BeNull();
    }

    // TEST-20: Non-schema failures never enter repair, including quota, transport, refusal and truncated output.
    /// <summary>Protects free provider quota while retaining a reason-specific API failure.</summary>
    [Theory]
    [InlineData(AgentLlmFailureKind.Configuration)][InlineData(AgentLlmFailureKind.Authentication)]
    [InlineData(AgentLlmFailureKind.Timeout)][InlineData(AgentLlmFailureKind.TurnTimeout)]
    [InlineData(AgentLlmFailureKind.Network)][InlineData(AgentLlmFailureKind.RateLimited)]
    [InlineData(AgentLlmFailureKind.RejectedRequest)][InlineData(AgentLlmFailureKind.ProviderUnavailable)]
    [InlineData(AgentLlmFailureKind.InvalidResponse)][InlineData(AgentLlmFailureKind.Refused)][InlineData(AgentLlmFailureKind.Truncated)]
    public async Task RunAsync_ProviderFailure_DoesNotRepair(AgentLlmFailureKind kind)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(kind, 429, 45));
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert act throws AgentProviderException, error.ErrorCode is "AGENT_UNAVAILABLE" and error.Failure is a new AgentLlmFailure.
        var error = (await act.Should().ThrowAsync<AgentProviderException>()).Which;
        error.ErrorCode.Should().Be("AGENT_UNAVAILABLE"); error.Failure.Should().Be(new AgentLlmFailure(kind, 429, 45));
        // STEP-04: Verify RunAsync is called once and RunAsync is never called.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Once);
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), true, It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-21: An unclassified missing response cannot be assumed to contain repairable model output.
    /// <summary>Fails closed without spending another provider request on an unknown failure.</summary>
    [Fact]
    public async Task RunAsync_NullResult_DoesNotRepair()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((AgentLlmStep?)null);
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None);
        // STEP-03: Assert act throws AgentProviderException and the thrown exception.Failure.Kind is AgentLlmFailureKind.InvalidResponse.
        (await act.Should().ThrowAsync<AgentProviderException>()).Which.Failure.Kind.Should().Be(AgentLlmFailureKind.InvalidResponse);
        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-22: One transient provider 503 is retried once and the recovered answer is rebuilt by the backend.
    /// <summary>Verifies classified temporary provider failures are retried within the turn deadline.</summary>
    [Fact]
    public async Task RunAsync_Transient503_RetriesWithoutRepeatingTools()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")));
        // STEP-02: Assert Create().RunAsync().Reply contains the expected text.
        (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("có trên ANGI").And.NotContain("Recovered");
        // STEP-03: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-23: Persistent provider 503 responses stop after two retries and surface a provider error.
    /// <summary>Caps transient retries so an outage cannot consume the LLM call budget.</summary>
    [Fact]
    public async Task RunAsync_Repeated503_StopsAfterTwoRetries()
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503));
        // STEP-02: Assert Create().RunAsync() throws AgentProviderException.
        await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)))
            .Should().ThrowAsync<AgentProviderException>();
        // STEP-03: Verify RunAsync is called 3 times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-24: A short per-minute 429 is retried once while a daily quota 429 fails immediately; covers rate_limit and daily_quota_exceeded.
    /// <summary>Distinguishes recoverable rate limits from exhausted quota before spending another provider call.</summary>
    [Theory]
    [InlineData("rate_limit", true)]
    [InlineData("daily_quota_exceeded", false)]
    public async Task RunAsync_Gemini429_RetriesOnlyShortTemporaryLimits(string reason, bool recover)
    {
        // STEP-01: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.RateLimited, 429, 1, "RESOURCE_EXHAUSTED", reason)))
            .ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer("Recovered")));
        // STEP-02: Assert depending on recover, Create().RunAsync().Reply contains the expected text, Create().RunAsync().Reply contains the expected text and 1 more.
        if (recover) (await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None)).Reply.Should().Contain("có trên ANGI").And.NotContain("Recovered");
        else await ((Func<Task>)(async () => await Create(llm.Object).RunAsync(AgentTestData.Context(), CancellationToken.None))).Should().ThrowAsync<AgentProviderException>();
        // STEP-03: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()), Times.Exactly(recover ? 2 : 1));
    }

    // TEST-25: A complete create_proposal selection over an existing proposal updates it with fresh ranking provenance and no repair call.
    /// <summary>Accepts create_proposal in update mode as a scoped snapshot instead of forcing a patch repair.</summary>
    [Fact]
    public async Task RunAsync_CompleteSelectionOverExistingProposal_UpdatesWithoutActionRepair()
    {
        // STEP-01: Mock IAgentLlmService.
        var llm = new Mock<IAgentLlmService>();
        // STEP-02: Get context from AgentTestData.Context, get grant from AgentTestData.Grant and add grant to context.Grants.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        var grant = AgentTestData.Grant([1]);
        context.Grants.Add(grant);
        // STEP-03: Stub llm.RunAsync.
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Selection", Items = [AgentTestData.Item()] }));
        // STEP-04: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, CancellationToken.None);
        // STEP-05: Assert result.Snapshot.Items.Single().RequestId is grant.RequestId, result.Reply does not contain AgentReplyBuilder.RatingFallbackNote and 1 more.
        result.Snapshot!.Items.Single().RequestId.Should().Be(grant.RequestId);
        result.Snapshot.Notes.Should().NotContain(AgentReplyBuilder.RatingFallbackNote);
        context.RepairFeedback.Should().BeNull();
        // STEP-06: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-26: Caller cancellation during a provider call propagates without repair or an availability fallback.
    /// <summary>Keeps the UI Stop action distinct from server or provider timeouts.</summary>
    [Fact]
    public async Task RunAsync_CallerCancellationDuringProvider_Propagates()
    {
        // STEP-01: Create cancellation as CancellationTokenSource; mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>(); using var cancellation = new CancellationTokenSource();
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (AgentContext _, bool _, CancellationToken ct) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); return (AgentLlmStep?)null; });
        // STEP-02: Wrap Create().RunAsync in act so its outcome can be asserted.
        var act = () => Create(llm.Object).RunAsync(AgentTestData.Context(), cancellation.Token);
        // STEP-03: Assert act throws OperationCanceledException.
        await act.Should().ThrowAsync<OperationCanceledException>();
        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-27: Failure telemetry includes safe diagnostic categories and timing without leaking prompt content.
    /// <summary>Makes future provider failures distinguishable without logging raw requests or upstream bodies.</summary>
    [Fact]
    public async Task RunAsync_ProviderFailure_RecordsSafeDiagnostics()
    {
        // STEP-01: Declare captured to capture a call argument; mock IAgentLlmService and IAgentTelemetry; stub telemetry.Record and llm.RunAsync.
        var llm = new Mock<IAgentLlmService>(); var telemetry = new Mock<IAgentTelemetry>(); object? captured = null;
        telemetry.Setup(t => t.Record("llm", It.IsAny<long>(), It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<object>()))
            .Callback<string, long, long?, long?, object?>((_, _, _, _, data) => captured = data);
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentLlmStep.Failed(AgentLlmFailureKind.RateLimited, 429, 45));
        // STEP-02: Get context from AgentTestData.Context and set context.Request.Content to "private-prompt-must-not-leak".
        var context = AgentTestData.Context(); context.Request.Content = "private-prompt-must-not-leak";
        // STEP-03: Wrap Create().RunAsync with context in act so its outcome can be asserted.
        var act = () => Create(llm.Object, telemetry: telemetry.Object).RunAsync(context, CancellationToken.None);
        // STEP-04: Read diagnostic from the response; assert act throws AgentProviderException, diagnostic.RootElement.failureKind is "RateLimited" and 5 more.
        await act.Should().ThrowAsync<AgentProviderException>();
        using var diagnostic = System.Text.Json.JsonDocument.Parse(AgentJson.Write(captured));
        diagnostic.RootElement.GetProperty("failureKind").GetString().Should().Be("RateLimited");
        diagnostic.RootElement.GetProperty("httpStatus").GetInt32().Should().Be(429);
        diagnostic.RootElement.GetProperty("retryAfterSeconds").GetInt32().Should().Be(45);
        diagnostic.RootElement.GetProperty("callNumber").GetInt32().Should().Be(1);
        diagnostic.RootElement.GetProperty("repair").GetBoolean().Should().BeFalse();
        diagnostic.RootElement.GetProperty("elapsedMs").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        // STEP-05: Assert AgentJson.Write() does not contain "private-prompt".
        AgentJson.Write(captured).Should().NotContain("private-prompt");
    }

    // TEST-28: A dish repeated on day 2 is swapped for an unused authorized dish without another LLM call, and a repeat with no unused alternative stays and is disclosed by the repeat note; covers both cases.
    /// <summary>Keeps multi-day plans varied for free while never blocking a plan whose ranked candidates cannot avoid a repeat.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_RepeatedDish_IsReplacedOnlyWhenAnUnusedCandidateExists(bool spare)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 1 more.
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", spare ? [1, 2] : [1], Guid.NewGuid(), state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2)] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty, result.Snapshot.Items.Select(filtered) equals spare ? [1, 2] : [1, 1] and 3 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(spare ? [1, 2] : [1, 1]);
        if (spare) result.Snapshot.Notes.Should().NotContain(AgentReplyBuilder.RepeatNote);
        else result.Snapshot.Notes.Should().Contain(AgentReplyBuilder.RepeatNote);
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-29: A proposal rejected only for exceeding the trip budget ends in a question about the budget with the observed figures
    // when the repaired output gives up (empty proposal), still exceeds the budget, or asks a budget clarification; covers all three.
    /// <summary>Turns "the dishes do not fit the budget" into a question the user can answer instead of a generic invalid-proposal error.</summary>
    [Theory]
    [InlineData("empty")][InlineData("still-over")][InlineData("clarify")]
    public async Task RunAsync_OverBudgetThenNoFittingProposal_AsksAboutBudget(string repair)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 2 more.
        var state = AgentTestData.State() with { BudgetAmount = 80000, MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2], Guid.NewGuid(), state));
        var plan = new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2)] };
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], plan))
            .ReturnsAsync(new AgentLlmStep([], repair switch
            {
                "empty" => new AgentOutput { Action = "create_proposal", Reply = "Không đủ ngân sách" },
                "still-over" => plan,
                _ => new AgentOutput { Action = "clarify", Clarification = "budget", Reply = "Tăng ngân sách nhé" }
            }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot is null, result.Reply contains the expected text, result.State.PendingQuestion is result.Reply and 1 more.
        result.Snapshot.Should().BeNull();
        result.Reply.Should().Contain("tổng 100.000 đồng/người").And.Contain("vượt ngân sách 80.000 đồng")
            .And.Contain("khoảng 40.000 đồng/bữa cho 2 bữa").And.Contain("tăng ngân sách").And.NotContain("không hợp lệ");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.Validation!.Errors.Should().Equal(AgentProposalValidator.BudgetExceeded);
        // STEP-05: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-30: A budget clarification without an observed over-budget proposal is repaired once, and the repaired proposal is built.
    /// <summary>Keeps the model from asking the user to raise the budget before the backend has seen a proposal exceed it.</summary>
    [Fact]
    public async Task RunAsync_UnsupportedBudgetClarification_RepairsAndBuildsProposal()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 1 more.
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2], Guid.NewGuid(), state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "budget", Reply = "Tăng ngân sách nhé" }))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft",
                Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2)] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot.Items has 2 item(s) and context.RepairFeedback contains the expected text.
        result.Snapshot!.Items.Should().HaveCount(2);
        context.RepairFeedback.Should().Contain("Chưa có bằng chứng vượt ngân sách");
        // STEP-05: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-31: An empty proposal before any validation is repaired once without blaming the budget; a second empty proposal ends in a question, not an invalid-proposal error.
    /// <summary>Keeps a model that gives up twice from surfacing "Lịch đề xuất không hợp lệ" to the user.</summary>
    [Fact]
    public async Task RunAsync_EmptyProposalTwiceBeforeValidation_AsksInsteadOfFailing()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 1 more.
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2], Guid.NewGuid(), state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Không lập được" }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot is null, result.Reply is AgentReplyBuilder.PlanningIncomplete and 2 more.
        result.Snapshot.Should().BeNull();
        result.Reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        result.State.PendingQuestion.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        context.RepairFeedback.Should().Contain("ứng viên đã được cấp").And.NotContain("budgetPerMeal").And.NotContain("get_recommendation");
        // STEP-05: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-32: An empty proposal with no issued candidates asks for recommendations while tools remain, and ends without another LLM call once tools are closed; covers both.
    /// <summary>Points the repair at the real gap (no candidates) and never spends a paid call that cannot fetch them.</summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RunAsync_EmptyProposalWithoutCandidates_RequestsRecommendationsOnlyWhenToolsRemain(bool toolsClosed)
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a copy of AgentTestData.State() and set context.ToolsClosed to toolsClosed.
        var context = AgentTestData.Context(); context.State = AgentTestData.State() with { MealSlots = ["lunch"] };
        context.ToolsClosed = toolsClosed;
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Không lập được" }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is AgentReplyBuilder.PlanningIncomplete, depending on toolsClosed, context.RepairFeedback is null and 1 more.
        result.Reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        if (toolsClosed) context.RepairFeedback.Should().BeNull();
        else context.RepairFeedback.Should().Contain("get_recommendation");
        // STEP-05: Verify RunAsync is called 1 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(toolsClosed ? 1 : 2));
    }

    // TEST-33: After an empty-proposal repair, a full proposal rejected by validation still gets its own repair and the corrected proposal is built.
    /// <summary>Keeps the empty-proposal repair from consuming the repair reserved for validation errors.</summary>
    [Fact]
    public async Task RunAsync_EmptyProposalRepair_KeepsValidationRepairAvailable()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 3 more.
        var state = AgentTestData.State() with { BudgetAmount = 80000, MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        context.Grants.Add(new(null, "lunch", [1, 2, 3], Guid.NewGuid(), state));
        var cheap = AgentTestData.Dish(3); cheap.Price = 30000;
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Không lập được" }))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft",
                Items = [AgentTestData.Item(4, "a", 1), AgentTestData.Item(2, "b", 2)] }))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Draft",
                Items = [AgentTestData.Item(3, "a", 1), AgentTestData.Item(1, "b", 2)] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2), cheap]).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty and result.Snapshot.Items.Select(filtered) equals 3.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(3, 1);
        // STEP-05: Verify RunAsync is called 3 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    // TEST-34: A recommendation requested before the budget is known ends the turn with the fixed budget question, keeps the accepted taste
    // and makes no further LLM call instead of failing with AGENT_UNAVAILABLE.
    /// <summary>Protects the user's taste answer and the turn itself when the model asks for recommendations too early.</summary>
    [Fact]
    public async Task RunAsync_RecommendationBeforeBudget_AsksBudgetAndKeepsTaste()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a copy of AgentTestData.State(), create input as AgentRecommendationInput and 1 more.
        var context = AgentTestData.Context(); context.State = AgentTestData.State() with { BudgetAmount = null }; context.Request.Content = "chua vừa";
        var input = new AgentRecommendationInput(context.State with { TasteTargets = [new("sour", .5, "chua vừa")] }, "Da Nang", "full", null, null, [], [], []);
        var call = new AgentToolCall("a", "get_recommendation", System.Text.Json.JsonSerializer.SerializeToElement(input, AgentJson.Options));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([call], null));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected text, result.State.PendingQuestion is result.Reply and 2 more.
        result.Reply.Should().Be("Bạn cho mình biết ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.TasteTargets.Should().Equal(new AgentTasteTarget("sour", .5));
        result.Snapshot.Should().BeNull();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-35: A whole-day taste proposed for "bữa 2 ngày 1" is stored as lunch of day 1, while an override already in state is not
    // narrowed by later edit wording such as "bữa 1 ngày 1"; covers both.
    /// <summary>Checks final-output narrowing applies only to the taste scope the model proposes from the user's taste wording this turn.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WholeDayTasteForNamedMeal_IsNarrowedOnlyWhenNewlyProposed(bool newTaste)
    {
        // STEP-01: Get context from AgentTestData.Context, create dayTaste as AgentTasteOverride, derive state from a copy of AgentTestData.State() and 2 more.
        var context = AgentTestData.Context();
        var dayTaste = new AgentTasteOverride(1, null, [new("sour", .5)]);
        var state = AgentTestData.State() with { MealSlots = ["breakfast", "lunch", "dinner"] };
        context.State = newTaste
            ? state with { PendingPlanningRequest = new(["Tôi muốn ăn ở Đà Nẵng 2 ngày, tôi thích ăn chua"]) }
            : state with { TasteOverrides = [dayTaste] };
        context.Request.Content = newTaste ? "Vừa, áp dụng cho bữa 2 ngày 1" : "Đổi món khác cho bữa 1 ngày 1";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            AgentTestData.Answer() with { State = state with { TasteOverrides = [new(1, null, [new("sour", .5, "Vừa")])] } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.State.TasteOverrides.Select(filtered) equals the expected value.
        result.State.TasteOverrides.Select(x => (x.DayNumber, x.MealSlot))
            .Should().Equal(((short)1, newTaste ? "lunch" : null));
    }

    // TEST-36: A repeat whose own grant has no unused dish is replaced from the backend-held reserve of the same ranked request without
    // another LLM call, and the reserve dish keeps its provenance in the snapshot.
    /// <summary>Prevents a small per-day grant from forcing a repeat while the ranked list still holds unused dishes.</summary>
    [Fact]
    public async Task RunAsync_RepeatWithOnlyReserveAlternative_IsReplacedFromReserve()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 3 more.
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        var requestId = Guid.NewGuid();
        context.Grants.Add(new(1, "lunch", [1], requestId, state));
        context.Grants.Add(new(2, "lunch", [1], requestId, state));
        context.ReserveGrants.Add(new(2, "lunch", [2], requestId, state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2)] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty, result.Snapshot.Items.Select(filtered) equals 1 and 3 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Equal(1, 2);
        result.Snapshot.Items.Should().OnlyContain(item => item.RequestId == requestId);
        result.Snapshot.Grants.Should().Contain(grant => grant.DayNumber == 2 && grant.DishIds.SequenceEqual(new[] { 2 }));
        result.Snapshot.Notes.Should().NotContain(AgentReplyBuilder.RepeatNote);
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-37: A restaurant the catalog cannot find is asked about without being kept in mealRequests, so naming another restaurant
    // next turn is a new request rather than a constraint change; a tag the turn added for that meal is kept.
    /// <summary>Stops an unfound name ("Hương Sen Phố Cổ") from turning the user's next choice into a "xác nhận thay đổi" question.</summary>
    [Fact]
    public async Task RunAsync_UnfoundRestaurant_IsAskedWithoutKeepingTheName()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.Request.Content to a test text and derive start from context.State.
        var context = AgentTestData.Context(); context.Request.Content = "Bữa tối ngày 1 tôi muốn ăn ở nhà hàng Hương Sen Phố Cổ";
        var start = context.State;
        // STEP-02: Get search from System.Text.Json.JsonSerializer.SerializeToElement; mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository and 1 more; stub catalog.SearchAsync and 1 more.
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.SearchAsync("restaurant", "Hương Sen Phố Cổ", It.IsAny<string?>(), 21, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var search = System.Text.Json.JsonSerializer.SerializeToElement(new AgentCatalogInput("restaurant", "Hương Sen Phố Cổ", "Da Nang"), AgentJson.Options);
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([new("a", "search_catalog", search)], null))
            .ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Reply = "?", Clarification = "catalog_support",
                State = start with { MealRequests = [new(1, "dinner", null, "Hương Sen Phố Cổ", [53])] } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalogSearch: catalog.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply starts with the expected text, result.State.MealRequests has exactly one matching item and 1 more.
        result.Reply.Should().StartWith("Chưa tìm thấy món/quán theo tên yêu cầu");
        result.State.MealRequests.Should().ContainSingle().Which.Should().Match<AgentMealRequest>(request =>
            request.RestaurantName == null && request.DayNumber == 1 && request.MealSlot == "dinner" && request.TagIds.Single() == 53);
    }

    // TEST-38: An answer closes any older question, because the model read the message and moved on; covers an offer and a taste question.
    /// <summary>A question left open would be repeated and would block saving the plan.</summary>
    [Theory]
    [InlineData("Bạn có muốn xem lựa chọn khác không?")]
    [InlineData("Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?")]
    public async Task RunAsync_AnswerWithOldQuestion_ClearsIt(string question)
    {
        // STEP-01: Get context from AgentTestData.Context, set context.Request.Content to a test text and set context.State to a copy of context.State.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "a", 1)));
        context.Request.Content = "Có những món gì?";
        context.State = context.State with { PendingQuestion = question };
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-04: Assert result.State.PendingQuestion is null.
        result.State.PendingQuestion.Should().BeNull();
    }

    // TEST-39: A proposal only over budget is repaired from cheaper ranked dishes without another provider call, and the reply discloses the swap.
    /// <summary>Fixes a low budget ending in "raise your budget" although cheaper dishes had been ranked.</summary>
    [Fact]
    public async Task RunAsync_OverBudgetWithCheaperDish_RepairsWithoutProvider()
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), get context from AgentTestData.Context, set context.State to state and 3 more.
        var state = AgentTestData.State() with { BudgetAmount = 80000, MealSlots = ["lunch"] };
        var context = AgentTestData.Context(); context.State = state;
        var cheap = AgentTestData.Dish(3); cheap.Price = 30000;
        context.Grants.Add(new(null, "lunch", [1, 2, 3], Guid.NewGuid(), state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Draft", Items = [AgentTestData.Item(1, "a", 1), AgentTestData.Item(2, "b", 2)] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2), cheap]).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty, result.Snapshot.Items.Select(filtered) contains 3 and 1 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Select(item => item.DishId).Should().Contain(3);
        result.Reply.Should().NotContain("chọn lại");
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-40: A budget of the wrong size gets one repair: a corrected figure is taken, and a repeated one is dropped instead of reaching the trip.
    /// <summary>Prevents the 1,500 đồng budget the model produced for "500k 1 ngày", without any pattern reading the user's wording to override it.</summary>
    [Theory]
    [InlineData(1000000, 1000000)]
    [InlineData(1000, null)]
    public async Task RunAsync_BudgetOfWrongSize_IsRepairedOnceThenDropped(int secondBudget, int? expected)
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a new AgentState and set context.Request.Content to a test text.
        var context = AgentTestData.Context();
        context.State = new AgentState { MealSlots = [] };
        context.Request.Content = "Lập lịch 2 ngày ở Da Nang, ngân sách 500k 1 ngày";
        AgentLlmStep Read(int budget) => new([], new AgentOutput { Action = "clarify", Clarification = "general", Reply = "Bạn muốn ăn gì nào?",
            State = new AgentState { Province = "Da Nang", NumDays = 2, BudgetAmount = budget, BudgetQuote = "500k 1 ngày" } });
        // STEP-02: Script the model to misread the budget, then answer the repair.
        var llm = Scripted(Read(1000), Read(secondBudget));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.State.BudgetAmount is expected, the quote was not kept and the model was asked twice.
        result.State.BudgetAmount.Should().Be(expected is { } amount ? amount : null);
        result.State.BudgetQuote.Should().BeNull();
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-41: A model-written social reply is shown, while its state, items and changes are ignored; covers a safe reply and one falling back for stating a price.
    /// <summary>Ensures a social turn can sound natural but can never alter the trip or the plan.</summary>
    [Theory]
    [InlineData("Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!", "Mình vẫn ổn, cảm ơn bạn đã hỏi nhé!")]
    [InlineData("Phở chỉ 50000 thôi", AgentReplyGuard.ChatFallback)]
    public async Task RunAsync_ChatAction_ShowsSafeReplyAndKeepsState(string draft, string expected)
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to a test text.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "Hôm nay bạn thế nào?";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "chat", Reply = draft, State = new AgentState { Province = "Hue", NumDays = 9 }, Items = [AgentTestData.Item(9, "z")] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is expected, result.Action is "chat", result.Snapshot is null, result.State.Province is "Da Nang" and 1 more.
        result.Reply.Should().Be(expected);
        result.Action.Should().Be("chat");
        result.Snapshot.Should().BeNull();
        result.State.Province.Should().Be("Da Nang"); result.State.NumDays.Should().Be(2);
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-42: "Hướng dẫn bóc cam" gets the owner's exact refusal in one call, whether or not a plan exists, ignoring the model's draft, state and items.
    /// <summary>Guarantees out-of-scope requests are refused instead of printing the plan or asking for trip details.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RunAsync_OutOfScope_RefusesWithExactSentence(bool hasPlan)
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a new AgentState and set context.Request.Content to a test text.
        var context = AgentTestData.Context(hasPlan ? AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng" }) : null);
        context.State = new AgentState();
        context.Request.Content = "Hướng dẫn bóc cam";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "out_of_scope", Reply = "Trước tiên hãy khía vỏ cam", State = new AgentState { Province = "Hue" }, Items = [AgentTestData.Item(9, "z")] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected text, result.Action is "out_of_scope", result.Snapshot is null and result.State.Province is null.
        result.Reply.Should().Be("Việc này nằm ngoài khả năng của mình rồi. Mình chỉ giúp được về món ăn, quán và lịch ăn uống thôi.");
        result.Action.Should().Be("out_of_scope");
        result.Snapshot.Should().BeNull();
        result.State.Province.Should().BeNull();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-43: A general food question is refused with an offer to find the dish the user named; covers a dish in the message and one the model invented.
    /// <summary>Gives a useful next step while keeping the offer tied to the user's own words.</summary>
    [Theory]
    [InlineData("phở", "Mình chưa hướng dẫn được phần này vì ANGI không có dữ liệu về công thức, dinh dưỡng hay sức khỏe. Bạn có muốn mình tìm quán phở không?")]
    [InlineData("bún chả", AgentReplyGuard.OutOfScope)]
    public async Task RunAsync_OutOfScopeFoodQuestion_OffersOnlyTheNamedDish(string dish, string expected)
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to a test text.
        var context = AgentTestData.Context();
        context.Request.Content = "Cách nấu phở";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "out_of_scope", Reply = "Draft", SuggestedDish = dish }));

        // STEP-03: Assert Create().RunAsync().Reply is expected.
        (await Create(llm.Object).RunAsync(context, default)).Reply.Should().Be(expected);
    }

    // TEST-44: An off-topic reply to an open taste question keeps that question and does not join the request being planned.
    /// <summary>Prevents "bóc cam" from clearing the taste question or being read as part of the trip request.</summary>
    [Fact]
    public async Task RunAsync_OutOfScopeDuringTasteQuestion_KeepsQuestionAndPlanningRequest()
    {
        // STEP-01: Get context from AgentTestData.Context, define question as the expected text, create pending as AgentPendingPlanningRequest and 3 more.
        var context = AgentTestData.Context();
        const string question = "Bạn muốn mức cay như thế nào: không, nhẹ, vừa, nhiều hay rất nhiều?";
        var pending = new AgentPendingPlanningRequest(["Lập lịch, tôi thích ăn cay"]);
        context.State = context.State with { PendingQuestion = question, PendingPlanningRequest = pending };
        context.History.Add(new("user", "Lập lịch, tôi thích ăn cay"));
        context.Request.Content = "Hướng dẫn bóc cam";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "out_of_scope", Reply = "Draft" }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected text, result.State.PendingQuestion is question and result.State.PendingPlanningRequest is pending.
        result.Reply.Should().Be($"{AgentReplyGuard.OutOfScope}\n{question}");
        result.State.PendingQuestion.Should().Be(question);
        result.State.PendingPlanningRequest.Should().Be(pending);
    }

    // TEST-45: A question answered by the model no longer dumps the plan; a request to see it over a saved plan still lists it.
    /// <summary>Verifies the end-to-end behaviour of the answer action for both kinds of message.</summary>
    [Theory]
    [InlineData("Vì sao chọn món này?", false)]
    [InlineData("Xem lại lịch giúp mình", true)]
    public async Task RunAsync_AnswerAction_ListsPlanOnlyWhenAsked(string content, bool listed)
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to content.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item() with { DishName = "Mì Quảng" }));
        context.Request.Content = content;
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply.Contains() is listed and depending on !listed, result.Reply is AgentReplyBuilder.AnswerUnavailable.
        result.Reply.Contains("Mì Quảng").Should().Be(listed);
        if (!listed) result.Reply.Should().Be(AgentReplyBuilder.AnswerUnavailable);
    }

    // TEST-46: An empty reply is not a failure: refusals and social messages use the backend's fixed text in one call; this was the "hướng dẫn tôi" 503.
    /// <summary>Reproduces conversation 88: the model left reply empty because the backend owns the refusal, which cost repairs and ended in a 503.</summary>
    [Theory]
    [InlineData("out_of_scope", AgentReplyGuard.OutOfScope)]
    [InlineData("chat", AgentReplyGuard.ChatFallback)]
    public async Task RunAsync_EmptyReply_UsesFixedReplyInOneCall(string action, string expected)
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to a test text.
        var context = AgentTestData.Context();
        context.Request.Content = "hướng dẫn tôi gọt vỏ cam";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "" }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is expected.
        result.Reply.Should().Be(expected);
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-47: An empty reply on a clarify turn also falls back to the fixed wording without a repair call; an empty answer is covered by RunAsync_EmptyAnswer_IsWrittenOnce.
    /// <summary>Ensures no paid repair call is spent on a question the backend can word itself.</summary>
    [Theory]
    [InlineData("clarify")]
    public async Task RunAsync_EmptyReplyOnQuestion_UsesFixedWordingWithoutRepair(string action)
    {
        // STEP-01: Get context from AgentTestData.Context.
        var context = AgentTestData.Context();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "   " }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply.Trim() is not empty.
        result.Reply.Trim().Should().NotBeEmpty();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-48: Output still unusable after its one repair asks the user again; the state is unchanged and telemetry never holds the model's text.
    /// <summary>Bounds paid calls at two and replaces the old 503 with a question the user can answer.</summary>
    [Fact]
    public async Task RunAsync_UnusableOutputAfterRepair_AsksAgainWithoutLeakingText()
    {
        // STEP-01: Get context from AgentTestData.Context and create secret as string.
        var context = AgentTestData.Context();
        var secret = new string('x', new AgentLimits().MaxMessageLength + 1);
        // STEP-02: Mock IAgentLlmService and IAgentTelemetry; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = secret }));
        var telemetry = new Mock<IAgentTelemetry>();

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, telemetry: telemetry.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is AgentReplyGuard.NotUnderstood and result.State is context.State.
        result.Reply.Should().Be(AgentReplyGuard.NotUnderstood);
        result.State.Should().Be(context.State);
        // STEP-05: Verify RunAsync is called 2 times and Record is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        telemetry.Verify(x => x.Record("invalid_output", 7, It.IsAny<long?>(), It.IsAny<long?>(), It.Is<object?>(data => !System.Text.Json.JsonSerializer.Serialize(data, (System.Text.Json.JsonSerializerOptions?)null).Contains("xxx"))), Times.Exactly(2));
    }

    // TEST-49: Items attached to a question are dropped rather than failing the turn, and never reach a proposal.
    /// <summary>A clarify or answer can never edit a plan, so its stray items are simply ignored.</summary>
    [Fact]
    public async Task RunAsync_QuestionWithItems_IgnoresItems()
    {
        // STEP-01: Get context from AgentTestData.Context.
        var context = AgentTestData.Context();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer() with { Items = [AgentTestData.Item()] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot is null and result.Reply has a value.
        result.Snapshot.Should().BeNull();
        result.Reply.Should().NotBeNullOrWhiteSpace();
    }

    // TEST-50: Discard or reset carrying a state or items is refused without a service error and without discarding anything.
    /// <summary>Keeps draft controls from also mutating the plan, while the user still gets an answer.</summary>
    [Fact]
    public async Task RunAsync_ControlWithPayload_AsksAgain()
    {
        // STEP-01: Get context from AgentTestData.Context.
        var context = AgentTestData.Context();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "reset_conversation", Reply = "ok", Items = [AgentTestData.Item()] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Action is null and result.Reply is AgentReplyGuard.NotUnderstood.
        result.Action.Should().BeNull();
        result.Reply.Should().Be(AgentReplyGuard.NotUnderstood);
    }

    // TEST-51: The reported flow: the model words each question and the backend records how far it got; a title holding a place does not answer it, and a stated daily budget is multiplied.
    /// <summary>Walks the whole intake over an empty three-day roadmap whose title names Hà Nội.</summary>
    [Fact]
    public async Task RunAsync_NewPlan_ModelAsksPlaceThenScopeThenPreferences()
    {
        // STEP-01: Create state as AgentState and script the model's three questions.
        var state = new AgentState { NumDays = 3, Title = "3 ngày ăn ngon Hà Nội" };
        var llm = Scripted(
            Asks("trip_details", "Bạn muốn ăn ở đâu nè?", state with { Province = "Hà Nội" }),
            Asks("place_scope", "Bạn muốn tìm trong toàn Hà Nội hay quanh một khu?", state with { Province = "Hà Nội" }),
            Asks("preferences", "Bạn có khẩu vị riêng gì không?", state with { Province = "Hà Nội", BudgetAmount = 1500000, BudgetQuote = "500k 1 ngày" }));
        var sut = Create(llm.Object, intakeCatalog: IntakeCatalog());

        // STEP-02: Run sut.RunAsync and keep place.
        var place = await sut.RunAsync(IntakeContext("lap lich cho toi", state), default);
        // STEP-03: Assert the model's question is shown, the title did not answer it and the stage and request are recorded.
        place.Reply.Should().Be("Bạn muốn ăn ở đâu nè?");
        place.State.Province.Should().BeNull();
        place.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        place.State.PendingQuestion.Should().Be(place.Reply);
        place.State.PendingPlanningRequest!.Messages.Should().Equal("lap lich cho toi");

        // STEP-04: Run sut.RunAsync and keep scope.
        var scope = await sut.RunAsync(IntakeContext("Hà Nội", place.State), default);
        // STEP-05: Assert the province is taken and the scope stage recorded, which shows the current-location button.
        scope.State.Province.Should().Be("Hà Nội");
        scope.State.PlanningIntake.Should().Be(AgentIntakePolicy.Scope);
        scope.State.PendingPlanningRequest!.Messages.Should().Equal("lap lich cho toi", "Hà Nội");

        // STEP-06: Run sut.RunAsync and keep preferences.
        var preferences = await sut.RunAsync(IntakeContext("ngân sách 500k 1 ngày", scope.State), default);
        // STEP-07: Assert the budget and the preferences stage, which shows the plan-now button.
        preferences.Reply.Should().Be("Bạn có khẩu vị riêng gì không?");
        preferences.State.BudgetAmount.Should().Be(1500000);
        preferences.State.PlanningIntake.Should().Be(AgentIntakePolicy.Preferences);
        preferences.State.PendingQuestion.Should().Be(preferences.Reply);
    }

    // TEST-52: The order holds whatever the model tries: asking about preferences or scope before the trip is known gets the backend's question for what is missing, with no further call.
    /// <summary>Only the wording belongs to the model; the steps and their order stay fixed.</summary>
    [Theory]
    [InlineData("preferences", null, "Bạn muốn trải nghiệm ẩm thực")]
    [InlineData("place_scope", null, "Bạn muốn trải nghiệm ẩm thực")]
    [InlineData("preferences", "Hà Nội", "số ngày")]
    public async Task RunAsync_ModelAsksAheadOfOrder_AsksWhatIsMissing(string clarification, string? province, string expected)
    {
        // STEP-01: Get context from IntakeContext and script the model.
        var context = IntakeContext("lập lịch", new AgentState { Province = province });
        var llm = Scripted(Asks(clarification, "Bạn có khẩu vị gì không?"));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-03: Assert the reply asks what is missing and the stage stays on the details.
        result.Reply.Should().Contain(expected);
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-53: Words that fail the check show the backend's own question of that kind, so a bad reply never loses the step.
    /// <summary>Covers each question the model now words.</summary>
    [Theory]
    [InlineData("trip_details", null)]
    [InlineData("place_scope", "Hà Nội")]
    [InlineData("preferences", "Hà Nội")]
    public async Task RunAsync_ModelQuestionFailsCheck_ShowsFixedQuestion(string clarification, string? province)
    {
        // STEP-01: Create state and get context from IntakeContext.
        var state = new AgentState { Province = province, NumDays = province is null ? null : (short)3, BudgetAmount = province is null ? null : 500000 };
        var context = IntakeContext("lập lịch", state);
        // STEP-02: Get result from Create().RunAsync with a question naming an unbacked price.
        var result = await Create(Scripted(Asks(clarification, "Thường khoảng 777k một người, được không?")).Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-03: Assert the fixed question of that kind is shown.
        result.Reply.Should().Be(clarification switch { "trip_details" => AgentIntakePolicy.PlaceQuestion, "place_scope" => AgentIntakePolicy.ScopeQuestion("Hà Nội"), _ => AgentIntakePolicy.PreferencesQuestion });
    }

    // TEST-54: Answering the preference question closes the intake and plans in the same turn; "không" reaches the model marked as adding no requirement.
    /// <summary>Ensures "không" is not read as a dismissal and that the plan is not blocked as an unrequested change on an empty roadmap.</summary>
    [Fact]
    public async Task RunAsync_PreferenceAnswer_ClosesIntakeAndPlans()
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext and add the computed value to context.Grants.
        var state = new AgentState { Province = "Da Nang", NumDays = 1, BudgetAmount = 500000, MealSlots = ["lunch"], PlanningIntake = AgentIntakePolicy.Preferences,
            PendingQuestion = AgentIntakePolicy.PreferencesQuestion, PendingPlanningRequest = new(["lap lich cho toi"]) };
        var context = IntakeContext("không", state, AgentTestData.Snapshot());
        context.Grants.Add(new(null, null, [1], Guid.NewGuid(), state));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([],
            new AgentOutput { Action = "create_proposal", Reply = "Xong rồi nhé!", Items = [AgentTestData.Item()] }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert context.IntakeComplete is true, result.Snapshot is not null, result.State.PlanningIntake is null and result.State.PendingQuestion is null.
        context.IntakeComplete.Should().BeTrue();
        result.Snapshot.Should().NotBeNull();
        result.State.PlanningIntake.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
    }

    // TEST-55: A model that asks again right after the intake gets one repair and then plans; a second question is shown without a third call.
    /// <summary>Bounds the extra paid call to one while protecting the user from being asked what they just answered.</summary>
    [Fact]
    public async Task RunAsync_ModelAsksAgainAfterIntake_RepairsOnce()
    {
        // STEP-01: Create state as AgentState and get context from IntakeContext.
        var state = new AgentState { Province = "Da Nang", NumDays = 1, BudgetAmount = 500000, PlanningIntake = AgentIntakePolicy.Preferences, PendingPlanningRequest = new(["lap lich"]) };
        var context = IntakeContext("không", state);
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "general", Reply = "Bạn muốn gì?" }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.Reply has a value and context.RepairFeedback contains the expected text.
        result.Reply.Should().NotBeNullOrWhiteSpace();
        context.RepairFeedback.Should().Contain("Không hỏi lại");
        // STEP-05: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-56: Current location resolves the province from the nearest restaurant and limits candidates to 10 km before the model runs; an uncovered location needs no model call.
    /// <summary>Keeps recommendations geographic without a map provider call, and never keeps coordinates the trip cannot use.</summary>
    [Theory]
    [InlineData("Hà Nội")]
    [InlineData(null)]
    public async Task RunAsync_CurrentLocation_ResolvesNearestProvince(string? nearest)
    {
        // STEP-01: Create state as AgentState and script the model's next question.
        var state = new AgentState { NumDays = 3, OriginMode = "gps", OriginLatitude = 21.03m, OriginLongitude = 105.85m, OriginLabel = "Vị trí hiện tại" };
        var context = IntakeContext("Dùng vị trí hiện tại của mình", state);
        var llm = Scripted(Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?"));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog(nearest)).RunAsync(context, default);

        // STEP-03: Assert depending on nearest is null, result.Reply contains the expected text, result.Reply contains the expected text and 7 more.
        if (nearest is null)
        {
            result.Reply.Should().Contain("chưa có quán nào").And.Contain("Hà Nội").And.EndWith(AgentIntakePolicy.PlaceQuestion);
            result.State.Province.Should().BeNull();
            result.State.OriginMode.Should().Be("none");
            result.State.OriginLatitude.Should().BeNull();
            result.State.RadiusKm.Should().BeNull();
            llm.Verify(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            result.Reply.Should().StartWith("Bạn đang ở gần Hà Nội").And.Contain("10 km").And.EndWith("Ngân sách của bạn khoảng bao nhiêu?");
            result.State.Province.Should().Be("Hà Nội");
            result.State.RadiusKm.Should().Be(AgentIntakePolicy.DefaultRadiusKm);
        }
    }

    // TEST-57: A province ANGI has no data for says where it has data and asks again, whatever the model wrote; an aside keeps the place in the questions.
    /// <summary>Ensures the questions neither start somewhere with no restaurants nor trap a user who changed the subject.</summary>
    [Fact]
    public async Task RunAsync_UnansweredPlace_RepeatsOrStepsAside()
    {
        // STEP-01: Create state as AgentState and get unknown from the model reading a province outside the catalog.
        var state = new AgentState { PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion };
        var unknown = await Create(Scripted(Asks("trip_details", "Đà Nẵng tuyệt đó, bạn đi mấy ngày?", state with { Province = "Đà Nẵng" })).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext("Đà Nẵng", state), default);
        // STEP-02: Assert unknown.Reply states the coverage and asks again, and unknown.State keeps no province.
        unknown.Reply.Should().StartWith("Mình chưa có dữ liệu quán ăn ở Đà Nẵng.").And.Contain("Hiện ANGI có dữ liệu ở Hà Nội").And.EndWith(AgentIntakePolicy.PlaceQuestion);
        unknown.State.Province.Should().BeNull();
        unknown.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);

        // STEP-03: Get aside from the model answering a question about the questions.
        var aside = await Create(Scripted(new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = "Mình cần biết địa điểm để chọn quán nha." })).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext("vì sao bạn lại hỏi mình nhiều câu như vậy", state), default);
        // STEP-04: Assert the question stays open and is repeated after the answer.
        aside.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        aside.Reply.Should().EndWith(AgentIntakePolicy.PlaceQuestion);
    }

    // TEST-58: A message stating everything is taken whole and only what is missing is asked; a plan attempted before the preferences question gets it, and a stated taste goes to its own question.
    /// <summary>Ensures nobody is asked again for what they already wrote in one sentence, while no step is skipped.</summary>
    [Fact]
    public async Task RunAsync_AllFactsInOneMessage_AsksOnlyWhatIsMissing()
    {
        // STEP-01: Create trip as the state the model read, and get all from a model that tries to plan right away.
        var trip = new AgentState { Province = "Hà Nội", NumDays = 3, BudgetAmount = 1000000, BudgetQuote = "1 triệu" };
        var all = await Create(Scripted(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi!", State = trip, Items = [AgentTestData.Item()] })).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext("lập lịch Hà Nội 3 ngày 1 triệu", new AgentState()), default);
        // STEP-02: Assert all.Reply is PreferencesQuestion, no plan was made and all.State holds the trip.
        all.Reply.Should().Be(AgentIntakePolicy.PreferencesQuestion);
        all.Snapshot.Should().BeNull();
        all.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.NumDays == 3 && x.BudgetAmount == 1000000 && x.PlanningIntake == AgentIntakePolicy.Preferences);

        // STEP-03: Get asked from a model that asks a question the trip already answers.
        var asked = await Create(Scripted(Asks("trip_details", "Bạn đi mấy ngày vậy?", trip)).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext("lập lịch Hà Nội 3 ngày 1 triệu", new AgentState()), default);
        // STEP-04: Assert asked.Reply moves on to preferences instead.
        asked.Reply.Should().Be(AgentIntakePolicy.PreferencesQuestion);

        // STEP-05: Get taste from a model asking the level of a stated taste.
        var taste = await Create(Scripted(Asks("taste_intensity", "Bạn thích cay nhẹ hay vừa?", trip)).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext("lập lịch Hà Nội 3 ngày 1 triệu, thích ăn cay", new AgentState()), default);
        // STEP-06: Assert taste.Reply is the model's question, taste.State keeps the trip and no intake stage opened.
        taste.Reply.Should().Be("Bạn thích cay nhẹ hay vừa?");
        taste.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.NumDays == 3 && x.BudgetAmount == 1000000);
        taste.State.PlanningIntake.Should().BeNull();
    }

    // TEST-59: With no plan to disturb, the user's own answer is applied without asking them to confirm what they just said; a budget already set is still protected.
    /// <summary>Answering the questions with a new duration or "no limit", including slang, moves straight on, because the model read it.</summary>
    [Theory]
    [InlineData("lập lịch Hà Nội 5 ngày", 5, false)]
    [InlineData("Hà Nội, không giới hạn", 3, true)]
    [InlineData("Hà Nội, xả láng", 3, true)]
    public async Task RunAsync_AnswerWithoutAPlan_AppliesWithoutConfirmation(string content, short numDays, bool unrestricted)
    {
        // STEP-01: Get result from a model that read the answer.
        var state = new AgentState { NumDays = 3, PlanningIntake = AgentIntakePolicy.Details };
        var result = await Create(Scripted(Asks("trip_details", "Bạn cho mình biết thêm được không?", state with { Province = "Hà Nội", NumDays = numDays, BudgetUnrestricted = unrestricted })).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext(content, state), default);

        // STEP-02: Assert result.State.PendingStateChange is null, result.State.NumDays is numDays, result.State.BudgetUnrestricted is unrestricted and 1 more.
        result.State.PendingStateChange.Should().BeNull();
        result.State.NumDays.Should().Be(numDays);
        result.State.BudgetUnrestricted.Should().Be(unrestricted);
        result.State.Province.Should().Be("Hà Nội");
    }

    // TEST-60: An invalid duration in an answer repeats the validator's message and keeps the question open.
    /// <summary>Ensures a bad answer is explained to the user instead of failing the turn.</summary>
    [Fact]
    public async Task RunAsync_InvalidAnswer_ExplainsLimit()
    {
        // STEP-01: Get result from a model that read the duration as stated.
        var result = await Create(Scripted(Asks("trip_details", "Ngân sách của bạn bao nhiêu?", new AgentState { Province = "Hà Nội", NumDays = 145 })).Object, intakeCatalog: IntakeCatalog())
            .RunAsync(IntakeContext("lập lịch Hà Nội 145 ngày", new AgentState()), default);

        // STEP-02: Assert result.Reply starts with the expected text, result.State.NumDays is null and result.State.PlanningIntake is AgentIntakePolicy.Details.
        result.Reply.Should().StartWith("Số ngày phải từ 1 đến 100.");
        result.State.NumDays.Should().BeNull();
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
    }

    // TEST-61: A plan already on screen skips the questions without reading provinces or places; only the reply check reads dish names.
    /// <summary>Ensures edits keep their existing path and ask nothing extra.</summary>
    [Fact]
    public async Task RunAsync_ExistingPlan_SkipsIntake()
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to a test text.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "lập lịch lại";
        // STEP-02: Mock ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository and IAgentLlmService; stub GetDishNamesAsync and llm.RunAsync.
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>(MockBehavior.Strict);
        catalog.Setup(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-03: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: catalog.Object).RunAsync(context, default);

        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-62: The reported itinerary reaches the budget question with every day placed: the model reads the days, the backend locates them and says so above the model's question.
    /// <summary>Replays the start of conversation 94, without searching places as restaurants.</summary>
    [Fact]
    public async Task RunAsync_ReportedItinerary_LocatesThenAsksBudget()
    {
        // STEP-01: Get context from IntakeContext and script the model.
        var context = IntakeContext("Ngày 1 tôi ở Hoàn kiếm\nNgày 2 tôi đi lăng bác\nNgày 3 tôi ở Hoàng Mai\nHãy lập lịch đi ăn", new AgentState());
        var llm = Scripted(Locates(null, new([1], "Hoàn kiếm"), new([2], "lăng bác"), new([3], "Hoàng Mai")), Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?"));
        var sut = Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places());

        // STEP-02: Run sut.RunAsync with context and keep result.
        var result = await sut.RunAsync(context, default);

        // STEP-03: Assert result.Reply starts with the backend's note and ends with the model's question, and result.State holds the days.
        result.Reply.Should().StartWith("Mình ghi nhận hành trình ở Hà Nội: ngày 1 quanh Hoàn Kiếm").And.Contain("ngày 2 quanh Lăng Bác").And.Contain("ngày 3 quanh Hoàng Mai").And.EndWith("Ngân sách của bạn khoảng bao nhiêu?");
        result.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.NumDays == 3 && x.PlanningIntake == AgentIntakePolicy.Details);
        result.State.DayAreas.Should().HaveCount(3);
    }

    // TEST-63: A longer trip keeps its duration and says what the unnamed days will do, which is how a four-day roadmap frame met a three-day itinerary.
    /// <summary>Ensures the mismatch of conversations 94 and 158 is stated to the user instead of silently planning a day they did not mention.</summary>
    [Fact]
    public async Task RunAsync_ItineraryShorterThanTrip_StatesTheRemainingDays()
    {
        // STEP-01: Get context from IntakeContext and script the model.
        var context = IntakeContext("Ngày 1 tôi ở Hoàn Kiếm, ngày 2 đi lăng bác", new AgentState { NumDays = 4, SourceRoadmapUpdatedAt = DateTime.UtcNow });
        var llm = Scripted(Locates(null, new([1], "Hoàn Kiếm"), new([2], "lăng bác")), Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?"));
        var sut = Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places());

        // STEP-02: Run sut.RunAsync with context and keep result.
        var result = await sut.RunAsync(context, default);

        // STEP-03: Assert result.State.NumDays is 4 and result.Reply contains the expected text.
        result.State.NumDays.Should().Be(4);
        result.Reply.Should().Contain("Những ngày còn lại trong 4 ngày, mình chọn trong toàn Hà Nội.");
    }

    // TEST-64: A named area answers the place question and is searched around; an unknown one is reported with no province, so nothing is planned somewhere wrong.
    /// <summary>Replays "Hai bà trưng", which used to end in "chưa nhận ra địa điểm", and an unknown name beside it.</summary>
    [Theory]
    [InlineData("Hai bà trưng", "khu vực Hai Bà Trưng", true)]
    [InlineData("Cầu Giấy", "khu vực Cầu Giấy", true)]
    [InlineData("xyzxyz", "Mình chưa tìm được quán quanh “xyzxyz”", false)]
    public async Task RunAsync_AreaAnswersPlaceQuestion(string place, string expected, bool located)
    {
        // STEP-01: Create open as AgentState and script the model.
        var open = new AgentState { PlanningIntake = AgentIntakePolicy.Details, PendingQuestion = AgentIntakePolicy.PlaceQuestion };
        var llm = Scripted(Locates(place), Asks("trip_details", "Bạn đi mấy ngày nè?"));

        // STEP-02: Run sut.RunAsync and keep result.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(IntakeContext(place, open), default);

        // STEP-03: Assert the note leads the reply and the state matches what was located.
        result.Reply.Should().Contain(expected).And.EndWith("Bạn đi mấy ngày nè?");
        if (located) result.State.Should().Match<AgentState>(x => x.OriginMode == "pinned" && x.Province == "Hà Nội" && x.RadiusKm != null);
        else result.State.Province.Should().BeNull();
    }

    // TEST-65: Over a plan a day moves through locate_places only as far as the plan allows; covers a whole-trip area, a day past the trip, and a place the day already has.
    /// <summary>The model reads "ngày 9 ở Hoàn Kiếm"; the backend decides what it may change, and an unchanged itinerary forces no replan.</summary>
    [Theory]
    [InlineData("area")]
    [InlineData("past")]
    [InlineData("same")]
    public async Task RunAsync_LocatePlacesOverPlan_MovesOnlyWhatItMay(string area)
    {
        // STEP-01: Get context over a one-day plan and script the model.
        var plan = AgentTestData.Snapshot(AgentTestData.Item());
        plan = plan with { State = plan.State with { Province = "Hà Nội", NumDays = 1, DayAreas = area == "same" ? [new(1, "Hoàn Kiếm", 21.03m, 105.85m, 3m)] : null } };
        var context = AgentTestData.Context(plan);
        context.State = plan.State;
        context.Request.Content = "ngày đó tôi ở Hoàn Kiếm";
        var call = area switch { "area" => Locates("Hoàn Kiếm"), "past" => Locates(null, new AgentLocateStop([9], "Hoàn Kiếm")), _ => Locates(null, new AgentLocateStop([1], "Hoàn Kiếm")) };
        var llm = Scripted(call, new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-03: Assert no day moved, a whole-trip area was refused and a day past the trip is named back without extending it.
        context.ItineraryChanged.Should().BeFalse();
        context.State.NumDays.Should().Be(1);
        if (area == "area") context.Exchanges.Single().Results[0].GetProperty("status").GetString().Should().Be("invalid_arguments");
        if (area == "past") context.Exchanges.Single().Results[0].GetProperty("outsideTrip")[0].GetInt16().Should().Be(9);
    }

    // TEST-66: A model that sends an unusable proposal twice on a new plan no longer fails the turn with PROPOSAL_NOT_COMMITTABLE: the backend assembles the plan for no further call.
    /// <summary>Reproduces the "bản nháp không đủ điều kiện" error of conversation 94's failed turn, which forced a second paid attempt.</summary>
    [Fact]
    public async Task RunAsync_UnusableProposalTwiceOnNewPlan_BackendAssemblesPlan()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a copy of context.State, add the computed value to context.Grants and 1 more.
        var context = AgentTestData.Context();
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"] };
        context.Grants.Add(new(1, "lunch", [1], null, context.State));
        var unusable = new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Items = [AgentTestData.Item()], Changes = [new("add", null, AgentTestData.Item())] };
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], unusable));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot.Items has exactly one matching item and result.Validation.Errors is empty.
        result.Snapshot!.Items.Should().ContainSingle(item => item.DishId == 1 && item.DayNumber == 1 && item.MealSlot == "lunch");
        result.Validation!.Errors.Should().BeEmpty();
        // STEP-05: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-67: The same failure on an edit keeps the plan on screen, savable and unchanged, instead of an error.
    /// <summary>Ensures a model failure never destroys or blocks the plan the user already reviewed.</summary>
    [Fact]
    public async Task RunAsync_UnusableProposalTwiceOnExistingPlan_KeepsPlan()
    {
        // STEP-01: Get source from AgentTestData.Snapshot, get context from AgentTestData.Context, set context.Request.Content to a test text and 1 more.
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var context = AgentTestData.Context(source);
        context.Request.Content = "đổi bữa trưa ngày 1 sang món khác";
        var unusable = new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Items = [AgentTestData.Item(2, "b")], Changes = [new("add", null, AgentTestData.Item(2, "b"))] };
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], unusable));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply is AgentReplyBuilder.PlanningIncomplete, result.Snapshot is null and result.State.PendingQuestion is null.
        result.Reply.Should().Be(AgentReplyBuilder.PlanningIncomplete);
        result.Snapshot.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
        // STEP-05: Verify RunAsync is called 2 times.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-68: A place the model read over an existing plan moves that day through locate_places, and the proposal for it is accepted.
    /// <summary>Ensures "ngày 1 tôi ở Hoàn Kiếm" changes the plan in whatever wording the model understood it.</summary>
    [Fact]
    public async Task RunAsync_ItineraryOnExistingPlan_MovesDayAndAcceptsProposal()
    {
        // STEP-01: Get source from AgentTestData.Snapshot, get context from AgentTestData.Context, set context.State to a copy of context.State and 5 more.
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var context = AgentTestData.Context(source);
        context.State = context.State with { Province = "Hà Nội", NumDays = 1, MealSlots = ["lunch"] };
        context.Request.Content = "hôm đó tôi qua Hoàn Kiếm chơi";
        var dish = AgentTestData.Dish(2);
        dish.Restaurant.ProvinceName = "Hà Nội"; dish.Restaurant.Latitude = 21.03m; dish.Restaurant.Longitude = 105.85m;
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        var calls = 0;
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            if (calls++ == 0) return Task.FromResult<AgentLlmStep?>(Locates(null, new AgentLocateStop([1], "Hoàn Kiếm")));
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [2], null, context.State));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Scope = new(1, "lunch"), Items = [AgentTestData.Item(2, "a")] }));
        });

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [dish], intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-04: Assert context.ItineraryChanged is true, result.Validation.Errors is empty and 2 more.
        context.ItineraryChanged.Should().BeTrue();
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.State.DayAreas!.Single().Label.Should().Be("Hoàn Kiếm");
        result.Snapshot.Items.Single().DishId.Should().Be(2);
    }

    // TEST-69: A trip only to a place with no restaurants says so through the backend's note, keeps no province and lets the model ask again.
    /// <summary>Ensures "Ngày 1 đi Đà Nẵng" is answered honestly instead of planning in Hà Nội or asking as if nothing was said.</summary>
    [Fact]
    public async Task RunAsync_OnlyPlaceOutsideCatalog_SaysSoAndAsks()
    {
        // STEP-01: Script the model and build sut through Create.
        var llm = Scripted(Locates(null, new AgentLocateStop([1], "Đà Nẵng")), Asks("trip_details", "Bạn muốn đổi sang nơi nào không?"));
        var sut = Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places());

        // STEP-02: Run sut.RunAsync and keep result.
        var result = await sut.RunAsync(IntakeContext("Ngày 1 đi Đà Nẵng\nlập lịch giúp mình", new AgentState()), default);

        // STEP-03: Assert result.Reply starts with the expected text, result.State.Province is null and result.State.DayAreas is null.
        result.Reply.Should().StartWith("Mình chưa tìm được quán quanh “Đà Nẵng”.").And.EndWith("Bạn muốn đổi sang nơi nào không?");
        result.State.Province.Should().BeNull();
        result.State.DayAreas.Should().BeNull();
    }

    // TEST-70: The itineraries of conversations 121 and 158 read Hồ Gươm at the lake, keep a free day for the whole province and still ask about preferences.
    /// <summary>Replays the message that put day 1 in Hà Đông and made day 3 a place called "Tự Do".</summary>
    [Fact]
    public async Task RunAsync_ItineraryWithFreeDay_ReadsCorrectly()
    {
        // STEP-01: Get context from IntakeContext and script the model.
        var context = IntakeContext("Hiện tại tôi có hành trình sau : \nNgày 1 : Tôi đi tham quan Hồ Gươm \nNgày 2 : Tôi đi Lăng Bác \nNgày 3 : Tôi đi tự do \nGợi ý món ăn theo lịch trình",
            new AgentState { BudgetAmount = 900000 });
        var llm = Scripted(Locates(null, new([1], "Hồ Gươm"), new([2], "Lăng Bác"), new([3], null)), Asks("preferences", "Bạn có khẩu vị riêng gì không?"));
        var sut = Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places());

        // STEP-02: Run sut.RunAsync with context and keep result.
        var result = await sut.RunAsync(context, default);

        // STEP-03: Assert result.State.NumDays is 3, result.State.DayAreas.Select(filtered) equals the expected value and 3 more.
        result.State.NumDays.Should().Be(3);
        result.State.DayAreas!.Select(area => (area.DayNumber, area.Label)).Should().Equal(((short)1, "Hồ Gươm"), ((short)2, "Lăng Bác"));
        result.State.DayAreas![0].Latitude.Should().Be(21.0287m);
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Preferences);
        result.Reply.Should().Contain("Những ngày còn lại trong 3 ngày, mình chọn trong toàn Hà Nội.").And.EndWith("Bạn có khẩu vị riêng gì không?");
    }

    // TEST-71: Complaints about a dish, and any plan without itinerary places, keep the model's normal edit flow.
    /// <summary>Guards the rule that the new correction case does not take over edits handled before.</summary>
    [Theory]
    [InlineData("bữa trưa ngày 1 sai vị rồi", true)]
    [InlineData("món này sai rồi", true)]
    [InlineData("ngày 1 sai rồi", false)]
    public async Task RunAsync_ComplaintAboutDishOrPlanWithoutPlaces_KeepsModelFlow(string content, bool withPlaces)
    {
        // STEP-01: Derive context from the expected value.
        var context = withPlaces ? WrongDayOneContext(content) : IntakeContext(content, AgentTestData.State() with { Province = "Hà Nội" }, AgentTestData.Snapshot(AgentTestData.Item()));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-03: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-04: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        // STEP-05: Assert context.ItineraryChanged is false.
        context.ItineraryChanged.Should().BeFalse();
    }

    // TEST-72: Changing taste after a proposal replans with the new taste, keeps the itinerary places although the model's state cannot carry them, and accepts the new dishes.
    /// <summary>Covers the question "người dùng bỗng đổi khẩu vị khi đã có đề xuất": every changed position needs a fresh search under the new taste.</summary>
    [Fact]
    public async Task RunAsync_TasteChangedOnProposalWithItinerary_ReplansAndKeepsPlaces()
    {
        // STEP-01: Derive before from a copy of AgentTestData.State(), create source as AgentSnapshot, get context from AgentTestData.Context and 3 more.
        var before = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"], TasteTargets = [new("spicy", .25)], DayAreas = [new(1, "Hoàn Kiếm", 16.05m, 108.2m, 3m)] };
        var source = new AgentSnapshot(before, [AgentTestData.Item() with { DishName = "Dish 1" }], []);
        var context = AgentTestData.Context(source);
        context.State = before;
        context.Request.Content = "đổi khẩu vị sang ngọt nhẹ cho cả chuyến";
        var fresh = AgentTestData.Dish(2);
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            var changed = context.State with { TasteTargets = [new("sweet", .25, "ngọt nhẹ")], DayAreas = null };
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [2], Guid.NewGuid(), context.State with { TasteTargets = [new("sweet", .25)] }));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", State = changed, Scope = new(1, null), Items = [AgentTestData.Item(2, "a")] }))!;
        });

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(), fresh], intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-04: Assert result.Validation.Errors is empty, result.Snapshot.Items.Single().DishId is 2 and 3 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Single().DishId.Should().Be(2);
        result.Snapshot.State.TasteTargets.Should().ContainSingle(target => target.Dimension == "sweet" && target.Target == .25);
        result.Snapshot.State.DayAreas!.Single().Label.Should().Be("Hoàn Kiếm");
        result.Snapshot.Notes.Should().Contain(note => note.Contains("ngọt mức nhẹ"));
    }

    // TEST-73: A bare taste the model acted on without a level changes no dish: the model gets one repair and then the question is asked; covers the model asking after the repair and insisting.
    /// <summary>"thích ăn cay" changes nothing on the plan until the user gives its level.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_BareTasteOverPlan_AsksInsteadOfChangingDishes(bool modelAsks)
    {
        // STEP-01: Get context over a plan with a message stating a taste and no level, and create the proposal the model sends.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "thích ăn cay";
        var proposal = new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = "Xong rồi", Scope = new(1, "lunch"), Items = [AgentTestData.Item(2, "a")] });
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync with the proposal, then the model's question or the same proposal.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(proposal)
            .ReturnsAsync(modelAsks ? new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "taste_intensity", Reply = "Bạn thích cay nhẹ hay vừa?" }) : proposal);

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, catalog: [AgentTestData.Dish(1), AgentTestData.Dish(2)]).RunAsync(context, default);

        // STEP-04: Assert no new plan and the question asked; verify RunAsync is called twice.
        result.Snapshot.Should().BeNull();
        result.Reply.Should().Be(modelAsks ? "Bạn thích cay nhẹ hay vừa?" : AgentReplyBuilder.Clarify(context, "taste_intensity"));
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-74: The current location shared at the scope question keeps its coordinates and radius through the model's next question.
    /// <summary>Ensures the location button works at this question, where the province is already known.</summary>
    [Fact]
    public async Task RunAsync_ScopeAnsweredByLocation_KeepsCoordinates()
    {
        // STEP-01: Create located as AgentState and script the model.
        var located = new AgentState { Province = "Hà Nội", PlanningIntake = AgentIntakePolicy.Scope, OriginMode = "gps", OriginLatitude = 21.03m, OriginLongitude = 105.85m, OriginLabel = "Vị trí hiện tại", RadiusKm = 10m };
        var sut = Create(Scripted(Asks("trip_details", "Bạn đi mấy ngày nè?")).Object, intakeCatalog: IntakeCatalog(), places: Places());

        // STEP-02: Run sut.RunAsync and keep result.
        var result = await sut.RunAsync(IntakeContext("Dùng vị trí hiện tại của mình", located), default);

        // STEP-03: Assert result.Reply is the model's question and result.State matches the condition.
        result.Reply.Should().Be("Bạn đi mấy ngày nè?");
        result.State.Should().Match<AgentState>(x => x.OriginMode == "gps" && x.RadiusKm == 10m && x.PlanningIntake == AgentIntakePolicy.Details);
    }

    // TEST-75: The roadmap of conversation 138 has a pin and no province: the pin answers "ở đâu" and gives the search circle, and the backend says so above the model's question.
    /// <summary>This message used to ask for a place the user had already chosen on the map.</summary>
    [Fact]
    public async Task RunAsync_PinnedRoadmapWithoutProvince_UsesPinInsteadOfAskingPlace()
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext and get result from Create().RunAsync.
        var state = new AgentState { NumDays = 7, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };
        var context = IntakeContext("lập lịch cho tôi", state, new AgentSnapshot(state, [], []));

        var result = await Create(Scripted(Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?")).Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-02: Assert result.Reply contains the expected text and result.State matches the condition.
        result.Reply.Should().Contain("quanh điểm bạn đã chấm").And.EndWith("Ngân sách của bạn khoảng bao nhiêu?");
        result.State.Should().Match<AgentState>(x => x.Province == "Hà Nội" && x.RadiusKm == 10m && x.OriginMode == "pinned" && x.OriginLatitude == 21.03m);
    }

    // TEST-76: A pin far from every restaurant says so and asks for a place, keeping the pin; covers the only case where the place question remains.
    /// <summary>Ensures the question is honest instead of looking as if the pin was never seen.</summary>
    [Fact]
    public async Task RunAsync_PinOutsideCatalog_SaysSoAndAsksPlace()
    {
        // STEP-01: Create state as AgentState and get result from Create().RunAsync.
        var state = new AgentState { NumDays = 7, OriginMode = "pinned", OriginLatitude = 10m, OriginLongitude = 106m };

        var result = await Create(new Mock<IAgentLlmService>(MockBehavior.Strict).Object, intakeCatalog: IntakeCatalog(), places: Places())
            .RunAsync(IntakeContext("lập lịch cho tôi", state), default);

        // STEP-02: Assert result.Reply starts with the expected text and result.State matches the condition.
        result.Reply.Should().StartWith("Quanh điểm bạn chấm trên bản đồ chưa có quán nào trên ANGI.").And.Contain("ở đâu");
        result.State.Should().Match<AgentState>(x => x.Province == null && x.OriginLatitude == 10m);
    }

    // TEST-77: A trip that already has a province keeps its search as it is, so a user who widened the search is not narrowed again.
    /// <summary>Only the first contact with a pin fills the circle.</summary>
    [Fact]
    public async Task RunAsync_PinWithProvince_KeepsSearch()
    {
        // STEP-01: Create state as AgentState and get result from Create().RunAsync.
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };

        var result = await Create(Scripted(Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?")).Object, intakeCatalog: IntakeCatalog(), places: Places())
            .RunAsync(IntakeContext("lập lịch cho tôi", state), default);

        // STEP-02: Assert result.State.RadiusKm is null.
        result.State.RadiusKm.Should().BeNull();
    }

    // TEST-78: A dish suggested in the middle of the planning questions keeps the trip values and the open question, which is repeated.
    /// <summary>In conversation 160 "tôi không biết" was misread as asking for a dish and the plan being built was lost; one wrong reading must not cost the user their place.</summary>
    [Fact]
    public async Task RunAsync_DishSuggestionDuringIntake_KeepsQuestionAndTrip()
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext, get llm from WordingLlm and get result from Suggesting().RunAsync.
        var state = new AgentState { Province = "Da Nang", NumDays = 7, BudgetAmount = 3500000, PlanningIntake = AgentIntakePolicy.Preferences, PendingQuestion = AgentIntakePolicy.PreferencesQuestion };
        var context = IntakeContext("không nhưng tôi chỉ muốn gợi ý 1 món bất kì chứ có phải lập lịch đâu nhỉ", state);
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Vậy bạn thử {name1} ở {restaurant1} nhé, khoảng {price1}." }));

        var result = await Suggesting(llm.Object, intake: true).RunAsync(context, default);

        // STEP-02: Assert result.Reply contains "Dish 1" and the question, result.Snapshot is null and result.State matches the condition.
        result.Reply.Should().Contain("Dish 1").And.EndWith(AgentIntakePolicy.PreferencesQuestion);
        result.Snapshot.Should().BeNull();
        result.State.Should().Match<AgentState>(x => x.PlanningIntake == AgentIntakePolicy.Preferences && x.PendingQuestion == AgentIntakePolicy.PreferencesQuestion && x.NumDays == 7 && x.BudgetAmount == 3500000);
    }

    /// <summary>Records what each provider call was allowed to do, because the context object is reused through the turn.</summary>
    private static Mock<IAgentLlmService> Phases(List<(AgentTurnPhase Phase, string? Tool)> seen, params AgentLlmStep[] steps)
    {
        var llm = new Mock<IAgentLlmService>();
        var next = new Queue<AgentLlmStep>(steps);
        llm.Setup(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<AgentContext, bool, CancellationToken>((context, _, _) => seen.Add((context.Phase, context.ForcedTool)))
            .ReturnsAsync(() => next.Dequeue());
        return llm;
    }

    // TEST-79: A chat message is read with no tools, a named tool is then the only one, and following calls have all tools again.
    /// <summary>A model holding tools calls one even when none fits: "1 + 1 bằng mấy" once became a dish suggestion.</summary>
    [Fact]
    public async Task RunAsync_ChatMessage_ReadsThenForcesTheNamedTool()
    {
        // STEP-01: Get context and script a read asking for a tool, the tool call, and the final answer.
        var context = IntakeContext("ở Cầu Giấy", new AgentState());
        var seen = new List<(AgentTurnPhase Phase, string? Tool)>();
        var llm = Phases(seen, new AgentLlmStep([], new AgentOutput { Action = "use_tool", Reply = "", Tool = "locate_places" }), Locates("Cầu Giấy"),
            Asks("trip_details", "Bạn đi mấy ngày nè?"));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object, intakeCatalog: IntakeCatalog(), places: Places()).RunAsync(context, default);

        // STEP-03: Assert the phases of the three calls and that the tool ran.
        seen.Should().Equal((AgentTurnPhase.Read, null), (AgentTurnPhase.Forced, "locate_places"), (AgentTurnPhase.Act, null));
        context.Exchanges.Should().ContainSingle();
    }

    // TEST-80: The steps that already know the work skip the read; covers a button command, a submitted form and a replan after confirmation.
    /// <summary>Reading again would only spend a call on a message that carries no new meaning.</summary>
    [Theory]
    [InlineData("button")][InlineData("form")][InlineData("replan")]
    public async Task RunAsync_KnownWork_SkipsTheRead(string kind)
    {
        // STEP-01: Get context for the kind and script the answer.
        var context = IntakeContext("ok", new AgentState());
        if (kind == "button") context.Request.Command = "alternative";
        if (kind == "form") context.Request.ConfirmedState = new AgentState();
        if (kind == "replan") context.ReplanAfterConfirmation = true;
        var seen = new List<(AgentTurnPhase Phase, string? Tool)>();
        var llm = Phases(seen, new AgentLlmStep([], AgentTestData.Answer()));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert the only call offered all tools.
        seen.Should().Equal((AgentTurnPhase.Act, null));
    }

    // TEST-81: A broken read is repaired as a read; any other repair may need tools; and a tool named outside the read is not used.
    /// <summary>Keeps the no-tools step through a schema repair without trapping a repair that needs data.</summary>
    [Fact]
    public async Task RunAsync_ReadRepairs_KeepOrLeaveTheRead()
    {
        // STEP-01: Script a malformed read, a correct one that fails its budget check, and the corrected answer.
        var context = IntakeContext("khoảng 2 triệu", new AgentState());
        var seen = new List<(AgentTurnPhase Phase, string? Tool)>();
        var unbacked = new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "general", Reply = "Bạn đi đâu?", State = new AgentState { BudgetAmount = 2000000 } });
        var llm = Phases(seen, new AgentLlmStep([], null, Failure: new(AgentLlmFailureKind.InvalidSchema, Reason: "x")), unbacked, Asks("general", "Bạn đi đâu nè?"));

        // STEP-02: Call Create().RunAsync.
        await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert the schema repair stayed a read and the budget repair did not.
        seen.Should().Equal((AgentTurnPhase.Read, null), (AgentTurnPhase.Read, null), (AgentTurnPhase.Act, null));
    }

    // TEST-82: An empty answer gets one chance to be written, still in the read step; a second empty one falls back to the fixed line; a reply is never rewritten.
    /// <summary>In conversation 193 "giữ nguyên" with nothing waiting got "Mình chưa trả lời cụ thể được câu này".</summary>
    [Theory]
    [InlineData("Ừ, mình giữ nguyên lịch nhé.", 2, "Ừ, mình giữ nguyên lịch nhé.")]
    [InlineData("", 2, AgentReplyBuilder.AnswerUnavailable)]
    public async Task RunAsync_EmptyAnswer_IsWrittenOnce(string second, int calls, string shown)
    {
        // STEP-01: Get context over a plan and script an empty reading, then the second output.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.Request.Content = "giữ nguyên";
        var seen = new List<(AgentTurnPhase Phase, string? Tool)>();
        var llm = Phases(seen, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "", PendingAnswer = "decline" }), new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = second }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert the reply, that both calls stayed in the read step and that a declined reading changed nothing.
        result.Reply.Should().Be(shown);
        seen.Should().HaveCount(calls).And.OnlyContain(item => item.Phase == AgentTurnPhase.Read);
        result.Snapshot.Should().BeNull();
    }

    // TEST-83: Agreeing with nothing waiting never reaches the user as a claim: with a ready plan it becomes a wish to save, which only asks; with no plan it stays an answer.
    /// <summary>Conversation 194 answered "chốt đi" with "Chốt nhé!" and saved nothing.</summary>
    [Theory]
    [InlineData("ready", "request_save")]
    [InlineData(null, null)]
    public async Task RunAsync_AcceptWithNothingWaiting_BecomesASaveWish(string? status, string? action)
    {
        // STEP-01: Get a context with or without a ready proposal and script the reading.
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        var context = new AgentContext { ConversationId = 7, UserId = 3, State = snapshot.State, Request = new ANGI.Application.DTOs.Agent.SendAgentMessageRequestDto { Content = "chốt đi" },
            CurrentProposal = status is null ? null : new(9, 2, "create", status, snapshot, new AgentValidation([], [], 0, []), DateTime.UtcNow.AddHours(1)) };
        var llm = Scripted(new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Chốt nhé!", PendingAnswer = "accept" }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert the action.
        result.Action.Should().Be(action);
    }

    // TEST-84: A tool asked for outside the read step is refused with the usual question, not run.
    /// <summary>Stops the use_tool action from becoming a way round the tool limits.</summary>
    [Fact]
    public async Task RunAsync_UseToolOutsideRead_IsNotRun()
    {
        // STEP-01: Get a replanning context and script the action.
        var context = IntakeContext("ok", new AgentState());
        context.ReplanAfterConfirmation = true;
        var llm = Scripted(new AgentLlmStep([], new AgentOutput { Action = "use_tool", Reply = "", Tool = "suggest_dish" }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-03: Assert no tool ran.
        context.Exchanges.Should().BeEmpty();
        result.Snapshot.Should().BeNull();
    }

    // TEST-85: A dish already suggested in this conversation is left out, so asking again gives another one.
    /// <summary>"Bất kì" must not repeat the same dish forever.</summary>
    [Fact]
    public async Task RunAsync_RepeatedSuggestion_GivesAnotherDish()
    {
        // STEP-01: Get context from IntakeContext, add the computed value to context.History, get llm from WordingLlm and get result from Suggesting().RunAsync.
        var context = IntakeContext("gợi ý một món", new AgentState());
        context.History.Add(new("assistant", "Mình gợi ý Dish 1 ở Real restaurant, khoảng 50.000 đồng."));
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = "Thử {name1} ở {restaurant1} nhé, khoảng {price1}." }));

        var result = await Suggesting(llm.Object).RunAsync(context, default);

        // STEP-02: Assert result.Reply contains "Dish 2" and result.State.DishOffered is true.
        result.Reply.Should().Contain("Dish 2").And.NotContain("Dish 1 ");
        result.State.DishOffered.Should().BeTrue();
    }

    // TEST-86: The model reads every message that used to get a backend reply, and its own wording is shown; covers greetings, thanks, help, vague edits, a bare "ok", tastes, novelty and a missing meal, with no trip, over an empty roadmap and over a plan.
    /// <summary>The user asked for the model to hold the conversation, with the backend guarding from behind.</summary>
    [Theory]
    [InlineData("none", "alo")]
    [InlineData("none", "Xin chào")]
    [InlineData("none", "cảm ơn nha")]
    [InlineData("none", "bạn là ai")]
    [InlineData("none", "hướng dẫn tôi")]
    [InlineData("none", "tôi thích ngọt")]
    [InlineData("none", "muốn thử món lạ")]
    [InlineData("frame", "Nhưng tôi thích ăn cay")]
    [InlineData("frame", "nhẹ")]
    [InlineData("plan", "alo")]
    [InlineData("plan", "ok")]
    [InlineData("plan", "đổi cái đó đi")]
    [InlineData("plan", "cái này không ngon")]
    [InlineData("plan", "tôi thích ăn cay")]
    [InlineData("plan", "bớt cay")]
    [InlineData("plan", "muốn thử món lạ")]
    [InlineData("plan", "bữa 5 ngày 1 đổi món")]
    [InlineData("plan", "bỏ bữa sáng")]
    public async Task RunAsync_MessageWithoutFacts_IsReadAndWordedByTheModel(string trip, string content)
    {
        // STEP-01: Create state from trip and get context from IntakeContext.
        var state = trip switch { "plan" => AgentTestData.State(), "frame" => new AgentState { NumDays = 3, Title = "Khám phá ẩm thực Hà Nội" }, _ => new AgentState() };
        var context = IntakeContext(content, state, trip == "plan" ? AgentTestData.Snapshot(AgentTestData.Item()) : null);
        const string reply = "Mình nghe đây nè, bạn muốn mình giúp gì thêm không?";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "chat", Reply = reply }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is reply, result.Snapshot is null and result.State.PendingQuestion is null.
        result.Reply.Should().Be(reply);
        result.Snapshot.Should().BeNull();
        result.State.PendingQuestion.Should().BeNull();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-87: The model's question about a taste or a vague edit is shown and stays open, and it opens no planning questions over an empty roadmap; an invented figure falls back to the fixed question.
    /// <summary>"Nhẹ" must continue this exchange, not start asking for the trip's place, duration and budget.</summary>
    [Theory]
    [InlineData("taste_intensity", "Bạn thích cay nhẹ hay vừa?", true)]
    [InlineData("taste_scope", "Bạn muốn cay cho cả chuyến hay riêng ngày nào?", true)]
    [InlineData("edit_scope", "Bạn muốn đổi món nào nè?", true)]
    [InlineData("novelty", "Món lạ là khác lịch này hay món bạn chưa từng ăn?", true)]
    [InlineData("taste_intensity", "Cay mức 7 được không?", false)]
    public async Task RunAsync_ModelQuestion_IsShownWithoutOpeningPlanningQuestions(string kind, string question, bool shown)
    {
        // STEP-01: Create state as an empty roadmap, get context from IntakeContext and mock IAgentLlmService; stub llm.RunAsync.
        var context = IntakeContext("tôi thích ăn cay", new AgentState { NumDays = 3 });
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = kind, Reply = question }));

        // STEP-02: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-03: Assert result.Reply, that the question stays open and that no planning stage opened.
        if (shown) result.Reply.Should().Be(question);
        else result.Reply.Should().NotContain("mức 7");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.PlanningIntake.Should().BeNull();
    }

    // TEST-88: A suggestion with conditions costs two provider calls: the model reads the conditions, then words the reply from the tool's facts; a state it returns is ignored.
    /// <summary>The user reads the model's own sentence while the plan and trip values stay untouched.</summary>
    [Fact]
    public async Task RunAsync_ConditionedSuggestion_ShowsModelWordingAndKeepsState()
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext and create reply.
        var state = new AgentState { Province = "Da Nang" };
        var context = IntakeContext("gợi ý món cay dưới 60k", state);
        const string reply = "Mình thấy {name1} ở {restaurant1} khoảng {price1} khá vừa ý bạn đó!";
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = reply, State = new AgentState { Province = "Hue" } }));

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object, intake: true).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the filled wording, result.Action is "chat", result.State.Province is "Da Nang" and result.State.PendingPlanningRequest is null.
        result.Reply.Should().Be("Mình thấy Dish 1 ở Real restaurant khoảng 50.000 đồng khá vừa ý bạn đó!");
        result.Action.Should().Be("chat");
        result.State.Province.Should().Be("Da Nang");
        result.State.PendingPlanningRequest.Should().BeNull();
        // STEP-05: Verify RunAsync is called twice and tools are closed for the second call.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        context.ToolsClosed.Should().BeTrue();
    }

    // TEST-89: A question about what a dish is like is answered, and neither that answer nor a refused recipe contradicts itself; covers the message of the bug report.
    /// <summary>"Mì Quảng có vị như thế nào" used to get "mình chỉ giúp được về món ăn".</summary>
    [Theory]
    [InlineData("food_info", "Mì Quảng có vị như thế nào vậy bạn?", "Mì Quảng thường đậm đà, nước dùng sền sệt.", "Mì Quảng thường đậm đà")]
    [InlineData("out_of_scope", "Cách nấu Mì Quảng", "Nháp", "chưa hướng dẫn được")]
    public async Task RunAsync_QuestionAboutDish_AnswersWithoutContradiction(string action, string content, string reply, string expected)
    {
        // STEP-01: Create state as AgentState and get context from IntakeContext.
        var state = new AgentState { Province = "Hà Nội" };
        var context = IntakeContext(content, state);
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = action, Reply = reply, SuggestedDish = "Mì Quảng", State = new AgentState { Province = "Hue" } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply contains expected, result.Action is action and result.State.Province is the expected text.
        result.Reply.Should().Contain(expected).And.Contain("tìm quán Mì Quảng").And.NotContain("chỉ giúp được về món ăn");
        result.Action.Should().Be(action);
        result.State.Province.Should().Be("Hà Nội");
    }

    // TEST-90: A model state that leaves the trip values empty neither erases them nor asks to confirm "số ngày: ; ngân sách:  đồng"; this is what wiped conversation 138.
    /// <summary>Only a real change reaches the confirmation question.</summary>
    [Fact]
    public async Task RunAsync_ModelLeavesTripValuesEmpty_KeepsThemWithoutConfirmation()
    {
        // STEP-01: Create state as AgentState and get context from IntakeContext.
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, BudgetAmount = 3500000, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };
        var context = IntakeContext("thử xem sao", state);
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "answer", Reply = "Bạn muốn món gì?", State = new AgentState { Province = "Hà Nội" } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply does not contain the expected text and result.State matches the condition.
        result.Reply.Should().NotContain("số ngày:");
        result.State.Should().Match<AgentState>(x => x.PendingStateChange == null && x.NumDays == 7 && x.BudgetAmount == 3500000 && x.OriginMode == "pinned" && x.OriginLatitude == 21.03m);
    }

    // TEST-91: Removing a pin the user names still asks to confirm, so the guard against empty values does not take that choice away.
    /// <summary>Keeps the explicit way out of a pinned search.</summary>
    [Fact]
    public async Task RunAsync_UserRemovesPin_StillAsksToConfirm()
    {
        // STEP-01: Create state as AgentState and get context from IntakeContext.
        var state = new AgentState { Province = "Hà Nội", NumDays = 7, BudgetAmount = 3500000, OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m };
        var context = IntakeContext("bỏ điểm đã chấm trên bản đồ đi", state);
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "answer", Reply = "Được", State = state with { OriginMode = "none", OriginLatitude = null, OriginLongitude = null } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply contains the expected text and result.State.PendingStateChange.OriginMode is "none".
        result.Reply.Should().Contain("điểm xuất phát hoặc bán kính tìm món");
        result.State.PendingStateChange!.OriginMode.Should().Be("none");
    }

    // TEST-92: A long trip is assembled by the backend after the one search, however long: one provider call and no paid route requests; covers just past the threshold, 30 days and the maximum.
    /// <summary>A model reply cannot hold hundreds of items, so these trips would otherwise end in a truncated output.</summary>
    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(100)]
    public async Task RunAsync_LongTrip_BackendAssemblesAfterOneSearch(int days)
    {
        // STEP-01: Create state as AgentState, get context from IntakeContext, set context.IntakeComplete to true, create search as AgentRecommendationInput and 1 more.
        var state = new AgentState { Province = "Da Nang", NumDays = (short)days, BudgetAmount = 99999999, MealSlots = ["breakfast", "lunch", "dinner"] };
        var context = IntakeContext("lập lịch đi", state);
        context.IntakeComplete = true;
        var search = new AgentRecommendationInput(Scope: "full", TagIds: [], ExcludeTagIds: [], ExcludeDishIds: [], TopK: 30);
        var call = new AgentToolCall("r", "get_recommendation", System.Text.Json.JsonSerializer.SerializeToElement(search, AgentJson.Options));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([call], null));
        // STEP-03: Get catalog from Range().Select(filtered).ToArray and create fixture through AgentTestData.Validator.
        var catalog = Enumerable.Range(1, 40).Select(id => AgentTestData.Dish(id)).ToArray();
        var fixture = AgentTestData.Validator(catalog);
        // STEP-04: Stub fixture.GetCandidateIdsAsync.
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<ANGI.Application.Common.Models.Discovery.DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(catalog.Select(dish => dish.Id).ToArray());
        // STEP-05: Create policy as AgentConstraintGuard and create limits as AgentLimits.
        var policy = new AgentConstraintGuard();
        var limits = new AgentLimits();
        // STEP-06: Mock IAgentTelemetry.
        var telemetry = Mock.Of<IAgentTelemetry>();
        // STEP-07: Create tools as AgentToolExecutor and get result from new AgentOrchestrator().RunAsync.
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), policy, new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);

        var result = await new AgentOrchestrator(llm.Object, tools, policy, new(), fixture.Validator, limits, telemetry).RunAsync(context, default);

        // STEP-08: Assert result.Validation.Errors is empty, result.Snapshot.Items has days * 3 item(s) and 1 more.
        result.Validation!.Errors.Should().BeEmpty();
        result.Snapshot!.Items.Should().HaveCount(days * 3);
        result.Snapshot.Items.Select(item => (item.DayNumber, item.MealSlot)).Distinct().Should().HaveCount(days * 3);
        // STEP-09: Verify RunAsync is called once and CalculateRouteAsync is never called.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Routes.Verify(x => x.CalculateRouteAsync(It.IsAny<ANGI.Application.Common.Models.Roadmap.RouteCoordinate?>(), It.IsAny<IReadOnlyList<ANGI.Application.Common.Models.Roadmap.RouteCoordinate>>(),
            It.IsAny<ANGI.Application.Common.Models.Roadmap.RouteProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-93: The duration limit reads up to 100 days in a message and refuses just above it.
    /// <summary>"30 ngày" used to be refused by the form and "100 ngày" could not even be read.</summary>
    [Theory]
    [InlineData("lập lịch Hà Nội 30 ngày", 30)]
    [InlineData("lập lịch Hà Nội 100 ngày", 100)]
    public async Task RunAsync_DurationUpToMaximum_IsAccepted(string content, int days)
    {
        // STEP-01: Get result from Create().RunAsync.
        var llm = Scripted(Asks("trip_details", "Ngân sách của bạn khoảng bao nhiêu?", new AgentState { Province = "Hà Nội", NumDays = (short)days }));
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(IntakeContext(content, new AgentState()), default);

        // STEP-02: Assert result.State.NumDays is days and result.Reply does not contain the expected text.
        result.State.NumDays.Should().Be((short)days);
        result.Reply.Should().NotContain("Số ngày phải");
    }

    // TEST-94: A user who has not decided a budget gets no second confirmation and no plan yet: the preferences question comes next, states the missing budget, and costs one call.
    /// <summary>Budget is optional in chat, while the order of the questions before a plan stays the same.</summary>
    [Fact]
    public async Task RunAsync_UndecidedBudget_AsksPreferencesWithoutConfirmation()
    {
        // STEP-01: Get context from IntakeContext.
        var context = IntakeContext("chưa chốt ngân sách", BudgetQuestionState());
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
        {
            Action = "clarify", Clarification = "trip_details", Reply = "Không sao, bạn muốn ăn gì?",
            State = BudgetQuestionState() with { BudgetUnrestricted = true, PlanningIntake = null, PendingQuestion = null }
        }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected text, result.Snapshot is null and result.State matches the condition.
        result.Reply.Should().Be($"{AgentReplyBuilder.NoBudgetNote} {AgentIntakePolicy.PreferencesQuestion}");
        result.Snapshot.Should().BeNull();
        result.State.Should().Match<AgentState>(x => x.BudgetUnrestricted && x.BudgetAmount == null && x.PendingStateChange == null &&
            x.PlanningIntake == AgentIntakePolicy.Preferences && x.PendingQuestion == result.Reply);
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-95: A model that jumps to ranking with the budget undecided is stopped by the tool, so the preferences question still comes first and no further call is made.
    /// <summary>Guards the order of the steps against the model, not only through the prompt.</summary>
    [Fact]
    public async Task RunAsync_UndecidedBudgetThenRecommendation_StillAsksPreferencesFirst()
    {
        // STEP-01: Get context from IntakeContext, create input as AgentRecommendationInput and create call as AgentToolCall.
        var context = IntakeContext("chưa chốt ngân sách", BudgetQuestionState());
        var input = new AgentRecommendationInput(BudgetQuestionState() with { BudgetUnrestricted = true, PlanningIntake = null, PendingQuestion = null }, null, "full", null, null, [], [], [], null, null, 30, false);
        var call = new AgentToolCall("a", "get_recommendation", System.Text.Json.JsonSerializer.SerializeToElement(input, AgentJson.Options));
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([call], null));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.State.PlanningIntake is AgentIntakePolicy.Preferences, result.Reply ends with PreferencesQuestion and context.Grants is empty.
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Preferences);
        result.Reply.Should().EndWith(AgentIntakePolicy.PreferencesQuestion);
        context.Grants.Should().BeEmpty();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-96: The model's question about missing trip details is shown when backed, and replaced by the fixed one when it invents a figure; the question stays open in both.
    /// <summary>The wording may answer what the user said, while the facts stay checked.</summary>
    [Theory]
    [InlineData("Không sao đâu, bạn ước chừng một mức cho mỗi người được không?", true)]
    [InlineData("Thường khoảng 700k một người, bạn chọn mức đó nhé?", false)]
    public async Task RunAsync_TripQuestionFromModel_IsShownOnlyWhenBacked(string reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext.
        var context = IntakeContext("mình chưa biết nữa", BudgetQuestionState() with { NumDays = 3 });
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "clarify", Clarification = "trip_details", Reply = reply }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected value, result.State.PendingQuestion is result.Reply and result.State.PlanningIntake is AgentIntakePolicy.Details.
        result.Reply.Should().Be(shown ? reply : AgentReplyBuilder.TripDetailsQuestion(context.State));
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
    }

    // TEST-97: Before a plan exists the model still cannot drop a budget the user gave: the unrestricted state waits for the user's confirmation.
    /// <summary>Only an unset budget may be left open without asking, so the exception cannot be used to skip a limit.</summary>
    [Fact]
    public async Task RunAsync_UnrestrictedOverAStatedBudget_StillNeedsConfirmation()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a copy of context.State and set context.Request.Content to a test text.
        var context = AgentTestData.Context(); context.State = context.State with { BudgetAmount = 800000 };
        context.Request.Content = "Tiền nong thì bạn lo giúp mình";
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = "answer", Reply = "Dạ bạn.", State = context.State with { BudgetAmount = null, BudgetUnrestricted = true } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.State.BudgetUnrestricted is false and State.PendingStateChange.BudgetUnrestricted is true.
        result.State.BudgetUnrestricted.Should().BeFalse();
        result.State.PendingStateChange!.BudgetUnrestricted.Should().BeTrue();
    }

    // TEST-98: An off-topic or social reply during the budget question keeps the question and the whole request, so the next message continues where the user left off.
    /// <summary>Passing the reply to the model must not lose the user's place in the questions.</summary>
    [Theory]
    [InlineData("out_of_scope")][InlineData("chat")]
    public async Task RunAsync_AsideDuringBudgetQuestion_KeepsThePlace(string action)
    {
        // STEP-01: Create pending as AgentPendingPlanningRequest and get context from IntakeContext.
        var pending = new AgentPendingPlanningRequest(["Lập lịch Hà Nội 3 ngày, ăn cay vừa"]);
        var context = IntakeContext("code giúp tôi", BudgetQuestionState() with { PendingPlanningRequest = pending });
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = "Dạ bạn." }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: IntakeCatalog()).RunAsync(context, default);

        // STEP-04: Assert result.State.PlanningIntake is AgentIntakePolicy.Details, result.State.PendingPlanningRequest is pending and 1 more.
        result.State.PlanningIntake.Should().Be(AgentIntakePolicy.Details);
        result.State.PendingPlanningRequest.Should().Be(pending);
        result.Reply.Should().EndWith(BudgetQuestionState().PendingQuestion);
    }

    // TEST-99: A chat turn over code or an unbacked name shows the fixed reply and logs the replacement without the wording; shown wording and refusals log nothing.
    /// <summary>Lets reviews measure how often the checks block the model, and never stores what the model wrote.</summary>
    [Theory]
    [InlineData("chat", "print(\"hello\")", true)]
    [InlineData("chat", "Thủ đô của Pháp là Paris.", true)]
    [InlineData("chat", "Mình vẫn ở đây nè!", false)]
    [InlineData("out_of_scope", "print(\"hello\")", false)]
    public async Task RunAsync_ReplacedWording_IsLoggedWithoutTheText(string action, string reply, bool replaced)
    {
        // STEP-01: Get context from AgentTestData.Context and set context.Request.Content to a test text.
        var context = AgentTestData.Context(); context.Request.Content = "Code giúp tôi";
        // STEP-02: Mock IAgentTelemetry and IAgentLlmService; stub llm.RunAsync.
        var telemetry = new Mock<IAgentTelemetry>();
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = reply }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, telemetry: telemetry.Object).RunAsync(context, default);

        // STEP-04: Assert depending on action == "chat", result.Reply is the expected value and result.Reply does not contain "print".
        if (action == "chat") result.Reply.Should().Be(replaced ? AgentReplyGuard.ChatFallback : reply);
        else result.Reply.Should().NotContain("print");
        // STEP-05: Verify Record is never called.
        telemetry.Verify(x => x.Record("reply_replaced", context.ConversationId, context.TurnId, context.AttemptId,
            It.Is<object>(data => !System.Text.Json.JsonSerializer.Serialize(data, AgentJson.Options).Contains(reply))), replaced ? Times.Once : Times.Never);
        // STEP-06: Verify the log names the rule the wording broke.
        if (replaced) telemetry.Verify(x => x.Record("reply_replaced", context.ConversationId, context.TurnId, context.AttemptId,
            It.Is<object>(data => System.Text.Json.JsonSerializer.Serialize(data, AgentJson.Options).Contains(reply.StartsWith("print") ? "\"reason\":\"code\"" : "\"reason\":\"name\""))), Times.Once);
    }

    // TEST-100: Consecutive refusals in one conversation use different fixed wordings and show no model text.
    /// <summary>Keeps repeated refusals from reading like a machine while nothing the model wrote reaches the user.</summary>
    [Fact]
    public async Task RunAsync_ConsecutiveRefusals_VaryWithoutModelText()
    {
        // STEP-01: Create replies as List<string> and 1 more.
        var replies = new List<string>();
        foreach (var turn in new long[] { 1, 3, 5 })
        {
            var context = AgentTestData.Context(); context.TurnId = turn; context.Request.Content = "Giải phương trình";
            var llm = new Mock<IAgentLlmService>();
            llm.Setup(x => x.RunAsync(context, false, It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = "out_of_scope", Reply = "Đáp án là bốn" }));
            replies.Add((await Create(llm.Object).RunAsync(context, default)).Reply);
        }

        // STEP-02: Assert replies has no duplicates.
        replies.Should().OnlyHaveUniqueItems().And.BeSubsetOf(AgentReplyGuard.OutOfScopeReplies);
    }

    // TEST-101: Wording that fails its check shows the fixed reply and logs the replacement; covers a written price, a missing restaurant, an extra dish in lower case and an unranked taste claim.
    /// <summary>The user always gets a true sentence, and a failed check costs no extra call.</summary>
    [Theory]
    [InlineData("Mình gợi ý {name1} ở {restaurant1}, {price1}, khoảng 45.000 đồng nhé.")]
    [InlineData("Mình gợi ý {name1} ở quán khác, khoảng {price1} nhé.")]
    [InlineData("Mình gợi ý {name1} ở {restaurant1}, khoảng {price1}, hoặc phở cũng ngon nhé.")]
    [InlineData("Theo khẩu vị của bạn, mình gợi ý {name1} ở {restaurant1}, khoảng {price1}.")]
    public async Task RunAsync_SuggestionWordingFailsCheck_ShowsFixedReply(string reply)
    {
        // STEP-01: Get context from IntakeContext and mock IAgentTelemetry.
        var context = IntakeContext("gợi ý món theo sở thích của tôi", new AgentState());
        var telemetry = new Mock<IAgentTelemetry>();
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = reply }));

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object, telemetry: telemetry.Object, dishNames: Enumerable.Repeat("Phở Bò", 5).ToArray()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the fixed reply.
        result.Reply.Should().Be(context.DishSuggestion!.FixedReply).And.StartWith("Mình gợi ý Dish 1 ở Real restaurant, khoảng 50.000 đồng.");
        // STEP-05: Verify RunAsync is called twice and Record logs reply_replaced once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        telemetry.Verify(x => x.Record("reply_replaced", context.ConversationId, context.TurnId, context.AttemptId, It.IsAny<object>()), Times.Once);
    }

    // TEST-102: When the wording call yields nothing usable the dish is still given, with no retry; covers invalid output, a provider failure that normally retries and a further tool request.
    /// <summary>The dish is already chosen, so a failed wording call must not become an error or spend more calls.</summary>
    [Theory]
    [InlineData("invalid")]
    [InlineData("provider")]
    [InlineData("tool")]
    public async Task RunAsync_SuggestionWordingCallUnusable_ShowsFixedReplyWithoutRetry(string kind)
    {
        // STEP-01: Get context from IntakeContext and create second from kind.
        var context = IntakeContext("gợi ý món cay dưới 60k", new AgentState());
        var second = kind switch
        {
            "invalid" => AgentLlmStep.Failed(AgentLlmFailureKind.InvalidSchema),
            "provider" => AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 503),
            _ => new AgentLlmStep([SuggestCall()], null)
        };
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, second);

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply starts with the fixed reply.
        result.Reply.Should().StartWith("Mình gợi ý Dish 1 ở Real restaurant");
        // STEP-05: Verify RunAsync is called twice.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-103: A turn timeout during the wording call still gives the dish; the caller's own cancellation is rethrown instead.
    /// <summary>Only the backend's deadline falls back; a client that left must not get a reply stored for it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_SuggestionWordingCancelled_FallsBackOnlyOnTimeout(bool callerCancelled)
    {
        // STEP-01: Get context from IntakeContext and create source as CancellationTokenSource.
        var context = IntakeContext("gợi ý món cay dưới 60k", new AgentState());
        using var source = new CancellationTokenSource();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync to return the tool call, then cancel when asked and throw.
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([SuggestCall()], null))
            .Returns(() => { if (callerCancelled) source.Cancel(); throw new OperationCanceledException(); });

        // STEP-03: Wrap Suggesting().RunAsync in act.
        var act = () => Suggesting(llm.Object).RunAsync(context, source.Token);

        // STEP-04: Assert act throws OperationCanceledException, or returns the fixed reply on a timeout.
        if (callerCancelled) await act.Should().ThrowAsync<OperationCanceledException>();
        else (await act()).Reply.Should().StartWith("Mình gợi ý Dish 1 ở Real restaurant");
    }

    // TEST-104: With no provider call left for the wording, the fixed reply is shown at once.
    /// <summary>Keeps the call limit binding without losing the dish already chosen.</summary>
    [Fact]
    public async Task RunAsync_NoCallLeftForWording_ShowsFixedReply()
    {
        // STEP-01: Get context from IntakeContext.
        var context = IntakeContext("gợi ý món cay dưới 60k", new AgentState());
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([SuggestCall()], null));

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object, limits: new AgentLimits { MaxLlmCalls = 1 }).RunAsync(context, default);

        // STEP-04: Assert result.Reply starts with the fixed reply.
        result.Reply.Should().StartWith("Mình gợi ý Dish 1 ở Real restaurant");
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-105: With no dish found the model words it, unless it names a dish nothing backs or the call fails; covers a plain sentence, an invented dish and a failure.
    /// <summary>The one case with no facts is where a model could offer a dish ANGI does not have.</summary>
    [Theory]
    [InlineData("Trên ANGI chưa có món nào hợp, bạn thử bỏ bớt điều kiện nhé.", true)]
    [InlineData("Trên ANGI chưa có, bạn thử phở xem sao.", false)]
    [InlineData(null, false)]
    public async Task RunAsync_NothingFound_ShowsCheckedWordingOrFixedReply(string? reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext and create second.
        var context = IntakeContext("gợi ý món cay dưới 60k", new AgentState());
        var second = reply is null ? AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 500) : new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = reply });
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, second);

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object, empty: true, dishNames: Enumerable.Repeat("Phở Bò", 5).ToArray()).RunAsync(context, default);

        // STEP-04: Assert result.Reply is the expected value.
        result.Reply.Should().Be(shown ? reply : AgentDishSuggestionPolicy.NothingFound);
    }

    // TEST-106: A question about the user's own favourites is worded by the model; in an answer, a guess at a dish in lower case is replaced because an answer states facts of the plan.
    /// <summary>A social reply may mention food, so the prompt carries the rule there; an answer with no plan behind it still cannot name one.</summary>
    [Theory]
    [InlineData("chat", "Mình chưa biết món bạn thích nhất, để mình gợi ý theo khẩu vị của bạn nhé?", true)]
    [InlineData("answer", "Món sở trường của bạn là phở đó!", false)]
    public async Task RunAsync_OwnFavourite_AnswerNeverNamesAnInventedDish(string action, string reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext and mock IAgentCatalogRepository.
        var context = IntakeContext("món ăn sở trường của tôi là gì?", new AgentState());
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.GetProvincesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["Hà Nội"]);
        catalog.Setup(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Enumerable.Repeat("Phở Bò", 5).ToArray());
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput { Action = action, Reply = reply }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, intakeCatalog: catalog.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply contains or lacks the wording.
        if (shown) result.Reply.Should().Be(reply);
        else result.Reply.Should().NotContain("phở");
        // STEP-05: Verify GetDishNamesAsync is called only where the reply is checked against dish words.
        catalog.Verify(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>()), action == "answer" ? Times.Once() : Times.Never());
    }

    // TEST-107: Over a plan even a request with no conditions goes to the model, leaves out the plan's dish, invites adding the suggestion, marks the offer and changes nothing else.
    /// <summary>"Vừa lập xong, gợi ý 1 món bất kì" used to repeat a dish of the plan and offer no way to add it.</summary>
    [Fact]
    public async Task RunAsync_PlainSuggestionOverPlan_InvitesAddingAnotherDish()
    {
        // STEP-01: Get context from a plan holding Dish 1 and create reply.
        var context = IntakeContext("gợi ý cho tôi 1 món bất kì", AgentTestData.State() with { NumDays = 2 }, AgentTestData.Snapshot(AgentTestData.Item() with { DishId = 1, DishName = "Dish 1" }));
        const string reply = "Bạn thử {name1} ở {restaurant1} nhé, khoảng {price1}. Muốn cho vào lịch thì nhắn mình ngày và bữa, ví dụ bữa tối ngày 2 nha.";
        // STEP-02: Get llm from WordingLlm.
        var llm = WordingLlm(context, new AgentLlmStep([], new AgentOutput { Action = "answer", Reply = reply }));

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply names Dish 2 with the invitation, result.Snapshot is null and result.State.DishOffered is true.
        result.Reply.Should().Be("Bạn thử Dish 2 ở Real restaurant nhé, khoảng 50.000 đồng. Muốn cho vào lịch thì nhắn mình ngày và bữa, ví dụ bữa tối ngày 2 nha.");
        result.Snapshot.Should().BeNull();
        result.State.DishOffered.Should().BeTrue();
        // STEP-05: Verify RunAsync is called twice.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-108: When the wording call fails over a plan, the fixed reply still invites adding the dish; with no plan it does not.
    /// <summary>The way into the plan must not depend on the model answering, and there is no plan to add to without one.</summary>
    [Theory]
    [InlineData(true, "Muốn cho vào lịch thì nhắn mình ngày và bữa nhé.")]
    [InlineData(false, "Muốn món khác cứ nhắn mình nhé.")]
    public async Task RunAsync_SuggestionWordingFails_FixedReplyFitsThePlan(bool hasPlan, string ending)
    {
        // STEP-01: Get context from IntakeContext, with a plan when hasPlan.
        var context = IntakeContext("gợi ý món cay dưới 60k", AgentTestData.State(), hasPlan ? AgentTestData.Snapshot(AgentTestData.Item() with { DishId = 9, DishName = "Dish 9" }) : null);
        // STEP-02: Get llm from WordingLlm with a failing second call.
        var llm = WordingLlm(context, AgentLlmStep.Failed(AgentLlmFailureKind.ProviderUnavailable, 500));

        // STEP-03: Get result from Suggesting().RunAsync.
        var result = await Suggesting(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.Reply ends with ending.
        result.Reply.Should().EndWith(ending);
    }

    // TEST-109: A plan proposed with a taste level the user never stated gets one repair, then the question goes to the user with nothing changed; a model that then asks is shown.
    /// <summary>"Tôi thích ăn cay" must not become spicy=0.75 in a plan, which is the invention conversation 10 suffered from.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ProposalWithUnstatedTasteLevel_NeverReachesThePlan(bool modelAsksAfterRepair)
    {
        // STEP-01: Get context from a plan and set the message.
        var context = IntakeContext("tôi thích ăn cay", AgentTestData.State(), AgentTestData.Snapshot(AgentTestData.Item()));
        // STEP-02: Create guess as the model's output and mock IAgentLlmService; stub llm.RunAsync.
        var guess = AgentTestData.Answer() with { State = context.State with { TasteTargets = [new("spicy", .75)] } };
        var question = new AgentOutput { Action = "clarify", Clarification = "taste_intensity", Reply = "Bạn thích cay nhẹ hay vừa?" };
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentLlmStep([], guess)).ReturnsAsync(new AgentLlmStep([], modelAsksAfterRepair ? question : guess));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert the reply is the model's question or the fixed one, the question stays open and no target or preview exists.
        if (modelAsksAfterRepair) result.Reply.Should().Be("Bạn thích cay nhẹ hay vừa?");
        else result.Reply.Should().StartWith("Bạn muốn mức cay");
        result.State.PendingQuestion.Should().Be(result.Reply);
        result.State.TasteTargets.Should().BeEmpty();
        result.Snapshot.Should().BeNull();
        // STEP-05: Verify RunAsync is called twice.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // TEST-110: A question from the model drops a guessed level without a repair, and a level the user stated is stored without its quote.
    /// <summary>A question costs no second call, and the quote must never reach stored state or a comparison of constraints.</summary>
    [Theory]
    [InlineData("clarify", null, false)]
    [InlineData("answer", "cay nhẹ", true)]
    public async Task RunAsync_ModelStateWithTaste_KeepsOnlyStatedLevelsWithoutQuote(string action, string? quote, bool kept)
    {
        // STEP-01: Get context from IntakeContext with no plan to refresh and set the message.
        var context = IntakeContext("tôi thích cay nhẹ", AgentTestData.State());
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([], new AgentOutput
            { Action = action, Clarification = action == "clarify" ? "taste_scope" : null, Reply = "Bạn muốn áp dụng cho cả chuyến nhé?", State = context.State with { TasteTargets = [new("spicy", .25, quote)] } }));

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object).RunAsync(context, default);

        // STEP-04: Assert result.State.TasteTargets holds the stated target with no quote, or nothing.
        if (kept) result.State.TasteTargets.Should().Equal(new AgentTasteTarget("spicy", .25));
        else result.State.TasteTargets.Should().BeEmpty();
        // STEP-05: Verify RunAsync is called once.
        llm.Verify(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-111: A preview shows the model's sentence filled with the final plan's total; a sentence that fails its check falls back to the fixed summary and logs the replacement.
    /// <summary>Makes previews read naturally at no extra provider call, since the slots are filled after the backend's checks.</summary>
    [Theory]
    [InlineData("Lịch xong rồi nè, tổng khoảng {total}, bạn xem rồi chốt nhé!", true)]
    [InlineData("Lịch xong rồi nè, tổng khoảng 999.000 đồng, {total} nhé!", false)]
    public async Task RunAsync_ProposalWording_IsFilledAfterChecks(string reply, bool shown)
    {
        // STEP-01: Get context from IntakeContext with a one-meal trip and a grant for its dish, and mock IAgentTelemetry.
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch"], BudgetAmount = 500000 };
        var context = IntakeContext("lập lịch đi", state);
        var telemetry = new Mock<IAgentTelemetry>();
        // STEP-02: Mock IAgentLlmService; stub llm.RunAsync to issue a grant and propose the dish.
        var llm = new Mock<IAgentLlmService>();
        llm.Setup(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            context.Grants.Clear();
            context.Grants.Add(new(1, "lunch", [1], Guid.NewGuid(), context.State));
            return Task.FromResult<AgentLlmStep?>(new AgentLlmStep([], new AgentOutput { Action = "create_proposal", Reply = reply, State = context.State, Scope = new(), Items = [AgentTestData.Item(1, "a")] }))!;
        });

        // STEP-03: Get result from Create().RunAsync.
        var result = await Create(llm.Object, telemetry: telemetry.Object, catalog: [AgentTestData.Dish(1)]).RunAsync(context, default);

        // STEP-04: Assert result.Snapshot exists and result.Reply is the filled sentence or the fixed summary.
        result.Snapshot.Should().NotBeNull();
        if (shown) result.Reply.Should().StartWith("Lịch xong rồi nè, tổng khoảng").And.Contain("đồng/người, bạn xem rồi chốt nhé!").And.NotContain("{total}");
        else result.Reply.Should().StartWith("Xong rồi!").And.Contain("Mình đã lên lịch").And.NotContain("999.000");
        // STEP-05: Verify Record logs reply_replaced only when the wording failed.
        telemetry.Verify(x => x.Record("reply_replaced", context.ConversationId, context.TurnId, context.AttemptId, It.IsAny<object>()), shown ? Times.Never : Times.Once);
    }

    /// <summary>Returns an orchestrator over a catalog of dishes and no ranking, for suggestion turns.</summary>
    private static AgentOrchestrator Suggesting(IAgentLlmService llm, bool intake = false, AgentLimits? limits = null, IAgentTelemetry? telemetry = null, string[]? dishNames = null, bool empty = false)
    {
        var catalog = Enumerable.Range(1, 3).Select(id => AgentTestData.Dish(id)).ToArray();
        var fixture = AgentTestData.Validator(catalog);
        fixture.Dishes.Setup(x => x.GetCandidateIdsAsync(It.IsAny<ANGI.Application.Common.Models.Discovery.DishCandidateFilter>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(empty ? [] : catalog.Select(dish => dish.Id).ToArray());
        var policy = new AgentConstraintGuard();
        limits ??= new AgentLimits();
        telemetry ??= Mock.Of<IAgentTelemetry>();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), policy, new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry);
        var names = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        names.Setup(x => x.GetDishNamesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(dishNames ?? []);
        return new(llm, tools, policy, new(), fixture.Validator, limits, telemetry, intake ? IntakeCatalog() : dishNames is null ? null : names.Object);
    }

    /// <summary>Builds the suggest_dish call a model makes for a condition it read.</summary>
    private static AgentToolCall SuggestCall() =>
        new("s", "suggest_dish", System.Text.Json.JsonSerializer.SerializeToElement(new AgentSuggestionInput(TagIds: [], ExcludeTagIds: [], MaxPrice: 60000, TasteTargets: []), AgentJson.Options));

    /// <summary>Returns a model that calls suggest_dish, then answers the wording call with the given step.</summary>
    private static Mock<IAgentLlmService> WordingLlm(AgentContext context, AgentLlmStep second)
    {
        var llm = new Mock<IAgentLlmService>();
        llm.SetupSequence(x => x.RunAsync(context, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AgentLlmStep([SuggestCall()], null)).ReturnsAsync(second);
        return llm;
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

    /// <summary>Returns the state right after the backend asked for the budget of a known trip with no plan yet.</summary>
    private static AgentState BudgetQuestionState() => new() { Province = "Hà Nội", NumDays = 3, PlanningIntake = AgentIntakePolicy.Details,
        PendingQuestion = "Bạn cho mình biết ngân sách cho một người (tổng chuyến hoặc mỗi ngày) để lập lịch nhé?" };

    /// <summary>Returns a catalog covering only Hà Nội, like the current local data, with a fixed nearest-province answer.</summary>
    private static ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository IntakeCatalog(string? nearest = "Hà Nội")
    {
        var catalog = new Mock<ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository>();
        catalog.Setup(x => x.GetProvincesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["Hà Nội"]);
        catalog.Setup(x => x.NearestProvinceAsync(It.IsAny<decimal>(), It.IsAny<decimal>(), AgentIntakePolicy.MaxNearbyKm, It.IsAny<CancellationToken>())).ReturnsAsync(nearest);
        return catalog.Object;
    }

    /// <summary>Returns a model that gives these steps in order, the way the provider would over the turns or calls of a test.</summary>
    private static Mock<IAgentLlmService> Scripted(params AgentLlmStep[] steps)
    {
        var llm = new Mock<IAgentLlmService>();
        var sequence = llm.SetupSequence(x => x.RunAsync(It.IsAny<AgentContext>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()));
        foreach (var step in steps) sequence = sequence.ReturnsAsync(step);
        return llm;
    }

    /// <summary>Builds the model's question of one kind, carrying the state it read.</summary>
    private static AgentLlmStep Asks(string clarification, string reply, AgentState? state = null) =>
        new([], new AgentOutput { Action = "clarify", Clarification = clarification, Reply = reply, State = state });

    /// <summary>Builds the locate_places call a model makes for an itinerary or an area it read.</summary>
    private static AgentLlmStep Locates(string? tripArea, params AgentLocateStop[] stops) =>
        new([new AgentToolCall("l", "locate_places", System.Text.Json.JsonSerializer.SerializeToElement(new AgentLocateInput(tripArea, stops), AgentJson.Options))], null);

    private static AgentOrchestrator Create(IAgentLlmService llm, AgentLimits? configuredLimits = null, IAgentTelemetry? telemetry = null,
        ANGI.Domain.Entities.Dish[]? catalog = null, ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? catalogSearch = null,
        ANGI.Application.Common.Interfaces.Repositories.Agent.IAgentCatalogRepository? intakeCatalog = null, AgentPlaceResolver? places = null)
    {
        var fixture = AgentTestData.Validator(catalog ?? [AgentTestData.Dish()]);
        telemetry ??= Mock.Of<IAgentTelemetry>(); var limits = configuredLimits ?? new AgentLimits(); var state = new AgentConstraintGuard();
        var tools = new AgentToolExecutor(fixture.Dishes.Object, fixture.Tags.Object, Mock.Of<IRecommendationService>(), state,
            new AgentRouteEstimator(fixture.Routes.Object), limits, telemetry, catalogSearch: catalogSearch ?? intakeCatalog, places: places);
        return new(llm, tools, state, new(), fixture.Validator, limits, telemetry, intakeCatalog, places);
    }
}
