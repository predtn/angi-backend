using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.RejectMenuSubmission;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.RejectMenuSubmission;

/// <summary>Verifies reject state transitions, atomic side effects, errors and cancellation.</summary>
public sealed class RejectMenuSubmissionUseCaseTests
{
    private readonly Mock<IMenuReviewRepository> _repository = new();
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IUnitOfWorkTransaction> _transaction = new();
    private readonly Mock<IAuditLogService> _audit = new();
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Provides an authenticated reviewer, successful permission check and transactional persistence.</summary>
    public RejectMenuSubmissionUseCaseTests()
    {
        _currentUser.SetupGet(x => x.UserId).Returns(10);
        _currentUser.SetupGet(x => x.Role).Returns("MOD");
        _repository.Setup(x => x.HasPermissionAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _unitOfWork.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_transaction.Object);
    }

    // TEST-01: Reject a pending submission without changing any live dish or proposal.
    /// <summary>Verifies successful review metadata, notification, audit and exactly one save and commit.</summary>
    [Fact]
    public async Task ExecuteAsync_Pending_ShouldPersistCompleteDecision()
    {
        var submission = Seed();
        _repository.Setup(x => x.LockAsync(14, It.IsAny<CancellationToken>())).ReturnsAsync(submission);
        var result = await Create().ExecuteAsync(14, new RejectMenuSubmissionRequestDto { ReviewNote = "Reviewed" }, CancellationToken.None);

        result.Status.Should().Be("rejected");
        result.ReviewedAt.Should().Be(Now);
        submission.ReviewedBy.Should().Be(10);
        result.ReviewNote.Should().Be("Reviewed");
        submission.Restaurant.Dishes.Select(x => x.Name).Should().Equal("Original", "Same", "Removed");
        submission.Items.Should().OnlyContain(x => x.ResultDishId == null);
        _repository.Verify(x => x.AddDish(It.IsAny<MenuSubmissionItem>(), It.IsAny<Dish>()), Times.Never);
        _repository.Verify(x => x.ReplaceTags(It.IsAny<Dish>(), It.IsAny<IReadOnlyCollection<short>>()), Times.Never);
        _repository.Verify(x => x.AddNotification(It.Is<ANGI.Domain.Entities.Notification>(n =>
            n.RecipientId == 20 && n.Type == "MENU_REJECTED" && n.Body == "Reviewed")), Times.Once);
        _audit.Verify(x => x.Add(It.Is<AuditLogEntry>(e => e.Action == AuditActions.MenuRejected &&
            e.EntityId == 14 && e.EntityType == AuditEntityTypes.MenuSubmission && e.SubjectUserId == 20)), Times.Once);
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        _transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-02: Reject draft, approved, rejected and cancelled submissions without saving or side effects.
    /// <summary>Verifies every non-pending state returns SUBMISSION_NOT_PENDING and disposes the transaction.</summary>
    [Theory]
    [InlineData(MenuSubmissionStatus.Draft)]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_NonPending_ShouldConflict(MenuSubmissionStatus status)
    {
        var submission = Seed();
        submission.Status = status;
        _repository.Setup(x => x.LockAsync(14, It.IsAny<CancellationToken>())).ReturnsAsync(submission);
        var act = () => Create().ExecuteAsync(14, new RejectMenuSubmissionRequestDto { ReviewNote = "Reason" }, CancellationToken.None);
        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("SUBMISSION_NOT_PENDING");
        AssertNoSave();
        _repository.Verify(x => x.AddNotification(It.IsAny<ANGI.Domain.Entities.Notification>()), Times.Never);
        _audit.Verify(x => x.Add(It.IsAny<AuditLogEntry>()), Times.Never);
        _transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-03: Return SUBMISSION_NOT_FOUND for a missing submission without committing.
    /// <summary>Verifies the documented missing-resource error.</summary>
    [Fact]
    public async Task ExecuteAsync_Missing_ShouldNotFound()
    {
        var act = () => Create().ExecuteAsync(-1, new RejectMenuSubmissionRequestDto { ReviewNote = "Reason" }, CancellationToken.None);
        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("SUBMISSION_NOT_FOUND");
        AssertNoSave();
    }

    // TEST-04: Validate the note before querying permissions or starting a transaction.
    /// <summary>Verifies invalid input cannot cause persistence or information disclosure.</summary>
    [Fact]
    public async Task ExecuteAsync_Invalid_ShouldValidateFirst()
    {
        var act = () => Create().ExecuteAsync(14, new RejectMenuSubmissionRequestDto { ReviewNote = new string('x', 1001) }, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();
        _repository.VerifyNoOtherCalls();
        _unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-05: Reject missing identities, wrong roles and denied permissions before starting a transaction.
    /// <summary>Verifies access checks fail closed for every unauthorized partition.</summary>
    [Theory]
    [InlineData(null, "MOD", true, "UNAUTHORIZED")]
    [InlineData(10, "TRAVELER", true, "FORBIDDEN")]
    [InlineData(10, "RESTAURANT_OWNER", true, "FORBIDDEN")]
    [InlineData(10, null, true, "FORBIDDEN")]
    [InlineData(10, "MOD", false, "FORBIDDEN")]
    [InlineData(10, "ADMIN", false, "FORBIDDEN")]
    public async Task ExecuteAsync_NoAccess_ShouldFail(int? id, string? role, bool permission, string code)
    {
        _currentUser.SetupGet(x => x.UserId).Returns(id);
        _currentUser.SetupGet(x => x.Role).Returns(role);
        _repository.Setup(x => x.HasPermissionAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(permission);
        var act = () => Create().ExecuteAsync(14, new RejectMenuSubmissionRequestDto { ReviewNote = "Reason" }, CancellationToken.None);
        (await act.Should().ThrowAsync<AppException>()).Which.ErrorCode.Should().Be(code);
        _unitOfWork.VerifyNoOtherCalls();
    }

    // TEST-06: Dispose without commit when saving fails.
    /// <summary>Verifies a persistence failure cannot commit a partial review.</summary>
    [Fact]
    public async Task ExecuteAsync_SaveFails_ShouldNotCommit()
    {
        _repository.Setup(x => x.LockAsync(14, It.IsAny<CancellationToken>())).ReturnsAsync(Seed());

        _unitOfWork.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("save failed"));
        var act = () => Create().ExecuteAsync(14, new RejectMenuSubmissionRequestDto { ReviewNote = "Reason" }, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        _transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        _transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    // TEST-07: Pass the caller token through permission, locking, reviewer loading, save and commit.
    /// <summary>Verifies ADMIN access and cancellation-token propagation through all asynchronous boundaries.</summary>
    [Fact]
    public async Task ExecuteAsync_Admin_ShouldForwardToken()
    {
        using var cancellation = new CancellationTokenSource();
        var ct = cancellation.Token;
        _currentUser.SetupGet(x => x.Role).Returns("ADMIN");
        var submission = Seed();

        _repository.Setup(x => x.LockAsync(14, ct)).ReturnsAsync(submission);
        await Create().ExecuteAsync(14, new RejectMenuSubmissionRequestDto { ReviewNote = "Reason" }, ct);
        _repository.Verify(x => x.HasPermissionAsync(10, ct), Times.Once);
        _repository.Verify(x => x.LockAsync(14, ct), Times.Once);
        _repository.Verify(x => x.LoadReviewerAsync(submission, ct), Times.Once);
        _unitOfWork.Verify(x => x.BeginTransactionAsync(ct), Times.Once);
        _unitOfWork.Verify(x => x.SaveChangesAsync(ct), Times.Once);
        _transaction.Verify(x => x.CommitAsync(ct), Times.Once);
    }

    // TEST-08: Preserve caller cancellation and dispose the transaction without committing.
    /// <summary>Verifies cancelled database operations are not converted into successful reviews.</summary>
    [Fact]
    public async Task ExecuteAsync_CancelledLock_ShouldPropagate()
    {
        _repository.Setup(x => x.LockAsync(14, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        var act = () => Create().ExecuteAsync(14, new RejectMenuSubmissionRequestDto { ReviewNote = "Reason" }, CancellationToken.None);
        await act.Should().ThrowAsync<OperationCanceledException>();
        AssertNoSave();
        _transaction.Verify(x => x.DisposeAsync(), Times.Once);
    }

    /// <summary>Asserts no persistence or transaction commit took place on an error path.</summary>
    private void AssertNoSave()
    {
        _unitOfWork.Verify(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _transaction.Verify(x => x.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Builds the production use case with isolated persistence boundaries and deterministic time.</summary>
    private RejectMenuSubmissionUseCase Create() => new(
        _repository.Object, _currentUser.Object, _unitOfWork.Object, _audit.Object,
        new FixedTimeProvider(), new RejectMenuSubmissionRequestDtoValidator());

    /// <summary>Creates a menu with edited, unchanged, new and omitted dishes.</summary>
    private static MenuSubmission Seed()
    {
        var restaurant = new ANGI.Domain.Entities.Restaurant { Id = 7, OwnerId = 20, Name = "Restaurant" };
        var edited = new Dish { Id = 1, RestaurantId = 7, Restaurant = restaurant, Name = "Original",
            Price = 100, LikeCount = 12, DislikeCount = 3, IsAvailable = false, Status = DishStatus.Hidden };
        var same = new Dish { Id = 2, RestaurantId = 7, Restaurant = restaurant, Name = "Same", Price = 100 };
        restaurant.Dishes = [edited, same, new Dish { Id = 3, Name = "Removed", Restaurant = restaurant }];
        return new MenuSubmission
        {
            Id = 14, RestaurantId = 7, Restaurant = restaurant, Status = MenuSubmissionStatus.Pending,
            Items =
            [
                new MenuSubmissionItem { Id = 1, SourceDishId = 1, SourceDish = edited, Name = "Edited", Price = 200 },
                new MenuSubmissionItem { Id = 2, SourceDishId = 2, SourceDish = same, Name = "Same", Price = 100 },
                new MenuSubmissionItem { Id = 3, Name = "New", Price = 50 }
            ]
        };
    }

    /// <summary>Provides deterministic UTC review timestamps.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        /// <summary>Returns the fixed instant used to verify review metadata.</summary>
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
