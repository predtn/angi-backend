using ANGI.Application.Common.Exceptions;
using ANGI.Application.UseCases.Moderation.GetModRestaurant;
using ANGI.Domain.Entities;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetModRestaurant;

/// <summary>Verifies the moderator detail contract and missing-resource behavior.</summary>
public sealed class GetModRestaurantUseCaseTests
{
    // TEST-01: Return owner and workflow fields alongside empty report, menu and audit collections.
    /// <summary>Checks the OwnerRestaurant extension and null optional associations.</summary>
    [Fact]
    public async Task MapsCompleteDetail()
    {
        // STEP-01: Create world as ModerationTestContext and add a RestaurantImage to world.Restaurant.Images.
        var world = new ModerationTestContext();
        world.Restaurant.Images.Add(new RestaurantImage { MediaId = 101, SortOrder = 1, Media = new MediaFile { Id = 101, Url = "https://cdn.test/b.jpg" } });
        world.Restaurant.Images.Add(new RestaurantImage { MediaId = 102, SortOrder = 0, Media = new MediaFile { Id = 102, Url = "https://cdn.test/a.jpg" } });
        // STEP-02: Stub world.GetDetailAsync, world.GetRecentReportsAsync and world.GetRecentAuditAsync.
        world.Repository.Setup(x => x.GetDetailAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(world.Restaurant);
        world.Repository.Setup(x => x.GetRecentReportsAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        world.Repository.Setup(x => x.GetRecentAuditAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // STEP-03: Run new GetModRestaurantUseCase().ExecuteAsync and keep result.
        var result = await new GetModRestaurantUseCase(world.Access, world.Repository.Object, world.Cloudinary.Object, world.Clock).ExecuteAsync(7, default);
        // STEP-04: Assert result.Id is 7, result.Owner.Role is "RESTAURANT_OWNER", result.Owner.Id is 12, result.Dishes is empty and 5 more.
        result.Id.Should().Be(7);
        result.Owner.Role.Should().Be("RESTAURANT_OWNER");
        result.Owner.Id.Should().Be(12);
        result.Dishes.Should().BeEmpty();
        result.Reports.Should().BeEmpty();
        result.RecentAudit.Should().BeEmpty();
        result.LatestVerification.Should().BeNull();
        result.Images.Should().Equal("https://cdn.test/a.jpg", "https://cdn.test/b.jpg");
        result.ImageMediaIds.Should().Equal(102, 101);
    }

    // TEST-02: Return RESTAURANT_NOT_FOUND and avoid loading child data for missing restaurants.
    /// <summary>Verifies the documented MOD-R2 missing-resource code.</summary>
    [Fact]
    public async Task RejectsMissingRestaurant()
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Wrap new GetModRestaurantUseCase().ExecuteAsync in action so its outcome can be asserted.
        var action = () => new GetModRestaurantUseCase(world.Access, world.Repository.Object, world.Cloudinary.Object, world.Clock).ExecuteAsync(404, default);
        // STEP-03: Assert action throws NotFoundException and the thrown exception.ErrorCode is "RESTAURANT_NOT_FOUND".
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        // STEP-04: Verify GetRecentReportsAsync is never called.
        world.Repository.Verify(x => x.GetRecentReportsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
