using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent.Proposals;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent.Proposals;

/// <summary>Verifies deterministic source identity, delta operations and scope preservation.</summary>
public sealed class AgentProposalBuilderTests
{
    // TEST-01: A complete selection over a source roadmap merges into its scope, keeps items outside the scope and retains stable item keys.
    /// <summary>Ensures a scoped rebuild changes only its scope and keeps item identities stable for later edits.</summary>
    [Fact]
    public void Build_CompleteSelectionOverSource_MergesScopeAndRetainsStableKeys()
    {
        // STEP-01: Derive outside from a copy of AgentTestData.Item(), derive changed from a copy of AgentTestData.Item(), get side from AgentTestData.Item and 3 more.
        var outside = AgentTestData.Item() with { SourceItemId = 10 };
        var changed = AgentTestData.Item(2, "existing-lunch", 2) with { SourceItemId = 20 };
        var side = AgentTestData.Item(3, "outside-dinner", 2, "dinner");
        var context = AgentTestData.Context(AgentTestData.Snapshot(outside, changed, side));
        context.ProposalVersion = 3;

        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Scope = new(2, "lunch"), Items = [AgentTestData.Item(4, "model-new-key", 2)] });

        // STEP-02: Assert result.Items has 3 item(s), result.Items.Single(filtered) is outside, result.Items.Single(filtered) is side and 5 more.
        result.Items.Should().HaveCount(3);
        result.Items.Single(item => item.ItemKey == outside.ItemKey).Should().Be(outside);
        result.Items.Single(item => item.ItemKey == side.ItemKey).Should().Be(side);
        result.Items.Single(item => item.ItemKey == changed.ItemKey).DishId.Should().Be(4);
        result.Items.Single(item => item.ItemKey == changed.ItemKey).SourceItemId.Should().BeNull();
        result.Changes.Should().ContainSingle().Which.Type.Should().Be("replace");
        result.RetainedItems.Should().BeEquivalentTo(new[] { outside, side });
    }

    // TEST-02: A complete selection built on a stale base proposal version is rejected.
    /// <summary>Prevents a proposal built on an outdated version from overwriting newer changes.</summary>
    [Fact]
    public void Build_CompleteSelectionWithStaleBaseVersion_Rejects()
    {
        // STEP-01: Get context from AgentTestData.Context and set context.ProposalVersion to 3.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ProposalVersion = 3;
        // STEP-02: Wrap new AgentProposalBuilder().Build with context and AgentOutput { Action, Reply, BaseProposalVersion, ... } in act so its outcome can be asserted.
        var act = () => new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            BaseProposalVersion = 2, Items = [AgentTestData.Item(2)] });

        // STEP-03: Assert act throws ConflictException and the thrown exception.ErrorCode is "PROPOSAL_VERSION_CONFLICT".
        act.Should().Throw<ConflictException>().Which.ErrorCode.Should().Be("PROPOSAL_VERSION_CONFLICT");
    }

    // TEST-03: A selected-item edit preserves the other rows and rejects a replacement wider than the selected item.
    /// <summary>Ensures editing one item cannot silently rewrite rows the user did not select.</summary>
    [Fact]
    public void Build_SelectedItemSnapshot_PreservesOtherRowsAndRejectsWiderReplacement()
    {
        // STEP-01: Get selected from AgentTestData.Item, derive side from a copy of AgentTestData.Item(), get context from AgentTestData.Context and 3 more.
        var selected = AgentTestData.Item();
        var side = AgentTestData.Item(2, "side") with { SortOrder = 1 };
        var context = AgentTestData.Context(AgentTestData.Snapshot(selected, side));
        context.Request.SelectedItemKey = selected.ItemKey;
        var output = new AgentOutput { Action = "create_proposal", Reply = "Selection", Items = [AgentTestData.Item(3)] };

        var result = new AgentProposalBuilder().Build(context, output);

        // STEP-02: Assert result.Items.Single(filtered) is side, result.Items.Single(filtered).DishId is 3 and 1 more.
        result.Items.Single(item => item.ItemKey == "side").Should().Be(side);
        result.Items.Single(item => item.ItemKey == selected.ItemKey).DishId.Should().Be(3);
        ((Action)(() => new AgentProposalBuilder().Build(context, output with { Items = [AgentTestData.Item(3), side] })))
            .Should().Throw<ConflictException>();
    }

    // TEST-04: Replace day-two lunch while retaining all other days and meals byte-for-byte.
    /// <summary>Verifies the main scoped-edit acceptance case and clears inherited feedback identity on replacement.</summary>
    [Fact]
    public void Build_ScopedReplacement_PreservesEverythingElse()
    {
        // STEP-01: Derive a from a copy of AgentTestData.Item(), derive b from a copy of AgentTestData.Item(), get source from AgentTestData.Snapshot and 3 more.
        var a = AgentTestData.Item() with { SourceItemId = 100 };
        var b = AgentTestData.Item(2, "b", 2) with { SourceItemId = 200 };
        var source = AgentTestData.Snapshot(a, b, AgentTestData.Item(3, "c", 2, "dinner"));
        var context = AgentTestData.Context(source); context.ProposalVersion = 3;
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Updated",
            BaseProposalVersion = 3, Scope = new(2, "lunch"), Changes = [new("replace", "b", b with { DishId = 4, SourceItemId = 999 })] });
        // STEP-02: Assert result.Items.Single(filtered) is a, result.Items.Single(filtered) is source.Items[2] and 3 more.
        result.Items.Single(x => x.ItemKey == "a").Should().Be(a);
        result.Items.Single(x => x.ItemKey == "c").Should().Be(source.Items[2]);
        result.Items.Single(x => x.ItemKey == "b").SourceItemId.Should().BeNull();
        result.Items.Select(x => x.ItemKey).Should().Equal("a", "b", "c");
        source.Items.Single(x => x.ItemKey == "b").DishId.Should().Be(2);
    }

    // TEST-05: Preserve feedback identity while moving an unchanged dish inside the permitted full scope.
    /// <summary>Verifies that moving an item never creates or transfers feedback to a different dish.</summary>
    [Fact]
    public void Build_Move_PreservesSourceIdentity()
    {
        // STEP-01: Derive item from a copy of AgentTestData.Item(), get context from AgentTestData.Context and create result through new AgentProposalBuilder().Build.
        var item = AgentTestData.Item() with { SourceItemId = 55 };
        var context = AgentTestData.Context(AgentTestData.Snapshot(item));
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Moved",
            Changes = [new("move", "a", item with { DayNumber = 2 })] });
        // STEP-02: Assert result.Items.Single().SourceItemId is 55 and result.Items.Single().DayNumber is 2.
        result.Items.Single().SourceItemId.Should().Be(55);
        result.Items.Single().DayNumber.Should().Be(2);
    }

    // TEST-06: Reject invalid delta types, ambiguous keys, scope escape, duplicate keys and stale proposal versions.
    /// <summary>Exercises rejection boundaries before a snapshot can be marked ready.</summary>
    [Theory]
    [InlineData("unknown")][InlineData("missing")][InlineData("outside")][InlineData("move-dish")][InlineData("duplicate")]
    [InlineData("stale")][InlineData("bad-scope")][InlineData("bad-meal")][InlineData("focus")][InlineData("wrong-selection")][InlineData("clear-ui-focus")]
    public void Build_InvalidDelta_Rejects(string area)
    {
        // STEP-01: Get item from AgentTestData.Item, get context from AgentTestData.Context, set context.ProposalVersion to 2, create output as AgentOutput and 1 more.
        var item = AgentTestData.Item();
        var context = AgentTestData.Context(AgentTestData.Snapshot(item));
        context.ProposalVersion = 2;
        var output = new AgentOutput { Action = "patch_proposal", Reply = "x", BaseProposalVersion = 2,
            Changes = [new("replace", "a", item with { DishId = 2 })] };
        switch (area)
        {
            case "unknown": output = output with { Changes = [new("delete_roadmap", "a", null)] }; break;
            case "missing": output = output with { Changes = [new("remove", "unknown", null)] }; break;
            case "outside": output = output with { Scope = new(2, "lunch") }; break;
            case "move-dish": output = output with { Changes = [new("move", "a", item with { DishId = 2 })] }; break;
            case "duplicate": output = output with { Changes = [new("add", null, item)] }; break;
            case "stale": output = output with { BaseProposalVersion = 1 }; break;
            case "bad-scope": output = output with { Scope = new(3) }; break;
            case "bad-meal": output = output with { Scope = new(null, "lunch") }; break;
            case "focus": context.State = context.State with { FocusDay = 2 }; break;
            case "wrong-selection": context.Request.SelectedItemKey = "foreign"; break;
            case "clear-ui-focus": context.Request.ConfirmedState = context.State with { FocusDay = 2, FocusMeal = "lunch" }; break;
        }
        // STEP-02: Wrap new AgentProposalBuilder().Build with context and output in act so its outcome can be asserted.
        var act = () => new AgentProposalBuilder().Build(context, output);
        // STEP-03: Assert act throws ConflictException.
        act.Should().Throw<ConflictException>();
    }

    // TEST-07: Explicit alternatives exclude the previous scoped dishes and preserve items outside the scope.
    /// <summary>Rejects an alternative that merely repeats the previous proposal's dishes.</summary>
    [Fact]
    public void Build_AlternativeCannotRepeatPreviousDish()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.Request.Command to "alternative" and create output as AgentOutput.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Command = "alternative";
        var output = new AgentOutput { Action = "create_proposal", Reply = "Alternative", Items = [AgentTestData.Item()] };
        // STEP-02: Wrap new AgentProposalBuilder().Build with context and output in act so its outcome can be asserted.
        var act = () => new AgentProposalBuilder().Build(context, output);
        // STEP-03: Assert act throws ConflictException.
        act.Should().Throw<ConflictException>();
        // STEP-04: Set output to a copy of output.
        output = output with { Items = [AgentTestData.Item(2)] };
        // STEP-05: Assert Build().Items.Single().DishId is 2.
        new AgentProposalBuilder().Build(context, output).Items.Single().DishId.Should().Be(2);
    }

    // TEST-08: Move an unsaved item across days without reranking and retain backend source proof for later create confirmation.
    /// <summary>Verifies changing position does not lose authorization when the original candidate grant was scoped to another day.</summary>
    [Fact]
    public async Task Build_MoveUnsavedItem_PreservesConfirmationProvenance()
    {
        // STEP-01: Get grant from AgentTestData.Grant, derive item from a copy of AgentTestData.Item(), create source as AgentSnapshot and 2 more.
        var grant = AgentTestData.Grant([1], 1, "lunch");
        var item = AgentTestData.Item() with { RequestId = grant.RequestId, Price = 50000 };
        var source = new AgentSnapshot(AgentTestData.State(), [item], [grant]);
        var context = AgentTestData.Context(source);
        var snapshot = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Moved",
            Changes = [new("move", item.ItemKey, item with { DayNumber = 2 })] });
        // STEP-02: Assert snapshot.RetainedItems has exactly one matching item and snapshot.RetainedItems.DishId is 1.
        snapshot.RetainedItems.Should().ContainSingle().Which.DishId.Should().Be(1);
        // STEP-03: Derive validator from AgentTestData.Validator().Validator.
        var validator = AgentTestData.Validator(AgentTestData.Dish()).Validator;
        // STEP-04: Validate snapshot and keep ready.
        var ready = await validator.ValidateAsync(snapshot, source, false, true, CancellationToken.None, []);
        // STEP-05: Read serialized from the response; assert ready.Validation.Errors is empty.
        ready.Validation.Errors.Should().BeEmpty();
        var serialized = AgentJson.Read<AgentSnapshot>(AgentJson.Write(ready.Snapshot));
        // STEP-06: Validate serialized and keep confirm.
        var confirm = await validator.ValidateAsync(serialized, null, true, false, CancellationToken.None);
        // STEP-07: Assert confirm.Validation.Errors is empty and confirm.Snapshot.Items.Single().RequestId is grant.RequestId.
        confirm.Validation.Errors.Should().BeEmpty();
        confirm.Snapshot.Items.Single().RequestId.Should().Be(grant.RequestId);
    }

    // TEST-09: Discard model-supplied price, names, request IDs and source identity on new items.
    /// <summary>Verifies catalog facts and provenance cannot be supplied by the model.</summary>
    [Fact]
    public void Build_NewItem_DropsUntrustedMetadata()
    {
        // STEP-01: Create output as AgentOutput and get result from Build().Items.Single.
        var output = new AgentOutput { Action = "create_proposal", Reply = "Preview", Items = [AgentTestData.Item() with
            { Price = 1, DishName = "Fake", RestaurantName = "Fake", SourceItemId = 900, RequestId = Guid.NewGuid() }] };
        var result = new AgentProposalBuilder().Build(AgentTestData.Context(), output).Items.Single();
        // STEP-02: Assert result.Price is 0, result.DishName is null, result.SourceItemId is null and result.RequestId is null.
        result.Price.Should().Be(0); result.DishName.Should().BeNull(); result.SourceItemId.Should().BeNull(); result.RequestId.Should().BeNull();
    }

    // TEST-10: A confirmed replan returns a complete replacement ordered chronologically.
    /// <summary>Ensures a confirmed replan replaces the whole plan in chronological order.</summary>
    [Fact]
    public void Build_ConfirmedReplan_ReturnsCompleteChronologicalReplacement()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.ReplanAfterConfirmation to true and create result through new AgentProposalBuilder().Build.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ReplanAfterConfirmation = true;
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Updated",
            Items = [AgentTestData.Item(3, "dinner", 2, "dinner"), AgentTestData.Item(2, "breakfast", 1, "breakfast")] });
        // STEP-02: Assert result.Items.Select(filtered) equals "breakfast" and result.Items holds no matching item.
        result.Items.Select(item => item.ItemKey).Should().Equal("breakfast", "dinner");
        result.Items.Should().NotContain(item => item.ItemKey == "a");
    }

    // TEST-11: The checked delta target owns the row key even when the model invents a replacement suffix.
    /// <summary>Retains the preview identity while preserving move feedback and clearing replacement feedback identity.</summary>
    [Theory]
    [InlineData("replace")]
    [InlineData("move")]
    public void Build_ModelRenamesReplacement_UsesCheckedTargetKey(string action)
    {
        // STEP-01: Derive item from a copy of AgentTestData.Item(), get other from AgentTestData.Item, get context from AgentTestData.Context and 2 more.
        var item = AgentTestData.Item() with { SourceItemId = 55 };
        var other = AgentTestData.Item(3, "other", 2, "dinner");
        var context = AgentTestData.Context(AgentTestData.Snapshot(item, other));
        var changed = item with { ItemKey = "a_alt", SourceItemId = 999,
            DishId = action == "replace" ? 2 : item.DishId, DayNumber = action == "move" ? (short)2 : item.DayNumber };
        var snapshot = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Edit",
            Changes = [new(action, item.ItemKey, changed)] });
        // STEP-02: Assert snapshot.Items holds no matching item, snapshot.Items.Single(filtered).SourceItemId is action == "move" ? 55 : null and 1 more.
        snapshot.Items.Should().NotContain(candidate => candidate.ItemKey == "a_alt");
        snapshot.Items.Single(candidate => candidate.ItemKey == item.ItemKey).SourceItemId.Should().Be(action == "move" ? 55 : null);
        snapshot.Items.Single(candidate => candidate.ItemKey == "other").Should().Be(other);
    }

    // TEST-12: Backend-recorded chat alternative scope permits a full replacement without a frontend alternative command.
    /// <summary>Preserves explicit scope authorization for both patch and replacement forms of a natural-language alternative.</summary>
    [Fact]
    public void Build_ChatAlternative_AllowsOnlyGrantedReplacementScope()
    {
        // STEP-01: Get context from AgentTestData.Context, add new(1, "lunch") to context.AlternativeScopes and create output as AgentOutput.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.AlternativeScopes.Add(new(1, "lunch"));
        var output = new AgentOutput { Action = "create_proposal", Reply = "Alternative", Scope = new(1, "lunch"),
            Items = [AgentTestData.Item(2, "new-alternative")] };
        // STEP-02: Assert Build().Items.Single().DishId is 2.
        new AgentProposalBuilder().Build(context, output).Items.Single().DishId.Should().Be(2);
        // STEP-03: Derive ungranted from a copy of output.
        var ungranted = output with { Scope = new() };
        // STEP-04: Assert new AgentProposalBuilder().Build() throws ConflictException.
        ((Action)(() => new AgentProposalBuilder().Build(context, ungranted))).Should().Throw<ConflictException>();
    }

    // TEST-13: A broad alternative search answered with a narrow patch keeps the other meals and the side dish unchanged.
    /// <summary>Ensures a narrow change after a broad search does not drop unrelated meals or side dishes.</summary>
    [Fact]
    public void Build_BroadAlternativeSearch_NarrowPatchPreservesOtherMealsAndSideDish()
    {
        // STEP-01: Get main from AgentTestData.Item, derive drink from a copy of AgentTestData.Item(), get outside from AgentTestData.Item and 3 more.
        var main = AgentTestData.Item(1, "main", 2);
        var drink = AgentTestData.Item(2, "drink", 2) with { SortOrder = 1 };
        var outside = AgentTestData.Item(3, "outside", 1, "dinner");
        var context = AgentTestData.Context(AgentTestData.Snapshot(outside, main, drink));
        context.AlternativeScopes.Add(new());
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Updated",
            Scope = new(2, "lunch"), Changes = [new("replace", "main", main with { DishId = 4 })] });
        // STEP-02: Assert result.Items has 3 item(s), result.Items.Single(filtered) is outside, result.Items.Single(filtered) is drink and 1 more.
        result.Items.Should().HaveCount(3);
        result.Items.Single(item => item.ItemKey == "outside").Should().Be(outside);
        result.Items.Single(item => item.ItemKey == "drink").Should().Be(drink);
        result.Items.Single(item => item.ItemKey == "main").DishId.Should().Be(4);
    }

    // TEST-14: A day-scoped selection that also copies unchanged rows of other days is accepted: the copies are ignored and only the
    // scoped day changes.
    /// <summary>Stops a harmless model habit (copying the untouched days into a day edit) from failing the whole edit.</summary>
    [Fact]
    public void Build_ScopedSelectionWithUnchangedCopies_IgnoresCopies()
    {
        // STEP-01: Get day1 from AgentTestData.Item, get day2 from AgentTestData.Item, get context from AgentTestData.Context and 1 more.
        var day1 = AgentTestData.Item(1, "d1", 1);
        var day2 = AgentTestData.Item(2, "d2", 2);
        var context = AgentTestData.Context(AgentTestData.Snapshot(day1, day2));

        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Scope = new(2, null), Items = [day1, AgentTestData.Item(5, "d2", 2)] });

        // STEP-02: Assert result.Items.Single(filtered) is day1, result.Items.Single(filtered).DishId is 5, result.Changes has exactly one matching item and 1 more.
        result.Items.Single(item => item.ItemKey == "d1").Should().Be(day1);
        result.Items.Single(item => item.ItemKey == "d2").DishId.Should().Be(5);
        result.Changes.Should().ContainSingle().Which.Type.Should().Be("replace");
    }

    // TEST-15: A day-scoped selection that changes a row of another day is rejected with feedback naming that day and meal.
    /// <summary>Keeps edits outside the requested scope forbidden while telling the model exactly what to remove.</summary>
    [Fact]
    public void Build_ScopedSelectionChangingOtherDay_RejectsWithSpecificFeedback()
    {
        // STEP-01: Get context from AgentTestData.Context.
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "d1", 1), AgentTestData.Item(2, "d2", 2)));

        // STEP-02: Wrap new AgentProposalBuilder().Build with context and AgentOutput { Action, Reply, Scope, ... } in act so its outcome can be asserted.
        var act = () => new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Scope = new(2, null), Items = [AgentTestData.Item(9, "d1", 1), AgentTestData.Item(5, "d2", 2)] });

        // STEP-03: Assert act throws ConflictException and the thrown exception.Message contains the expected text.
        act.Should().Throw<ConflictException>().Which.Message.Should().Contain("ngày 1 bữa trưa").And.Contain("Chỉ gửi các món thuộc phạm vi");
    }

    // TEST-16: A new plan is filled from issued candidates in rank order, one dish per position, using only the grant issued for that day and meal.
    /// <summary>Ensures the backend can finish a plan without another LLM call, and that the result passes the normal builder.</summary>
    [Fact]
    public void Assemble_NewPlan_FillsEveryPositionFromItsGrant()
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a copy of context.State, add the computed value to context.Grants and 1 more.
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["dinner", "lunch"] };
        context.Grants.Add(new(1, "lunch", [1, 2], null, context.State));
        context.Grants.Add(new(1, "dinner", [3], null, context.State));
        context.Grants.Add(new(2, "lunch", [1, 4], null, context.State));
        context.Grants.Add(new(2, "dinner", [5], null, context.State));

        var output = AgentProposalBuilder.Assemble(context)!;

        // STEP-02: Assert output.Items.Select(filtered) equals ((short)1, "lunch", 1) and new AgentProposalBuilder().Build().Items has 4 item(s).
        output.Items.Select(item => (item.DayNumber, item.MealSlot, item.DishId)).Should().Equal(
            ((short)1, "lunch", 1), ((short)1, "dinner", 3), ((short)2, "lunch", 4), ((short)2, "dinner", 5));
        new AgentProposalBuilder().Build(context, output).Items.Should().HaveCount(4);
    }

    // TEST-17: Nothing is assembled when a position lacks a candidate, a grant is for other constraints, or the turn edits an existing plan, a focused day, a selected item or an alternative.
    /// <summary>Ensures the fallback never replaces what the user is editing and never leaves a meal empty.</summary>
    [Theory]
    [InlineData("missing-position")][InlineData("other-constraints")][InlineData("existing-plan")][InlineData("focus")][InlineData("selected")][InlineData("alternative")]
    public void Assemble_NotANewCompletePlan_ReturnsNull(string area)
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to a copy of context.State, derive issued from the expected value and 3 more.
        var context = AgentTestData.Context(area == "existing-plan" ? AgentTestData.Snapshot(AgentTestData.Item()) : null);
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"], FocusDay = area == "focus" ? (short)1 : null };
        var issued = area == "other-constraints" ? context.State with { TasteTargets = [new("sour", .25)] } : context.State;
        if (area != "missing-position") context.Grants.Add(new(1, "lunch", [1], null, issued));
        if (area == "selected") context.Request.SelectedItemKey = "a";
        if (area == "alternative") context.AlternativeScopes.Add(new(1, "lunch"));

        // STEP-02: Assert AgentProposalBuilder.Assemble() is null.
        AgentProposalBuilder.Assemble(context).Should().BeNull();
    }

    // TEST-18: A long plan reaches the model cut to the days and dishes the message names; a short plan stays whole; this keeps each call on a 100-day plan affordable.
    /// <summary>Resending hundreds of items on every call would cost more than the edit itself and exceed the context limit.</summary>
    [Theory]
    [InlineData(25, "đổi bữa trưa ngày 12", new short[] { 12 })]
    [InlineData(25, "đổi Dish 25?", new short[] { 9 })]
    [InlineData(25, "đổi bớt món đi", new short[0])]
    public void PromptSource_LongPlan_KeepsOnlyNamedDays(int days, string content, short[] expected)
    {
        // STEP-01: Get context from AgentTestData.Context, set context.State to context.Source.State, set context.Request.Content to content and 1 more.
        var context = AgentTestData.Context(Long(days));
        context.State = context.Source!.State;
        context.Request.Content = content;

        var prompt = AgentProposalBuilder.PromptSource(context)!;

        // STEP-02: Assert prompt.Items.Select(filtered).Distinct() equals expected, prompt.Grants is empty and context.Source.Items has days * 3 item(s).
        prompt.Items.Select(item => item.DayNumber).Distinct().Should().Equal(expected);
        prompt.Grants.Should().BeEmpty();
        context.Source!.Items.Should().HaveCount(days * 3);
    }

    // TEST-19: A plan that fits is sent whole, exactly as before.
    /// <summary>Short plans must not change behaviour.</summary>
    [Fact]
    public void PromptSource_ShortPlan_IsNull()
    {
        // STEP-01: Get context from AgentTestData.Context and set context.State to context.Source.State.
        var context = AgentTestData.Context(Long(7));
        context.State = context.Source!.State;

        // STEP-02: Assert AgentProposalBuilder.PromptSource() is null.
        AgentProposalBuilder.PromptSource(context).Should().BeNull();
    }

    // TEST-20: Over a source, a full-scope selection that also changes a meal outside editMeals keeps that meal from the source and records it;
    // with no editMeals, or an edit staying inside them, nothing is put back (conversation 214).
    /// <summary>The model reads which meals to change; the backend makes sure no other meal of a saved plan is touched, without rejecting the turn.</summary>
    [Theory]
    [InlineData("stray")]
    [InlineData("inside")]
    [InlineData("none")]
    public void Build_EditMeals_KeepsEveryOtherMealFromSource(string area)
    {
        // STEP-01: A plan of day 1 breakfast and dinner and day 2 lunch; the user named day 1 dinner and day 2 lunch.
        var breakfast = AgentTestData.Item(1, "b1", 1, "breakfast");
        var dinner = AgentTestData.Item(2, "d1", 1, "dinner");
        var lunch = AgentTestData.Item(3, "l2", 2, "lunch");
        var context = AgentTestData.Context(AgentTestData.Snapshot(breakfast, dinner, lunch));
        AgentScope[] edits = area == "none" ? [] : [new(1, "dinner"), new(2, "lunch")];

        // STEP-02: Build a full-scope selection replacing dinner and lunch, and also breakfast unless area is "inside".
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection", EditMeals = edits,
            Items = [area == "inside" ? breakfast : AgentTestData.Item(7, "b1", 1, "breakfast"), AgentTestData.Item(8, "d1", 1, "dinner"), AgentTestData.Item(9, "l2", 2, "lunch")] });

        // STEP-03: Assert breakfast is kept only when editMeals leave it out, both named meals change, and what was put back is recorded.
        result.Items.Single(item => item.ItemKey == "b1").DishId.Should().Be(area == "none" ? 7 : 1);
        result.Items.Single(item => item.ItemKey == "d1").DishId.Should().Be(8);
        result.Items.Single(item => item.ItemKey == "l2").DishId.Should().Be(9);
        result.Changes.Should().HaveCount(area == "none" ? 3 : 2);
        context.RestoredMeals.Should().Equal(area == "stray" ? [new AgentScope(1, "breakfast")] : Array.Empty<AgentScope>());
    }

    // TEST-21: Candidates fetched per named meal (alternative slot scopes) allow a full-scope output whose editMeals stay inside them, and changes sent
    // under create_proposal with no items are applied as a patch (conversation 236).
    /// <summary>Keeps a correct per-meal edit from failing on its action label or on a scope wider than the candidate search.</summary>
    [Theory]
    [InlineData("create_proposal")]
    [InlineData("patch_proposal")]
    public void Build_PerMealCandidatesWithEditMeals_AcceptsFullScopeEdit(string action)
    {
        // STEP-01: A plan of day 1 breakfast and dinner and day 2 lunch, with candidates fetched for day 1 dinner and day 2 lunch only.
        var breakfast = AgentTestData.Item(1, "b1", 1, "breakfast");
        var context = AgentTestData.Context(AgentTestData.Snapshot(breakfast, AgentTestData.Item(2, "d1", 1, "dinner"), AgentTestData.Item(3, "l2", 2, "lunch")));
        context.AlternativeScopes.AddRange([new(1, "dinner"), new(2, "lunch")]);

        // STEP-02: Build replace changes for both named meals under scope full.
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = action, Reply = "Selection", EditMeals = [new(1, "dinner"), new(2, "lunch")],
            Changes = [new("replace", "d1", AgentTestData.Item(8, "d1", 1, "dinner")), new("replace", "l2", AgentTestData.Item(9, "l2", 2, "lunch"))] });

        // STEP-03: Assert both named meals changed and breakfast stayed.
        result.Items.Single(item => item.ItemKey == "b1").Should().Be(breakfast);
        result.Items.Single(item => item.ItemKey == "d1").DishId.Should().Be(8);
        result.Items.Single(item => item.ItemKey == "l2").DishId.Should().Be(9);
    }

    /// <summary>A plan of three dishes a day, named in order.</summary>
    private static AgentSnapshot Long(int days)
    {
        var state = AgentTestData.State() with { NumDays = (short)days, MealSlots = ["breakfast", "lunch", "dinner"] };
        var items = Enumerable.Range(0, days * 3).Select(index => AgentTestData.Item(index + 1, $"i{index}", (short)(index / 3 + 1), state.MealSlots[index % 3]) with { DishName = $"Dish {index + 1}" }).ToArray();
        return new(state, items, []);
    }
}
