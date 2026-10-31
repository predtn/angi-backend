using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.GetUserDetail;
using FluentAssertions;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetUserDetail;

/// <summary>Verifies MOD-U2 user detail retrieval and error handling.</summary>
public sealed class GetUserDetailUseCaseTests
{
    [Fact]
    public async Task GetUserDetail_HappyPath_ReturnsCompleteDetail()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();

        var expectedDetail = new ModUserDetailDto
        {
            Id = 12,
            Email = "an.nguyen@gmail.com",
            DisplayName = "Nguyễn An",
            Role = "TRAVELER",
            Status = "active",
            SuspendedUntil = null,
            LastLoginAt = new DateTime(2026, 10, 1, 12, 30, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc),
            Sanctions =
            [
                new SanctionDto
                {
                    Id = 31,
                    Type = "suspend",
                    Reason = "Spam bình luận quảng cáo",
                    ReportId = 88,
                    IssuedBy = new UserSummaryDto
                    {
                        Id = 3,
                        DisplayName = "Mod",
                        AvatarUrl = "https://cdn.angi.vn/avatars/3.jpg"
                    },
                    StartsAt = new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc),
                    EndsAt = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc)
                }
            ],
            BlogCount = 4,
            ReviewCount = 11,
            ReportsAgainstContent = 3,
            Restaurant = null
        };

        repository.Setup(x => x.GetDetailAsync(12, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDetail);

        var useCase = new GetUserDetailUseCase(world.Access, repository.Object);

        // Act
        var result = await useCase.ExecuteAsync(12, default);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(12);
        result.DisplayName.Should().Be("Nguyễn An");
        result.Role.Should().Be("TRAVELER");
        result.Status.Should().Be("active");
        result.BlogCount.Should().Be(4);
        result.ReviewCount.Should().Be(11);
        result.ReportsAgainstContent.Should().Be(3);
        result.Restaurant.Should().BeNull();
        result.Sanctions.Should().ContainSingle();
        result.Sanctions[0].Id.Should().Be(31);
        result.Sanctions[0].Type.Should().Be("suspend");
        result.Sanctions[0].Reason.Should().Be("Spam bình luận quảng cáo");
    }

    [Fact]
    public async Task GetUserDetail_UserNotFound_ThrowsNotFoundException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();

        repository.Setup(x => x.GetDetailAsync(999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModUserDetailDto?)null);

        var useCase = new GetUserDetailUseCase(world.Access, repository.Object);

        // Act
        var act = () => useCase.ExecuteAsync(999, default);

        // Assert
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task GetUserDetail_InvalidUserId_ThrowsNotFoundException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();
        var useCase = new GetUserDetailUseCase(world.Access, repository.Object);

        // Act
        var act = () => useCase.ExecuteAsync(0, default);

        // Assert
        var exception = await act.Should().ThrowAsync<NotFoundException>();
        exception.Which.ErrorCode.Should().Be("USER_NOT_FOUND");
        repository.Verify(x => x.GetDetailAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
