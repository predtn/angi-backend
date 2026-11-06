using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.CancelMenuSubmission;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.CancelMenuSubmission;

/// <summary>Verifies CancelMenuSubmissionUseCase cancels only owned draft or pending submissions.</summary>
public sealed class CancelMenuSubmissionUseCaseTests
{
    // TEST-01: Cancel a pending submission and persist the state transition.
    /// <summary>Verifies the successful OWN-18 withdrawal workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPendingSubmission_ShouldCancel()
    {
        // STEP-01: Create submission as MenuSubmission.
        var submission = new MenuSubmission { Id = 14, Status = MenuSubmissionStatus.Pending };
        // STEP-02: Mock IRestaurantMenuRepository, ICurrentUserService and 1 more; stub repository.LockOwnedSubmissionAsync, currentUser.UserId and 2 more.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        // STEP-03: Create the CancelMenuSubmissionUseCase under test as useCase.
        var useCase = new CancelMenuSubmissionUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);

        // STEP-04: Run useCase.ExecuteAsync and keep result.
        var result = await useCase.ExecuteAsync(14, CancellationToken.None);

        // STEP-05: Assert submission.Status is MenuSubmissionStatus.Cancelled and result.Status is "cancelled".
        submission.Status.Should().Be(MenuSubmissionStatus.Cancelled);
        result.Status.Should().Be("cancelled");
    }

    // TEST-02: Reject cancellation after a submission has been reviewed.
    /// <summary>Verifies the documented SUBMISSION_NOT_CANCELLABLE conflict.</summary>
    [Fact]
    public async Task ExecuteAsync_WithApprovedSubmission_ShouldThrowConflict()
    {
        // STEP-01: Mock IRestaurantMenuRepository, ICurrentUserService and 1 more; stub repository.LockOwnedSubmissionAsync, currentUser.UserId and 1 more.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = MenuSubmissionStatus.Approved });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        // STEP-02: Create the CancelMenuSubmissionUseCase under test as useCase.
        var useCase = new CancelMenuSubmissionUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);

        // STEP-03: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, CancellationToken.None);

        // STEP-04: Assert act throws ConflictException and exception.ErrorCode is "SUBMISSION_NOT_CANCELLABLE".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_CANCELLABLE");
    }

    // TEST-03: Allow both draft cancellation and pending withdrawal.
    /// <summary>Ensures an owner can discard a draft or withdraw a pending submission.</summary>
    [Theory]
    [InlineData(MenuSubmissionStatus.Draft)]
    [InlineData(MenuSubmissionStatus.Pending)]
    public async Task ExecuteAsync_WithCancellableStatus_ShouldCancel(MenuSubmissionStatus status)
    {
        // STEP-01: Create submission as MenuSubmission.
        var submission = new MenuSubmission { Id = 14, Status = status };
        // STEP-02: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.LockOwnedSubmissionAsync and unitOfWork.BeginTransactionAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        // STEP-03: Run CreateUseCase().ExecuteAsync and keep result.
        var result = await CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, CancellationToken.None);

        // STEP-04: Assert result.Status is "cancelled".
        result.Status.Should().Be("cancelled");
    }

    // TEST-04: Reject cancellation for every final workflow status.
    /// <summary>Ensures a reviewed or finished submission cannot be cancelled.</summary>
    [Theory]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithFinalStatus_ShouldReject(MenuSubmissionStatus status)
    {
        // STEP-01: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.LockOwnedSubmissionAsync and unitOfWork.BeginTransactionAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = status });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        // STEP-02: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, CancellationToken.None);

        // STEP-03: Assert act throws ConflictException.
        await act.Should().ThrowAsync<ConflictException>();
    }

    // TEST-05: Hide missing and unowned submissions behind the not-found response.
    /// <summary>Ensures another owner's submission is indistinguishable from a missing one.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownOrOtherOwnerSubmission_ShouldThrowNotFound()
    {
        // STEP-01: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.LockOwnedSubmissionAsync and unitOfWork.BeginTransactionAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MenuSubmission?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        // STEP-02: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException.
        await act.Should().ThrowAsync<NotFoundException>();
    }

    /// <summary>Creates the use case for owner 12 over the given repository and unit of work.</summary>
    private static CancelMenuSubmissionUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository,
        Mock<IUnitOfWork> unitOfWork)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        return new CancelMenuSubmissionUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);
    }
}
