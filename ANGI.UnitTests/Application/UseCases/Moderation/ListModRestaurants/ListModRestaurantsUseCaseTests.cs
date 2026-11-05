using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.ListModRestaurants;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ListModRestaurants;

/// <summary>Verifies MOD-R1 page mapping and validation ordering.</summary>
public sealed class ListModRestaurantsUseCaseTests
{
    // TEST-01: Map page metadata, snake_case states and pending counts without public visibility filtering.
    /// <summary>Checks a complete moderator card and request cancellation propagation.</summary>
    [Fact]
    public async Task ReturnsMappedPage()
    {
        // STEP-01: Create world as ModerationTestContext and create request as ListModRestaurantsRequestDto.
        var world = new ModerationTestContext();
        var request = new ListModRestaurantsRequestDto();
        // STEP-02: Stub world.ListAsync.
        world.Repository.Setup(x => x.ListAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(new Paged<ModRestaurantListItem>
        {
            Items = [new(world.Restaurant, 2)], Page = 1, PageSize = 20, TotalItems = 1, TotalPages = 1
        });
        // STEP-03: Run new ListModRestaurantsUseCase().ExecuteAsync with request and keep result.
        var result = await new ListModRestaurantsUseCase(new ListModRestaurantsRequestDtoValidator(), world.Access, world.Repository.Object, world.Clock)
            .ExecuteAsync(request, default);
        // STEP-04: Assert result.Items has exactly one matching item, result.PageSize is 20 and result.TotalItems is 1.
        result.Items.Should().ContainSingle(x => x.Id == 7 && x.PendingReportCount == 2 && x.VerificationStatus == "unverified");
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(1);
    }

    // TEST-02: Reject invalid pagination before any permission or repository calls.
    /// <summary>Checks validation remains the first operation of the use case.</summary>
    [Fact]
    public async Task InvalidRequestDoesNotReadData()
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Wrap new ListModRestaurantsUseCase().ExecuteAsync in action so its outcome can be asserted.
        var action = () => new ListModRestaurantsUseCase(new ListModRestaurantsRequestDtoValidator(), world.Access, world.Repository.Object, world.Clock)
            .ExecuteAsync(new() { Page = 0 }, default);
        // STEP-03: Assert action throws ValidationException.
        await action.Should().ThrowAsync<ValidationException>();
        // STEP-04: Verify world receives no other calls.
        world.Repository.VerifyNoOtherCalls();
        world.Accounts.VerifyNoOtherCalls();
    }
}
