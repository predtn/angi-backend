using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant.SubmitMenuSubmission;
using ANGI.Application.UseCases.Validators.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Restaurant.SubmitMenuSubmission;

/// <summary>Verifies SubmitMenuSubmissionUseCase sends only a changed, non-empty draft menu for review.</summary>
public sealed class SubmitMenuSubmissionUseCaseTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);

    // TEST-01: Submit an edited draft and persist its pending state, note, and UTC timestamp.
    /// <summary>Verifies the successful OWN-17 state transition and response mapping.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEditedDraft_ShouldSubmitForReview()
    {
        // STEP-01: Create sourceDish through CreateDish, create submission through CreateSubmission and set submission.Items.Single().Price to 55000.
        var sourceDish = CreateDish();
        var submission = CreateSubmission(CloneItem(sourceDish));
        submission.Items.Single().Price = 55000;
        // STEP-02: Build repository under test through CreateRepository.
        var repository = CreateRepository(submission, [sourceDish]);
        // STEP-03: Mock IUnitOfWorkTransaction.
        var transaction = new Mock<IUnitOfWorkTransaction>();
        // STEP-04: Create unitOfWork through CreateUnitOfWork.
        var unitOfWork = CreateUnitOfWork(transaction);
        // STEP-05: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, unitOfWork);

        // STEP-06: Run useCase.ExecuteAsync with SubmitMenuSubmissionRequestDto { OwnerNote = the expected text } and keep result.
        var result = await useCase.ExecuteAsync(
            14,
            new SubmitMenuSubmissionRequestDto { OwnerNote = "  Tăng giá món  " },
            CancellationToken.None);

        // STEP-07: Assert submission.Status is MenuSubmissionStatus.Pending, submission.OwnerNote is the expected text and submission.SubmittedAt is Now.
        submission.Status.Should().Be(MenuSubmissionStatus.Pending);
        submission.OwnerNote.Should().Be("Tăng giá món");
        submission.SubmittedAt.Should().Be(Now);
        // STEP-08: Assert result.Status is "pending" and result.SubmittedAt is Now.
        result.Status.Should().Be("pending");
        result.SubmittedAt.Should().Be(Now);
        // STEP-09: Verify SaveChangesAsync is called once and CommitAsync is called once.
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Reject a draft that contains no proposed dishes.
    /// <summary>Verifies the documented MENU_EMPTY failure before menu comparison.</summary>
    [Fact]
    public async Task ExecuteAsync_WithEmptyDraft_ShouldThrowMenuEmpty()
    {
        // STEP-01: Create submission through CreateSubmission.
        var submission = CreateSubmission();
        // STEP-02: Build repository under test through CreateRepository.
        var repository = CreateRepository(submission, []);
        // STEP-03: Create unitOfWork through CreateUnitOfWork.
        var unitOfWork = CreateUnitOfWork();
        // STEP-04: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, unitOfWork);

        // STEP-05: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            14,
            new SubmitMenuSubmissionRequestDto(),
            CancellationToken.None);

        // STEP-06: Assert act throws BadRequestException and exception.ErrorCode is "MENU_EMPTY".
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("MENU_EMPTY");
        // STEP-07: Verify GetDraftSourceDishesAsync is never called and SaveChangesAsync is never called.
        repository.Verify(
            value => value.GetDraftSourceDishesAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Reject a cloned draft when every field and tag still matches the live menu.
    /// <summary>Verifies the documented MENU_UNCHANGED failure for an untouched draft.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnchangedDraft_ShouldThrowMenuUnchanged()
    {
        // STEP-01: Create sourceDish through CreateDish and create submission through CreateSubmission.
        var sourceDish = CreateDish();
        var submission = CreateSubmission(CloneItem(sourceDish));
        // STEP-02: Build repository under test through CreateRepository.
        var repository = CreateRepository(submission, [sourceDish]);
        // STEP-03: Create unitOfWork through CreateUnitOfWork.
        var unitOfWork = CreateUnitOfWork();
        // STEP-04: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, unitOfWork);

        // STEP-05: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            14,
            new SubmitMenuSubmissionRequestDto(),
            CancellationToken.None);

        // STEP-06: Assert act throws BadRequestException and exception.ErrorCode is "MENU_UNCHANGED".
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("MENU_UNCHANGED");
        // STEP-07: Verify SaveChangesAsync is never called.
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Treat adding or removing a dish as a menu change even when retained dishes match.
    /// <summary>Verifies that OWN-17 compares menu membership as well as editable dish fields.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WithAddedOrRemovedDish_ShouldSubmit(bool addDish)
    {
        // STEP-01: Create sourceDish through CreateDish, create items as List<MenuSubmissionItem>, create currentDishes as List<Dish> and 1 more.
        var sourceDish = CreateDish();
        var items = new List<MenuSubmissionItem> { CloneItem(sourceDish) };
        var currentDishes = new List<Dish> { sourceDish };
        if (addDish)
        {
            items.Add(new MenuSubmissionItem { Name = "Món mới", Price = 30000 });
        }
        else
        {
            currentDishes.Add(CreateDish(106));
        }

        // STEP-02: Build repository under test through CreateRepository; build useCase under test through CreateUseCase.
        var repository = CreateRepository(CreateSubmission(items.ToArray()), currentDishes);
        var useCase = CreateUseCase(repository, CreateUnitOfWork());

        // STEP-03: Run useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto and keep result.
        var result = await useCase.ExecuteAsync(
            14,
            new SubmitMenuSubmissionRequestDto(),
            CancellationToken.None);

        // STEP-04: Assert result.Status is "pending".
        result.Status.Should().Be("pending");
    }

    // TEST-05: Reject a submission that is no longer a draft.
    /// <summary>Verifies the documented SUBMISSION_NOT_EDITABLE workflow conflict.</summary>
    [Fact]
    public async Task ExecuteAsync_WithPendingSubmission_ShouldThrowNotEditable()
    {
        // STEP-01: Create submission through CreateSubmission and set submission.Status to MenuSubmissionStatus.Pending.
        var submission = CreateSubmission(new MenuSubmissionItem { Name = "Món" });
        submission.Status = MenuSubmissionStatus.Pending;
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            CreateRepository(submission, []),
            CreateUnitOfWork());

        // STEP-03: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            14,
            new SubmitMenuSubmissionRequestDto(),
            CancellationToken.None);

        // STEP-04: Assert act throws ConflictException and exception.ErrorCode is "SUBMISSION_NOT_EDITABLE".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_EDITABLE");
    }

    // TEST-06: Hide a missing or differently owned submission behind the not-found response.
    /// <summary>Verifies ownership-safe lookup through SUBMISSION_NOT_FOUND.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownSubmission_ShouldThrowNotFound()
    {
        // STEP-01: Build repository under test through CreateRepository; build useCase under test through CreateUseCase.
        var repository = CreateRepository(null, []);
        var useCase = CreateUseCase(repository, CreateUnitOfWork());

        // STEP-02: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            14,
            new SubmitMenuSubmissionRequestDto(),
            CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and exception.ErrorCode is "SUBMISSION_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_FOUND");
    }

    // TEST-07: Reject every non-draft terminal or review state as not editable.
    /// <summary>Verifies all remaining non-draft workflow boundaries.</summary>
    [Theory]
    [InlineData(MenuSubmissionStatus.Approved)]
    [InlineData(MenuSubmissionStatus.Rejected)]
    [InlineData(MenuSubmissionStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonDraftSubmission_ShouldThrowNotEditable(
        MenuSubmissionStatus status)
    {
        // STEP-01: Create submission through CreateSubmission and set submission.Status to status.
        var submission = CreateSubmission(new MenuSubmissionItem { Name = "Món" });
        submission.Status = status;
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(CreateRepository(submission, []), CreateUnitOfWork());

        // STEP-03: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws ConflictException and exception.ErrorCode is "SUBMISSION_NOT_EDITABLE".
        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_EDITABLE");
    }

    // TEST-08: Treat a change to each editable dish field as a real menu change.
    /// <summary>Verifies field-by-field change detection for OWN-17.</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("dishForm")]
    [InlineData("price")]
    [InlineData("coverMediaId")]
    [InlineData("sortOrder")]
    [InlineData("tagIds")]
    public async Task ExecuteAsync_WithChangedEditableField_ShouldSubmit(string field)
    {
        // STEP-01: Create sourceDish through CreateDish, get item from CloneItem and call MutateField.
        var sourceDish = CreateDish();
        var item = CloneItem(sourceDish);
        MutateField(item, field);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            CreateRepository(CreateSubmission(item), [sourceDish]),
            CreateUnitOfWork());

        // STEP-03: Run useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto and keep result.
        var result = await useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-04: Assert result.Status is "pending".
        result.Status.Should().Be("pending");
    }

    // TEST-09: Ignore tag ordering when deciding whether a menu changed.
    /// <summary>Verifies that tagIds are compared as an order-independent set.</summary>
    [Fact]
    public async Task ExecuteAsync_WithOnlyReorderedTags_ShouldThrowMenuUnchanged()
    {
        // STEP-01: Create sourceDish through CreateDish, get item from CloneItem and set item.TagIds to item.TagIds.Reverse().ToArray().
        var sourceDish = CreateDish();
        var item = CloneItem(sourceDish);
        item.TagIds = item.TagIds.Reverse().ToArray();
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            CreateRepository(CreateSubmission(item), [sourceDish]),
            CreateUnitOfWork());

        // STEP-03: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-04: Assert act throws BadRequestException and exception.ErrorCode is "MENU_UNCHANGED".
        var exception = await act.Should().ThrowAsync<BadRequestException>();
        exception.Which.ErrorCode.Should().Be("MENU_UNCHANGED");
    }

    // TEST-10: Treat adding or removing a tag as a menu change.
    /// <summary>Verifies both tag-membership directions.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WithAddedOrRemovedTag_ShouldSubmit(bool addTag)
    {
        // STEP-01: Create sourceDish through CreateDish, get item from CloneItem and set item.TagIds to . item.TagIds, 9] : [item.TagIds[0]].
        var sourceDish = CreateDish();
        var item = CloneItem(sourceDish);
        item.TagIds = addTag ? [.. item.TagIds, 9] : [item.TagIds[0]];
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            CreateRepository(CreateSubmission(item), [sourceDish]),
            CreateUnitOfWork());

        // STEP-03: Run useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto and keep result.
        var result = await useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-04: Assert result.Status is "pending".
        result.Status.Should().Be("pending");
    }

    // TEST-11: Treat cover-media transitions in both nullable directions as changes.
    /// <summary>Verifies null-to-value and value-to-null cover changes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WithChangedNullableCover_ShouldSubmit(bool addCover)
    {
        // STEP-01: Create sourceDish through CreateDish, set sourceDish.CoverMediaId to addCover ? null : 8, get item from CloneItem and 1 more.
        var sourceDish = CreateDish();
        sourceDish.CoverMediaId = addCover ? null : 8;
        var item = CloneItem(sourceDish);
        item.CoverMediaId = addCover ? 8 : null;
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            CreateRepository(CreateSubmission(item), [sourceDish]),
            CreateUnitOfWork());

        // STEP-03: Run useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto and keep result.
        var result = await useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-04: Assert result.Status is "pending".
        result.Status.Should().Be("pending");
    }

    // TEST-12: Return not found for zero, negative, and maximum submission identifiers.
    /// <summary>Verifies numeric identifier boundaries without leaking ownership information.</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task ExecuteAsync_WithBoundarySubmissionId_ShouldThrowNotFound(long submissionId)
    {
        // STEP-01: Build repository under test through CreateRepository; build useCase under test through CreateUseCase.
        var repository = CreateRepository(null, [], submissionId);
        var useCase = CreateUseCase(repository, CreateUnitOfWork());

        // STEP-02: Wrap useCase.ExecuteAsync with submissionId and an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(
            submissionId,
            new SubmitMenuSubmissionRequestDto(),
            CancellationToken.None);

        // STEP-03: Assert act throws NotFoundException and exception.ErrorCode is "SUBMISSION_NOT_FOUND".
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("SUBMISSION_NOT_FOUND");
        // STEP-04: Verify LockOwnedSubmissionAsync is called once.
        repository.Verify(
            value => value.LockOwnedSubmissionAsync(12, submissionId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // TEST-13: Reject a use-case invocation when the current user has no identifier.
    /// <summary>Verifies defense in depth below the HTTP authorization layer.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorized()
    {
        // STEP-01: Build repository under test through CreateRepository; build useCase under test through CreateUseCase.
        var repository = CreateRepository(null, []);
        var useCase = CreateUseCase(repository, CreateUnitOfWork(), null);

        // STEP-02: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-03: Assert act throws UnauthorizedException and exception.ErrorCode is "UNAUTHORIZED".
        var exception = await act.Should().ThrowAsync<UnauthorizedException>();
        exception.Which.ErrorCode.Should().Be("UNAUTHORIZED");
        // STEP-04: Verify LockOwnedSubmissionAsync is never called.
        repository.Verify(
            value => value.LockOwnedSubmissionAsync(
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // TEST-14: Normalize empty, whitespace-only, and padded owner notes before persistence.
    /// <summary>Verifies all optional-text normalization boundaries.</summary>
    [Theory]
    [InlineData("", null)]
    [InlineData("   \t\r\n", null)]
    [InlineData("  Gửi duyệt  ", "Gửi duyệt")]
    public async Task ExecuteAsync_WithOptionalOwnerNote_ShouldNormalize(
        string ownerNote,
        string? expected)
    {
        // STEP-01: Create sourceDish through CreateDish, get item from CloneItem, increment item.Price and create submission through CreateSubmission.
        var sourceDish = CreateDish();
        var item = CloneItem(sourceDish);
        item.Price++;
        var submission = CreateSubmission(item);
        // STEP-02: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            CreateRepository(submission, [sourceDish]),
            CreateUnitOfWork());

        // STEP-03: Run useCase.ExecuteAsync with SubmitMenuSubmissionRequestDto { OwnerNote = ownerNote }.
        await useCase.ExecuteAsync(
            14,
            new SubmitMenuSubmissionRequestDto { OwnerNote = ownerNote },
            CancellationToken.None);

        // STEP-04: Assert submission.OwnerNote is expected.
        submission.OwnerNote.Should().Be(expected);
    }

    // TEST-15: Propagate cancellation while locking the submission and dispose the transaction.
    /// <summary>Verifies cancellation during the ownership query.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenLockQueryIsCancelled_ShouldPropagateAndDisposeTransaction()
    {
        // STEP-01: Create cancellation as CancellationTokenSource.
        using var cancellation = new CancellationTokenSource();
        // STEP-02: Build repository under test through CreateRepository.
        var repository = CreateRepository(null, []);
        // STEP-03: Mock IUnitOfWorkTransaction; stub repository.LockOwnedSubmissionAsync.
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, 14, cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var transaction = new Mock<IUnitOfWorkTransaction>();
        // STEP-04: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, CreateUnitOfWork(transaction));

        // STEP-05: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), cancellation.Token);

        // STEP-06: Assert act throws OperationCanceledException.
        await act.Should().ThrowAsync<OperationCanceledException>();
        // STEP-07: Verify DisposeAsync is called once.
        transaction.Verify(value => value.DisposeAsync(), Times.Once);
    }

    // TEST-16: Propagate cancellation while reading the live menu and avoid persistence.
    /// <summary>Verifies cancellation during the menu-comparison query.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenMenuQueryIsCancelled_ShouldNotSaveOrCommit()
    {
        // STEP-01: Create cancellation as CancellationTokenSource and create sourceDish through CreateDish.
        using var cancellation = new CancellationTokenSource();
        var sourceDish = CreateDish();
        // STEP-02: Build repository under test through CreateRepository.
        var repository = CreateRepository(CreateSubmission(CloneItem(sourceDish)), [sourceDish]);
        // STEP-03: Mock IUnitOfWorkTransaction; stub repository.GetDraftSourceDishesAsync.
        repository.Setup(value => value.GetDraftSourceDishesAsync(7, cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var transaction = new Mock<IUnitOfWorkTransaction>();
        // STEP-04: Create unitOfWork through CreateUnitOfWork.
        var unitOfWork = CreateUnitOfWork(transaction);
        // STEP-05: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(repository, unitOfWork);

        // STEP-06: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), cancellation.Token);

        // STEP-07: Assert act throws OperationCanceledException.
        await act.Should().ThrowAsync<OperationCanceledException>();
        // STEP-08: Verify SaveChangesAsync is never called and CommitAsync is never called.
        unitOfWork.Verify(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-17: Restore the aggregate and skip commit when saving is cancelled.
    /// <summary>Verifies cancellation after the pending state has been staged.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveIsCancelled_ShouldRestoreDraftState()
    {
        // STEP-01: Create cancellation as CancellationTokenSource, create sourceDish through CreateDish, get item from CloneItem, increment item.Price and 4 more.
        using var cancellation = new CancellationTokenSource();
        var sourceDish = CreateDish();
        var item = CloneItem(sourceDish);
        item.Price++;
        var submission = CreateSubmission(item);
        var previousSubmittedAt = Now.AddDays(-1);
        submission.OwnerNote = "Ghi chú trước đó";
        submission.SubmittedAt = previousSubmittedAt;
        // STEP-02: Create unitOfWork through CreateUnitOfWork; mock IUnitOfWorkTransaction; stub unitOfWork.SaveChangesAsync.
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = CreateUnitOfWork(transaction);
        unitOfWork.Setup(value => value.SaveChangesAsync(cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(CreateRepository(submission, [sourceDish]), unitOfWork);

        // STEP-04: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), cancellation.Token);

        // STEP-05: Assert act throws OperationCanceledException and AssertRestoredDraft succeeds.
        await act.Should().ThrowAsync<OperationCanceledException>();
        AssertRestoredDraft(submission, "Ghi chú trước đó", previousSubmittedAt);
        // STEP-06: Verify CommitAsync is never called and DisposeAsync is called once.
        transaction.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(value => value.DisposeAsync(), Times.Once);
    }

    // TEST-18: Restore the aggregate and roll back when SaveChanges fails.
    /// <summary>Verifies failure atomicity before the database transaction is committed.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveFails_ShouldRestoreDraftState()
    {
        // STEP-01: Create sourceDish through CreateDish, get item from CloneItem, increment item.Price, create submission through CreateSubmission and 3 more.
        var sourceDish = CreateDish();
        var item = CloneItem(sourceDish);
        item.Price++;
        var submission = CreateSubmission(item);
        var previousSubmittedAt = Now.AddDays(-1);
        submission.OwnerNote = "Ghi chú trước đó";
        submission.SubmittedAt = previousSubmittedAt;
        // STEP-02: Create unitOfWork through CreateUnitOfWork; mock IUnitOfWorkTransaction; stub unitOfWork.SaveChangesAsync.
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = CreateUnitOfWork(transaction);
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("save failed"));
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(CreateRepository(submission, [sourceDish]), unitOfWork);

        // STEP-04: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-05: Assert act throws InvalidOperationException and AssertRestoredDraft succeeds.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("save failed");
        AssertRestoredDraft(submission, "Ghi chú trước đó", previousSubmittedAt);
        // STEP-06: Verify CommitAsync is never called and DisposeAsync is called once.
        transaction.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(value => value.DisposeAsync(), Times.Once);
    }

    // TEST-19: Restore the aggregate and roll back when transaction commit fails.
    /// <summary>Verifies failure atomicity after SaveChanges but before commit succeeds.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenCommitFails_ShouldRestoreDraftState()
    {
        // STEP-01: Create sourceDish through CreateDish, get item from CloneItem, increment item.Price and create submission through CreateSubmission.
        var sourceDish = CreateDish();
        var item = CloneItem(sourceDish);
        item.Price++;
        var submission = CreateSubmission(item);
        // STEP-02: Mock IUnitOfWorkTransaction; stub transaction.CommitAsync.
        var transaction = new Mock<IUnitOfWorkTransaction>();
        transaction.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("commit failed"));
        // STEP-03: Build useCase under test through CreateUseCase.
        var useCase = CreateUseCase(
            CreateRepository(submission, [sourceDish]),
            CreateUnitOfWork(transaction));

        // STEP-04: Wrap useCase.ExecuteAsync with an empty SubmitMenuSubmissionRequestDto in act so its outcome can be asserted.
        var act = () => useCase.ExecuteAsync(14, new SubmitMenuSubmissionRequestDto(), CancellationToken.None);

        // STEP-05: Assert act throws InvalidOperationException and AssertRestoredDraft succeeds.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("commit failed");
        AssertRestoredDraft(submission);
        // STEP-06: Verify DisposeAsync is called once.
        transaction.Verify(value => value.DisposeAsync(), Times.Once);
    }

    /// <summary>Creates a current menu dish with every field relevant to unchanged detection.</summary>
    private static Dish CreateDish(int id = 105)
    {
        return new Dish
        {
            Id = id,
            Name = $"Món {id}",
            Description = "Mô tả món hiện tại",
            DishForm = DishForm.Soup,
            Price = 50000,
            CoverMediaId = 8,
            SortOrder = 2,
            DishTags =
            [
                new DishTag { DishId = id, TagId = 3 },
                new DishTag { DishId = id, TagId = 5 }
            ]
        };
    }

    /// <summary>Copies the live dish fields into a proposed submission item.</summary>
    private static MenuSubmissionItem CloneItem(Dish sourceDish)
    {
        return new MenuSubmissionItem
        {
            SourceDishId = sourceDish.Id,
            Name = sourceDish.Name,
            Description = sourceDish.Description,
            DishForm = sourceDish.DishForm,
            Price = sourceDish.Price,
            CoverMediaId = sourceDish.CoverMediaId,
            TagIds = sourceDish.DishTags.Select(value => value.TagId).ToArray(),
            SortOrder = sourceDish.SortOrder
        };
    }

    /// <summary>Creates a draft aggregate with the supplied proposed items.</summary>
    private static MenuSubmission CreateSubmission(params MenuSubmissionItem[] items)
    {
        var submission = new MenuSubmission
        {
            Id = 14,
            RestaurantId = 7,
            Status = MenuSubmissionStatus.Draft
        };
        foreach (var item in items)
        {
            submission.Items.Add(item);
        }

        return submission;
    }

    /// <summary>Creates repository behavior for locked submission and current-menu reads.</summary>
    private static Mock<IRestaurantMenuRepository> CreateRepository(
        MenuSubmission? submission,
        IReadOnlyList<Dish> currentDishes,
        long submissionId = 14)
    {
        var repository = new Mock<IRestaurantMenuRepository>();
        repository.Setup(value => value.LockOwnedSubmissionAsync(12, submissionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(submission);
        repository.Setup(value => value.GetDraftSourceDishesAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentDishes);
        return repository;
    }

    /// <summary>Creates a unit of work with an optional observable transaction mock.</summary>
    private static Mock<IUnitOfWork> CreateUnitOfWork(Mock<IUnitOfWorkTransaction>? transaction = null)
    {
        transaction ??= new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);
        return unitOfWork;
    }

    /// <summary>Creates the use case with a fixed authenticated owner and UTC clock.</summary>
    private static SubmitMenuSubmissionUseCase CreateUseCase(
        Mock<IRestaurantMenuRepository> repository,
        Mock<IUnitOfWork> unitOfWork,
        int? ownerId = 12)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(ownerId);
        return new SubmitMenuSubmissionUseCase(
            new SubmitMenuSubmissionRequestDtoValidator(),
            repository.Object,
            currentUser.Object,
            unitOfWork.Object,
            new FixedTimeProvider(Now));
    }

    /// <summary>Mutates exactly one field used by menu change detection.</summary>
    private static void MutateField(MenuSubmissionItem item, string field)
    {
        switch (field)
        {
            case "name": item.Name += " mới"; break;
            case "description": item.Description += " mới"; break;
            case "dishForm": item.DishForm = DishForm.Dry; break;
            case "price": item.Price++; break;
            case "coverMediaId": item.CoverMediaId = 9; break;
            case "sortOrder": item.SortOrder++; break;
            case "tagIds": item.TagIds = [.. item.TagIds, 9]; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    /// <summary>Asserts the pre-submit values restored after a persistence failure.</summary>
    private static void AssertRestoredDraft(
        MenuSubmission submission,
        string? ownerNote = null,
        DateTime? submittedAt = null)
    {
        submission.Status.Should().Be(MenuSubmissionStatus.Draft);
        submission.OwnerNote.Should().Be(ownerNote);
        submission.SubmittedAt.Should().Be(submittedAt);
    }

    /// <summary>Provides a deterministic UTC timestamp to the submission workflow.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        /// <summary>Initializes the provider with the desired UTC instant.</summary>
        public FixedTimeProvider(DateTime now)
        {
            _now = new DateTimeOffset(now);
        }

        /// <summary>Returns the fixed UTC instant for deterministic assertions.</summary>
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
