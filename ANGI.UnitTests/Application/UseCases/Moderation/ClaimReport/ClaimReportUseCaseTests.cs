using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.UseCases.Moderation.ClaimReport;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ClaimReport;

/// <summary>Checks exclusive claims, target permissions, cancellation and transaction failure boundaries.</summary>
public sealed class ClaimReportUseCaseTests
{
    /// <summary>Builds the claim operation over isolated transaction and persistence boundaries.</summary>
    private static ClaimReportUseCase Create(ModerationTestContext world) =>
        new(world.Access, world.Repository.Object, world.UnitOfWork.Object, world.Cloudinary.Object);

    /// <summary>Registers a report whose immutable target and locked row agree.</summary>
    private static Report Setup(ModerationTestContext world, ReportTargetType type = ReportTargetType.Restaurant)
    {
        var report = new Report { Id = 88, ReporterId = 4, TargetType = type,
            RestaurantId = type == ReportTargetType.Restaurant ? 7 : null, BlogId = type == ReportTargetType.Blog ? 7 : null };
        world.Repository.Setup(x => x.GetReportTargetAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(new ReportTarget(type, 7));
        world.Repository.Setup(x => x.LockReportAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(report);
        return report;
    }

    // TEST-01: Claim a pending restaurant or blog report once with the exact caller and cancellation token.
    /// <summary>Checks single-save semantics, handler assignment, unchanged resolution fields and successful commit.</summary>
    [Theory]
    [InlineData(ReportTargetType.Restaurant)] [InlineData(ReportTargetType.Blog)]
    public async Task ClaimsPendingReport(ReportTargetType type)
    {
        var world = new ModerationTestContext();
        var report = Setup(world, type);
        using var cancellation = new CancellationTokenSource();
        var response = await Create(world).ExecuteAsync(88, cancellation.Token);
        report.Status.Should().Be(ReportStatus.InReview);
        report.HandledBy.Should().Be(3);
        report.HandledAt.Should().BeNull();
        report.ResolutionAction.Should().BeNull();
        response.Status.Should().Be("in_review");
        response.HandledBy!.Id.Should().Be(3);
        world.Repository.Verify(x => x.LockReportAsync(88, cancellation.Token), Times.Once);
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(cancellation.Token), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(cancellation.Token), Times.Once);
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-02: Reject all non-pending statuses including a repeat claim by the same moderator.
    /// <summary>Checks state-specific conflicts and guarantees no reassignment, save or commit.</summary>
    [Theory]
    [InlineData(ReportStatus.InReview, 3, "REPORT_ALREADY_CLAIMED")]
    [InlineData(ReportStatus.InReview, 6, "REPORT_ALREADY_CLAIMED")]
    [InlineData(ReportStatus.Resolved, 6, "REPORT_CLOSED")]
    [InlineData(ReportStatus.Dismissed, 6, "REPORT_CLOSED")]
    public async Task RejectsNonPendingReports(ReportStatus status, int handler, string code)
    {
        var world = new ModerationTestContext();
        var report = Setup(world);
        report.Status = status;
        report.HandledBy = handler;
        var action = () => Create(world).ExecuteAsync(88, default);
        (await action.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be(code);
        report.HandledBy.Should().Be(handler);
        report.Status.Should().Be(status);
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-03: Reject missing reports before or after acquiring the transaction without saving.
    /// <summary>Checks missing-resource behavior including a concurrent deletion between target lookup and locking.</summary>
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectsMissingReports(bool missingAfterLock)
    {
        var world = new ModerationTestContext();
        if (missingAfterLock)
        {
            Setup(world);
            world.Repository.Setup(x => x.LockReportAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync((Report?)null);
        }
        var action = () => Create(world).ExecuteAsync(88, default);
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("REPORT_NOT_FOUND");
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: An unrelated report permission cannot claim the other target type.
    /// <summary>Checks target permission boundaries before transaction creation or content access.</summary>
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
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-05: Save, response-mapping and commit failures dispose the transaction and never report success.
    /// <summary>Checks rollback boundaries for every operation after the report has been locked.</summary>
    [Theory]
    [InlineData("save")] [InlineData("display")] [InlineData("commit")]
    public async Task DisposesFailedTransaction(string phase)
    {
        var world = new ModerationTestContext();
        Setup(world);
        if (phase == "save") world.UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        if (phase == "display") world.Repository.Setup(x => x.GetReportDisplayDataAsync(It.IsAny<ReportTarget>(), It.IsAny<IReadOnlyList<Report>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        if (phase == "commit") world.Transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        var action = () => Create(world).ExecuteAsync(88, default);
        await action.Should().ThrowAsync<InvalidOperationException>();
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), phase == "commit" ? Times.Once() : Times.Never());
    }

    // TEST-06: Caller cancellation propagates without saving or committing a claim.
    /// <summary>Checks that cancellation remains observable across the database-lock boundary.</summary>
    [Fact]
    public async Task PropagatesCancellation()
    {
        var world = new ModerationTestContext();
        Setup(world);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        world.Repository.Setup(x => x.LockReportAsync(88, cancellation.Token)).ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var action = () => Create(world).ExecuteAsync(88, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-07: Reject claim when another moderator has already claimed the report with 409 REPORT_ALREADY_CLAIMED.
    /// <summary>Checks Exception Path 1: 409 Conflict when claiming a report already taken by another moderator.</summary>
    [Fact]
    public async Task RejectsClaimWhenAlreadyClaimedByAnotherModerator()
    {
        var world = new ModerationTestContext();
        var report = Setup(world);
        report.Status = ReportStatus.InReview;
        report.HandledBy = 999; // Claimed by another moderator
        var action = () => Create(world).ExecuteAsync(88, default);
        (await action.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("REPORT_ALREADY_CLAIMED");
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Reject claim when reportId is invalid with 404 REPORT_NOT_FOUND.
    /// <summary>Checks Exception Path 3: 404 Not Found when claiming an invalid reportId.</summary>
    [Fact]
    public async Task RejectsClaimWhenReportNotFound()
    {
        var world = new ModerationTestContext();
        world.Repository.Setup(x => x.GetReportTargetAsync(99999, It.IsAny<CancellationToken>())).ReturnsAsync((ReportTarget?)null);
        var action = () => Create(world).ExecuteAsync(99999, default);
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("REPORT_NOT_FOUND");
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
