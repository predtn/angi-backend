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
        // STEP-01: Create world as ModerationTestContext, get report from Setup and create cancellation as CancellationTokenSource.
        var world = new ModerationTestContext();
        var report = Setup(world, type);
        using var cancellation = new CancellationTokenSource();
        // STEP-02: Run Create().ExecuteAsync and keep response.
        var response = await Create(world).ExecuteAsync(88, cancellation.Token);
        // STEP-03: Assert report.Status is ReportStatus.InReview, report.HandledBy is 3, report.HandledAt is null and report.ResolutionAction is null.
        report.Status.Should().Be(ReportStatus.InReview);
        report.HandledBy.Should().Be(3);
        report.HandledAt.Should().BeNull();
        report.ResolutionAction.Should().BeNull();
        // STEP-04: Assert response.Status is "in_review" and response.HandledBy.Id is 3.
        response.Status.Should().Be("in_review");
        response.HandledBy!.Id.Should().Be(3);
        // STEP-05: Verify LockReportAsync is called once, SaveChangesAsync is called once, CommitAsync is called once and DisposeAsync is called once.
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
        // STEP-01: Create world as ModerationTestContext, get report from Setup, set report.Status to status and set report.HandledBy to handler.
        var world = new ModerationTestContext();
        var report = Setup(world);
        report.Status = status;
        report.HandledBy = handler;
        // STEP-02: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, default);
        // STEP-03: Assert action throws ConflictException, the thrown exception.ErrorCode is code, report.HandledBy is handler and report.Status is status.
        (await action.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be(code);
        report.HandledBy.Should().Be(handler);
        report.Status.Should().Be(status);
        // STEP-04: Verify SaveChangesAsync is never called, CommitAsync is never called and DisposeAsync is called once.
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
        // STEP-01: Create world as ModerationTestContext and 1 more.
        var world = new ModerationTestContext();
        if (missingAfterLock)
        {
            Setup(world);
            world.Repository.Setup(x => x.LockReportAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync((Report?)null);
        }
        // STEP-02: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, default);
        // STEP-03: Assert action throws NotFoundException and the thrown exception.ErrorCode is "REPORT_NOT_FOUND".
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("REPORT_NOT_FOUND");
        // STEP-04: Verify SaveChangesAsync is never called.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: An unrelated report permission cannot claim the other target type.
    /// <summary>Checks target permission boundaries before transaction creation or content access.</summary>
    [Theory]
    [InlineData(ReportTargetType.Restaurant, "REPORT_SOCIAL_HANDLE")]
    [InlineData(ReportTargetType.Blog, "REPORT_RESTAURANT_HANDLE")]
    public async Task RejectsWrongTargetPermission(ReportTargetType type, string permission)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock and world.GetEffectivePermissionsAsync.
        Setup(world, type);
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>())).ReturnsAsync([permission]);
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, default);
        // STEP-04: Assert action throws ForbiddenException.
        await action.Should().ThrowAsync<ForbiddenException>();
        // STEP-05: Verify world receives no other calls.
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-05: Save, response-mapping and commit failures dispose the transaction and never report success.
    /// <summary>Checks rollback boundaries for every operation after the report has been locked.</summary>
    [Theory]
    [InlineData("save")] [InlineData("display")] [InlineData("commit")]
    public async Task DisposesFailedTransaction(string phase)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock.
        Setup(world);
        // STEP-03: When phase == "save", call world.UnitOfWork.Setup(filtered).ThrowsAsync and 2 more.
        if (phase == "save") world.UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        if (phase == "display") world.Repository.Setup(x => x.GetReportDisplayDataAsync(It.IsAny<ReportTarget>(), It.IsAny<IReadOnlyList<Report>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        if (phase == "commit") world.Transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException());
        // STEP-04: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, default);
        // STEP-05: Assert action throws InvalidOperationException.
        await action.Should().ThrowAsync<InvalidOperationException>();
        // STEP-06: Verify DisposeAsync is called once and CommitAsync is never called.
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), phase == "commit" ? Times.Once() : Times.Never());
    }

    // TEST-06: Caller cancellation propagates without saving or committing a claim.
    /// <summary>Checks that cancellation remains observable across the database-lock boundary.</summary>
    [Fact]
    public async Task PropagatesCancellation()
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock.
        Setup(world);
        // STEP-03: Create cancellation as CancellationTokenSource and cancel cancellation.
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // STEP-04: Stub world.LockReportAsync.
        world.Repository.Setup(x => x.LockReportAsync(88, cancellation.Token)).ThrowsAsync(new OperationCanceledException(cancellation.Token));
        // STEP-05: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, cancellation.Token);
        // STEP-06: Assert action throws OperationCanceledException.
        await action.Should().ThrowAsync<OperationCanceledException>();
        // STEP-07: Verify SaveChangesAsync is never called and DisposeAsync is called once.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-07: Reject claim when another moderator has already claimed the report with 409 REPORT_ALREADY_CLAIMED.
    /// <summary>Checks Exception Path 1: 409 Conflict when claiming a report already taken by another moderator.</summary>
    [Fact]
    public async Task RejectsClaimWhenAlreadyClaimedByAnotherModerator()
    {
        // STEP-01: Create world as ModerationTestContext, get report from Setup, set report.Status to ReportStatus.InReview and set report.HandledBy to 999.
        var world = new ModerationTestContext();
        var report = Setup(world);
        report.Status = ReportStatus.InReview;
        report.HandledBy = 999; // Claimed by another moderator
        // STEP-02: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, default);
        // STEP-03: Assert action throws ConflictException and the thrown exception.ErrorCode is "REPORT_ALREADY_CLAIMED".
        (await action.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("REPORT_ALREADY_CLAIMED");
        // STEP-04: Verify SaveChangesAsync is never called and CommitAsync is never called.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-08: Reject claim when reportId is invalid with 404 REPORT_NOT_FOUND.
    /// <summary>Checks Exception Path 3: 404 Not Found when claiming an invalid reportId.</summary>
    [Fact]
    public async Task RejectsClaimWhenReportNotFound()
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub world.GetReportTargetAsync.
        world.Repository.Setup(x => x.GetReportTargetAsync(99999, It.IsAny<CancellationToken>())).ReturnsAsync((ReportTarget?)null);
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(99999, default);
        // STEP-04: Assert action throws NotFoundException and the thrown exception.ErrorCode is "REPORT_NOT_FOUND".
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("REPORT_NOT_FOUND");
        // STEP-05: Verify SaveChangesAsync is never called.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
