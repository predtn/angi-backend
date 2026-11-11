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
        // STEP-01: Derive selected from a copy of AgentTestData.Item(), derive copied from a copy of AgentTestData.Item() and 5 more.
        var selected = AgentTestData.Item() with { Price = 50000 };
        var copied = AgentTestData.Item(2, "untouched", 2) with { Price = 50000, RequestId = Guid.NewGuid() };
        var source = AgentTestData.Snapshot(selected, copied);
        var context = AgentTestData.Context(source);
        var grant = AgentTestData.Grant([1], 1, "lunch");
        context.Grants.Add(grant);
        var snapshot = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection",
            Items = [selected, copied] });

        // STEP-02: Validate snapshot and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish(), AgentTestData.Dish(2)).Validator
            .ValidateAsync(snapshot, source, false, false, default, [grant]);

        // STEP-03: Assert result.Validation.Errors is empty, result.Snapshot.Items.Single(filtered).RequestId is grant.RequestId and 3 more.
        result.Validation.Errors.Should().BeEmpty();
        result.Snapshot.Items.Single(item => item.ItemKey == selected.ItemKey).RequestId.Should().Be(grant.RequestId);
        result.Snapshot.Items.Single(item => item.ItemKey == copied.ItemKey).RequestId.Should().Be(copied.RequestId);
        snapshot.RetainedItems.Should().ContainSingle().Which.Should().Be(copied);
    }

    // TEST-02: A trip with no budget stated is valid and its dishes are priced from the catalog (conversation 216 showed empty dishes).
    /// <summary>Budget is optional: no budget means no budget limit, not an unfinished plan.</summary>
    [Fact]
    public async Task ValidateAsync_NoBudget_PricesDishesWithoutError()
    {
        // STEP-01: Build a new plan whose state has no budget and does not mark it unrestricted.
        var item = AgentTestData.Item();
        var context = AgentTestData.Context();
        context.State = context.State with { BudgetAmount = null, BudgetUnrestricted = false };
        var grant = AgentTestData.Grant([1], 1, "lunch");
        context.Grants.Add(grant);
        var snapshot = new AgentProposalBuilder().Build(context, new AgentOutput { Action = "create_proposal", Reply = "Selection", Items = [item] });

        // STEP-02: Validate the snapshot.
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(snapshot, null, false, false, default, [grant]);

        // STEP-03: Assert no error and the dish carries its catalog name and price.
        result.Validation.Errors.Should().BeEmpty();
        result.Snapshot.Items.Single().DishName.Should().NotBeNullOrEmpty();
        result.Snapshot.Items.Single().Price.Should().BeGreaterThan(0);
    }

    // TEST-03: Only integrity errors block a save: an over-budget plan has none although ValidateAsync reports the budget, while a changed price,
    // a dish missing from the catalog and a trip with no day count each do; covers all four.
    /// <summary>Keeps a plan the chat showed savable despite plan-rule errors, while data that would make the roadmap wrong is still refused (conversation 223).</summary>
    [Theory]
    [InlineData("budget", null)]
    [InlineData("price", "Giá món 1 đã thay đổi; cần xem đề xuất mới.")]
    [InlineData("missing", "Món 99 không còn khả dụng.")]
    [InlineData("structure", "Chưa chốt đủ tỉnh, số ngày hoặc câu hỏi đang chờ.")]
    public async Task IntegrityErrorsAsync_BlocksOnlyDataErrors(string area, string? expected)
    {
        // STEP-01: Build a one-item snapshot priced like the catalog dish, then break it for area.
        var item = AgentTestData.Item() with { Price = 50000 };
        var snapshot = AgentTestData.Snapshot(item);
        snapshot = area switch
        {
            "budget" => snapshot with { State = snapshot.State with { BudgetAmount = 10000 } },
            "price" => snapshot with { Items = [item with { Price = 40000 }] },
            "missing" => snapshot with { Items = [item with { DishId = 99 }] },
            "structure" => snapshot with { State = snapshot.State with { NumDays = null } },
            _ => snapshot
        };
        var validator = AgentTestData.Validator(AgentTestData.Dish()).Validator;

        // STEP-02: Run IntegrityErrorsAsync and keep errors.
        var errors = await validator.IntegrityErrorsAsync(snapshot, null, default);

        // STEP-03: Assert the over-budget plan has no integrity error but is still reported over budget, and each broken plan carries its error.
        if (expected is null)
        {
            errors.Should().BeEmpty();
            (await validator.ValidateAsync(snapshot, null, true, false, default)).Validation.Errors.Should().Contain(AgentProposalValidator.BudgetExceeded);
        }
        else errors.Should().Contain(expected);
    }

    // TEST-04: Reselecting a fallback-ranked dish requires a fresh grant and then carries the new request ID; covers create_proposal and patch_proposal.
    /// <summary>Ensures a re-ranked dish never keeps stale fallback provenance.</summary>
    [Theory]
    [InlineData("create_proposal")]
    [InlineData("patch_proposal")]
    public async Task ValidateAsync_ReselectedFallbackDish_UsesFreshRecoAndRequiresItsGrant(string action)
    {
        // STEP-01: Derive item from a copy of AgentTestData.Item(), get source from AgentTestData.Snapshot, get context from AgentTestData.Context and 5 more.
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

        // STEP-02: Assert validator.ValidateAsync().Validation.Errors is not empty.
        (await validator.ValidateAsync(snapshot, source, false, false, default, [])).Validation.Errors.Should().NotBeEmpty();
        // STEP-03: Validate snapshot and keep result.
        var result = await validator.ValidateAsync(snapshot, source, false, false, default, [grant]);

        // STEP-04: Assert result.Validation.Errors is empty, result.Validation.Warnings holds no matching item and 3 more.
        result.Validation.Errors.Should().BeEmpty();
        result.Validation.Warnings.Should().NotContain(warning => warning.Contains("đánh giá"));
        result.Snapshot.Items.Single().RequestId.Should().Be(grant.RequestId);
        result.Snapshot.Items.Single().SourceItemId.Should().Be(33);
        (await validator.ValidateAsync(result.Snapshot, source, true, false, default)).Validation.Errors.Should().BeEmpty();
    }

    // TEST-05: A taste change after ranking invalidates the old grant and requires new candidates.
    /// <summary>Blocks dishes ranked under a different taste fingerprint.</summary>
    [Fact]
    public async Task ValidateAsync_TasteChangedAfterRanking_RequiresNewCandidates()
    {
        // STEP-01: Create fixture through AgentTestData.Validator, get snapshot from AgentTestData.Snapshot and set snapshot to a copy of snapshot.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { TasteTargets = [new("spicy", 1)] } };
        // STEP-02: Validate snapshot and keep result.
        var result = await fixture.Validator.ValidateAsync(snapshot, null, false, false, default);
        // STEP-03: Assert result.Validation.Errors is not empty.
        result.Validation.Errors.Should().NotBeEmpty();
    }

    // TEST-06: Reprice an issued candidate from the backend and calculate the day route before ready.
    /// <summary>Verifies backend metadata overrides all untrusted proposal metadata.</summary>
    [Fact]
    public async Task ValidateAsync_ValidCandidate_PopulatesTrustedMetadata()
    {
        // STEP-01: Create fixture through AgentTestData.Validator and get snapshot from AgentTestData.Snapshot.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item() with { Price = 1, DishName = "Fake" });
        // STEP-02: Validate snapshot and keep result.
        var result = await fixture.Validator.ValidateAsync(snapshot, null, false, true, CancellationToken.None);
        // STEP-03: Assert result.Validation.Errors is empty, result.Validation.TotalCost is 50000, result.Snapshot.Items.Single().DishName is "Dish 1" and 2 more.
        result.Validation.Errors.Should().BeEmpty(); result.Validation.TotalCost.Should().Be(50000);
        result.Snapshot.Items.Single().DishName.Should().Be("Dish 1");
        result.Snapshot.Items.Single().RequestId.Should().Be(snapshot.Grants.Single().RequestId);
        result.Validation.Routes.Should().HaveCount(1);
    }

    // TEST-07: Reject unavailable catalog states, hard constraints, wrong scopes and forged source identity.
    /// <summary>Checks each deterministic rejection class independently.</summary>
    [Theory]
    [InlineData("missing")][InlineData("hidden")][InlineData("removed")][InlineData("sold-out")][InlineData("restaurant-closed")]
    [InlineData("restaurant-hidden")][InlineData("restaurant-unverified")][InlineData("restaurant-deleted")][InlineData("province")]
    [InlineData("price")][InlineData("budget")][InlineData("grant-missing")][InlineData("grant-day")][InlineData("grant-meal")]
    [InlineData("forged-source")][InlineData("pending")][InlineData("bad-day")][InlineData("bad-meal")]
    [InlineData("bad-order")][InlineData("duplicate-key")][InlineData("duplicate-position")][InlineData("unknown-tag")][InlineData("meal-tag")]
    [InlineData("tag-required")][InlineData("tag-excluded")][InlineData("radius")][InlineData("grant-price")]
    public async Task ValidateAsync_InvalidProposal_ReturnsBlockingErrors(string area)
    {
        // STEP-01: Get dish from AgentTestData.Dish, get state from AgentTestData.State, get item from AgentTestData.Item, build the grants collection and 3 more.
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
        // STEP-02: Validate new(state, items, grants) and keep result.
        var result = await fixture.Validator.ValidateAsync(new(state, items, grants), null, false, true, CancellationToken.None);
        // STEP-03: Assert result.Validation.Errors is not empty.
        result.Validation.Errors.Should().NotBeEmpty(area);
        // STEP-04: Verify CalculateRouteAsync is never called.
        fixture.Routes.Verify(x => x.CalculateRouteAsync(It.IsAny<RouteCoordinate?>(), It.IsAny<IReadOnlyList<RouteCoordinate>>(), It.IsAny<RouteProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: A price change or unavailable dish before confirmation cannot be silently committed.
    /// <summary>Requires a refreshed preview for any changed business price, including decreases.</summary>
    [Theory]
    [InlineData(49999)][InlineData(50001)]
    public async Task ValidateAsync_ConfirmationPriceChange_Blocks(decimal previewPrice)
    {
        // STEP-01: Create fixture through AgentTestData.Validator.
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        // STEP-02: Validate AgentTestData.Snapshot() and keep result.
        var result = await fixture.Validator.ValidateAsync(AgentTestData.Snapshot(AgentTestData.Item() with { Price = previewPrice }), null, true, false, CancellationToken.None);
        // STEP-03: Assert result.Validation.Errors contains a matching item.
        result.Validation.Errors.Should().Contain(x => x.Contains("Giá"));
    }

    // TEST-09: New replacements require a candidate grant from this turn even when an older snapshot once authorized the same ID.
    /// <summary>Prevents old candidate grants from authorizing a newly added or replaced dish in a later turn.</summary>
    [Fact]
    public async Task ValidateAsync_NewReplacement_RequiresFreshScopedGrant()
    {
        // STEP-01: Get source from AgentTestData.Snapshot, derive replacement from a copy of source and create fixture through AgentTestData.Validator.
        var source = AgentTestData.Snapshot(AgentTestData.Item());
        var replacement = source with { Items = [AgentTestData.Item(2)] };
        var fixture = AgentTestData.Validator(AgentTestData.Dish(2));
        // STEP-02: Validate replacement and keep stale.
        var stale = await fixture.Validator.ValidateAsync(replacement, source, false, false, CancellationToken.None, []);
        // STEP-03: Assert stale.Validation.Errors is not empty.
        stale.Validation.Errors.Should().NotBeEmpty();
        // STEP-04: Validate replacement and keep current.
        var current = await fixture.Validator.ValidateAsync(replacement, source, false, false, CancellationToken.None, [AgentTestData.Grant([2], 1, "lunch")]);
        // STEP-05: Assert current.Validation.Errors is empty.
        current.Validation.Errors.Should().BeEmpty();
    }

    // TEST-10: Removing the final saved item is valid while creating an empty official roadmap from chat is rejected.
    /// <summary>Checks the empty-itinerary boundary without inventing destination or unlimited-budget consent for a removal-only update.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ValidateAsync_EmptyItinerary_OnlyAllowsSavedRoadmapUpdate(bool update)
    {
        // STEP-01: Create state as AgentState and create snapshot as AgentSnapshot.
        var state = new AgentState { NumDays = 1 };
        var snapshot = new AgentSnapshot(state, [], [], update ? 88 : null, update ? DateTime.UnixEpoch : null);
        // STEP-02: Validate snapshot and keep result.
        var result = await AgentTestData.Validator().Validator.ValidateAsync(snapshot, null, false, false, CancellationToken.None);
        // STEP-03: Assert result.Validation.Errors.Length == 0 is update.
        (result.Validation.Errors.Length == 0).Should().Be(update);
    }

    // TEST-11: An unchanged source item is valid without reranking and retains its recommendation request ID.
    /// <summary>Verifies explanation/reordering paths can reuse existing provenance.</summary>
    [Fact]
    public async Task ValidateAsync_UnchangedSource_DoesNotRequireNewGrant()
    {
        // STEP-01: Derive item from a copy of AgentTestData.Item() and create source as AgentSnapshot.
        var item = AgentTestData.Item() with { SourceItemId = 33, RequestId = Guid.NewGuid() };
        var source = new AgentSnapshot(AgentTestData.State(), [item], []);
        // STEP-02: Validate source and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(source, source, false, false, CancellationToken.None);
        // STEP-03: Assert result.Validation.Errors is empty and result.Snapshot.Items.Single().RequestId is item.RequestId.
        result.Validation.Errors.Should().BeEmpty(); result.Snapshot.Items.Single().RequestId.Should().Be(item.RequestId);
    }

    // TEST-12: Rejected item keys are exposed for repair only when a missing scoped grant is the sole error; covers scope-only,
    // scope plus budget and a valid proposal.
    /// <summary>Prevents backend substitution from masking any other business error.</summary>
    [Theory]
    [InlineData("scope-only")][InlineData("scope-and-budget")][InlineData("valid")]
    public async Task ValidateAsync_ScopeRejection_ExposesItemKeysOnlyWhenItIsTheOnlyError(string area)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State() and derive grants from the expected value.
        var state = AgentTestData.State() with { BudgetAmount = area == "scope-and-budget" ? 49999 : 500000 };
        AgentCandidateGrant[] grants = area == "valid" ? [AgentTestData.Grant([1])] : [AgentTestData.Grant([1], day: 2)];

        // STEP-02: Validate the computed value and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator
            .ValidateAsync(new(state, [AgentTestData.Item()], grants), null, false, false, CancellationToken.None);

        // STEP-03: Assert depending on area == "scope-only", result.Validation.ScopeRejectedItemKeys equals "a" and 2 more.
        if (area == "scope-only") result.Validation.ScopeRejectedItemKeys.Should().Equal("a");
        else result.Validation.ScopeRejectedItemKeys.Should().BeNull();
        result.Validation.Errors.Should().HaveCount(area switch { "scope-only" => 1, "scope-and-budget" => 2, _ => 0 });
    }

    // TEST-13: A drink-only breakfast is rejected by default but accepted when the user asked for drinks at that breakfast or when the slot was kept unchanged from the source; covers default, drink request and retained slot.
    /// <summary>Ensures main meals contain a main dish unless the user's explicit request or an untouched saved slot says otherwise.</summary>
    [Theory]
    [InlineData("default")][InlineData("drink-request")][InlineData("retained")]
    public async Task ValidateAsync_DrinkOnlyBreakfast_NeedsMainDishUnlessRequestedOrRetained(string area)
    {
        // STEP-01: Create drink as ANGI.Domain.Entities.Tag, create main as ANGI.Domain.Entities.Tag, get dish from AgentTestData.Dish and 2 more.
        var drink = new ANGI.Domain.Entities.Tag { Id = 4, Code = "local_sf_drink", Name = "Đồ uống", TagType = TagType.Category };
        var main = new ANGI.Domain.Entities.Tag { Id = 5, Code = "local_sf_main", Name = "Món Chính", TagType = TagType.Category };
        var dish = AgentTestData.Dish(); dish.DishTags.Add(new() { DishId = 1, TagId = 4, Tag = drink });
        var fixture = AgentTestData.Validator(dish);
        // STEP-02: Stub fixture.GetTagsAsync.
        fixture.Tags.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { drink, main });
        // STEP-03: Derive state from a copy of AgentTestData.State(), create grant as AgentCandidateGrant and create snapshot as AgentSnapshot.
        var state = AgentTestData.State() with { NumDays = 1, MealSlots = ["breakfast"],
            MealRequests = area == "drink-request" ? [new(1, "breakfast", null, null, [4])] : [] };
        var grant = new AgentCandidateGrant(1, "breakfast", [1], Guid.NewGuid(), state);
        var snapshot = new AgentSnapshot(state, [AgentTestData.Item(1, "a", 1, "breakfast") with { Price = 50000 }], [grant]);

        // STEP-04: Validate snapshot and keep result.
        var result = await fixture.Validator.ValidateAsync(snapshot, area == "retained" ? snapshot : null, false, false, default, [grant]);

        // STEP-05: Assert depending on area == "default", result.Validation.Errors has exactly one matching item and result.Validation.Errors is empty.
        if (area == "default") result.Validation.Errors.Should().ContainSingle(error => error.Contains("cần ít nhất một món chính"));
        else result.Validation.Errors.Should().BeEmpty();
    }

    // TEST-14: A new item repeating an earlier dish name is flagged for backend replacement only when an unused authorized dish fits that position; the flag never becomes an error; covers with and without a spare dish.
    /// <summary>Lets the backend vary a multi-day plan while never blocking a plan whose catalog cannot avoid the repeat.</summary>
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ValidateAsync_RepeatedDishName_IsFlaggedOnlyWhenAnUnusedCandidateExists(bool spare)
    {
        // STEP-01: Derive state from a copy of AgentTestData.State(), create grant as AgentCandidateGrant and create snapshot as AgentSnapshot.
        var state = AgentTestData.State() with { MealSlots = ["lunch"] };
        var grant = new AgentCandidateGrant(null, "lunch", spare ? [1, 2] : [1], Guid.NewGuid(), state);
        var snapshot = new AgentSnapshot(state, [AgentTestData.Item(1, "a", 1), AgentTestData.Item(1, "b", 2)], [grant]);

        // STEP-02: Validate snapshot and keep result.
        var result = await AgentTestData.Validator(AgentTestData.Dish(1), AgentTestData.Dish(2)).Validator
            .ValidateAsync(snapshot, null, false, false, default, [grant]);

        // STEP-03: Assert result.Validation.Errors is empty, depending on spare, result.Validation.AvoidableRepeatItemKeys equals "b" and 1 more.
        result.Validation.Errors.Should().BeEmpty();
        if (spare) result.Validation.AvoidableRepeatItemKeys.Should().Equal("b");
        else result.Validation.AvoidableRepeatItemKeys.Should().BeNull();
    }

    // TEST-15: A declared meal may stay empty when the user removed it or the source already lacked it; covers a skipped meal, a pre-existing gap,
    // a gap this edit creates, a day new to this plan and a meal slot new to this plan.
    /// <summary>Stops an unrelated edit being rejected for a meal removed earlier, while a real coverage gap still blocks.</summary>
    [Theory]
    [InlineData("skipped", true)][InlineData("already-empty", true)][InlineData("new-gap", false)][InlineData("new-day", false)][InlineData("new-slot", false)]
    public async Task ValidateAsync_MissingDeclaredMeal_DependsOnWhyItIsEmpty(string area, bool valid)
    {
        // STEP-01: Get lunch from AgentTestData.Item, get dinner from AgentTestData.Item, derive state from a copy of AgentTestData.State() and 7 more.
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

        // STEP-02: Validate snapshot and keep result.
        var result = await validator.ValidateAsync(snapshot, source, false, false, default, []);

        // STEP-03: Assert result.Validation.Errors.Any(filtered) is !valid.
        result.Validation.Errors.Any(error => error.StartsWith("Chưa có món cho bữa", StringComparison.Ordinal)).Should().Be(!valid);
    }

    // TEST-16: Budget repair swaps the most expensive new dish for a cheaper ranked dish of that position; requested names and retained rows stay.
    /// <summary>Avoids asking the user to raise a budget while cheaper ranked dishes exist.</summary>
    [Fact]
    public async Task RepairBudgetAsync_CheaperRankedDish_BringsTotalWithinBudget()
    {
        // STEP-01: Get expensive from AgentTestData.Dish, set expensive.Price to 300000, get mid from AgentTestData.Dish, set mid.Price to 100000 and 7 more.
        var expensive = AgentTestData.Dish(1); expensive.Price = 300000;
        var mid = AgentTestData.Dish(2); mid.Price = 100000;
        var cheap = AgentTestData.Dish(3); cheap.Price = 60000;
        var state = AgentTestData.State() with { BudgetAmount = 200000, MealSlots = ["lunch"], NumDays = 2 };
        var grant = new AgentCandidateGrant(null, "lunch", [2, 3], Guid.NewGuid(), state);
        var items = new[] { AgentTestData.Item(1, "a", 1) with { Price = 300000 }, AgentTestData.Item(2, "b", 2) with { Price = 100000 } };
        var (validator, _, _, _) = AgentTestData.Validator(expensive, mid, cheap);

        var repaired = await validator.RepairBudgetAsync(new(state, items, [grant], Scope: new()), null, [grant], default);

        // STEP-02: Assert repaired.Value.Replaced is 1 and repaired.Value.Snapshot.Items.Select(filtered) equals 3.
        repaired!.Value.Replaced.Should().Be(1);
        repaired.Value.Snapshot.Items.Select(item => item.DishId).Should().Equal(3, 2);
    }

    // TEST-17: Budget repair returns null when it cannot fit; covers no cheaper dish, no budget, a requested dish name and a retained source row.
    /// <summary>Keeps the honest budget question when no cheaper authorized dish can solve the overrun.</summary>
    [Theory]
    [InlineData("no-cheaper")][InlineData("no-budget")][InlineData("named")][InlineData("retained")]
    public async Task RepairBudgetAsync_CannotFit_ReturnsNull(string area)
    {
        // STEP-01: Get expensive from AgentTestData.Dish, set expensive.Price to 300000, get cheap from AgentTestData.Dish, set cheap.Price to 60000 and 7 more.
        var expensive = AgentTestData.Dish(1); expensive.Price = 300000;
        var cheap = AgentTestData.Dish(3); cheap.Price = 60000;
        var state = AgentTestData.State() with { BudgetAmount = 200000, MealSlots = ["lunch"] };
        if (area == "no-budget") state = state with { BudgetAmount = null };
        if (area == "named") state = state with { MealRequests = [new(1, "lunch", "Dish 1", null, [])] };
        var item = AgentTestData.Item(1, "a", 1) with { Price = 300000 };
        var grant = new AgentCandidateGrant(null, "lunch", area == "no-cheaper" ? [1] : [3], Guid.NewGuid(), state);
        var source = area == "retained" ? new AgentSnapshot(state, [item], [grant]) : null;
        var (validator, _, _, _) = AgentTestData.Validator(expensive, cheap);

        // STEP-02: Assert validator.RepairBudgetAsync() is null.
        (await validator.RepairBudgetAsync(new(state, [item], [grant], Scope: new()), source, [grant], default)).Should().BeNull();
    }

    // TEST-18: A dish must lie within its own day's place; another day without a place accepts it, and a day place overrides the trip's origin radius.
    /// <summary>Ensures a model cannot move day-1 dishes across the city, which is the point of following the itinerary.</summary>
    [Fact]
    public async Task ValidateAsync_DayArea_AppliesOnlyToItsDay()
    {
        // STEP-01: Unpack (validator, _, _, _) from AgentTestData.Validator(), create near as AgentDayArea, create far as AgentDayArea and define helper Errors.
        var (validator, _, _, _) = AgentTestData.Validator(AgentTestData.Dish());
        var near = new AgentDayArea(1, "Gần", 16.05m, 108.2m, 3m);
        var far = new AgentDayArea(1, "Xa", 21.03m, 105.85m, 3m);
        async Task<string[]> Errors(AgentState state)
        {
            var snapshot = new AgentSnapshot(state, [AgentTestData.Item()], [new(null, null, [1], null, state)]);
            return (await validator.ValidateAsync(snapshot, null, false, false, default, [new(null, null, [1], null, state)])).Validation.Errors;
        }

        // STEP-02: Assert Errors() is empty and Errors() contains a matching item.
        (await Errors(AgentTestData.State() with { DayAreas = [near] })).Should().BeEmpty();
        (await Errors(AgentTestData.State() with { DayAreas = [far] })).Should().Contain(error => error.Contains("không đáp ứng ràng buộc chuyến đi"));
        (await Errors(AgentTestData.State() with { DayAreas = [far with { DayNumber = 2 }] })).Should().BeEmpty();
        (await Errors(AgentTestData.State() with { OriginMode = "pinned", OriginLatitude = 21.03m, OriginLongitude = 105.85m, RadiusKm = 3m, DayAreas = [near] })).Should().BeEmpty();
    }

    // TEST-19: A widened meal accepts a dish within its wider radius; another meal of that day, or a dish past that radius, is still rejected.
    /// <summary>Ensures widening one meal never loosens the rest of the day, and passes the dish the search issued.</summary>
    [Fact]
    public async Task ValidateAsync_WidenedMeal_AppliesOnlyToThatMeal()
    {
        // STEP-01: Unpack (validator, _, _, _) from AgentTestData.Validator(), create place as AgentDayArea and define helper Errors.
        var (validator, _, _, _) = AgentTestData.Validator(AgentTestData.Dish());
        // Món nằm cách tâm khoảng 5,6 km: ngoài 3 km, trong 8 km.
        var place = new AgentDayArea(1, "Gần", 16.10m, 108.2m, 3m);
        async Task<string[]> Errors(AgentWidenedMeal[]? widened)
        {
            var state = AgentTestData.State() with { DayAreas = [place], WidenedMeals = widened };
            var snapshot = new AgentSnapshot(state, [AgentTestData.Item()], [new(null, null, [1], null, state)]);
            return (await validator.ValidateAsync(snapshot, null, false, false, default, [new(null, null, [1], null, state)])).Validation.Errors;
        }

        // STEP-02: Assert Errors() contains a matching item and Errors() is empty.
        (await Errors(null)).Should().Contain(error => error.Contains("không đáp ứng ràng buộc chuyến đi"));
        (await Errors([new(1, "lunch", 8m)])).Should().BeEmpty();
        (await Errors([new(1, "dinner", 8m)])).Should().Contain(error => error.Contains("không đáp ứng ràng buộc chuyến đi"));
        (await Errors([new(1, "lunch", 5m)])).Should().Contain(error => error.Contains("không đáp ứng ràng buộc chuyến đi"));
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
