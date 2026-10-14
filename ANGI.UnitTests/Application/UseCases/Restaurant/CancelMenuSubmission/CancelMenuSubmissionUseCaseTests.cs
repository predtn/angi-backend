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

public sealed class CancelMenuSubmissionUseCaseTests
{
    // TEST-01: Cancel a pending submission and persist the state transition.
    /// <summary>Verifies the successful OWN-18 withdrawal workflow.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPendingSubmission_ShouldCancel()
    {
        var submission = new MenuSubmission { Id = 14, Status = MenuSubmissionStatus.Pending };
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var useCase = new CancelMenuSubmissionUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);

        var result = await useCase.ExecuteAsync(14, CancellationToken.None);

        submission.Status.Should().Be(MenuSubmissionStatus.Cancelled);
        result.Status.Should().Be("cancelled");
    }

    // TEST-02: Reject cancellation after a submission has been reviewed.
    /// <summary>Verifies the documented SUBMISSION_NOT_CANCELLABLE conflict.</summary>
    [Fact]
    public async Task ExecuteAsync_WithApprovedSubmission_ShouldThrowConflict()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = MenuSubmissionStatus.Approved });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        var useCase = new CancelMenuSubmissionUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(14, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_CANCELLABLE");
    }

    // TEST-03: Allow both draft cancellation and pending withdrawal.
    [Theory]
    [InlineData(MenuSubmissionStatus.Draft)]
    [InlineData(MenuSubmissionStatus.Pending)]
    public async Task ExecuteAsync_WithCancellableStatus_ShouldCancel(MenuSubmissionStatus status)
    {
        var submission = new MenuSubmission { Id = 14, Status = status };
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var result = await CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, CancellationToken.None);

        result.Status.Should().Be("cancelled");
    }

    // TEST-04: Reject cancellation for every final workflow status.
    [Theory]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithFinalStatus_ShouldReject(MenuSubmissionStatus status)
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = status });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var act = () => CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // TEST-05: Hide missing and unowned submissions behind the not-found response.
    [Fact]
    public async Task ExecuteAsync_WithUnknownOrOtherOwnerSubmission_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync((MenuSubmission?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var act = () => CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

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
