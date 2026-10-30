using ANGI.Application.Common.Models.Agent;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.Services.Agent;

/// <summary>Exercises catalog, provenance, budget and current-price validation boundaries.</summary>
public sealed class AgentProposalValidatorTests
{
    [Fact]
    public async Task ValidateAsync_TasteChangedAfterRanking_RequiresNewCandidates()
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var snapshot = AgentTestData.Snapshot(AgentTestData.Item());
        snapshot = snapshot with { State = snapshot.State with { TasteTargets = [new("spicy", 1)] } };
        var result = await fixture.Validator.ValidateAsync(snapshot, null, false, false, default);
        result.Validation.Errors.Should().NotBeEmpty();
    }

    // TEST-01: Reprice an issued candidate from the backend and calculate the day route before ready.
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

    // TEST-02: Reject unavailable catalog states, hard constraints, wrong scopes and forged source identity.
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

    // TEST-03: A price change or unavailable dish before confirmation cannot be silently committed.
    /// <summary>Requires a refreshed preview for any changed business price, including decreases.</summary>
    [Theory]
    [InlineData(49999)][InlineData(50001)]
    public async Task ValidateAsync_ConfirmationPriceChange_Blocks(decimal previewPrice)
    {
        var fixture = AgentTestData.Validator(AgentTestData.Dish());
        var result = await fixture.Validator.ValidateAsync(AgentTestData.Snapshot(AgentTestData.Item() with { Price = previewPrice }), null, true, false, CancellationToken.None);
        result.Validation.Errors.Should().Contain(x => x.Contains("Giá"));
    }

    // TEST-04: New replacements require a candidate grant from this turn even when an older snapshot once authorized the same ID.
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

    // TEST-05: Removing the final saved item is valid while creating an empty official roadmap from chat is rejected.
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

    // TEST-06: An unchanged source item is valid without reranking and retains its recommendation request ID.
    /// <summary>Verifies explanation/reordering paths can reuse existing provenance.</summary>
    [Fact]
    public async Task ValidateAsync_UnchangedSource_DoesNotRequireNewGrant()
    {
        var item = AgentTestData.Item() with { SourceItemId = 33, RequestId = Guid.NewGuid() };
        var source = new AgentSnapshot(AgentTestData.State(), [item], []);
        var result = await AgentTestData.Validator(AgentTestData.Dish()).Validator.ValidateAsync(source, source, false, false, CancellationToken.None);
        result.Validation.Errors.Should().BeEmpty(); result.Snapshot.Items.Single().RequestId.Should().Be(item.RequestId);
    }
}
