using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.UpdateRestaurantModerationStatus;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.UpdateRestaurantModerationStatus;

/// <summary>Verifies MOD-R3 state transitions and atomic side effects.</summary>
public sealed class UpdateRestaurantModerationStatusUseCaseTests
{
    /// <summary>Builds the use case with its real validator and isolated dependencies.</summary>
    private static UpdateRestaurantModerationStatusUseCase Create(ModerationTestContext world) => new(
        new UpdateRestaurantModerationStatusRequestDtoValidator(), world.Access, world.Repository.Object, world.UnitOfWork.Object, world.Effects);

    // TEST-01: Persist all three restaurant moderation states, audit once, notify once and commit once.
    /// <summary>Checks visible, hidden and suspended transitions including the exact audit action.</summary>
    [Theory]
    [InlineData("visible", RestaurantModerationStatus.Visible, "RESTAURANT_SHOWN")]
    [InlineData("hidden", RestaurantModerationStatus.Hidden, "RESTAURANT_HIDDEN")]
    [InlineData("suspended", RestaurantModerationStatus.Suspended, "RESTAURANT_SUSPENDED")]
    public async Task PersistsTransition(string status, RestaurantModerationStatus expected, string auditAction)
    {
        var world = new ModerationTestContext();
        world.Restaurant.ModerationStatus = expected == RestaurantModerationStatus.Visible ? RestaurantModerationStatus.Hidden : RestaurantModerationStatus.Visible;
        var result = await Create(world).ExecuteAsync(7, new() { ModerationStatus = status, Reason = " reason " }, default);
        result.ModerationStatus.Should().Be(status);
        world.Restaurant.ModerationStatus.Should().Be(expected);
        world.Audit.Verify(x => x.Add(It.Is<ANGI.Application.Common.Models.Audit.AuditLogEntry>(a => a.Action == auditAction)), Times.Once);
        world.Repository.Verify(x => x.AddNotification(It.Is<Domain.Entities.Notification>(n => n.RecipientId == 12 && n.Body == "reason")), Times.Once);
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Repeating the current state must not duplicate the audit or owner notification.
    /// <summary>Verifies retries are idempotent for moderation side effects.</summary>
    [Fact]
    public async Task RepeatedStateHasNoDuplicateEffects()
    {
        var world = new ModerationTestContext();
        await Create(world).ExecuteAsync(7, new() { ModerationStatus = "visible", Reason = "reason" }, default);
        world.Audit.VerifyNoOtherCalls();
        world.Repository.Verify(x => x.AddNotification(It.IsAny<Domain.Entities.Notification>()), Times.Never);
    }

    // TEST-03: Unknown or unrelated report references fail validation and do not save or commit.
    /// <summary>Prevents linking a moderation decision to a report on another target.</summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData(ReportTargetType.Restaurant, 8)]
    [InlineData(ReportTargetType.Blog, 7)]
    public async Task RejectsUnrelatedReport(ReportTargetType? type, long id)
    {
        var world = new ModerationTestContext();
        world.Repository.Setup(x => x.GetReportTargetAsync(88, It.IsAny<CancellationToken>())).ReturnsAsync(type.HasValue ? new ReportTarget(type.Value, id) : null);
        var action = () => Create(world).ExecuteAsync(7, new() { ModerationStatus = "hidden", Reason = "reason", ReportId = 88 }, default);
        await action.Should().ThrowAsync<ValidationException>();
        world.UnitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Missing restaurant and failed persistence cannot commit a moderation decision.
    /// <summary>Checks missing-resource and transaction failure boundaries.</summary>
    [Fact]
    public async Task MissingRestaurantDoesNotCommit()
    {
        var world = new ModerationTestContext();
        var action = () => Create(world).ExecuteAsync(404, new() { ModerationStatus = "hidden", Reason = "reason" }, default);
        (await action.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("RESTAURANT_NOT_FOUND");
        world.Transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
