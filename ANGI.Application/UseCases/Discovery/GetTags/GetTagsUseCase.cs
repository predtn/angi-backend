using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Discovery;
using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Discovery.GetTags;

/// <summary>Implements CAT-02: the tag catalogue, sorted by type (meal_time, dietary, category), then name.</summary>
public sealed class GetTagsUseCase : IGetTagsUseCase
{
    // Invariant rather than vi-VN: a container in globalization-invariant mode has no vi-VN culture.
    private static readonly StringComparer Names = StringComparer.InvariantCultureIgnoreCase;

    private readonly IValidator<GetTagsRequestDto> _validator;
    private readonly ITagRepository _tagRepository;

    public GetTagsUseCase(IValidator<GetTagsRequestDto> validator, ITagRepository tagRepository)
    {
        _validator = validator;
        _tagRepository = tagRepository;
    }

    public async Task<IReadOnlyList<TagDto>> ExecuteAsync(GetTagsRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        TagType? type = request.Type is null
            ? null
            : Enum.GetValues<TagType>().Single(value => ToApiValue(value) == request.Type);
        var tags = await _tagRepository.GetTagsAsync(type, ct);

        return tags
            .OrderBy(tag => tag.TagType)
            .ThenBy(tag => tag.Name, Names)
            .Select(tag => new TagDto
            {
                Id = tag.Id,
                TagType = ToApiValue(tag.TagType),
                Code = tag.Code,
                Name = tag.Name
            })
            .ToArray();
    }

    private static string ToApiValue(TagType value) => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
