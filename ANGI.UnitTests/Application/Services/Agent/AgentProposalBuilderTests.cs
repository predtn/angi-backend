using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Services.Agent;
using FluentAssertions;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Verifies deterministic source identity, delta operations and scope preservation.</summary>
public sealed class AgentProposalBuilderTests
{
    // TEST-01: Replace day-two lunch while retaining all other days and meals byte-for-byte.
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
        source.Items.Single(x => x.ItemKey == "b").DishId.Should().Be(2);
    }

    // TEST-02: Preserve feedback identity while moving an unchanged dish inside the permitted full scope.
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

    // TEST-03: Reject invalid delta types, ambiguous keys, scope escape, duplicate keys and stale proposal versions.
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

    // TEST-04: Explicit alternatives exclude the previous scoped dishes and preserve items outside the scope.
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

    // TEST-05: Move an unsaved item across days without reranking and retain backend source proof for later create confirmation.
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

    // TEST-06: Discard model-supplied price, names, request IDs and source identity on new items.
    /// <summary>Verifies catalog facts and provenance cannot be supplied by the model.</summary>
    [Fact]
    public void Build_NewItem_DropsUntrustedMetadata()
    {
        var output = new AgentOutput { Action = "create_proposal", Reply = "Preview", Items = [AgentTestData.Item() with
            { Price = 1, DishName = "Fake", RestaurantName = "Fake", SourceItemId = 900, RequestId = Guid.NewGuid() }] };
        var result = new AgentProposalBuilder().Build(AgentTestData.Context(), output).Items.Single();
        result.Price.Should().Be(0); result.DishName.Should().BeNull(); result.SourceItemId.Should().BeNull(); result.RequestId.Should().BeNull();
    }
}
