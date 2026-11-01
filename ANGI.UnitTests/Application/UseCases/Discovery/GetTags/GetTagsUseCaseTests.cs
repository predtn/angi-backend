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
