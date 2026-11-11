using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Social.GetMyBlogs;

/// <summary>Coordinates validation, identity resolution, and persistence mapping for BLOG-05.</summary>
public sealed class GetMyBlogsUseCase : IGetMyBlogsUseCase
{
    private const int ExcerptLength = 200;

    private readonly IValidator<GetMyBlogsRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes dependencies required to list personal blogs.</summary>
    public GetMyBlogsUseCase(
        IValidator<GetMyBlogsRequestDto> validator,
        IBlogRepository blogRepository,
        ICurrentUserService currentUserService)
    {
        _validator = validator;
        _blogRepository = blogRepository;
        _currentUserService = currentUserService;
    }

    /// <summary>Validates paging and filter, scopes to the current user, and returns paged blog cards.</summary>
    public async Task<Paged<MyBlogCardDto>> ExecuteAsync(
        GetMyBlogsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thực hiện thao tác này.");

        BlogStatus? filterStatus = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            filterStatus = request.Status.Trim().ToLowerInvariant() switch
            {
                "published" => BlogStatus.Published,
                "hidden" => BlogStatus.Hidden,
                "removed" => BlogStatus.Removed,
                _ => null
            };
        }

        var (items, moderationNotes, totalItems) = await _blogRepository.GetByAuthorAsync(
            userId,
            filterStatus,
            request.Page,
            request.PageSize,
            ct);

        var totalPages = (int)Math.Ceiling((double)totalItems / request.PageSize);

        var mappedItems = items.Select(blog =>
        {
            var excerpt = blog.Content.Length <= ExcerptLength
                ? blog.Content.Trim()
                : blog.Content[..ExcerptLength].TrimEnd();

            var statusStr = blog.Status switch
            {
                BlogStatus.Published => "published",
                BlogStatus.Hidden => "hidden",
                BlogStatus.Removed => "removed",
                _ => blog.Status.ToString().ToLowerInvariant()
            };

            return new MyBlogCardDto
            {
                Id = blog.Id,
                Title = blog.Title,
                Slug = blog.Slug,
                Excerpt = excerpt,
                CoverUrl = blog.CoverMedia?.Url,
                Author = new UserSummaryDto
                {
                    Id = blog.Author.Id,
                    DisplayName = blog.Author.DisplayName,
                    AvatarUrl = blog.Author.AvatarMedia?.Url
                },
                HasRoadmap = !string.IsNullOrEmpty(blog.RoadmapSnapshot),
                LikeCount = blog.LikeCount,
                CommentCount = blog.CommentCount,
                ViewCount = blog.ViewCount,
                PublishedAt = blog.PublishedAt,
                Status = statusStr,
                ModerationNote = moderationNotes.GetValueOrDefault(blog.Id)
            };
        }).ToList();

        return new Paged<MyBlogCardDto>
        {
            Items = mappedItems,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }
}
