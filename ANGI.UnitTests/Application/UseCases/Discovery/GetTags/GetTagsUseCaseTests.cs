using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.Application.UseCases.Discovery.GetTags;
using ANGI.Application.UseCases.Validators.Discovery;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentAssertions;
using FluentValidation;
using Moq;

namespace ANGI.UnitTests.Application.UseCases.Discovery.GetTags;

/// <summary>Verifies GetTagsUseCase filters and orders tags and rejects unknown tag types.</summary>
public sealed class GetTagsUseCaseTests
{
    private readonly Mock<ITagRepository> _tagRepository = new();

    /// <summary>Creates the use case with the real validator and the tag repository mock.</summary>
    private GetTagsUseCase CreateUseCase() => new(new GetTagsRequestDtoValidator(), _tagRepository.Object);

    // TEST-01: Sort by type (meal_time, dietary, category), then name, with snake_case types.
    /// <summary>Ensures clients receive tags in the documented type order with snake_case type names.</summary>
    [Fact]
    public async Task ExecuteAsync_ShouldSortByTypeThenName()
    {
        _tagRepository.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new Tag { Id = 9, TagType = TagType.Category, Code = "noodle", Name = "Món nước" },
            new Tag { Id = 3, TagType = TagType.MealTime, Code = "dinner", Name = "Bữa tối" },
            new Tag { Id = 6, TagType = TagType.Dietary, Code = "vegan", Name = "Chay" },
            new Tag { Id = 2, TagType = TagType.MealTime, Code = "breakfast", Name = "Bữa sáng" }
        ]);

        var result = await CreateUseCase().ExecuteAsync(new GetTagsRequestDto(), CancellationToken.None);

        result.Select(tag => (tag.Id, tag.TagType)).Should().Equal(
            (2, "meal_time"), (3, "meal_time"), (6, "dietary"), (9, "category"));
    }

    // TEST-04: The meal role, cooking method and ingredient groups follow category, in that order.
    /// <summary>Ensures the three newer tag groups come after the original three with their snake_case names.</summary>
    [Fact]
    public async Task ExecuteAsync_WithNewerTypes_ShouldSortThemAfterCategory()
    {
        _tagRepository.Setup(x => x.GetTagsAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new Tag { Id = 40, TagType = TagType.Ingredient, Code = "seafood", Name = "Hải sản" },
            new Tag { Id = 30, TagType = TagType.CookingMethod, Code = "method.nuong", Name = "Nướng" },
            new Tag { Id = 20, TagType = TagType.MealRole, Code = "role.mon_chinh", Name = "Món chính" },
            new Tag { Id = 10, TagType = TagType.Category, Code = "form.lau", Name = "Lẩu" }
        ]);

        var result = await CreateUseCase().ExecuteAsync(new GetTagsRequestDto(), CancellationToken.None);

        result.Select(tag => (tag.Id, tag.TagType)).Should().Equal(
            (10, "category"), (20, "meal_role"), (30, "cooking_method"), (40, "ingredient"));
    }

    // TEST-05: Accept every documented type as a filter.
    /// <summary>Ensures each of the six documented types reaches the repository as its enum value.</summary>
    [Theory]
    [InlineData("meal_time", TagType.MealTime)]
    [InlineData("dietary", TagType.Dietary)]
    [InlineData("category", TagType.Category)]
    [InlineData("meal_role", TagType.MealRole)]
    [InlineData("cooking_method", TagType.CookingMethod)]
    [InlineData("ingredient", TagType.Ingredient)]
    public async Task ExecuteAsync_WithEachDocumentedType_ShouldFilterByIt(string type, TagType expected)
    {
        _tagRepository.Setup(x => x.GetTagsAsync(expected, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await CreateUseCase().ExecuteAsync(new GetTagsRequestDto { Type = type }, CancellationToken.None);

        _tagRepository.Verify(x => x.GetTagsAsync(expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-02: Pass the requested type to the repository.
    /// <summary>Ensures the type filter reaches the repository query.</summary>
    [Fact]
    public async Task ExecuteAsync_WithType_ShouldFilterByIt()
    {
        _tagRepository.Setup(x => x.GetTagsAsync(TagType.Dietary, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await CreateUseCase().ExecuteAsync(new GetTagsRequestDto { Type = "dietary" }, CancellationToken.None);

        _tagRepository.Verify(x => x.GetTagsAsync(TagType.Dietary, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TEST-03: Reject an unknown type.
    /// <summary>Ensures an unknown tag type is rejected instead of returning an empty list.</summary>
    [Fact]
    public async Task ExecuteAsync_WithUnknownType_ShouldThrowValidation()
    {
        var act = () => CreateUseCase().ExecuteAsync(new GetTagsRequestDto { Type = "MealTime" }, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        _tagRepository.VerifyNoOtherCalls();
    }
}
