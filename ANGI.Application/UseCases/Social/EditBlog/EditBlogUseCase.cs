using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.Services.Social;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.Common.Models.Social;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Social.EditBlog;

/// <summary>Implements BLOG-06 as an atomic update of a traveler-owned blog aggregate.</summary>
public sealed partial class EditBlogUseCase : IEditBlogUseCase
{
    private const int ExcerptLength = 200;
    private readonly IValidator<EditBlogRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly IBlogContentSanitizer _contentSanitizer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes BLOG-06 with validation, repository, identity, sanitization, and transaction boundaries.</summary>
    public EditBlogUseCase(
        IValidator<EditBlogRequestDto> validator,
        IBlogRepository blogRepository,
        IMediaRepository mediaRepository,
        IBlogContentSanitizer contentSanitizer,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _blogRepository = blogRepository;
        _mediaRepository = mediaRepository;
        _contentSanitizer = contentSanitizer;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<BlogDetailDto> ExecuteAsync(long blogId, EditBlogRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var currentUserId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để chỉnh sửa bài viết.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        var blog = await _blogRepository.GetTrackedByIdAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");

        if (blog.AuthorId != currentUserId)
        {
            throw new ForbiddenException("NOT_OWNER", "Bạn không có quyền chỉnh sửa bài viết này.");
        }

        if (blog.Status == BlogStatus.Removed)
        {
            throw new ConflictException("BLOG_REMOVED", "Bài viết đã bị gỡ bởi kiểm duyệt viên nên không thể chỉnh sửa.");
        }

        var mediaIdsToCheck = GetMediaIdsToCheck(request);
        var ownedMedia = mediaIdsToCheck.Count == 0
            ? Array.Empty<MediaFile>()
            : await _mediaRepository.GetOwnedByIdsAsync(currentUserId, mediaIdsToCheck, ct);
        EnsureAllMediaExist(request, ownedMedia);

        if (request.TitleSpecified && request.Title is not null)
        {
            var trimmedTitle = request.Title.Trim();
            if (!string.Equals(blog.Title, trimmedTitle, StringComparison.Ordinal))
            {
                blog.Title = trimmedTitle;
                blog.Slug = await CreateUniqueSlugAsync(trimmedTitle, blog.Id, ct);
            }
        }

        if (request.ContentSpecified && request.Content is not null)
        {
            var sanitizedContent = _contentSanitizer.Sanitize(request.Content.Trim());
            EnsureSanitizedContentIsNotEmpty(sanitizedContent);
            blog.Content = sanitizedContent;
        }

        if (request.CoverMediaIdSpecified)
        {
            blog.CoverMediaId = request.CoverMediaId;
            if (!request.CoverMediaId.HasValue)
            {
                blog.CoverMedia = null;
            }
        }

        if (request.ImageMediaIdsSpecified)
        {
            UpdateImages(blog, request.ImageMediaIds);
        }

        RoadmapSnapshotDto? snapshot = null;
        if (request.RefreshRoadmapSnapshotSpecified && request.RefreshRoadmapSnapshot == true)
        {
            if (blog.RoadmapId.HasValue)
            {
                var refreshedSnapshot = await CreateSnapshotAsync(
                    blog.RoadmapId.Value,
                    currentUserId,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    ct);

                if (refreshedSnapshot is not null)
                {
                    blog.RoadmapSnapshot = JsonSerializer.Serialize(
                        refreshedSnapshot,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    snapshot = refreshedSnapshot;
                }
            }
        }

        if (snapshot is null && !string.IsNullOrWhiteSpace(blog.RoadmapSnapshot))
        {
            try
            {
                snapshot = JsonSerializer.Deserialize<RoadmapSnapshotDto>(
                    blog.RoadmapSnapshot,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            catch
            {
                snapshot = null;
            }
        }

        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var allMediaIds = new HashSet<long>();
        if (blog.CoverMediaId.HasValue)
        {
            allMediaIds.Add(blog.CoverMediaId.Value);
        }
        foreach (var image in blog.Images)
        {
            allMediaIds.Add(image.MediaId);
        }

        var mediaMap = allMediaIds.Count == 0
            ? new Dictionary<long, MediaFile>()
            : (await _mediaRepository.GetOwnedByIdsAsync(currentUserId, allMediaIds, ct)).ToDictionary(item => item.Id);

        var author = blog.Author ?? await _blogRepository.GetAuthorAsync(blog.AuthorId, ct)
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Tài khoản không còn tồn tại.");

        return MapToDetail(blog, author, mediaMap, snapshot);
    }

    /// <summary>Collects media IDs from the request that require caller ownership verification.</summary>
    private static IReadOnlyCollection<long> GetMediaIdsToCheck(EditBlogRequestDto request)
    {
        var ids = new HashSet<long>();
        if (request.CoverMediaIdSpecified && request.CoverMediaId is { } coverId)
        {
            ids.Add(coverId);
        }

        if (request.ImageMediaIdsSpecified && request.ImageMediaIds is not null)
        {
            foreach (var imgId in request.ImageMediaIds)
            {
                ids.Add(imgId);
            }
        }

        return ids;
    }

    /// <summary>Raises validation failures when requested media identifiers do not exist or are not owned.</summary>
    private static void EnsureAllMediaExist(
        EditBlogRequestDto request,
        IReadOnlyCollection<MediaFile> media)
    {
        var foundIds = media.Select(item => item.Id).ToHashSet();
        var failures = new List<ValidationFailure>();

        if (request.CoverMediaIdSpecified && request.CoverMediaId is { } coverId && !foundIds.Contains(coverId))
        {
            failures.Add(new ValidationFailure(
                nameof(request.CoverMediaId),
                $"Ảnh bìa {coverId} không tồn tại hoặc không phải ảnh bạn đã tải lên."));
        }

        if (request.ImageMediaIdsSpecified && request.ImageMediaIds is not null)
        {
            var missingIds = request.ImageMediaIds
                .Where(id => !foundIds.Contains(id))
                .ToArray();

            if (missingIds.Length > 0)
            {
                failures.Add(new ValidationFailure(
                    nameof(request.ImageMediaIds),
                    $"Ảnh {string.Join(", ", missingIds)} không tồn tại hoặc không phải ảnh bạn đã tải lên."));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    /// <summary>Rejects HTML content whose entire body was stripped by the security sanitizer.</summary>
    private static void EnsureSanitizedContentIsNotEmpty(string content)
    {
        if (string.IsNullOrWhiteSpace(StripTagsRegex().Replace(content, string.Empty)))
        {
            throw new ValidationException([
                new ValidationFailure(nameof(EditBlogRequestDto.Content), "Nội dung bài viết phải có nội dung an toàn.")
            ]);
        }
    }

    /// <summary>Synchronizes the blog's join table collection with the newly provided ordered list of media identifiers.</summary>
    private static void UpdateImages(Blog blog, IReadOnlyList<long>? newImageIds)
    {
        var newIds = newImageIds ?? Array.Empty<long>();
        var newIdSet = newIds.ToHashSet();

        foreach (var removedImage in blog.Images.Where(image => !newIdSet.Contains(image.MediaId)).ToArray())
        {
            blog.Images.Remove(removedImage);
        }

        var existingImages = blog.Images.ToDictionary(image => image.MediaId);
        for (short index = 0; index < newIds.Count; index++)
        {
            if (existingImages.TryGetValue(newIds[index], out var existingImage))
            {
                existingImage.SortOrder = index;
            }
            else
            {
                blog.Images.Add(new BlogImage { MediaId = newIds[index], SortOrder = index });
            }
        }
    }

    /// <summary>Creates a readable unique slug from a Vietnamese blog title, ignoring the current blog id collision.</summary>
    private async Task<string> CreateUniqueSlugAsync(string title, long blogId, CancellationToken ct)
    {
        var normalized = title.Trim().ToLowerInvariant().Replace('đ', 'd');
        var decomposed = normalized.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var baseSlug = NonSlugCharacterRegex()
            .Replace(builder.ToString().Normalize(NormalizationForm.FormC), "-")
            .Trim('-');
        if (baseSlug.Length == 0)
        {
            baseSlug = "blog";
        }

        var slug = baseSlug.Length <= 280 ? baseSlug : baseSlug[..280].TrimEnd('-');
        for (var suffix = 2; await _blogRepository.SlugExistsAsync(slug, blogId, ct); suffix++)
        {
            var suffixText = $"-{suffix}";
            slug = $"{baseSlug[..Math.Min(baseSlug.Length, 280 - suffixText.Length)].TrimEnd('-')}{suffixText}";
        }

        return slug;
    }

    /// <summary>Loads an owned roadmap and creates an immutable snapshot dto.</summary>
    private async Task<RoadmapSnapshotDto?> CreateSnapshotAsync(
        long roadmapId,
        int authorId,
        DateTime capturedAt,
        CancellationToken ct)
    {
        var roadmap = await _blogRepository.GetOwnedRoadmapForSnapshotAsync(roadmapId, authorId, ct);
        if (roadmap is null)
        {
            return null;
        }

        return new RoadmapSnapshotDto
        {
            RoadmapId = roadmap.RoadmapId,
            Title = roadmap.Title,
            StartDate = roadmap.StartDate,
            NumDays = roadmap.NumDays,
            TotalCost = roadmap.Days.SelectMany(day => day.Items).Sum(item => item.Price),
            CapturedAt = capturedAt,
            Days = roadmap.Days
                .OrderBy(day => day.DayNumber)
                .Select(day => new RoadmapSnapshotDayDto
                {
                    DayNumber = day.DayNumber,
                    Items = day.Items
                        .Select(MapSnapshotItem)
                        .ToArray()
                })
                .ToArray()
        };
    }

    /// <summary>Copies one roadmap item and its current dish and restaurant fields into the immutable snapshot.</summary>
    private static RoadmapSnapshotItemDto MapSnapshotItem(RoadmapSnapshotItemSource item)
    {
        return new RoadmapSnapshotItemDto
        {
            MealSlot = JsonNamingPolicy.SnakeCaseLower.ConvertName(item.MealSlot.ToString()),
            DishId = item.DishId,
            DishName = item.DishName,
            Price = item.Price,
            RestaurantId = item.RestaurantId,
            RestaurantName = item.RestaurantName,
            Address = JoinAddress(item)
        };
    }

    /// <summary>Builds the human-readable restaurant address stored in a roadmap snapshot.</summary>
    private static string JoinAddress(RoadmapSnapshotItemSource item) =>
        string.Join(", ", new[]
        {
            item.AddressLine,
            item.Ward,
            item.ProvinceName
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Maps the saved blog aggregate to the BlogDetailDto contract.</summary>
    private static BlogDetailDto MapToDetail(
        Blog blog,
        User author,
        IReadOnlyDictionary<long, MediaFile> media,
        RoadmapSnapshotDto? snapshot)
    {
        var excerpt = blog.Content.Length <= ExcerptLength
            ? blog.Content.Trim()
            : blog.Content[..ExcerptLength].TrimEnd();

        return new BlogDetailDto
        {
            Id = blog.Id,
            Title = blog.Title,
            Slug = blog.Slug,
            Excerpt = excerpt,
            CoverUrl = blog.CoverMediaId is { } coverId
                ? (media.TryGetValue(coverId, out var coverMedia) ? coverMedia.Url : blog.CoverMedia?.Url)
                : null,
            Author = new UserSummaryDto
            {
                Id = author.Id,
                DisplayName = author.DisplayName,
                AvatarUrl = author.AvatarMedia?.Url
            },
            HasRoadmap = snapshot is not null,
            LikeCount = blog.LikeCount,
            CommentCount = blog.CommentCount,
            ViewCount = blog.ViewCount,
            PublishedAt = blog.PublishedAt,
            Content = blog.Content,
            Images = blog.Images
                .OrderBy(image => image.SortOrder)
                .Select(image => media.TryGetValue(image.MediaId, out var imgMedia) ? imgMedia.Url : image.Media?.Url ?? string.Empty)
                .Where(url => !string.IsNullOrEmpty(url))
                .ToArray(),
            RoadmapId = blog.RoadmapId,
            RoadmapSnapshot = snapshot,
            LikedByMe = false,
            IsMine = true,
            UpdatedAt = blog.UpdatedAt
        };
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacterRegex();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex StripTagsRegex();
}
