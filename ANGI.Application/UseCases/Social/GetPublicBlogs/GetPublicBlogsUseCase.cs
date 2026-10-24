using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;
using FluentValidation;

namespace ANGI.Application.UseCases.Social.GetPublicBlogs;

/// <summary>Implements BLOG-01 by validating query filters and returning a paginated list of public blogs.</summary>
public sealed class GetPublicBlogsUseCase : IGetPublicBlogsUseCase
{
    public const int ExcerptLength = 200;

    private readonly IValidator<GetPublicBlogsRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;

    /// <summary>Initializes BLOG-01 with its request validator and blog repository.</summary>
    public GetPublicBlogsUseCase(
        IValidator<GetPublicBlogsRequestDto> validator,
        IBlogRepository blogRepository)
    {
        _validator = validator;
        _blogRepository = blogRepository;
    }

    /// <inheritdoc />
    public async Task<Paged<BlogCardDto>> ExecuteAsync(
        GetPublicBlogsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var (items, totalItems) = await _blogRepository.GetPublicBlogsAsync(
            request.Q,
            request.AuthorId,
            request.HasRoadmap,
            request.Sort,
            request.Page,
            request.PageSize,
            ct);

        var totalPages = (int)Math.Ceiling(totalItems / (double)request.PageSize);

        return new Paged<BlogCardDto>
        {
            Items = items.Select(MapToCard).ToArray(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }

    private static BlogCardDto MapToCard(Blog blog) => new()
    {
        Id = blog.Id,
        Title = blog.Title,
        Slug = blog.Slug,
        Excerpt = blog.Content.Length <= ExcerptLength
            ? blog.Content.Trim()
            : blog.Content[..ExcerptLength].TrimEnd(),
        CoverUrl = blog.CoverMedia?.Url,
        Author = new UserSummaryDto
        {
            Id = blog.Author?.Id ?? blog.AuthorId,
            DisplayName = blog.Author?.DisplayName ?? string.Empty,
            AvatarUrl = blog.Author?.AvatarMedia?.Url
        },
        HasRoadmap = blog.RoadmapSnapshot is not null,
        LikeCount = blog.LikeCount,
        CommentCount = blog.CommentCount,
        ViewCount = blog.ViewCount,
        PublishedAt = blog.PublishedAt
    };
}
