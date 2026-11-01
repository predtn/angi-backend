using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.GetUsers;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetUsers;

/// <summary>Verifies MOD-U1 user list logic, pagination, filtering and validation.</summary>
public sealed class GetUsersUseCaseTests
{
    [Fact]
    public async Task ListUsers_HappyPath_ReturnsPagedUsers()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();
        var request = new ListUsersRequestDto { Page = 1, PageSize = 20 };

        var pagedUsers = new Paged<AdminUserCardDto>
        {
            Items =
            [
                new AdminUserCardDto
                {
                    Id = 12,
                    Email = "an.nguyen@gmail.com",
                    DisplayName = "Nguyễn An",
                    Role = "TRAVELER",
                    Status = "active",
                    CreatedAt = new DateTime(2026, 9, 20, 3, 0, 0, DateTimeKind.Utc)
                }
            ],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        repository.Setup(x => x.ListAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pagedUsers);

        var useCase = new GetUsersUseCase(new ListUsersRequestDtoValidator(), world.Access, repository.Object);

        // Act
        var result = await useCase.ExecuteAsync(request, default);

        // Assert
        result.Should().NotBeNull();
        result.Items.Should().ContainSingle();
        result.Items[0].Id.Should().Be(12);
        result.Items[0].Email.Should().Be("an.nguyen@gmail.com");
        result.Items[0].Role.Should().Be("TRAVELER");
        result.Items[0].Status.Should().Be("active");
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(1);
        result.TotalPages.Should().Be(1);
    }

    [Fact]
    public async Task ListUsers_InvalidPagination_ThrowsValidationException()
    {
        // Arrange
        var world = new ModerationTestContext();
        var repository = new Mock<IUserModerationRepository>();
        var useCase = new GetUsersUseCase(new ListUsersRequestDtoValidator(), world.Access, repository.Object);
        var request = new ListUsersRequestDto { Page = 0, PageSize = 20 };

        // Act
        var act = () => useCase.ExecuteAsync(request, default);

        // Assert
        await act.Should().ThrowAsync<ValidationException>();
        repository.Verify(x => x.ListAsync(It.IsAny<ListUsersRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
