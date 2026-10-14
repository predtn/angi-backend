using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Account;
using ANGI.Application.UseCases.Account.UpdateMyProfile;
using ANGI.Application.UseCases.Validators.Account;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Account.UpdateMyProfile;

public sealed class UpdateMyProfileUseCaseTests
{
    private readonly IValidator<UpdateProfileRequestDto> _validator = new UpdateProfileRequestDtoValidator();
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IAccountRepository> _accountRepository = new();
    private readonly Mock<IMediaRepository> _mediaRepository = new();
    private readonly Mock<IAuditLogService> _auditLogService = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private UpdateMyProfileUseCase CreateUseCase() =>
        new(
            _validator,
            _currentUserService.Object,
            _accountRepository.Object,
            _mediaRepository.Object,
            _auditLogService.Object,
            _recommendationService.Object,
            _unitOfWork.Object);

    [Fact]
    public async Task ExecuteAsync_WithValidScalarUpdates_ShouldApplyChangesWriteAuditLogAndSave()
    {
        // Arrange
        const int userId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            Email = "an.nguyen@gmail.com",
            DisplayName = "Old Name",
            Phone = "0901111111",
            Bio = "Old Bio",
            Status = UserStatus.Active,
            Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" },
            CreatedAt = DateTime.UtcNow
        };

        _accountRepository.Setup(r => r.GetUserForUpdateAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _accountRepository.Setup(r => r.GetLinkedProvidersAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _accountRepository.Setup(r => r.GetEffectivePermissionsAsync(userId, "TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        AuditLogEntry? loggedEntry = null;
        _auditLogService.Setup(s => s.Add(It.IsAny<AuditLogEntry>()))
            .Callback<AuditLogEntry>(entry => loggedEntry = entry);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var request = new UpdateProfileRequestDto
        {
            DisplayName = "An Nguyễn Mới",
            Phone = "0909999999",
            Bio = "Bio Mới"
        };

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.DisplayName.Should().Be("An Nguyễn Mới");
        result.Phone.Should().Be("0909999999");
        result.Bio.Should().Be("Bio Mới");

        user.DisplayName.Should().Be("An Nguyễn Mới");
        user.Phone.Should().Be("0909999999");
        user.Bio.Should().Be("Bio Mới");

        loggedEntry.Should().NotBeNull();
        loggedEntry!.Action.Should().Be(AuditActions.ProfileUpdated);
        loggedEntry.EntityType.Should().Be(AuditEntityTypes.User);
        loggedEntry.EntityId.Should().Be(userId);
        loggedEntry.SubjectUserId.Should().Be(userId);

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithValidAvatarMediaId_ShouldUpdateAvatarAndReturnProfile()
    {
        // Arrange
        const int userId = 12;
        const long newAvatarId = 9050;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            Email = "an.nguyen@gmail.com",
            DisplayName = "Nguyễn An",
            AvatarMediaId = null,
            Status = UserStatus.Active,
            Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" },
            CreatedAt = DateTime.UtcNow
        };

        _accountRepository.Setup(r => r.GetUserForUpdateAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _accountRepository.Setup(r => r.GetLinkedProvidersAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _accountRepository.Setup(r => r.GetEffectivePermissionsAsync(userId, "TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var media = new MediaFile
        {
            Id = newAvatarId,
            UploaderId = userId,
            Url = "https://cdn.angi.vn/avatars/new.jpg",
            MimeType = "image/jpeg"
        };

        _mediaRepository.Setup(m => m.GetOwnedByIdsAsync(userId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([media]);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var request = new UpdateProfileRequestDto
        {
            AvatarMediaId = newAvatarId
        };

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/new.jpg");
        user.AvatarMediaId.Should().Be(newAvatarId);
        _auditLogService.Verify(s => s.Add(It.IsAny<AuditLogEntry>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WithNullAvatarMediaId_ShouldClearAvatar()
    {
        // Arrange
        const int userId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            Email = "an.nguyen@gmail.com",
            DisplayName = "Nguyễn An",
            AvatarMediaId = 8888,
            AvatarMedia = new MediaFile { Id = 8888, Url = "https://cdn.angi.vn/avatars/old.jpg" },
            Status = UserStatus.Active,
            Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" },
            CreatedAt = DateTime.UtcNow
        };

        _accountRepository.Setup(r => r.GetUserForUpdateAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _accountRepository.Setup(r => r.GetLinkedProvidersAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _accountRepository.Setup(r => r.GetEffectivePermissionsAsync(userId, "TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var request = new UpdateProfileRequestDto
        {
            AvatarMediaId = null
        };

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.AvatarUrl.Should().BeNull();
        user.AvatarMediaId.Should().BeNull();
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);
        var request = new UpdateProfileRequestDto { DisplayName = "Test" };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAvatarMediaNotFoundOrNotOwned_ShouldThrowNotFoundException()
    {
        // Arrange
        const int userId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            DisplayName = "Nguyễn An",
            Status = UserStatus.Active,
            Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" }
        };

        _accountRepository.Setup(r => r.GetUserForUpdateAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _mediaRepository.Setup(m => m.GetOwnedByIdsAsync(userId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var request = new UpdateProfileRequestDto { AvatarMediaId = 99999 };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("MEDIA_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_WhenAvatarMediaIsNotImage_ShouldThrowNotFoundException()
    {
        // Arrange
        const int userId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            DisplayName = "Nguyễn An",
            Status = UserStatus.Active,
            Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" }
        };

        _accountRepository.Setup(r => r.GetUserForUpdateAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        var pdfMedia = new MediaFile
        {
            Id = 5555,
            UploaderId = userId,
            Url = "https://cdn.angi.vn/docs/test.pdf",
            MimeType = "application/pdf"
        };

        _mediaRepository.Setup(m => m.GetOwnedByIdsAsync(userId, It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([pdfMedia]);

        var request = new UpdateProfileRequestDto { AvatarMediaId = 5555 };
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("MEDIA_NOT_FOUND");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNothingChanged_ShouldNotWriteAuditLogOrSave()
    {
        // Arrange
        const int userId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            DisplayName = "Nguyễn An",
            Phone = "0905123456",
            Bio = "Mê bún bò",
            Status = UserStatus.Active,
            Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" },
            CreatedAt = DateTime.UtcNow
        };

        _accountRepository.Setup(r => r.GetUserForUpdateAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _accountRepository.Setup(r => r.GetLinkedProvidersAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _accountRepository.Setup(r => r.GetEffectivePermissionsAsync(userId, "TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var request = new UpdateProfileRequestDto
        {
            DisplayName = "Nguyễn An",
            Phone = "0905123456",
            Bio = "Mê bún bò"
        };

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);

        // Assert
        result.DisplayName.Should().Be("Nguyễn An");
        _auditLogService.Verify(s => s.Add(It.IsAny<AuditLogEntry>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
