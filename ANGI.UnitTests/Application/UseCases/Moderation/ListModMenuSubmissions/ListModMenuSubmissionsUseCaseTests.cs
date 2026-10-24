using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.UseCases.Moderation.ListModMenuSubmissions;
using ANGI.Application.UseCases.Validators.Moderation;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Moderation.ListModMenuSubmissions;

/// <summary>Covers queue mapping, empty pages and validation before querying.</summary>
public sealed class ListModMenuSubmissionsUseCaseTests
{
    // TEST-01: Map new, edited and removed counts with exact pagination metadata and unchanged tag sets.
    /// <summary>Verifies count classification, filter forwarding and partial final-page arithmetic.</summary>
    [Fact]
    public async Task ExecuteAsync_ShouldMapCountsAndPagination()
    {
        var restaurant = new ANGI.Domain.Entities.Restaurant { Id = 7, Name = "Restaurant" };
        var same = new Dish { Id = 1, Name = "Same", Restaurant = restaurant,
            DishTags = [new DishTag { TagId = 2 }, new DishTag { TagId = 1 }] };
        var edited = new Dish { Id = 2, Name = "Old", Restaurant = restaurant };
        restaurant.Dishes = [same, edited, new Dish { Id = 3, Restaurant = restaurant }];
        var submission = new MenuSubmission { Id = 14, RestaurantId = 7, Restaurant = restaurant,
            Status = MenuSubmissionStatus.Pending, Items =
            [
                new MenuSubmissionItem { Id = 1, SourceDishId = 1, SourceDish = same, Name = "Same", TagIds = [1, 2] },
                new MenuSubmissionItem { Id = 2, SourceDishId = 2, SourceDish = edited, Name = "Changed" },
                new MenuSubmissionItem { Id = 3, Name = "New" }
            ] };
        var request = new ListModMenuSubmissionsRequestDto { Page = 2, PageSize = 20, RestaurantId = 7 };
        using var cancellation = new CancellationTokenSource();
        var repository = new Mock<IMenuReviewRepository>();
        repository.Setup(x => x.HasPermissionAsync(10, cancellation.Token)).ReturnsAsync(true);
        repository.Setup(x => x.ListAsync(request, cancellation.Token)).ReturnsAsync((new[] { submission }, 21L));
        var result = await Create(repository).ExecuteAsync(request, cancellation.Token);

        result.Page.Should().Be(2);
        result.PageSize.Should().Be(20);
        result.TotalItems.Should().Be(21);
        result.TotalPages.Should().Be(2);
        result.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = 14L, Status = "pending", NewCount = 1, EditedCount = 1, RemovedCount = 1
        });
    }

    // TEST-02: Return zero pages for an empty queue and preserve total pages for an out-of-range page.
    /// <summary>Verifies empty collection behavior for both empty and non-empty queues.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    public async Task ExecuteAsync_EmptyPage_ShouldKeepMetadata(long total, int pages)
    {
        var repository = new Mock<IMenuReviewRepository>();
        repository.Setup(x => x.HasPermissionAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(x => x.ListAsync(It.IsAny<ListModMenuSubmissionsRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Array.Empty<MenuSubmission>(), total));
        var result = await Create(repository).ExecuteAsync(new ListModMenuSubmissionsRequestDto { Page = 99 }, CancellationToken.None);
        result.Items.Should().BeEmpty();
        result.TotalPages.Should().Be(pages);
        result.TotalItems.Should().Be(total);
    }

    // TEST-03: Reject invalid filters before permission or queue queries.
    /// <summary>Verifies validation is the first operation.</summary>
    [Fact]
    public async Task ExecuteAsync_Invalid_ShouldValidateFirst()
    {
        var repository = new Mock<IMenuReviewRepository>();
        var act = () => Create(repository).ExecuteAsync(new ListModMenuSubmissionsRequestDto { Status = "draft" }, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>();
        repository.VerifyNoOtherCalls();
    }

    // TEST-04: Reject denied queue access without loading submissions.
    /// <summary>Verifies effective permission guards the listing as well as decisions.</summary>
    [Fact]
    public async Task ExecuteAsync_Denied_ShouldNotQuery()
    {
        var repository = new Mock<IMenuReviewRepository>();
        var act = () => Create(repository).ExecuteAsync(new ListModMenuSubmissionsRequestDto(), CancellationToken.None);
        await act.Should().ThrowAsync<ForbiddenException>();
        repository.Verify(x => x.ListAsync(It.IsAny<ListModMenuSubmissionsRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // TEST-05: Classify each independently changed proposal field as edited and unchanged values as unchanged.
    /// <summary>Covers every field in the menu comparison, including tag-set equality and optional descriptions and covers.</summary>
    [Theory]
    [InlineData("name")]
    [InlineData("description")]
    [InlineData("form")]
    [InlineData("price")]
    [InlineData("cover")]
    [InlineData("order")]
    [InlineData("tags")]
    [InlineData("unchanged")]
    public async Task ExecuteAsync_ShouldCompareEveryProposedField(string field)
    {
        var restaurant = new ANGI.Domain.Entities.Restaurant { Id = 7, Name = "Restaurant" };
        var dish = new Dish { Id = 1, Name = "Dish", Restaurant = restaurant,
            Description = null, Price = 0, DishForm = DishForm.Other, CoverMediaId = null, SortOrder = 0 };
        restaurant.Dishes = [dish];
        var item = new MenuSubmissionItem { Id = 1, SourceDishId = 1, SourceDish = dish, Name = "Dish" };
        switch (field)
        {
            case "name": item.Name = "Changed"; break;
            case "description": item.Description = "New description"; break;
            case "form": item.DishForm = DishForm.Soup; break;
            case "price": item.Price = 1; break;
            case "cover": item.CoverMediaId = 9; break;
            case "order": item.SortOrder = 1; break;
            case "tags": item.TagIds = [1]; break;
        }
        var submission = new MenuSubmission { Id = 14, Restaurant = restaurant, Items = [item] };
        var repository = new Mock<IMenuReviewRepository>();
        repository.Setup(x => x.HasPermissionAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(x => x.ListAsync(It.IsAny<ListModMenuSubmissionsRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new[] { submission }, 1L));
        var result = await Create(repository).ExecuteAsync(new ListModMenuSubmissionsRequestDto(), CancellationToken.None);
        result.Items.Single().EditedCount.Should().Be(field == "unchanged" ? 0 : 1);
        result.Items.Single().NewCount.Should().Be(0);
        result.Items.Single().RemovedCount.Should().Be(0);
    }

    /// <summary>Builds an authenticated reviewer and the production queue validator.</summary>
    private static ListModMenuSubmissionsUseCase Create(Mock<IMenuReviewRepository> repository)
    {
        var current = new Mock<ICurrentUserService>();
        current.SetupGet(x => x.UserId).Returns(10);
        current.SetupGet(x => x.Role).Returns("MOD");
        return new(repository.Object, current.Object, new ListModMenuSubmissionsRequestDtoValidator());
    }
}
