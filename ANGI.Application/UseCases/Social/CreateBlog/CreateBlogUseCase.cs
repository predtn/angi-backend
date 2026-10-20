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
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.Application.Common.Models.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Social.CreateBlog;

/// <summary>Implements BLOG-04 as an atomic published-blog creation with an optional immutable roadmap snapshot.</summary>
public sealed partial class CreateBlogUseCase : ICreateBlogUseCase
{
    private const int ExcerptLength = 200;
    private readonly IValidator<CreateBlogRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly IBlogContentSanitizer _contentSanitizer;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes BLOG-04 with validation, identity, sanitization, persistence, and time boundaries.</summary>
    public CreateBlogUseCase(
        IValidator<CreateBlogRequestDto> validator,
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

    /// <summary>Validates and sanitizes content, captures an owned roadmap, then publishes and maps the blog.</summary>
    public async Task<BlogDetailDto> ExecuteAsync(CreateBlogRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var authorId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để viết bài.");
        var publishedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var author = await _blogRepository.GetAuthorAsync(authorId, ct)
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Tài khoản không còn tồn tại.");

        var mediaIds = GetMediaIds(request);
        var media = mediaIds.Count == 0
            ? Array.Empty<MediaFile>()
            : await _mediaRepository.GetOwnedByIdsAsync(authorId, mediaIds, ct);
        EnsureAllMediaExist(request, media);

        var snapshot = await CreateSnapshotAsync(request.RoadmapId, authorId, publishedAt, ct);
        var sanitizedContent = _contentSanitizer.Sanitize(request.Content.Trim());
        EnsureSanitizedContentIsNotEmpty(sanitizedContent);

        var blog = new Blog
        {
            AuthorId = authorId,
            RoadmapId = request.RoadmapId,
            RoadmapSnapshot = snapshot is null
                ? null
                : JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Title = request.Title.Trim(),
            Slug = await CreateUniqueSlugAsync(request.Title, ct),
            Content = sanitizedContent,
            CoverMediaId = request.CoverMediaId,
            Status = BlogStatus.Published,
            PublishedAt = publishedAt
        };
        AddImages(blog, request.ImageMediaIds);

        _blogRepository.Add(blog);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return MapToDetail(blog, author, media.ToDictionary(item => item.Id), snapshot);
    }

    /// <summary>Collects distinct cover and inline-image identifiers supplied by the caller.</summary>
    private static IReadOnlyCollection<long> GetMediaIds(CreateBlogRequestDto request)
    {
        var ids = new HashSet<long>(request.ImageMediaIds ?? []);
        if (request.CoverMediaId.HasValue)
        {
            ids.Add(request.CoverMediaId.Value);
        }

        return ids;
    }

    /// <summary>Raises field-specific validation errors for missing or foreign media references.</summary>
    private static void EnsureAllMediaExist(
        CreateBlogRequestDto request,
        IReadOnlyCollection<MediaFile> media)
    {
        var foundIds = media.Select(item => item.Id).ToHashSet();
        var failures = new List<ValidationFailure>();

        if (request.CoverMediaId is { } coverId && !foundIds.Contains(coverId))
        {
            failures.Add(new ValidationFailure(
                nameof(request.CoverMediaId),
                $"Ảnh bìa {coverId} không tồn tại hoặc không phải ảnh bạn đã tải lên."));
        }

        var missingIds = (request.ImageMediaIds ?? [])
            .Where(id => !foundIds.Contains(id))
            .ToArray();
        if (missingIds.Length > 0)
        {
            failures.Add(new ValidationFailure(
                nameof(request.ImageMediaIds),
                $"Ảnh {string.Join(", ", missingIds)} không tồn tại hoặc không phải ảnh bạn đã tải lên."));
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    /// <summary>Rejects content whose only meaningful input was removed by the security sanitizer.</summary>
    private static void EnsureSanitizedContentIsNotEmpty(string content)
    {
        if (string.IsNullOrWhiteSpace(StripTagsRegex().Replace(content, string.Empty)))
        {
            throw new ValidationException([
                new ValidationFailure(nameof(CreateBlogRequestDto.Content), "Nội dung bài viết phải có nội dung an toàn.")
            ]);
        }
    }

    /// <summary>Loads and maps an owned roadmap or emits the non-disclosing BLOG-04 missing-resource error.</summary>
    private async Task<RoadmapSnapshotDto?> CreateSnapshotAsync(
        long? roadmapId,
        int authorId,
        DateTime capturedAt,
        CancellationToken ct)
    {
        if (!roadmapId.HasValue)
        {
            return null;
        }

        var roadmap = await _blogRepository.GetOwnedRoadmapForSnapshotAsync(roadmapId.Value, authorId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");

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
            item.District,
            item.ProvinceName
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>Creates a readable unique slug from a Vietnamese blog title.</summary>
    private async Task<string> CreateUniqueSlugAsync(string title, CancellationToken ct)
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
        for (var suffix = 2; await _blogRepository.SlugExistsAsync(slug, ct); suffix++)
        {
            var suffixText = $"-{suffix}";
            slug = $"{baseSlug[..Math.Min(baseSlug.Length, 280 - suffixText.Length)].TrimEnd('-')}{suffixText}";
        }

        return slug;
    }

    /// <summary>Adds ordered blog-image join records without persisting them independently.</summary>
    private static void AddImages(Blog blog, IReadOnlyList<long>? imageMediaIds)
    {
        if (imageMediaIds is null)
        {
            return;
        }

        for (short index = 0; index < imageMediaIds.Count; index++)
        {
            blog.Images.Add(new BlogImage { MediaId = imageMediaIds[index], SortOrder = index });
        }
    }

    /// <summary>Maps the saved aggregate and already validated related data to the complete BlogDetail contract.</summary>
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
            CoverUrl = blog.CoverMediaId is { } coverId ? media[coverId].Url : null,
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
                .Select(image => media[image.MediaId].Url)
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
