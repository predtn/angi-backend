using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.GetModBlogs;
using ANGI.Application.UseCases.Validators.Moderation;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.GetModBlogs;

/// <summary>Verifies MOD-B1 query validation, authorization, and repository dispatching.</summary>
public sealed class GetModBlogsUseCaseTests
{
    private static GetModBlogsUseCase Create(ModerationTestContext world) => new(
        new GetModBlogsRequestDtoValidator(),
        world.Access,
        world.BlogModerationRepo.Object);

    // TEST-01: Happy Path - Calls repository and returns paged result
    [Fact]
    public async Task HappyPath_ReturnsPagedBlogs()
    {
        var world = new ModerationTestContext();
        var expectedResult = new Paged<ModBlogCardDto>
        {
            Items =
            [
                new ModBlogCardDto
                {
                    Id = 45,
                    Title = "Test Blog",
                    Status = "published",
                    PendingReportCount = 1
                }
            ],
            Page = 1,
            PageSize = 20,
            TotalItems = 1,
            TotalPages = 1
        };

        world.BlogModerationRepo.Setup(x => x.ListBlogsAsync(It.IsAny<GetModBlogsRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var request = new GetModBlogsRequestDto
        {
            Page = 1,
            PageSize = 20,
            Status = "published"
        };

        var result = await Create(world).ExecuteAsync(request, default);

        result.Should().BeEquivalentTo(expectedResult);
        world.BlogModerationRepo.Verify(x => x.ListBlogsAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Validation Error - Page < 1 throws ValidationException
    [Fact]
    public async Task InvalidPage_ThrowsValidationException()
    {
        var world = new ModerationTestContext();
        var request = new GetModBlogsRequestDto { Page = 0 };

        var act = () => Create(world).ExecuteAsync(request, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-03: Validation Error - Invalid status throws ValidationException
    [Fact]
    public async Task InvalidStatus_ThrowsValidationException()
    {
        var world = new ModerationTestContext();
        var request = new GetModBlogsRequestDto { Status = "unknown" };

        var act = () => Create(world).ExecuteAsync(request, default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // TEST-04: Forbidden - Mod without BLOG_MODERATE throws ForbiddenException
    [Fact]
    public async Task MissingPermission_ThrowsForbiddenException()
    {
        var world = new ModerationTestContext();
        world.Accounts.Setup(x => x.GetEffectivePermissionsAsync(3, "MOD", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["RESTAURANT_MODERATE"]);

        var request = new GetModBlogsRequestDto();

        var act = () => Create(world).ExecuteAsync(request, default);

        var ex = await act.Should().ThrowAsync<ForbiddenException>();
        ex.Which.ErrorCode.Should().Be("FORBIDDEN");
    }
}
