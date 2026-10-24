using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Moderation.GetReport;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetReport;

/// <summary>Checks report detail authorization, target shape, related reports and missing resources.</summary>
public sealed class GetReportUseCaseTests
{
    /// <summary>Creates a read-only detail operation with a fixed clock.</summary>
    private static GetReportUseCase Create(ModerationTestContext world) =>
        new(world.Access, world.Repository.Object, world.Cloudinary.Object, world.Clock);

    /// <summary>Configures report, target and empty related-report data for an authorized detail request.</summary>
    private static void Setup(ModerationTestContext world, ReportTargetType type)
    {
        world.Repository.Setup(x => x.GetReportTargetAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(new ReportTarget(type, 7));
        world.Repository.Setup(x => x.GetReportAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(new Report
        { Id = 88, ReporterId = 4, TargetType = type, RestaurantId = type == ReportTargetType.Restaurant ? 7 : null, BlogId = type == ReportTargetType.Blog ? 7 : null });
        world.Repository.Setup(x => x.GetRelatedReportsAsync(new ReportTarget(type, 7), 88, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        world.Repository.Setup(x => x.GetReportRestaurantAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(world.Restaurant);
        world.Repository.Setup(x => x.GetReportBlogAsync(7, 3, It.IsAny<CancellationToken>())).ReturnsAsync(new Blog
        {
            Id = 7, AuthorId = 3, Author = new User { Id = 3, DisplayName = "Author" }, Title = "Blog", Slug = "blog",
            Content = new string('a', 201), ViewCount = 8, Likes = [new() { UserId = 3 }],
            RoadmapId = 9, RoadmapSnapshot = "{\"roadmapId\":9,\"title\":\"Snapshot\",\"days\":[]}",
            Images = [new() { MediaId = 2, SortOrder = 1, Media = new() { Url = "second" } },
                new() { MediaId = 1, SortOrder = 0, Media = new() { Url = "first" } }]
        });
    }

    // TEST-01: Return the base Report fields, the exact RestaurantCard target and other open reports.
    /// <summary>Checks detail mapping and propagation of identifiers and cancellation to each read.</summary>
    [Fact]
    public async Task ReturnsRestaurantDetail()
    {
        var world = new ModerationTestContext();
        Setup(world, ReportTargetType.Restaurant);
        using var cancellation = new CancellationTokenSource();
        world.Repository.Setup(x => x.GetRelatedReportsAsync(new ReportTarget(ReportTargetType.Restaurant, 7), 88, cancellation.Token))
            .ReturnsAsync([new() { Id = 89, ReporterId = 4, RestaurantId = 7, TargetType = ReportTargetType.Restaurant, Status = ReportStatus.InReview }]);
        var result = await Create(world).ExecuteAsync(88, cancellation.Token);
        result.Id.Should().Be(88);
        result.Reporter.Id.Should().Be(4);
        var target = result.Target.Should().BeOfType<RestaurantCardDto>().Subject;
        target.Id.Should().Be(7);
        target.Name.Should().Be("Restaurant");
        result.RelatedReports.Should().ContainSingle(report => report.Id == 89 && report.Status == "in_review");
        world.Repository.Verify(x => x.GetReportAsync(88, cancellation.Token), Times.Once);
        world.Repository.Verify(x => x.GetReportRestaurantAsync(7, cancellation.Token), Times.Once);
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-02: Return BlogDetail with ordered images, the stored snapshot and caller flags without increasing views.
    /// <summary>Checks the shared social target branch, excerpt boundary and immutable snapshot deserialization.</summary>
    [Fact]
    public async Task ReturnsBlogDetail()
    {
        var world = new ModerationTestContext();
        Setup(world, ReportTargetType.Blog);
        var result = await Create(world).ExecuteAsync(88, default);
        var target = result.Target.Should().BeOfType<BlogDetailDto>().Subject;
        target.Title.Should().Be("Blog");
        target.Excerpt.Should().HaveLength(200);
        target.Content.Should().HaveLength(201);
        target.Images.Should().Equal("first", "second");
        target.ViewCount.Should().Be(8);
        target.LikedByMe.Should().BeTrue();
        target.IsMine.Should().BeTrue();
        target.HasRoadmap.Should().BeTrue();
        target.RoadmapSnapshot!.Title.Should().Be("Snapshot");
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-03: Reject a missing report or missing target with the documented resource error.
    /// <summary>Checks absent reports and soft-deleted restaurant/blog targets without writes.</summary>
    [Theory]
    [InlineData("report", "REPORT_NOT_FOUND")] [InlineData("row", "REPORT_NOT_FOUND")]
    [InlineData("restaurant", "RESTAURANT_NOT_FOUND")] [InlineData("blog", "BLOG_NOT_FOUND")]
    public async Task RejectsMissingResources(string missing, string code)
    {
        var world = new ModerationTestContext();
        if (missing != "report") Setup(world, missing == "blog" ? ReportTargetType.Blog : ReportTargetType.Restaurant);
        if (missing == "row") world.Repository.Setup(x => x.GetReportAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync((Report?)null);
        if (missing == "restaurant") world.Repository.Setup(x => x.GetReportRestaurantAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync((Domain.Entities.Restaurant?)null);
        if (missing == "blog") world.Repository.Setup(x => x.GetReportBlogAsync(7, 3, It.IsAny<CancellationToken>())).ReturnsAsync((Blog?)null);
        var action = () => Create(world).ExecuteAsync(88, default);
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be(code);
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-04: Reject a permission for the other target before reading report content or evidence.
    /// <summary>Prevents cross-target data disclosure by a Mod with only one report permission.</summary>
    [Theory]
    [InlineData(ReportTargetType.Restaurant, "REPORT_SOCIAL_HANDLE")]
    [InlineData(ReportTargetType.Blog, "REPORT_RESTAURANT_HANDLE")]
    public async Task RejectsWrongTargetPermission(ReportTargetType type, string permission)
    {
        var world = new ModerationTestContext();
        Setup(world, type);
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>())).ReturnsAsync([permission]);
        var action = () => Create(world).ExecuteAsync(88, default);
        await action.Should().ThrowAsync<ForbiddenException>();
        world.Repository.Verify(x => x.GetReportAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        world.Repository.Verify(x => x.GetReportDisplayDataAsync(It.IsAny<ReportTarget>(), It.IsAny<IReadOnlyList<Report>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
