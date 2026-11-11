using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.UseCases.Restaurant.DeleteMenuSubmissionItem;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.DeleteMenuSubmissionItem;

/// <summary>Verifies DeleteMenuSubmissionItemUseCase removes items only from the caller's own draft menu.</summary>
public sealed class DeleteMenuSubmissionItemUseCaseTests
{
    // TEST-01: Reject item deletion after the menu draft has been submitted.
    /// <summary>Verifies the documented SUBMISSION_NOT_EDITABLE conflict for OWN-16.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPendingSubmission_ShouldThrowConflict()
    {
        // STEP-01: Create item as MenuSubmissionItem.
        var item = new MenuSubmissionItem
        {
            Id = 340,
            Submission = new MenuSubmission { Id = 14, Status = MenuSubmissionStatus.Pending }
        };
        // STEP-02: Mock IRestaurantMenuRepository, ICurrentUserService and 1 more; stub repository.LockOwnedSubmissionAsync, repository.GetTrackedItemAsync and 2 more.
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
        // STEP-03: Create the DeleteMenuSubmissionItemUseCase under test as useCase.
        var useCase = new DeleteMenuSubmissionItemUseCase(
            repository.Object,
            currentUser.Object,
            unitOfWork.Object);

        // STEP-04: Wrap useCase.ExecuteAsync in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, 340, CancellationToken.None);

        // STEP-05: Assert act throws ConflictException and exception.ErrorCode is "SUBMISSION_NOT_EDITABLE".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_EDITABLE");
        // STEP-06: Verify RemoveItem is never called.
        repository.Verify(value => value.RemoveItem(It.IsAny<MenuSubmissionItem>()), Times.Never);
    }

    // TEST-02: Remove an owned draft item and commit the transaction.
    /// <summary>Ensures an owner can remove a dish from their draft.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOwnedDraftItem_ShouldRemoveAndCommit()
    {
        // STEP-01: Create submission as MenuSubmission and create item as MenuSubmissionItem.
        var submission = new MenuSubmission { Id = 14 };
        var item = new MenuSubmissionItem { Id = 340, Submission = submission };
        // STEP-02: Mock IRestaurantMenuRepository, IUnitOfWorkTransaction and 1 more; stub repository.LockOwnedSubmissionAsync, repository.GetTrackedItemAsync and 1 more.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        repository.Setup(value => value.GetTrackedItemAsync(12, 14, 340, It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);

        // STEP-03: Run CreateUseCase().ExecuteAsync.
        await CreateUseCase(repository, unitOfWork).ExecuteAsync(14, 340, CancellationToken.None);

        // STEP-04: Verify RemoveItem is called once, SaveChangesAsync is called once and CommitAsync is called once.
        repository.Verify(value => value.RemoveItem(item), Times.Once);
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Hide missing and unowned items behind the not-found response.
    /// <summary>Ensures another owner's item is indistinguishable from a missing one.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownOrOtherOwnerItem_ShouldThrowNotFound()
    {
        // STEP-01: Mock IRestaurantMenuRepository and IUnitOfWork; stub repository.LockOwnedSubmissionAsync and unitOfWork.BeginTransactionAsync.
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MenuSubmission { Id = 14 });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IUnitOfWorkTransaction>());

        // STEP-02: Wrap CreateUseCase().ExecuteAsync in act so its outcome can be asserted.
        var act = () => CreateUseCase(repository, unitOfWork)
            .ExecuteAsync(14, 999, CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException.
        await act.Should().ThrowAsync<NotFoundException>();
        // STEP-04: Verify RemoveItem is never called.
        repository.Verify(value => value.RemoveItem(It.IsAny<MenuSubmissionItem>()), Times.Never);
    }

    // TEST-04: Reject deletion for every non-draft submission status.
    /// <summary>Ensures a menu already sent for review cannot be changed.</summary>
    [Theory]
    [InlineData(MenuSubmissionStatus.Pending)]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonDraftStatus_ShouldReject(MenuSubmissionStatus status)
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
            .ExecuteAsync(14, 340, CancellationToken.None);

        // STEP-03: Assert act throws ConflictException.
        await act.Should().ThrowAsync<ConflictException>();
    }

    /// <summary>Creates the use case for owner 12 over the given repository and unit of work.</summary>
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
