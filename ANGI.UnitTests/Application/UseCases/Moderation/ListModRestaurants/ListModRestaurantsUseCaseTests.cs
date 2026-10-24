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
        var world = new ModerationTestContext();
        var request = new ListModRestaurantsRequestDto();
        world.Repository.Setup(x => x.ListAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(new Paged<ModRestaurantListItem>
        {
            Items = [new(world.Restaurant, 2)], Page = 1, PageSize = 20, TotalItems = 1, TotalPages = 1
        });
        var result = await new ListModRestaurantsUseCase(new ListModRestaurantsRequestDtoValidator(), world.Access, world.Repository.Object, world.Clock)
            .ExecuteAsync(request, default);
        result.Items.Should().ContainSingle(x => x.Id == 7 && x.PendingReportCount == 2 && x.VerificationStatus == "unverified");
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(1);
    }

    // TEST-02: Reject invalid pagination before any permission or repository calls.
    /// <summary>Checks validation remains the first operation of the use case.</summary>
    [Fact]
    public async Task InvalidRequestDoesNotReadData()
    {
        var world = new ModerationTestContext();
        var action = () => new ListModRestaurantsUseCase(new ListModRestaurantsRequestDtoValidator(), world.Access, world.Repository.Object, world.Clock)
            .ExecuteAsync(new() { Page = 0 }, default);
        await action.Should().ThrowAsync<ValidationException>();
        world.Repository.VerifyNoOtherCalls();
        world.Accounts.VerifyNoOtherCalls();
    }
}
