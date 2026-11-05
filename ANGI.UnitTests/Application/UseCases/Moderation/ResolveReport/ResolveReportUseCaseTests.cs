using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.ResolveReport;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ResolveReport;

/// <summary>Verifies report conclusions, target actions, permissions and atomic failure behavior.</summary>
public sealed class ResolveReportUseCaseTests
{
    /// <summary>Builds a resolver with real request validation and isolated persistence boundaries.</summary>
    private static ResolveReportUseCase Create(ModerationTestContext world) => new(
        new ResolveReportRequestDtoValidator(world.Clock), world.Access, world.Repository.Object, world.UnitOfWork.Object,
        world.Effects, world.Audit.Object, world.Cloudinary.Object, world.Cache.Object, world.Clock);

    /// <summary>Registers a target and its report set for the transaction under test.</summary>
    private static Report[] Setup(ModerationTestContext world, ReportTargetType type = ReportTargetType.Restaurant)
    {
        var reports = new[]
        {
            new Report { Id = 88, ReporterId = 4, TargetType = type, RestaurantId = type == ReportTargetType.Restaurant ? 7 : null,
                BlogId = type == ReportTargetType.Blog ? 7 : null },
            new Report { Id = 89, ReporterId = 5, TargetType = type, RestaurantId = type == ReportTargetType.Restaurant ? 7 : null,
                BlogId = type == ReportTargetType.Blog ? 7 : null, Status = ReportStatus.InReview, HandledBy = 6 },
            new Report { Id = 90, ReporterId = 5, TargetType = type, Status = ReportStatus.Resolved }
        };
        world.Repository.Setup(x => x.GetReportTargetAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(new ReportTarget(type, 7));
        world.Repository.Setup(x => x.LockReportsAsync(new ReportTarget(type, 7), 88, It.IsAny<CancellationToken>())).ReturnsAsync(reports);
        return reports;
    }

    // TEST-01: Resolve or dismiss open reports; default related processing closes pending and in-review reports only.
    /// <summary>Checks both outcomes and both related-report options, including audit, handler and notification effects.</summary>
    [Theory]
    [InlineData("resolved", true, ReportStatus.Resolved, 2)]
    [InlineData("dismissed", true, ReportStatus.Dismissed, 2)]
    [InlineData("resolved", false, ReportStatus.Resolved, 1)]
    [InlineData("dismissed", false, ReportStatus.Dismissed, 1)]
    public async Task ClosesSelectedReports(string outcome, bool related, ReportStatus expected, int count)
    {
        // STEP-01: Create world as ModerationTestContext and get reports from Setup.
        var world = new ModerationTestContext();
        var reports = Setup(world);
        // STEP-02: Run Create().ExecuteAsync and keep response.
        var response = await Create(world).ExecuteAsync(88, new() { Outcome = outcome, ResolutionAction = "none", ApplyToRelatedReports = related }, default);
        // STEP-03: Assert reports[0].Status is expected and reports[0].HandledBy is 3.
        reports[0].Status.Should().Be(expected);
        reports[0].HandledBy.Should().Be(3);
        // STEP-04: Assert reports[0].HandledAt is world.Clock.GetUtcNow().UtcDateTime, reports[1].Status is InReview and reports[2].Status is ReportStatus.Resolved.
        reports[0].HandledAt.Should().Be(world.Clock.GetUtcNow().UtcDateTime);
        reports[1].Status.Should().Be(related ? expected : ReportStatus.InReview);
        reports[2].Status.Should().Be(ReportStatus.Resolved);
        // STEP-05: Assert response.Status is outcome.
        response.Status.Should().Be(outcome);
        // STEP-06: Verify Add is called times and AddNotification is called 1 times.
        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a => a.Action == AuditActions.ReportResolved)), Times.Exactly(count));
        world.Repository.Verify(x => x.AddNotification(It.Is<Domain.Entities.Notification>(n => n.Type == "REPORT_RESOLVED")), Times.Exactly(count + 1));
        // STEP-07: Verify SaveChangesAsync is called once and CommitAsync is called once.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        // STEP-08: Verify world receives no other calls.
        world.Cache.VerifyNoOtherCalls();
    }

    // TEST-02: Closed reports return REPORT_CLOSED without applying actions, saving or committing.
    /// <summary>Checks both terminal report states against accidental repeated processing.</summary>
    [Theory]
    [InlineData(ReportStatus.Resolved)]
    [InlineData(ReportStatus.Dismissed)]
    public async Task RejectsClosedReports(ReportStatus status)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock.
        Setup(world)[0].Status = status;
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = "hide_restaurant" }, default);
        // STEP-04: Assert action throws ConflictException and the thrown exception.ErrorCode is "REPORT_CLOSED".
        (await action.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("REPORT_CLOSED");
        // STEP-05: Verify SaveChangesAsync is never called and world receives no other calls.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Audit.VerifyNoOtherCalls();
    }

    // TEST-03: Reject actions for the wrong target type before starting a transaction.
    /// <summary>Checks every restaurant-versus-blog action mismatch.</summary>
    [Theory]
    [InlineData(ReportTargetType.Restaurant, "hide_blog")]
    [InlineData(ReportTargetType.Restaurant, "remove_blog")]
    [InlineData(ReportTargetType.Blog, "hide_restaurant")]
    [InlineData(ReportTargetType.Blog, "suspend_restaurant")]
    public async Task RejectsMismatchedAction(ReportTargetType type, string resolutionAction)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock.
        Setup(world, type);
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = resolutionAction }, default);
        // STEP-04: Assert action throws BadRequestException and the thrown exception.ErrorCode is "ACTION_NOT_ALLOWED_FOR_TARGET".
        (await action.Should().ThrowAsync<BadRequestException>()).Which.ErrorCode.Should().Be("ACTION_NOT_ALLOWED_FOR_TARGET");
        // STEP-05: Verify world receives no other calls.
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-04: Restaurant actions update visibility and write both restaurant and report audit entries.
    /// <summary>Checks the action effects remain part of the same single save operation.</summary>
    [Theory]
    [InlineData("hide_restaurant", RestaurantModerationStatus.Hidden)]
    [InlineData("suspend_restaurant", RestaurantModerationStatus.Suspended)]
    public async Task AppliesRestaurantAction(string resolutionAction, RestaurantModerationStatus status)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock.
        Setup(world);
        // STEP-03: Run Create().ExecuteAsync.
        await Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = resolutionAction }, default);
        // STEP-04: Assert world.Restaurant.ModerationStatus is status.
        world.Restaurant.ModerationStatus.Should().Be(status);
        // STEP-05: Verify SaveChangesAsync is called once and Add is called once.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a => a.EntityType == "restaurant")), Times.Once);
    }

    // TEST-05: Blog actions hide or remove the reported blog and write the corresponding content audit.
    /// <summary>Covers both allowed blog state-changing actions of the shared resolver.</summary>
    [Theory]
    [InlineData("hide_blog", BlogStatus.Hidden, "BLOG_HIDDEN")]
    [InlineData("remove_blog", BlogStatus.Removed, "BLOG_REMOVED")]
    public async Task AppliesBlogAction(string resolutionAction, BlogStatus expected, string auditAction)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Create blog as Blog; stub the mock and world.LockBlogAsync.
        Setup(world, ReportTargetType.Blog);
        var blog = new Blog { Id = 7, AuthorId = 12 };
        world.Repository.Setup(x => x.LockBlogAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(blog);
        // STEP-03: Run Create().ExecuteAsync.
        await Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = resolutionAction }, default);
        // STEP-04: Assert blog.Status is expected.
        blog.Status.Should().Be(expected);
        // STEP-05: Verify Add is called once.
        world.Audit.Verify(x => x.Add(It.Is<AuditLogEntry>(a => a.Action == auditAction)), Times.Once);
    }

    // TEST-06: Warn preserves account status; suspend and ban revoke sessions and invalidate status after commit.
    /// <summary>Checks sanction type, UTC deadline, session revocation and cache invalidation boundaries.</summary>
    [Theory]
    [InlineData("warn_user", SanctionType.Warn, UserStatus.Active)]
    [InlineData("suspend_user", SanctionType.Suspend, UserStatus.Suspended)]
    [InlineData("ban_user", SanctionType.Ban, UserStatus.Banned)]
    public async Task AppliesAccountSanction(string resolutionAction, SanctionType expectedType, UserStatus expectedStatus)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock.
        Setup(world);
        // STEP-03: Create user as User and create session as UserSession.
        var user = new User { Id = 12, Status = UserStatus.Active, Role = new Role { Code = "RESTAURANT_OWNER" } };
        var session = new UserSession { Id = 1, UserId = 12 };
        // STEP-04: Declare committed to capture a call argument; stub world.LockUserAsync, world.GetSessionsAsync and world.CommitAsync.
        world.Repository.Setup(x => x.LockUserAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(user);
        world.Repository.Setup(x => x.GetSessionsAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync([session]);
        var committed = false;
        world.Transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).Callback(() => committed = true).Returns(Task.CompletedTask);
        // STEP-05: Assert committed is true.
        world.Cache.Setup(x => x.Invalidate(12)).Callback(() => committed.Should().BeTrue());
        // STEP-06: Run Create().ExecuteAsync.
        await Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = resolutionAction,
            SuspendUntil = world.Clock.GetUtcNow().AddHours(1).ToOffset(TimeSpan.FromHours(7)), ResolutionNote = " reason " }, default);
        // STEP-07: Assert user.Status is expectedStatus.
        user.Status.Should().Be(expectedStatus);
        // STEP-08: Verify AddSanction is called once.
        world.Repository.Verify(x => x.AddSanction(It.Is<UserSanction>(s => s.Type == expectedType && s.ReportId == 88 && s.Reason == "reason" && s.StartsAt.Kind == DateTimeKind.Utc)), Times.Once);
        // STEP-09: Assert session.RevokedAt is null and 2 more; verify Invalidate is never called and 1 more.
        if (expectedType == SanctionType.Warn)
        {
            session.RevokedAt.Should().BeNull();
            world.Cache.Verify(x => x.Invalidate(It.IsAny<int>()), Times.Never);
        }
        else
        {
            session.RevokedAt.Should().Be(world.Clock.GetUtcNow().UtcDateTime);
            user.SuspendedUntil.Should().Be(expectedType == SanctionType.Suspend ? world.Clock.GetUtcNow().AddHours(1).UtcDateTime : null);
            world.Cache.Verify(x => x.Invalidate(12), Times.Once);
        }
    }

    // TEST-07: Report-specific permissions and additional sanction permissions are enforced independently.
    /// <summary>Prevents using an unrelated report permission or lacking the required user-sanction permission.</summary>
    [Theory]
    [InlineData("REPORT_SOCIAL_HANDLE", "none")]
    [InlineData("REPORT_RESTAURANT_HANDLE", "warn_user")]
    [InlineData("REPORT_RESTAURANT_HANDLE", "suspend_user")]
    [InlineData("REPORT_RESTAURANT_HANDLE", "ban_user")]
    public async Task RejectsMissingPermissions(string permission, string resolutionAction)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock and world.GetEffectivePermissionsAsync.
        Setup(world);
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>())).ReturnsAsync([permission]);
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = resolutionAction, SuspendUntil = world.Clock.GetUtcNow().AddHours(1) }, default);
        // STEP-04: Assert action throws ForbiddenException and the thrown exception.ErrorCode is "FORBIDDEN".
        (await action.Should().ThrowAsync<ForbiddenException>()).Which.ErrorCode.Should().Be("FORBIDDEN");
        // STEP-05: Verify world receives no other calls.
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-08: Missing reports and targets return their documented resource errors without committing.
    /// <summary>Checks missing-report behavior before target locks are acquired.</summary>
    [Fact]
    public async Task MissingReportDoesNotStartTransaction()
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(404, new() { Outcome = "resolved", ResolutionAction = "none" }, default);
        // STEP-03: Assert action throws NotFoundException and the thrown exception.ErrorCode is "REPORT_NOT_FOUND".
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("REPORT_NOT_FOUND");
        // STEP-04: Verify world receives no other calls.
        world.UnitOfWork.VerifyNoOtherCalls();
    }

    // TEST-09: Persistence and commit failures do not invalidate account caches or report success.
    /// <summary>Checks failure propagation and transaction disposal on both failure paths.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureDoesNotInvalidateCache(bool failCommit)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock, world.LockUserAsync and world.GetSessionsAsync.
        Setup(world);
        world.Repository.Setup(x => x.LockUserAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(new User
        { Id = 12, Status = UserStatus.Active, Role = new Role { Code = "RESTAURANT_OWNER" } });
        world.Repository.Setup(x => x.GetSessionsAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        // STEP-03: When failCommit, call world.Transaction.Setup(filtered).ThrowsAsync.
        if (failCommit) world.Transaction.Setup(x => x.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("commit"));
        else world.UnitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("save"));
        // STEP-04: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = "ban_user" }, default);
        // STEP-05: Assert action throws InvalidOperationException.
        await action.Should().ThrowAsync<InvalidOperationException>();
        // STEP-06: Verify world receives no other calls and DisposeAsync is called once.
        world.Cache.VerifyNoOtherCalls();
        world.Transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-10: Staff accounts and already-banned users cannot receive another report sanction.
    /// <summary>Checks staff protection and banned-account conflict behavior.</summary>
    [Theory]
    [InlineData("MOD", UserStatus.Active, "CANNOT_SANCTION_STAFF")]
    [InlineData("ADMIN", UserStatus.Active, "CANNOT_SANCTION_STAFF")]
    [InlineData("RESTAURANT_OWNER", UserStatus.Banned, "USER_ALREADY_BANNED")]
    public async Task RejectsProtectedAccounts(string role, UserStatus status, string code)
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub the mock and world.LockUserAsync.
        Setup(world);
        world.Repository.Setup(x => x.LockUserAsync(12, It.IsAny<CancellationToken>())).ReturnsAsync(new User
        { Id = 12, Role = new Role { Code = role }, Status = status });
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = "ban_user" }, default);
        // STEP-04: Assert action throws AppException and the thrown exception.ErrorCode is code.
        (await action.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code);
        // STEP-05: Verify AddSanction is never called and SaveChangesAsync is never called.
        world.Repository.Verify(x => x.AddSanction(It.IsAny<UserSanction>()), Times.Never);
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-11: Successfully resolve a report claimed by the current moderator.
    /// <summary>Checks that the moderator who claimed the report can successfully resolve it.</summary>
    [Fact]
    public async Task ResolvesReportClaimedByCurrentModerator()
    {
        // STEP-01: Create world as ModerationTestContext, get reports from Setup, set reports[0].Status to ReportStatus.InReview and set reports[0].HandledBy to 3.
        var world = new ModerationTestContext();
        var reports = Setup(world);
        reports[0].Status = ReportStatus.InReview;
        reports[0].HandledBy = 3; // Claimed by current user (3)
        // STEP-02: Run Create().ExecuteAsync and keep response.
        var response = await Create(world).ExecuteAsync(88, new() { Outcome = "resolved", ResolutionAction = "none" }, default);
        // STEP-03: Assert reports[0].Status is ReportStatus.Resolved, reports[0].HandledBy is 3 and response.Status is "resolved".
        reports[0].Status.Should().Be(ReportStatus.Resolved);
        reports[0].HandledBy.Should().Be(3);
        response.Status.Should().Be("resolved");
        // STEP-04: Verify SaveChangesAsync is called once and CommitAsync is called once.
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-12: Reject with 404 NOT_FOUND when reportId does not exist.
    /// <summary>Checks Exception Path 3: 404 Not Found when reportId is invalid.</summary>
    [Fact]
    public async Task RejectsNonExistentReportWith404NotFound()
    {
        // STEP-01: Create world as ModerationTestContext.
        var world = new ModerationTestContext();
        // STEP-02: Stub world.GetReportTargetAsync.
        world.Repository.Setup(x => x.GetReportTargetAsync(99999, It.IsAny<CancellationToken>())).ReturnsAsync((ReportTarget?)null);
        // STEP-03: Wrap Create().ExecuteAsync in action so its outcome can be asserted.
        var action = () => Create(world).ExecuteAsync(99999, new() { Outcome = "resolved", ResolutionAction = "none" }, default);
        // STEP-04: Assert action throws NotFoundException and the thrown exception.ErrorCode is "REPORT_NOT_FOUND".
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("REPORT_NOT_FOUND");
        // STEP-05: Verify world receives no other calls.
        world.UnitOfWork.VerifyNoOtherCalls();
    }
}
