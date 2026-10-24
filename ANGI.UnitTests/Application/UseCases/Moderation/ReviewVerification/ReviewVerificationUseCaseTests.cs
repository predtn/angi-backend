using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.ReviewVerification;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ReviewVerification;

public sealed class ReviewVerificationUseCaseTests
{
    private static readonly DateTime ReviewedAt = new(2026, 10, 21, 4, 30, 0, DateTimeKind.Utc);

    // TEST-01: Apply every decision branch with the documented statuses, notification, audit, and atomic commit.
    /// <summary>Verifies all successful MOD-V3 state transitions and side effects.</summary>
    [Theory]
    [InlineData("approve", null, VerificationStatus.Approved, RestaurantVerificationStatus.Verified, "VERIFICATION_APPROVED", "RESTAURANT_VERIFIED")]
    [InlineData("reject", "Invalid license", VerificationStatus.Rejected, RestaurantVerificationStatus.Rejected, "VERIFICATION_REJECTED", "RESTAURANT_VERIFICATION_REJECTED")]
    [InlineData("needs_more_info", "Provide food certificate", VerificationStatus.NeedsMoreInfo, RestaurantVerificationStatus.Rejected, "VERIFICATION_NEEDS_MORE_INFO", "RESTAURANT_VERIFICATION_REJECTED")]
    public async Task ExecuteAsync_WithPendingVerification_ShouldApplyDecisionAtomically(
        string decision,
        string? note,
        VerificationStatus expectedVerificationStatus,
        RestaurantVerificationStatus expectedRestaurantStatus,
        string expectedNotificationType,
        string expectedAuditAction)
    {
        var verification = PendingVerification();
        var repository = AuthorizedRepository();
        repository.Setup(item => item.LockForDecisionAsync(verification.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(verification);
        repository.Setup(item => item.LoadReviewerAsync(verification, It.IsAny<CancellationToken>()))
            .Callback(() => verification.Reviewer = new User { Id = 9, DisplayName = "Mod Reviewer" })
            .Returns(Task.CompletedTask);
        Domain.Entities.Notification? notification = null;
        repository.Setup(item => item.AddNotification(It.IsAny<Domain.Entities.Notification>()))
            .Callback<Domain.Entities.Notification>(value => notification = value);
        AuditLogEntry? audit = null;
        var auditService = new Mock<IAuditLogService>();
        auditService.Setup(item => item.Add(It.IsAny<AuditLogEntry>()))
            .Callback<AuditLogEntry>(value => audit = value);
        var unitOfWork = UnitOfWork(out var transaction);
        var useCase = CreateUseCase(repository, auditService.Object, unitOfWork.Object);

        var result = await useCase.ExecuteAsync(verification.Id, new ReviewVerificationRequestDto
        {
            Decision = decision,
            ReviewNote = note
        }, CancellationToken.None);

        verification.Status.Should().Be(expectedVerificationStatus);
        verification.Restaurant.VerificationStatus.Should().Be(expectedRestaurantStatus);
        verification.ReviewedBy.Should().Be(9);
        verification.ReviewedAt.Should().Be(ReviewedAt);
        result.Status.Should().Be(ToApiValue(expectedVerificationStatus));
        result.ReviewedBy!.DisplayName.Should().Be("Mod Reviewer");
        notification.Should().NotBeNull();
        notification!.RecipientId.Should().Be(12);
        notification.Type.Should().Be(expectedNotificationType);
        notification.Data.Should().Contain("\"verificationId\":21");
        audit.Should().NotBeNull();
        audit!.Action.Should().Be(expectedAuditAction);
        audit.EntityType.Should().Be("restaurant");
        audit.EntityId.Should().Be(7);
        audit.SubjectUserId.Should().Be(12);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Validate a conditional note before permission checks or transaction creation.
    /// <summary>Verifies MOD-V3 first-statement validation for rejection without a note.</summary>
    [Fact]
    public async Task ExecuteAsync_WithInvalidRequest_ShouldFailBeforeAuthorization()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(repository, Mock.Of<IAuditLogService>(), unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(21, new ReviewVerificationRequestDto
        {
            Decision = "reject"
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        repository.Verify(item => item.HasEffectivePermissionAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-03: Return VERIFICATION_NOT_FOUND without writing or committing.
    /// <summary>Verifies the documented MOD-V3 missing-record branch inside the transaction.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenVerificationMissing_ShouldThrowNotFound()
    {
        var repository = AuthorizedRepository();
        repository.Setup(item => item.LockForDecisionAsync(404, It.IsAny<CancellationToken>()))
            .ReturnsAsync((RestaurantVerification?)null);
        var unitOfWork = UnitOfWork(out var transaction);
        var useCase = CreateUseCase(repository, Mock.Of<IAuditLogService>(), unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(404, ApproveRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("VERIFICATION_NOT_FOUND");
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-04: Reject every non-pending state as already reviewed.
    /// <summary>Verifies the full excluded status region for MOD-V3 idempotency and concurrency safety.</summary>
    [Theory]
    [InlineData(VerificationStatus.Approved)]
    [InlineData(VerificationStatus.Rejected)]
    [InlineData(VerificationStatus.NeedsMoreInfo)]
    [InlineData(VerificationStatus.Cancelled)]
    public async Task ExecuteAsync_WithNonPendingVerification_ShouldThrowConflict(VerificationStatus status)
    {
        var verification = PendingVerification();
        verification.Status = status;
        var repository = AuthorizedRepository();
        repository.Setup(item => item.LockForDecisionAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(verification);
        var unitOfWork = UnitOfWork(out var transaction);
        var useCase = CreateUseCase(repository, Mock.Of<IAuditLogService>(), unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(21, ApproveRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ConflictException>();
        exception.Which.ErrorCode.Should().Be("VERIFICATION_ALREADY_REVIEWED");
        repository.Verify(item => item.AddNotification(It.IsAny<Domain.Entities.Notification>()), Times.Never);
        unitOfWork.Verify(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Reject a denied effective permission before opening a transaction.
    /// <summary>Verifies MOD-V3 cannot be raced or probed by a Mod lacking RESTAURANT_VERIFY.</summary>
    [Fact]
    public async Task ExecuteAsync_WithoutEffectivePermission_ShouldThrowForbidden()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        repository.Setup(item => item.HasEffectivePermissionAsync(9, "RESTAURANT_VERIFY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(repository, Mock.Of<IAuditLogService>(), unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(21, ApproveRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenException>();
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-06: Do not commit when persistence fails after all side effects are staged.
    /// <summary>Verifies rollback-by-disposal keeps verification, notification, and audit atomic.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenSaveFails_ShouldNotCommit()
    {
        var verification = PendingVerification();
        var repository = AuthorizedRepository();
        repository.Setup(item => item.LockForDecisionAsync(21, It.IsAny<CancellationToken>()))
            .ReturnsAsync(verification);
        repository.Setup(item => item.LoadReviewerAsync(verification, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var unitOfWork = UnitOfWork(out var transaction);
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database failure"));
        var useCase = CreateUseCase(repository, Mock.Of<IAuditLogService>(), unitOfWork.Object);

        var act = () => useCase.ExecuteAsync(21, ApproveRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        transaction.Verify(item => item.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-07: Reject non-Mod roles before opening a transaction or staging side effects.
    /// <summary>Verifies MOD-V3 never accepts an Admin decision despite permission grants.</summary>
    [Theory]
    [InlineData("ADMIN")]
    [InlineData("TRAVELER")]
    [InlineData("RESTAURANT_OWNER")]
    public async Task ExecuteAsync_WithNonModRole_ShouldThrowForbidden(string role)
    {
        var repository = AuthorizedRepository();
        var unitOfWork = new Mock<IUnitOfWork>();
        var useCase = CreateUseCase(repository, Mock.Of<IAuditLogService>(), unitOfWork.Object, role);

        var act = () => useCase.ExecuteAsync(21, ApproveRequest(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ForbiddenException>();
        exception.Which.ErrorCode.Should().Be("FORBIDDEN");
        unitOfWork.Verify(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(item => item.AddNotification(It.IsAny<Domain.Entities.Notification>()), Times.Never);
    }

    /// <summary>Creates MOD-V3 with real validation, deterministic time, and a configurable authenticated role.</summary>
    private static ReviewVerificationUseCase CreateUseCase(
        Mock<IVerificationReviewRepository> repository,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork,
        string role = "MOD")
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(item => item.UserId).Returns(9);
        currentUser.SetupGet(item => item.Role).Returns(role);
        var cloudinary = new Mock<ICloudinaryService>();
        cloudinary.Setup(item => item.GetUrl(It.IsAny<string>()))
            .Returns<string>(key => $"https://signed.test/{key}");
        return new ReviewVerificationUseCase(
            new ReviewVerificationRequestDtoValidator(),
            repository.Object,
            currentUser.Object,
            cloudinary.Object,
            auditLogService,
            unitOfWork,
            new FixedTimeProvider(ReviewedAt));
    }

    /// <summary>Creates a repository mock that grants the required effective permission.</summary>
    private static Mock<IVerificationReviewRepository> AuthorizedRepository()
    {
        var repository = new Mock<IVerificationReviewRepository>();
        repository.Setup(item => item.HasEffectivePermissionAsync(9, "RESTAURANT_VERIFY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return repository;
    }

    /// <summary>Creates a unit of work with an async-disposable transaction.</summary>
    private static Mock<IUnitOfWork> UnitOfWork(out Mock<IUnitOfWorkTransaction> transaction)
    {
        transaction = new Mock<IUnitOfWorkTransaction>();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(item => item.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction.Object);
        unitOfWork.Setup(item => item.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        return unitOfWork;
    }

    /// <summary>Creates a complete pending aggregate for decision and response mapping.</summary>
    private static RestaurantVerification PendingVerification()
    {
        var restaurant = new Domain.Entities.Restaurant
        {
            Id = 7,
            OwnerId = 12,
            Name = "Restaurant",
            Slug = "restaurant",
            AddressLine = "1 Street",
            Latitude = 10m,
            Longitude = 106m,
            VerificationStatus = RestaurantVerificationStatus.Pending
        };
        return new RestaurantVerification
        {
            Id = 21,
            RestaurantId = restaurant.Id,
            Restaurant = restaurant,
            Status = VerificationStatus.Pending,
            LegalName = "Legal Restaurant",
            BusinessLicenseNo = "LICENSE-001",
            SubmittedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Documents =
            [
                new RestaurantVerificationDocument
                {
                    MediaId = 9001,
                    DocType = VerificationDocumentType.BusinessLicense,
                    Media = new MediaFile { Id = 9001, StorageKey = "license.pdf" }
                }
            ]
        };
    }

    /// <summary>Creates the valid approve request used by exception-path tests.</summary>
    private static ReviewVerificationRequestDto ApproveRequest() => new() { Decision = "approve" };

    /// <summary>Converts an enum to the expected snake_case API value.</summary>
    private static string ToApiValue(VerificationStatus status) => status switch
    {
        VerificationStatus.NeedsMoreInfo => "needs_more_info",
        _ => status.ToString().ToLowerInvariant()
    };

    /// <summary>Supplies a deterministic UTC instant to MOD-V3.</summary>
    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        /// <summary>Returns the configured UTC instant.</summary>
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
