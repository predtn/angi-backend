using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Roadmap.RateItem;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Roadmap.RateItem;

public sealed class RateRoadmapItemUseCaseTests
{
    private static readonly Guid RecoRequestId = Guid.Parse("6f0c2b1e-8d7a-4c1e-9b3f-2a5d7e9c1f00");
    private static readonly DateTime CreatedAt = new(2026, 10, 10, 2, 0, 0, DateTimeKind.Utc);

    // TEST-01: Store the feedback with the item's dish and reco request id, update the counters, commit once.
    /// <summary>Verifies the complete successful RM-13 transaction for both values.</summary>
    [Theory]
    [InlineData("like", FeedbackValue.Like)]
    [InlineData("dislike", FeedbackValue.Dislike)]
    public async Task ExecuteAsync_WithUnratedOwnedItem_ShouldStoreFeedbackAndReturnCounters(
        string value,
        FeedbackValue expected)
    {
        var fixture = CreateFixture(new RoadmapItem { Id = 1502, DishId = 105, RecoRequestId = RecoRequestId }, 12);
        using var cancellationSource = new CancellationTokenSource();

        var result = await fixture.UseCase.ExecuteAsync(
            88, 1502, new RateRoadmapItemRequestDto { Value = value }, cancellationSource.Token);

        fixture.Added.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            UserId = 12,
            DishId = 105,
            Value = expected,
            RoadmapItemId = (long?)1502,
            RecoRequestId = (Guid?)RecoRequestId
        });
        fixture.Repository.Verify(repository => repository.IncrementDishFeedbackCountAsync(
            105, expected, cancellationSource.Token), Times.Once);
        fixture.Transaction.Verify(transaction => transaction.CommitAsync(cancellationSource.Token), Times.Once);
        result.Should().BeEquivalentTo(new RateRoadmapItemResponseDto
        {
            FeedbackId = 2201,
            Value = value,
            DishId = 105,
            LikeCount = 129,
            DislikeCount = 9,
            CreatedAt = CreatedAt
        });
    }

    // TEST-02: Reject an unauthenticated caller before opening a transaction.
    /// <summary>Verifies RM-13 authorization is enforced within Application as a defense in depth.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCurrentUser_ShouldThrowUnauthorizedWithoutPersistence()
    {
        var fixture = CreateFixture(new RoadmapItem(), null);

        var act = () => fixture.UseCase.ExecuteAsync(88, 1502, Like(), CancellationToken.None);

        (await act.Should().ThrowAsync<UnauthorizedException>()).Which.ErrorCode.Should().Be("UNAUTHORIZED");
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Hide an item outside an owned visible roadmap behind ROADMAP_ITEM_NOT_FOUND.
    /// <summary>Verifies nothing is written when the item lookup misses.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutOwnedItem_ShouldThrowNotFoundWithoutWriting()
    {
        var fixture = CreateFixture(null, 12);

        var act = () => fixture.UseCase.ExecuteAsync(88, 404, Like(), CancellationToken.None);

        (await act.Should().ThrowAsync<NotFoundException>()).Which.ErrorCode.Should().Be("ROADMAP_ITEM_NOT_FOUND");
        fixture.Added.Should().BeEmpty();
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Return FEEDBACK_ALREADY_EXISTS when the item was rated, found either before or by the unique index.
    /// <summary>Verifies the early check and the race fallback both end in 409 without counting twice.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteAsync_WhenItemAlreadyRated_ShouldThrowConflictWithoutCounting(bool foundBeforeInsert)
    {
        var fixture = CreateFixture(new RoadmapItem { Id = 1502, DishId = 105 }, 12, alreadyRated: foundBeforeInsert);
        if (!foundBeforeInsert)
        {
            fixture.UnitOfWork.Setup(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UniqueConstraintViolationException(
                    RateRoadmapItemUseCase.ItemFeedbackUniqueIndex, new InvalidOperationException()));
        }

        var act = () => fixture.UseCase.ExecuteAsync(88, 1502, Like(), CancellationToken.None);

        (await act.Should().ThrowAsync<ConflictException>()).Which.ErrorCode.Should().Be("FEEDBACK_ALREADY_EXISTS");
        fixture.Repository.Verify(repository => repository.IncrementDishFeedbackCountAsync(
            It.IsAny<int>(), It.IsAny<FeedbackValue>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Transaction.Verify(transaction => transaction.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Reject an unknown value before touching persistence.
    /// <summary>Verifies the validator runs first.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownValue_ShouldThrowValidation()
    {
        var fixture = CreateFixture(new RoadmapItem(), 12);

        var act = () => fixture.UseCase.ExecuteAsync(
            88, 1502, new RateRoadmapItemRequestDto { Value = "love" }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        fixture.UnitOfWork.Verify(unitOfWork => unitOfWork.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static RateRoadmapItemRequestDto Like() => new() { Value = "like" };

    private static Fixture CreateFixture(RoadmapItem? item, int? userId, bool alreadyRated = false)
    {
        var added = new List<DishFeedback>();
        var repository = new Mock<IRoadmapRepository>();
        repository.Setup(value => value.LockAndGetOwnedItemForFeedbackAsync(
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        repository.Setup(value => value.ItemHasFeedbackAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(alreadyRated);
        repository.Setup(value => value.AddFeedback(It.IsAny<DishFeedback>())).Callback<DishFeedback>(added.Add);
        repository.Setup(value => value.IncrementDishFeedbackCountAsync(
                It.IsAny<int>(), It.IsAny<FeedbackValue>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((129, 9));
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(value => value.UserId).Returns(userId);
        var transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(value => value.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        // The database assigns the id and created_at on save.
        unitOfWork.Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => added.ForEach(feedback =>
            {
                feedback.Id = 2201;
                feedback.CreatedAt = CreatedAt;
            }))
            .ReturnsAsync(1);

        return new Fixture(
            new RateRoadmapItemUseCase(
                new RateRoadmapItemRequestDtoValidator(), repository.Object, currentUser.Object, unitOfWork.Object),
            repository,
            unitOfWork,
            transaction,
            added);
    }

    private sealed record Fixture(
        RateRoadmapItemUseCase UseCase,
        Mock<IRoadmapRepository> Repository,
        Mock<IUnitOfWork> UnitOfWork,
        Mock<IUnitOfWorkTransaction> Transaction,
        List<DishFeedback> Added);
}
