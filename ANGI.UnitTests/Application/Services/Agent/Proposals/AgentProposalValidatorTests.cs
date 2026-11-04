using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.Services.Agent.Proposals;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent.Proposals;

/// <summary>Exercises catalog, provenance, budget and current-price validation boundaries.</summary>
public sealed class AgentProposalValidatorTests
{
    // TEST-01: A full snapshot that copies an unaffected day keeps that day's provenance while the edited item takes the fresh grant.
    /// <summary>Lets a full-scope selection copy untouched rows without forcing a rerank.</summary>
    [Fact]
    public async Task ValidateAsync_FullSnapshotCopiesUnaffectedDay_PreservesItsProvenanceWithoutReranking()
    {
        var selected = AgentTestData.Item() with { Price = 50000 };
        var copied = AgentTestData.Item(2, "untouched", 2) with { Price = 50000, RequestId = Guid.NewGuid() };
        var source = AgentTestData.Snapshot(selected, copied);
        var context = AgentTestData.Context(source);
        var grant = AgentTestData.Grant([1], 1, "lunch");
        context.Grants.Add(grant);
        var snapshot = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Items = [selected, copied] });

        var result = await AgentTestData.Validator(AgentTestData.Dish(), AgentTestData.Dish(2)).Validator
            .ValidateAsync(snapshot, source, false, false, default, [grant]);

        result.Validation.Errors.Should().BeEmpty();
        result.Snapshot.Items.Single(item => item.ItemKey == selected.ItemKey).RequestId.Should().Be(grant.RequestId);
        result.Snapshot.Items.Single(item => item.ItemKey == copied.ItemKey).RequestId.Should().Be(copied.RequestId);
        snapshot.RetainedItems.Should().ContainSingle().Which.Should().Be(copied);
    }

    // TEST-02: Reselecting a fallback-ranked dish requires a fresh grant and then carries the new request ID; covers create_proposal and patch_proposal.
    /// <summary>Ensures a re-ranked dish never keeps stale fallback provenance.</summary>
    [Theory]
    [InlineData("create_proposal")]
    [InlineData("patch_proposal")]
    public async Task ValidateAsync_ReselectedFallbackDish_UsesFreshRecoAndRequiresItsGrant(string action)
    {
        var item = AgentTestData.Item() with { SourceItemId = 33, Price = 50000 };
        var source = AgentTestData.Snapshot(item);
        var context = AgentTestData.Context(source);
        var grant = AgentTestData.Grant([1], 1, "lunch");
        context.Grants.Add(grant);
        var output = new AgentOutput { Action = action, Reply = "Reselected", Scope = new(1, "lunch"),
            Items = action == "create_proposal" ? [item] : [],
            Changes = action == "patch_proposal" ? [new("replace", item.ItemKey, item)] : [] };
        var snapshot = new AgentProposalBuilder().Build(context, output);
        var validator = AgentTestData.Validator(AgentTestData.Dish()).Validator;

        (await validator.ValidateAsync(snapshot, source, false, false, default, [])).Validation.Errors.Should().NotBeEmpty();
        var result = await validator.ValidateAsync(snapshot, source, false, false, default, [grant]);

        result.Validation.Errors.Should().BeEmpty();
        result.Validation.Warnings.Should().NotContain(warning => warning.Contains("đánh giá"));
        result.Snapshot.Items.Single().RequestId.Should().Be(grant.RequestId);
        result.Snapshot.Items.Single().SourceItemId.Should().Be(33);
        (await validator.ValidateAsync(result.Snapshot, source, true, false, default)).Validation.Errors.Should().BeEmpty();
    }

    // TEST-03: A taste change after ranking invalidates the old grant and requires new candidates.
    /// <summary>Blocks dishes ranked under a different taste fingerprint.</summary>
    [Fact]
    public async Task ValidateAsync_TasteChangedAfterRanking_RequiresNewCandidates()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { TasteTargets = [new("spicy", 1)] } };
        var result = await fixture.Validator.ValidateAsync(snapshot, null, false, false, default);
        result.Validation.Errors.Should().NotBeEmpty();
    }

    // TEST-04: Reprice an issued candidate from the backend and calculate the day route before ready.
    /// <summary>Verifies backend metadata overrides all untrusted proposal metadata.</summary>
    [Fact]
    public async Task ValidateAsync_ValidCandidate_PopulatesTrustedMetadata()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { Price = 1, DishName = "Fake" });
        var result = await fixture.Validator.ValidateAsync(snapshot, null, false, true, CancellationToken.None);
        result.Validation.Errors.Should().BeEmpty(); result.Validation.TotalCost.Should().Be(50000);
        result.Snapshot.Items.Single().DishName.Should().Be("Dish 1");
        result.Snapshot.Items.Single().RequestId.Should().Be(snapshot.Grants.Single().RequestId);
        result.Validation.Routes.Should().HaveCount(1);
    }

    // TEST-05: Reject unavailable catalog states, hard constraints, wrong scopes and forged source identity.
    /// <summary>Checks each deterministic rejection class independently.</summary>
    [Theory]
    [InlineData("missing")][InlineData("hidden")][InlineData("removed")][InlineData("sold-out")][InlineData("restaurant-closed")]
    [InlineData("restaurant-hidden")][InlineData("restaurant-unverified")][InlineData("restaurant-deleted")][InlineData("province")]
    [InlineData("price")][InlineData("budget")][InlineData("grant-missing")][InlineData("grant-day")][InlineData("grant-meal")]
    [InlineData("forged-source")][InlineData("pending")][InlineData("no-budget")][InlineData("bad-day")][InlineData("bad-meal")]
    [InlineData("bad-order")][InlineData("duplicate-key")][InlineData("duplicate-position")][InlineData("unknown-tag")][InlineData("meal-tag")]
    [InlineData("tag-required")][InlineData("tag-excluded")][InlineData("radius")][InlineData("grant-price")]
    public async Task ValidateAsync_InvalidProposal_ReturnsBlockingErrors(string area)
    {
        var dish = AgentTestData.Dish();
        var state = AgentTestData.State();
        var item = AgentTestData.Item();
        var grants = new[] { AgentTestData.Grant() };
        var items = new[] { item };
        switch (area)
        {
            case "hidden": dish.Status = DishStatus.Hidden; break;
            case "removed": dish.Status = DishStatus.Removed; break;
            case "sold-out": dish.IsAvailable = false; break;
            case "restaurant-closed": dish.Restaurant.OperatingStatus = RestaurantOperatingStatus.TemporarilyClosed; break;
            case "restaurant-hidden": dish.Restaurant.ModerationStatus = RestaurantModerationStatus.Hidden; break;
            case "restaurant-unverified": dish.Restaurant.VerificationStatus = RestaurantVerificationStatus.Unverified; break;
            case "restaurant-deleted": dish.Restaurant.DeletedAt = DateTime.UtcNow; break;
            case "province": dish.Restaurant.ProvinceName = "Other"; break;
            case "price": state = state with { MaxPricePerMeal = 49999 }; break;
            case "budget": state = state with { BudgetAmount = 49999 }; break;
            case "grant-missing": grants = []; break;
            case "grant-day": grants = [AgentTestData.Grant(day: 2)]; break;
            case "grant-meal": grants = [AgentTestData.Grant(meal: "dinner")]; break;
            case "forged-source": items = [item with { SourceItemId = 999 }]; break;
            case "pending": state = state with { PendingQuestion = "What budget?" }; break;
            case "no-budget": state = state with { BudgetAmount = null }; break;
            case "bad-day": items = [item with { DayNumber = 3 }]; break;
            case "bad-meal": items = [item with { MealSlot = "brunch" }]; break;
            case "bad-order": items = [item with { SortOrder = -1 }]; break;
            case "duplicate-key": items = [item, item with { DayNumber = 2 }]; break;
            case "duplicate-position": items = [item, item with { ItemKey = "b" }]; break;
            case "unknown-tag": state = state with { ExcludeTagIds = [99] }; break;
            case "meal-tag": dish.DishTags.Add(new() { TagId = 1, Tag = new() { Id = 1, TagType = TagType.MealTime, Code = "breakfast", Name = "Breakfast" } }); break;
            case "tag-required": state = state with { TagIds = [99] }; break;
            case "tag-excluded": state = state with { ExcludeTagIds = [1] }; dish.DishTags.Add(new() { TagId = 1, Tag = new() { Id = 1, Code = "spicy", Name = "Spicy" } }); break;
            case "radius": state = state with { OriginMode = "pinned", OriginLatitude = 10, OriginLongitude = 106, RadiusKm = 1 }; break;
            case "grant-price": grants = [grants[0] with { Constraints = state with { MaxPricePerMeal = 49999 } }]; break;
        }
        var fixture = AgentTestData.Validator(area == "missing" ? [] : [dish]);
        var result = await fixture.Validator.ValidateAsync(new(state, items, grants), null, false, true, CancellationToken.None);
        result.Validation.Errors.Should().NotBeEmpty(area);
        fixture.Routes.Verify(x => x.CalculateRouteAsync(It.IsAny<RouteCoordinate?>(), It.IsAny<IReadOnlyList<RouteCoordinate>>(), It.IsAny<RouteProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: A price change or unavailable dish before confirmation cannot be silently committed.
    /// <summary>Requires a refreshed preview for any changed business price, including decreases.</summary>
    [Theory]
    [InlineData(49999)][InlineData(50001)]
    public async Task ValidateAsync_ConfirmationPriceChange_Blocks(decimal previewPrice)
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var result = await fixture.Validator.ValidateAsync(AgentTestData.Snapshot(AgentTestData.Item() with { Price = previewPrice }), null, true, false, CancellationToken.None);
        result.Validation.Errors.Should().Contain(x => x.Contains("Giá"));
    }

    // TEST-07: New replacements require a candidate grant from this turn even when an older snapshot once authorized the same ID.
    /// <summary>Prevents old candidate grants from authorizing a newly added or replaced dish in a later turn.</summary>
    [Fact]
    public async Task ValidateAsync_NewReplacement_RequiresFreshScopedGrant()
    {
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var replacement = source with { Items = [AgentTestData.Item(2)] };
        var fixture = AgentTestData.Validator(AgentTestData.Dish(2));
        var stale = await fixture.Validator.ValidateAsync(replacement, source, false, false, CancellationToken.None, []);
        stale.Validation.Errors.Should().NotBeEmpty();
        var current = await fixture.Validator.ValidateAsync(replacement, source, false, false, CancellationToken.None, [AgentTestData.Grant([2], 1, "lunch")]);
        current.Validation.Errors.Should().BeEmpty();
    }

    // TEST-08: Removing the final saved item is valid while creating an empty official roadmap from chat is rejected.
    /// <summary>Checks the empty-itinerary boundary without inventing destination or unlimited-budget consent for a removal-only update.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ValidateAsync_EmptyItinerary_OnlyAllowsSavedRoadmapUpdate(bool update)
    {
        var state = new AgentState { NumDays = 1 };
        var snapshot = new AgentSnapshot(state, [], [], update ? 88 : null, update ? DateTime.UnixEpoch : null);
        var result = await AgentTestData.Validator().Validator.ValidateAsync(snapshot, null, false, false, CancellationToken.None);
        (result.Validation.Errors.Length == 0).Should().Be(update);
    }

    // TEST-09: An unchanged source item is valid without reranking and retains its recommendation request ID.
    /// <summary>Verifies explanation/reordering paths can reuse existing provenance.</summary>
    [Fact]
    public async Task ValidateAsync_UnchangedSource_DoesNotRequireNewGrant()
    {
        var item = AgentTestData.Item() with { SourceItemId = 33, RequestId = Guid.NewGuid() };
        var source = new AgentSnapshot(AgentTestData.State(), [item], []);
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(source, source, false, false, CancellationToken.None);
        result.Validation.Errors.Should().BeEmpty(); result.Snapshot.Items.Single().RequestId.Should().Be(item.RequestId);
    }

    // TEST-10: Rejected item keys are exposed for repair only when a missing scoped grant is the sole error; covers scope-only,
    // scope plus budget and a valid proposal.
    /// <summary>Prevents backend substitution from masking any other business error.</summary>
    [Theory]
    [InlineData("scope-only")][InlineData("scope-and-budget")][InlineData("valid")]
    public async Task ValidateAsync_ScopeRejection_ExposesItemKeysOnlyWhenItIsTheOnlyError(string area)
    {
        var state = AgentTestData.State() with { BudgetAmount = area == "scope-and-budget" ? 49999 : 500000 };
        AgentCandidateGrant[] grants = area == "valid" ? [AgentTestData.Grant([1])] : [AgentTestData.Grant([1], day: 2)];

        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator
            .ValidateAsync(new(state, [AgentTestData.Item()], grants), null, false, false, CancellationToken.None);

        if (area == "scope-only") result.Validation.ScopeRejectedItemKeys.Should().Equal("a");
        else result.Validation.ScopeRejectedItemKeys.Should().BeNull();
        result.Validation.Errors.Should().HaveCount(area switch { "scope-only" => 1, "scope-and-budget" => 2, _ => 0 });
    }

    // TEST-11: A day-1-only candidate copied into day 3 is replaced by the best authorized dish for that position, with grants pruned,
    // changes updated and backend-owned metadata cleared for revalidation.
    /// <summary>Verifies the substitution produces a snapshot that passes full validation without another LLM call.</summary>
    [Fact]
    public async Task RepairScopeAsync_OutOfScopeCandidate_SubstitutesBestAuthorizedDish()
    {
        var (snapshot, grants, catalog) = ScopeScenario();
        var target = snapshot.Items.Single(item => item.ItemKey == "item6");
        snapshot = snapshot with { Items = snapshot.Items.Select(item => item.ItemKey == "item6" ? item with { Price = 1, DishName = "Fake" } : item).ToArray(),
            Changes = [new("add", null, target)] };
        var validator = AgentTestData.Validator(catalog).Validator;

        var repaired = await validator.RepairScopeAsync(snapshot, null, grants, ["item6"], CancellationToken.None);

        var replaced = repaired!.Items.Single(item => item.ItemKey == "item6");
        replaced.DishId.Should().Be(7);
        replaced.Should().Be(new AgentItem("item6", null, 3, "breakfast", 0, 7));
        repaired.Changes!.Single().Item!.DishId.Should().Be(7);
        repaired.Grants.Should().Contain(grant => grant.DayNumber == null && grant.DishIds.Contains(7))
            .And.NotContain(grant => grant.DishIds.Contains(221));
        (await validator.ValidateAsync(repaired, null, false, false, CancellationToken.None, grants)).Validation.Errors.Should().BeEmpty();
    }

    // TEST-12: Substitution keeps recommendation order but prefers a narrower, newer grant and a name not already in the plan;
    // covers default rank order, reversed rank order, a day/meal grant and a duplicate dish name.
    /// <summary>Checks the selection priority so the substitute stays as close as possible to the user's ranking.</summary>
    [Theory]
    [InlineData("rank-order", 7)][InlineData("reversed-rank", 10)][InlineData("specific-grant", 10)][InlineData("same-name", 10)]
    public async Task RepairScopeAsync_Candidates_FollowSelectionPriority(string area, int expected)
    {
        var (snapshot, grants, catalog) = ScopeScenario();
        var state = snapshot.State;
        if (area == "reversed-rank") grants[0] = grants[0] with { DishIds = [1, 2, 3, 4, 5, 6, 8, 9, 10, 7] };
        if (area == "specific-grant") grants.Add(new(3, "breakfast", [10], Guid.NewGuid(), state));
        if (area == "same-name") catalog.Single(dish => dish.Id == 7).Name = "Dish 1";

        var repaired = await AgentTestData.Validator(catalog).Validator.RepairScopeAsync(snapshot, null, grants, ["item6"], CancellationToken.None);

        repaired!.Items.Single(item => item.ItemKey == "item6").DishId.Should().Be(expected);
    }

    // TEST-13: No substitution is made when it could exceed the safe boundary; covers more than a third of items, an unknown key,
    // an item outside the edit scope, an unchanged source item, a different taste fingerprint, wrong meal tags, unavailable dishes,
    // the trip budget, the per-meal price cap and only already-used candidates.
    /// <summary>Ensures the backend never widens scope, relaxes constraints or reuses dishes when substituting.</summary>
    [Theory]
    [InlineData("too-many")][InlineData("unknown-key")][InlineData("outside-scope")][InlineData("unchanged-source")][InlineData("taste")]
    [InlineData("meal-tag")][InlineData("unavailable")][InlineData("budget")][InlineData("meal-price")][InlineData("used-only")]
    public async Task RepairScopeAsync_UnsafeSubstitution_ReturnsNull(string area)
    {
        var (snapshot, grants, catalog) = ScopeScenario();
        string[] keys = ["item6"];
        AgentSnapshot? source = null;
        var lunchTag = new ANGI.Domain.Entities.Tag { Id = 2, Code = "lunch", Name = "Bữa trưa", TagType = TagType.MealTime };
        switch (area)
        {
            case "too-many": keys = ["item0", "item1", "item2", "item6"]; break;
            case "unknown-key": keys = ["item6", "missing"]; break;
            case "outside-scope": snapshot = snapshot with { Scope = new(1, null) }; break;
            case "unchanged-source": source = snapshot; break;
            case "taste":
                for (var index = 0; index < grants.Count; index++)
                    grants[index] = grants[index] with { Constraints = snapshot.State with { TasteTargets = [new("spicy", 1)] } };
                break;
            case "meal-tag":
                foreach (var dish in catalog.Where(dish => dish.Id is 7 or 10)) dish.DishTags.Add(new() { TagId = 2, Tag = lunchTag });
                break;
            case "unavailable": foreach (var dish in catalog.Where(dish => dish.Id is 7 or 10)) dish.IsAvailable = false; break;
            case "budget": snapshot = snapshot with { State = snapshot.State with { BudgetAmount = 8 * 50000 + 49999 } }; break;
            case "meal-price": snapshot = snapshot with { State = snapshot.State with { MaxPricePerMeal = 49999 } }; break;
            case "used-only": grants[0] = grants[0] with { DishIds = [1, 2, 3, 4, 5, 6, 8, 9] }; break;
        }

        var repaired = await AgentTestData.Validator(catalog).Validator.RepairScopeAsync(snapshot, source, grants, keys, CancellationToken.None);

        repaired.Should().BeNull(area);
    }

    // TEST-14: A drink-only breakfast is rejected by default but accepted when the user asked for drinks at that breakfast or when the slot was kept unchanged from the source; covers default, drink request and retained slot.
    /// <summary>Ensures main meals contain a main dish unless the user's explicit request or an untouched saved slot says otherwise.</summary>
    [Theory]
    [InlineData("default")][InlineData("drink-request")][InlineData("retained")]
    public async Task ValidateAsync_DrinkOnlyBreakfast_NeedsMainDishUnlessRequestedOrRetained(string area)
    {
        var drink = new ANGI.Domain.Entities.Tag { Id = 4, Code = "local_sf_drink", Name = "Đồ uống", TagType = TagType.Category };
        var main = new ANGI.Domain.Entities.Tag { Id = 5, Code = "local_sf_main", Name = "Món Chính", TagType = TagType.Category };
        var dish = AgentTestData.Dish(); dish.DishTags.Add(new() { DishId = 1, TagId = 4, Tag = drink });
        var fixture = AgentTestData.Validator(dish);
        fixture.Tags.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { drink, main });
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["breakfast"],
            MealRequests = area == "drink-request" ? [new(1, "breakfast", null, null, [4])] : [] };
        var grant = new AgentCandidateGrant(1, "breakfast", [1], Guid.NewGuid(), state);
        var snapshot = new AgentSnapshot(state, [AgentTestData.Item(1, "a", 1, "breakfast") with { Price = 50000 }], [grant]);

        var result = await fixture.Validator.ValidateAsync(snapshot, area == "retained" ? snapshot : null, false, false, default, [grant]);

        if (area == "default") result.Validation.Errors.Should().ContainSingle(error => error.Contains("cần ít nhất một món chính"));
        else result.Validation.Errors.Should().BeEmpty();
    }

    // TEST-15: A new item repeating an earlier dish name is flagged for backend replacement only when an unused authorized dish fits that position; the flag never becomes an error; covers with and without a spare dish.
    /// <summary>Lets the backend vary a multi-day plan while never blocking a plan whose catalog cannot avoid the repeat.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ValidateAsync_RepeatedDishName_IsFlaggedOnlyWhenAnUnusedCandidateExists(bool spare)
    {
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var grant = new AgentCandidateGrant(null, "lunch", spare ? [1, 2] : [1], Guid.NewGuid(), state);
        var snapshot = new AgentSnapshot(state, [AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2)], [grant]);

        var result = await AgentTestData.Validator(AgentTestData.Dish(1), AgentTestData.Dish(2)).Validator
            .ValidateAsync(snapshot, null, false, false, default, [grant]);

        result.Validation.Errors.Should().BeEmpty();
        if (spare) result.Validation.AvoidableRepeatItemKeys.Should().Equal("b");
        else result.Validation.AvoidableRepeatItemKeys.Should().BeNull();
    }

    // TEST-16: Repeat repair replaces every repeated position that still has an unused dish name and keeps the others unchanged,
    // instead of discarding the whole repair; covers a partly fixable plan and a plan with no spare dish at all.
    /// <summary>Ensures one exhausted position (e.g. the last day) cannot cancel the repeat fixes of every other position.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RepairScopeAsync_RepeatsOnly_KeepsPositionsWithoutSpareAndFixesTheRest(bool spare)
    {
        var state = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000, MealSlots = ["lunch"] };
        var items = new[] { AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2), AgentTestData.Item(1, "c", 3) };
        AgentCandidateGrant[] grants = [new(2, "lunch", spare ? [1, 2] : [1], Guid.NewGuid(), state), new(3, "lunch", [1], Guid.NewGuid(), state)];
        var snapshot = new AgentSnapshot(state, items, grants, Scope: new());

        var repaired = await AgentTestData.Validator(AgentTestData.Dish(1), AgentTestData.Dish(2)).Validator
            .RepairScopeAsync(snapshot, null, grants, ["b", "c"], CancellationToken.None, repeatsOnly: true);

        if (!spare) { repaired.Should().BeNull(); return; }
        repaired!.Items.Select(item => (item.ItemKey, item.DishId)).Should().Equal(("a", 1), ("b", 2), ("c", 1));
    }

    // TEST-17: A declared meal may stay empty when the user removed it or the source already lacked it; covers a skipped meal, a pre-existing gap,
    // a gap this edit creates, a day new to this plan and a meal slot new to this plan.
    /// <summary>Stops an unrelated edit being rejected for a meal removed earlier, while a real coverage gap still blocks.</summary>
    [Theory]
    [InlineData("skipped", true)][InlineData("already-empty", true)][InlineData("new-gap", false)][InlineData("new-day", false)][InlineData("new-slot", false)]
    public async Task ValidateAsync_MissingDeclaredMeal_DependsOnWhyItIsEmpty(string area, bool valid)
    {
        var lunch = AgentTestData.Item(1, "a", 1, "lunch");
        var dinner = AgentTestData.Item(2, "b", 1, "dinner");
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["lunch", "dinner"] };
        var source = new AgentSnapshot(state, area is "new-gap" or "skipped" ? [lunch, dinner] : [lunch], [AgentTestData.Grant()]);
        if (area == "new-slot") source = source with { Items = [lunch, dinner], State = state with { MealSlots = ["lunch"] } };
        if (area == "skipped") state = state with { SkippedMeals = [new(1, "dinner")] };
        if (area == "new-day") { source = source with { Items = [lunch, dinner] }; state = state with { NumDays = 2 }; }
        var kept = area == "new-slot" ? new[] { lunch } : new[] { lunch };
        var snapshot = new AgentSnapshot(state, area == "new-day" ? [lunch, dinner] : kept, [AgentTestData.Grant()], Scope: new(), RetainedItems: kept);
        var (validator, _, _, _) = AgentTestData.Validator(AgentTestData.Dish(1), AgentTestData.Dish(2));

        var result = await validator.ValidateAsync(snapshot, source, false, false, default, []);

        result.Validation.Errors.Any(error => error.StartsWith("Chưa có món cho bữa", StringComparison.Ordinal)).Should().Be(!valid);
    }

    // TEST-18: Budget repair swaps the most expensive new dish for a cheaper ranked dish of that position; requested names and retained rows stay.
    /// <summary>Avoids asking the user to raise a budget while cheaper ranked dishes exist.</summary>
    [Fact]
    public async Task RepairBudgetAsync_CheaperRankedDish_BringsTotalWithinBudget()
    {
        var expensive = AgentTestData.Dish(1); expensive.Price = 300000;
        var mid = AgentTestData.Dish(2); mid.Price = 100000;
        var cheap = AgentTestData.Dish(3); cheap.Price = 60000;
        var state = AgentTestData.State() with { BudgetAmount = 200000, MealSlots = ["lunch"], NumDays = 2 };
        var grant = new AgentCandidateGrant(null, "lunch", [2, 3], Guid.NewGuid(), state);
        var items = new[] { AgentTestData.Item(1, "a", 1) with { Price = 300000 }, AgentTestData.Item(2, "b", 2) with { Price = 100000 } };
        var (validator, _, _, _) = AgentTestData.Validator(expensive, mid, cheap);

        var repaired = await validator.RepairBudgetAsync(new(state, items, [grant], Scope: new()), null, [grant], default);

        repaired!.Value.Replaced.Should().Be(1);
        repaired.Value.Snapshot.Items.Select(item => item.DishId).Should().Equal(3, 2);
    }

    // TEST-19: Budget repair returns null when it cannot fit; covers no cheaper dish, no budget, a requested dish name and a retained source row.
    /// <summary>Keeps the honest budget question when no cheaper authorized dish can solve the overrun.</summary>
    [Theory]
    [InlineData("no-cheaper")][InlineData("no-budget")][InlineData("named")][InlineData("retained")]
    public async Task RepairBudgetAsync_CannotFit_ReturnsNull(string area)
    {
        var expensive = AgentTestData.Dish(1); expensive.Price = 300000;
        var cheap = AgentTestData.Dish(3); cheap.Price = 60000;
        var state = AgentTestData.State() with { BudgetAmount = 200000, MealSlots = ["lunch"] };
        if (area == "no-budget") state = state with { BudgetAmount = null };
        if (area == "named") state = state with { MealRequests = [new(1, "lunch", "Dish 1", null, [])] };
        var item = AgentTestData.Item(1, "a", 1) with { Price = 300000 };
        var grant = new AgentCandidateGrant(null, "lunch", area == "no-cheaper" ? [1] : [3], Guid.NewGuid(), state);
        var source = area == "retained" ? new AgentSnapshot(state, [item], [grant]) : null;
        var (validator, _, _, _) = AgentTestData.Validator(expensive, cheap);

        (await validator.RepairBudgetAsync(new(state, [item], [grant], Scope: new()), source, [grant], default)).Should().BeNull();
    }

    /// <summary>Builds a three-day plan whose day-3 breakfast holds a day-1-only candidate, with dishes 7 and 10 still unused.</summary>
    private static (AgentSnapshot Snapshot, List<AgentCandidateGrant> Grants, ANGI.Domain.Entities.Dish[] Catalog) ScopeScenario()
    {
        var state = AgentTestData.State() with { NumDays = 3, BudgetAmount = 1500000, MealSlots = ["breakfast", "lunch", "dinner"] };
        var items = Enumerable.Range(0, 9).Select(index => AgentTestData.Item(index == 6 ? 221 : index + 1, $"item{index}",
            (short)(index / 3 + 1), state.MealSlots[index % 3])).ToArray();
        var grants = new List<AgentCandidateGrant>
        {
            new(null, null, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], Guid.NewGuid(), state),
            new(1, "breakfast", [221], Guid.NewGuid(), state)
        };
        var catalog = Enumerable.Range(1, 10).Append(221).Select(id => AgentTestData.Dish(id)).ToArray();
        return (new AgentSnapshot(state, items, grants.ToArray(), Scope: new()), grants, catalog);
    }
}
