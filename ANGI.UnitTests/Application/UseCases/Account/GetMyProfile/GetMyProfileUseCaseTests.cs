using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Account;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Recommendation;
using ANGI.Application.Common.Models.Recommendation;
using ANGI.Application.UseCases.Account.GetMyProfile;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Account.GetMyProfile;

public sealed class GetMyProfileUseCaseTests
{
    private readonly Mock<ICurrentUserService> _currentUserService = new();
    private readonly Mock<IAccountRepository> _accountRepository = new();
    private readonly Mock<IRecommendationService> _recommendationService = new();

    private GetMyProfileUseCase CreateUseCase() =>
        new(_currentUserService.Object, _accountRepository.Object, _recommendationService.Object);

    // TEST-01: Verify ExecuteAsync when authenticated traveler; should return complete profile.
    /// <summary>Verify ExecuteAsync when authenticated traveler; should return complete profile.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenAuthenticatedTraveler_ShouldReturnCompleteProfile()
    {
        // Arrange
        const int userId = 12;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            Email = "an.nguyen@gmail.com",
            DisplayName = "Nguyễn An",
            Phone = "0905123456",
            Bio = "Mê ăn bún bò",
            Status = UserStatus.Active,
            EmailVerifiedAt = DateTime.UtcNow,
            PasswordHash = "hash",
            Role = new Role { Id = 1, Code = "TRAVELER", Name = "Traveler" },
            AvatarMedia = new MediaFile { Id = 9050, Url = "https://cdn.angi.vn/avatars/12.jpg" },
            CreatedAt = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc)
        };

        _accountRepository.Setup(r => r.GetProfileUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _accountRepository.Setup(r => r.GetLinkedProvidersAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(["google"]);
        _accountRepository.Setup(r => r.GetEffectivePermissionsAsync(userId, "TRAVELER", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _recommendationService.Setup(r => r.GetSurveyStatusAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SurveyStatus(Completed: true, SurveyAt: DateTime.UtcNow, AnswerCount: 5));

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(12);
        result.Email.Should().Be("an.nguyen@gmail.com");
        result.DisplayName.Should().Be("Nguyễn An");
        result.Role.Should().Be("TRAVELER");
        result.AvatarUrl.Should().Be("https://cdn.angi.vn/avatars/12.jpg");
        result.Phone.Should().Be("0905123456");
        result.Bio.Should().Be("Mê ăn bún bò");
        result.Status.Should().Be("active");
        result.EmailVerified.Should().BeTrue();
        result.HasPassword.Should().BeTrue();
        result.LinkedProviders.Should().ContainSingle().Which.Should().Be("google");
        result.PreferenceStatus.Should().Be("completed");
        result.RestaurantId.Should().BeNull();
        result.Permissions.Should().BeEmpty();
    }

    // TEST-02: Verify ExecuteAsync when restaurant owner; should include restaurant id.
    /// <summary>Verify ExecuteAsync when restaurant owner; should include restaurant id.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenRestaurantOwner_ShouldIncludeRestaurantId()
    {
        // Arrange
        const int userId = 25;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            Email = "owner@angi.vn",
            DisplayName = "Chủ Quán",
            Status = UserStatus.Active,
            Role = new Role { Id = 2, Code = "RESTAURANT_OWNER", Name = "Restaurant Owner" },
            CreatedAt = DateTime.UtcNow
        };

        _accountRepository.Setup(r => r.GetProfileUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _accountRepository.Setup(r => r.GetLinkedProvidersAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _accountRepository.Setup(r => r.GetOwnerRestaurantIdAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(99L);
        _accountRepository.Setup(r => r.GetEffectivePermissionsAsync(userId, "RESTAURANT_OWNER", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        result.Role.Should().Be("RESTAURANT_OWNER");
        result.RestaurantId.Should().Be(99L);
        result.PreferenceStatus.Should().BeNull();
    }

    // TEST-03: Verify ExecuteAsync when mod user; should include effective permissions.
    /// <summary>Verify ExecuteAsync when mod user; should include effective permissions.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenModUser_ShouldIncludeEffectivePermissions()
    {
        // Arrange
        const int userId = 30;
        _currentUserService.Setup(s => s.UserId).Returns(userId);

        var user = new User
        {
            Id = userId,
            Email = "mod@angi.vn",
            DisplayName = "Mod User",
            Status = UserStatus.Active,
            Role = new Role { Id = 3, Code = "MOD", Name = "Mod" },
            CreatedAt = DateTime.UtcNow
        };

        _accountRepository.Setup(r => r.GetProfileUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        _accountRepository.Setup(r => r.GetLinkedProvidersAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _accountRepository.Setup(r => r.GetEffectivePermissionsAsync(userId, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["USER_SUSPEND", "MENU_APPROVE"]);

        var useCase = CreateUseCase();

        // Act
        var result = await useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        result.Role.Should().Be("MOD");
        result.Permissions.Should().BeEquivalentTo(["USER_SUSPEND", "MENU_APPROVE"]);
    }

    // TEST-04: Verify ExecuteAsync when not authenticated; should throw unauthorized exception.
    /// <summary>Verify ExecuteAsync when not authenticated; should throw unauthorized exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenNotAuthenticated_ShouldThrowUnauthorizedException()
    {
        // Arrange
        _currentUserService.Setup(s => s.UserId).Returns((int?)null);
        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<UnauthorizedException>();
        ex.Which.ErrorCode.Should().Be("UNAUTHORIZED");
    }

    // TEST-05: Verify ExecuteAsync when user not found; should throw not found exception.
    /// <summary>Verify ExecuteAsync when user not found; should throw not found exception.</summary>
    [Fact]
    public async Task ExecuteAsync_WhenUserNotFound_ShouldThrowNotFoundException()
    {
        // Arrange
        const int userId = 999;
        _currentUserService.Setup(s => s.UserId).Returns(userId);
        _accountRepository.Setup(r => r.GetProfileUserAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        var useCase = CreateUseCase();

        // Act
        var act = () => useCase.ExecuteAsync(CancellationToken.None);

        // Assert
        var ex = await act.Should().ThrowAsync<NotFoundException>();
        ex.Which.ErrorCode.Should().Be("USER_NOT_FOUND");
    }
}
