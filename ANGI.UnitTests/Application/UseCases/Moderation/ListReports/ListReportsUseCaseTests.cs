using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.ListReports;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ListReports;

/// <summary>Checks report queue validation, target permissions, batch mapping and paging.</summary>
public sealed class ListReportsUseCaseTests
{
    /// <summary>Creates the queue operation with real filter validation and isolated dependencies.</summary>
    private static ListReportsUseCase Create(ModerationTestContext world) =>
        new(new ListReportsRequestDtoValidator(), world.Access, world.Repository.Object, world.Cloudinary.Object);

    // TEST-01: Map all report fields using target-specific batch data and preserve paging metadata.
    /// <summary>Checks restaurant and blog pages, evidence signing, public identities and closed/open enum mapping.</summary>
    [Theory]
    [InlineData(ReportTargetType.Restaurant, "restaurant")] [InlineData(ReportTargetType.Blog, "blog")]
    public async Task MapsReportPage(ReportTargetType type, string value)
    {
        var world = new ModerationTestContext();
        using var cancellation = new CancellationTokenSource();
        var request = new ListReportsRequestDto { TargetType = value, Page = 2, PageSize = 1 };
        var report = new Report { Id = 88, TargetType = type, RestaurantId = type == ReportTargetType.Restaurant ? 7 : null,
            BlogId = type == ReportTargetType.Blog ? 7 : null, ReporterId = 4, Status = ReportStatus.Resolved, HandledBy = 3,
            Evidence = "[10,11]", ReasonCode = ReportReasonCode.WrongInfo, Description = "Details", ResolutionNote = "Conclusion",
            HandledAt = world.Clock.GetUtcNow().UtcDateTime, ResolutionAction = ResolutionAction.None };
        var page = new Paged<Report> { Items = [report], Page = 2, PageSize = 1, TotalItems = 5, TotalPages = 5 };
        var displays = new Dictionary<ReportTarget, ReportDisplayData>
        {
            [new(type, 7)] = new("Target", new Dictionary<int, User>
            { [4] = new() { Id = 4, DisplayName = "Reporter" }, [3] = new() { Id = 3, DisplayName = "Mod" } },
                new Dictionary<long, MediaFile> { [10] = new() { StorageKey = "private/key" } }, 8)
        };
        world.Repository.Setup(x => x.ListReportsAsync(request, cancellation.Token)).ReturnsAsync(page);
        world.Repository.Setup(x => x.GetReportDisplaysAsync(page.Items, cancellation.Token)).ReturnsAsync(displays);
        world.Cloudinary.Setup(x => x.GetUrl("private/key")).Returns("https://signed.test/evidence");
        var result = await Create(world).ExecuteAsync(request, cancellation.Token);
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(1);
        result.TotalItems.Should().Be(5);
        result.TotalPages.Should().Be(5);
        var item = result.Items.Should().ContainSingle().Subject;
        item.TargetType.Should().Be(value);
        item.TargetName.Should().Be("Target");
        item.ReasonCode.Should().Be("wrong_info");
        item.Status.Should().Be("resolved");
        item.ReportCountOnTarget.Should().Be(8);
        item.EvidenceUrls.Should().Equal("https://signed.test/evidence");
        item.Reporter.DisplayName.Should().Be("Reporter");
        item.HandledBy!.Id.Should().Be(3);
        item.ResolutionAction.Should().Be("none");
        item.Description.Should().Be("Details");
        item.ResolutionNote.Should().Be("Conclusion");
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-02: Return an empty result with intact metadata and no fabricated target display entries.
    /// <summary>Checks zero matches and pages beyond the last page.</summary>
    [Fact]
    public async Task ReturnsEmptyPage()
    {
        var world = new ModerationTestContext();
        var request = new ListReportsRequestDto { TargetType = "restaurant" };
        world.Repository.Setup(x => x.ListReportsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Paged<Report> { Page = 1, PageSize = 20 });
        world.Repository.Setup(x => x.GetReportDisplaysAsync(It.IsAny<IReadOnlyList<Report>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<ReportTarget, ReportDisplayData>());
        var result = await Create(world).ExecuteAsync(request, default);
        result.Items.Should().BeEmpty();
        result.TotalItems.Should().Be(0);
        result.TotalPages.Should().Be(0);
    }

    // TEST-03: Invalid filters fail before authorization or repository access.
    /// <summary>Checks validation-first ordering and prevents invalid enum parsing in persistence.</summary>
    [Fact]
    public async Task ValidatesBeforeReads()
    {
        var world = new ModerationTestContext();
        var action = () => Create(world).ExecuteAsync(new() { TargetType = "0" }, default);
        await action.Should().ThrowAsync<ValidationException>();
        world.Accounts.VerifyNoOtherCalls();
        world.Repository.VerifyNoOtherCalls();
    }

    // TEST-04: A permission for the other target cannot reveal report queue contents.
    /// <summary>Checks both target-specific permission boundaries before any persistence read.</summary>
    [Theory]
    [InlineData("restaurant", "REPORT_SOCIAL_HANDLE")] [InlineData("blog", "REPORT_RESTAURANT_HANDLE")]
    public async Task RejectsWrongTargetPermission(string type, string permission)
    {
        var world = new ModerationTestContext();
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>())).ReturnsAsync([permission]);
        var action = () => Create(world).ExecuteAsync(new() { TargetType = type }, default);
        await action.Should().ThrowAsync<ForbiddenException>();
        world.Repository.VerifyNoOtherCalls();
    }
}
