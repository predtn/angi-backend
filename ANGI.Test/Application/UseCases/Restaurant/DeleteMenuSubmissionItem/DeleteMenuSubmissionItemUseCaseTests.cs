using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.DeleteMenuSubmissionItem;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.Test.Application.UseCases.Restaurant.DeleteMenuSubmissionItem;

public sealed class DeleteMenuSubmissionItemUseCaseTests
{
    // TEST-01: Reject item deletion after the menu draft has been submitted.
    /// <summary>Verifies the documented SUBMISSION_NOT_EDITABLE conflict for OWN-16.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPendingSubmission_ShouldThrowConflict()
    {
        var item = new MenuSubmissionItem
        {
            Id = 340,
            Submission = new MenuSubmission { Id = 14, Status = MenuSubmissionStatus.Pending }
        };
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item.Submission);
        repository.Setup(value => value.GetTrackedItemAsync(12, 14, 340, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());
        var useCase = new DeleteMenuSubmissionItemUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(14, 340, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_EDITABLE");
        repository.Verify(value => value.RemoveItem(It.IsAny<MenuSubmissionItem>()), Times.Never);
    }

    // TEST-02: Remove an owned draft item and commit the transaction.
    [Fact]
    public async Task ExecuteAsync_WithOwnedDraftItem_ShouldRemoveAndCommit()
    {
        var submission = new MenuSubmission { Id = 14 };
        var item = new MenuSubmissionItem { Id = 340, Submission = submission };
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        repository.Setup(value => value.GetTrackedItemAsync(12, 14, 340, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);

        await CreateUseCase(repository, unitOfWork).ExecuteAsync(14, 340, CancellationToken.None);

        repository.Verify(value => value.RemoveItem(item), Times.Once);
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Hide missing and unowned items behind the not-found response.
    [Fact]
    public async Task ExecuteAsync_WithUnknownOrOtherOwnerItem_ShouldThrowNotFound()
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14 });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var act = () => CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, 999, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
        repository.Verify(value => value.RemoveItem(It.IsAny<MenuSubmissionItem>()), Times.Never);
    }

    // TEST-04: Reject deletion for every non-draft submission status.
    [Theory]
    [InlineData(MenuSubmissionStatus.Pending)]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonDraftStatus_ShouldReject(MenuSubmissionStatus status)
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14, Status = status });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        var act = () => CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, 340, CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    private static DeleteMenuSubmissionItemUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository,
        Mock<IUnitOfWork> unitOfWork)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(12);
        return new DeleteMenuSubmissionItemUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);
    }
}
