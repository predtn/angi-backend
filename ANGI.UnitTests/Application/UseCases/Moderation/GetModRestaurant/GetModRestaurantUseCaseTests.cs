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
        var world = new ModerationTestContext();
        world.Repository.Setup(x => x.GetDetailAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(world.Restaurant);
        world.Repository.Setup(x => x.GetRecentReportsAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        world.Repository.Setup(x => x.GetRecentAuditAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var result = await new GetModRestaurantUseCase(world.Access, world.Repository.Object, world.Cloudinary.Object, world.Clock).ExecuteAsync(7, default);
        result.Id.Should().Be(7);
        result.Owner.Role.Should().Be("RESTAURANT_OWNER");
        result.Owner.Id.Should().Be(12);
        result.Dishes.Should().BeEmpty();
        result.Reports.Should().BeEmpty();
        result.RecentAudit.Should().BeEmpty();
        result.LatestVerification.Should().BeNull();
    }

    // TEST-02: Return RESTAURANT_NOT_FOUND and avoid loading child data for missing restaurants.
    /// <summary>Verifies the documented MOD-R2 missing-resource code.</summary>
    [Fact]
    public async Task RejectsMissingRestaurant()
    {
        var world = new ModerationTestContext();
        var action = () => new GetModRestaurantUseCase(world.Access, world.Repository.Object, world.Cloudinary.Object, world.Clock).ExecuteAsync(404, default);
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        world.Repository.Verify(x => x.GetRecentReportsAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
