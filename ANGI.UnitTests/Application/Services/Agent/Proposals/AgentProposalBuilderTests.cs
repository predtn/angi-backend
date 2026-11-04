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
        var outside = AgentTestData.Item() with { SourceItemId = 10 };
        var changed = AgentTestData.Item(2, "existing-lunch", 2) with { SourceItemId = 20 };
        var side = AgentTestData.Item(3, "outside-dinner", 2, "dinner");
        var context = AgentTestData.Context(AgentTestData.Snapshot(outside, changed, side));
        context.ProposalVersion = 3;

        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Scope = new(2, "lunch"), Items = [AgentTestData.Item(4, "model-new-key", 2)] });

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
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ProposalVersion = 3;
        var act = () => new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            BaseProposalVersion = 2, Items = [AgentTestData.Item(2)] });

        act.Should().Throw<ConflictException>().Which.ErrorCode.Should().Be("PROPOSAL_VERSION_CONFLICT");
    }

    // TEST-03: A selected-item edit preserves the other rows and rejects a replacement wider than the selected item.
    /// <summary>Ensures editing one item cannot silently rewrite rows the user did not select.</summary>
    [Fact]
    public void Build_SelectedItemSnapshot_PreservesOtherRowsAndRejectsWiderReplacement()
    {
        var selected = AgentTestData.Item();
        var side = AgentTestData.Item(2, "side") with { SortOrder = 1 };
        var context = AgentTestData.Context(AgentTestData.Snapshot(selected, side));
        context.Request.SelectedItemKey = selected.ItemKey;
        var output = new AgentOutput { Action = "create_proposal", Reply = "Selection", Items = [AgentTestData.Item(3)] };

        var result = new AgentProposalBuilder().Build(context, output);

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
        var a = AgentTestData.Item() with { SourceItemId = 100 };
        var b = AgentTestData.Item(2, "b", 2) with { SourceItemId = 200 };
        var source = AgentTestData.Snapshot(a, b, AgentTestData.Item(3, "c", 2, "dinner"));
        var context = AgentTestData.Context(source); context.ProposalVersion = 3;
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Updated",
            BaseProposalVersion = 3, Scope = new(2, "lunch"), Changes = [new("replace", "b", b with { DishId = 4, SourceItemId = 999 })] });
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
        var item = AgentTestData.Item() with { SourceItemId = 55 };
        var context = AgentTestData.Context(AgentTestData.Snapshot(item));
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Moved",
            Changes = [new("move", "a", item with { DayNumber = 2 })] });
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
        var act = () => new AgentProposalBuilder().Build(context, output);
        act.Should().Throw<ConflictException>();
    }

    // TEST-07: Explicit alternatives exclude the previous scoped dishes and preserve items outside the scope.
    /// <summary>Rejects an alternative that merely repeats the previous proposal's dishes.</summary>
    [Fact]
    public void Build_AlternativeCannotRepeatPreviousDish()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item())); context.Request.Command = "alternative";
        var output = new AgentOutput { Action = "create_proposal", Reply = "Alternative", Items = [AgentTestData.Item()] };
        var act = () => new AgentProposalBuilder().Build(context, output);
        act.Should().Throw<ConflictException>();
        output = output with { Items = [AgentTestData.Item(2)] };
        new AgentProposalBuilder().Build(context, output).Items.Single().DishId.Should().Be(2);
    }

    // TEST-08: Move an unsaved item across days without reranking and retain backend source proof for later create confirmation.
    /// <summary>Verifies changing position does not lose authorization when the original candidate grant was scoped to another day.</summary>
    [Fact]
    public async Task Build_MoveUnsavedItem_PreservesConfirmationProvenance()
    {
        var grant = AgentTestData.Grant([1], 1, "lunch");
        var item = AgentTestData.Item() with { RequestId = grant.RequestId, Price = 50000 };
        var source = new AgentSnapshot(AgentTestData.State(), [item], [grant]);
        var context = AgentTestData.Context(source);
        var snapshot = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Moved",
            Changes = [new("move", item.ItemKey, item with { DayNumber = 2 })] });
        snapshot.RetainedItems.Should().ContainSingle().Which.DishId.Should().Be(1);
        var validator = AgentTestData.Validator(AgentTestData.Dish()).Validator;
        var ready = await validator.ValidateAsync(snapshot, source, false, true, CancellationToken.None, []);
        ready.Validation.Errors.Should().BeEmpty();
        var serialized = AgentJson.Read<AgentSnapshot>(AgentJson.Write(ready.Snapshot));
        var confirm = await validator.ValidateAsync(serialized, null, true, false, CancellationToken.None);
        confirm.Validation.Errors.Should().BeEmpty();
        confirm.Snapshot.Items.Single().RequestId.Should().Be(grant.RequestId);
    }

    // TEST-09: Discard model-supplied price, names, request IDs and source identity on new items.
    /// <summary>Verifies catalog facts and provenance cannot be supplied by the model.</summary>
    [Fact]
    public void Build_NewItem_DropsUntrustedMetadata()
    {
        var output = new AgentOutput { Action = "create_proposal", Reply = "Preview", Items = [AgentTestData.Item() with
            { Price = 1, DishName = "Fake", RestaurantName = "Fake", SourceItemId = 900, RequestId = Guid.NewGuid() }] };
        var result = new AgentProposalBuilder().Build(AgentTestData.Context(), output).Items.Single();
        result.Price.Should().Be(0); result.DishName.Should().BeNull(); result.SourceItemId.Should().BeNull(); result.RequestId.Should().BeNull();
    }

    // TEST-10: A confirmed replan returns a complete replacement ordered chronologically.
    /// <summary>Ensures a confirmed replan replaces the whole plan in chronological order.</summary>
    [Fact]
    public void Build_ConfirmedReplan_ReturnsCompleteChronologicalReplacement()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.ReplanAfterConfirmation = true;
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Updated",
            Items = [AgentTestData.Item(3, "dinner", 2, "dinner"), AgentTestData.Item(2, "breakfast", 1, "breakfast")] });
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
        var item = AgentTestData.Item() with { SourceItemId = 55 };
        var other = AgentTestData.Item(3, "other", 2, "dinner");
        var context = AgentTestData.Context(AgentTestData.Snapshot(item, other));
        var changed = item with { ItemKey = "a_alt", SourceItemId = 999,
            DishId = action == "replace" ? 2 : item.DishId, DayNumber = action == "move" ? (short)2 : item.DayNumber };
        var snapshot = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Edit",
            Changes = [new(action, item.ItemKey, changed)] });
        snapshot.Items.Should().NotContain(candidate => candidate.ItemKey == "a_alt");
        snapshot.Items.Single(candidate => candidate.ItemKey == item.ItemKey).SourceItemId.Should().Be(action == "move" ? 55 : null);
        snapshot.Items.Single(candidate => candidate.ItemKey == "other").Should().Be(other);
    }

    // TEST-12: Backend-recorded chat alternative scope permits a full replacement without a frontend alternative command.
    /// <summary>Preserves explicit scope authorization for both patch and replacement forms of a natural-language alternative.</summary>
    [Fact]
    public void Build_ChatAlternative_AllowsOnlyGrantedReplacementScope()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item()));
        context.AlternativeScopes.Add(new(1, "lunch"));
        var output = new AgentOutput { Action = "create_proposal", Reply = "Alternative", Scope = new(1, "lunch"),
            Items = [AgentTestData.Item(2, "new-alternative")] };
        new AgentProposalBuilder().Build(context, output).Items.Single().DishId.Should().Be(2);
        var ungranted = output with { Scope = new() };
        ((Action)(() => new AgentProposalBuilder().Build(context, ungranted))).Should().Throw<ConflictException>();
    }

    // TEST-13: A broad alternative search answered with a narrow patch keeps the other meals and the side dish unchanged.
    /// <summary>Ensures a narrow change after a broad search does not drop unrelated meals or side dishes.</summary>
    [Fact]
    public void Build_BroadAlternativeSearch_NarrowPatchPreservesOtherMealsAndSideDish()
    {
        var main = AgentTestData.Item(1, "main", 2);
        var drink = AgentTestData.Item(2, "drink", 2) with { SortOrder = 1 };
        var outside = AgentTestData.Item(3, "outside", 1, "dinner");
        var context = AgentTestData.Context(AgentTestData.Snapshot(outside, main, drink));
        context.AlternativeScopes.Add(new());
        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "patch_proposal", Reply = "Updated",
            Scope = new(2, "lunch"), Changes = [new("replace", "main", main with { DishId = 4 })] });
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
        var day1 = AgentTestData.Item(1, "d1", 1);
        var day2 = AgentTestData.Item(2, "d2", 2);
        var context = AgentTestData.Context(AgentTestData.Snapshot(day1, day2));

        var result = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Scope = new(2, null), Items = [day1, AgentTestData.Item(5, "d2", 2)] });

        result.Items.Single(item => item.ItemKey == "d1").Should().Be(day1);
        result.Items.Single(item => item.ItemKey == "d2").DishId.Should().Be(5);
        result.Changes.Should().ContainSingle().Which.Type.Should().Be("replace");
    }

    // TEST-15: A day-scoped selection that changes a row of another day is rejected with feedback naming that day and meal.
    /// <summary>Keeps edits outside the requested scope forbidden while telling the model exactly what to remove.</summary>
    [Fact]
    public void Build_ScopedSelectionChangingOtherDay_RejectsWithSpecificFeedback()
    {
        var context = AgentTestData.Context(AgentTestData.Snapshot(AgentTestData.Item(1, "d1", 1), AgentTestData.Item(2, "d2", 2)));

        var act = () => new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Scope = new(2, null), Items = [AgentTestData.Item(9, "d1", 1), AgentTestData.Item(5, "d2", 2)] });

        act.Should().Throw<ConflictException>().Which.Message.Should().Contain("ngày 1 bữa trưa").And.Contain("Chỉ gửi các món thuộc phạm vi");
    }

    // TEST-16: A new plan is filled from issued candidates in rank order, one dish per position, using only the grant issued for that day and meal.
    /// <summary>Ensures the backend can finish a plan without another LLM call, and that the result passes the normal builder.</summary>
    [Fact]
    public void Assemble_NewPlan_FillsEveryPositionFromItsGrant()
    {
        var context = AgentTestData.Context();
        context.State = context.State with { MealSlots = ["dinner", "lunch"] };
        context.Grants.Add(new(1, "lunch", [1, 2], null, context.State));
        context.Grants.Add(new(1, "dinner", [3], null, context.State));
        context.Grants.Add(new(2, "lunch", [1, 4], null, context.State));
        context.Grants.Add(new(2, "dinner", [5], null, context.State));

        var output = AgentProposalBuilder.Assemble(context)!;

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
        var context = AgentTestData.Context(area == "existing-plan" ? AgentTestData.Snapshot(AgentTestData.Item()) : null);
        context.State = context.State with { NumDays = 1, MealSlots = ["lunch"], FocusDay = area == "focus" ? (short)1 : null };
        var issued = area == "other-constraints" ? context.State with { TasteTargets = [new("sour", .25)] } : context.State;
        if (area != "missing-position") context.Grants.Add(new(1, "lunch", [1], null, issued));
        if (area == "selected") context.Request.SelectedItemKey = "a";
        if (area == "alternative") context.AlternativeScopes.Add(new(1, "lunch"));

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
        var context = AgentTestData.Context(Long(days));
        context.State = context.Source!.State;
        context.Request.Content = content;

        var prompt = AgentProposalBuilder.PromptSource(context)!;

        prompt.Items.Select(item => item.DayNumber).Distinct().Should().Equal(expected);
        prompt.Grants.Should().BeEmpty();
        context.Source!.Items.Should().HaveCount(days * 3);
    }

    // TEST-19: A plan that fits is sent whole, exactly as before.
    /// <summary>Short plans must not change behaviour.</summary>
    [Fact]
    public void PromptSource_ShortPlan_IsNull()
    {
        var context = AgentTestData.Context(Long(7));
        context.State = context.Source!.State;

        AgentProposalBuilder.PromptSource(context).Should().BeNull();
    }

    /// <summary>A plan of three dishes a day, named in order.</summary>
    private static AgentSnapshot Long(int days)
    {
        var state = AgentTestData.State() with { NumDays = (short)days, MealSlots = ["breakfast", "lunch", "dinner"] };
        var items = Enumerable.Range(0, days * 3).Select(index => AgentTestData.Item(index + 1, $"i{index}", (short)(index / 3 + 1), state.MealSlots[index % 3]) with { DishName = $"Dish {index + 1}" }).ToArray();
        return new(state, items, []);
    }
}
