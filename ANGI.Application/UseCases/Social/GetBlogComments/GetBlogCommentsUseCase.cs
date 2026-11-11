using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Social.GetBlogComments;

/// <summary>Implements BLOG-03 by retrieving paginated visible comments for a published blog.</summary>
public sealed class GetBlogCommentsUseCase : IGetBlogCommentsUseCase
{
    private readonly IValidator<GetBlogCommentsRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes BLOG-03 with validation, blog persistence, and caller identity services.</summary>
    public GetBlogCommentsUseCase(
        IValidator<GetBlogCommentsRequestDto> validator,
        IBlogRepository blogRepository,
        ICurrentUserService currentUserService)
    {
        _validator = validator;
        _blogRepository = blogRepository;
        _currentUserService = currentUserService;
    }

    /// <inheritdoc />
    public async Task<Paged<CommentDto>> ExecuteAsync(
        long blogId,
        GetBlogCommentsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var exists = await _blogRepository.ExistsPublishedAsync(blogId, ct);
        if (!exists)
        {
            throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");
        }

        var (items, totalItems) = await _blogRepository.GetVisibleCommentsAsync(
            blogId,
            request.Page,
            request.PageSize,
            ct);

        var currentUserId = _currentUserService.UserId;
        var totalPages = (int)Math.Ceiling(totalItems / (double)request.PageSize);

        return new Paged<CommentDto>
        {
            Items = items.Select(comment => BlogMapper.MapToComment(comment, currentUserId)).ToArray(),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }
}
