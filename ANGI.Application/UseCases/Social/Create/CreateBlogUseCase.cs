using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Social.Create;

/// <summary>Implements the BLOG-04 workflow for creating a blog post and optional roadmap snapshot.</summary>
public sealed class CreateBlogUseCase : ICreateBlogUseCase
{
    private const int ExcerptLength = 200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly IValidator<CreateBlogRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CreateBlogUseCase(
        IValidator<CreateBlogRequestDto> validator,
        IBlogRepository blogRepository,
        IRoadmapRepository roadmapRepository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _blogRepository = blogRepository;
        _roadmapRepository = roadmapRepository;
        _mediaRepository = mediaRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<BlogDetailDto> ExecuteAsync(CreateBlogRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để tạo bài viết.");

        if (!string.Equals(_currentUserService.Role, "TRAVELER", StringComparison.OrdinalIgnoreCase))
        {
            throw new ForbiddenException("FORBIDDEN", "Chỉ người dùng có quyền Traveler mới có thể viết blog.");
        }

        await EnsureMediaFilesExistAsync(userId, request, ct);

        string? roadmapSnapshotJson = null;
        RoadmapSnapshotDto? snapshotDto = null;
        if (request.RoadmapId.HasValue)
        {
            var roadmap = await _roadmapRepository.GetByIdForSnapshotAsync(request.RoadmapId.Value, userId, ct);
            if (roadmap == null)
            {
                throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy lộ trình.");
            }

            snapshotDto = CreateRoadmapSnapshot(roadmap);
            roadmapSnapshotJson = JsonSerializer.Serialize(snapshotDto, JsonOptions);
        }

        var slug = await CreateUniqueSlugAsync(request.Title, ct);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var blog = new Blog
        {
            AuthorId = userId,
            RoadmapId = request.RoadmapId,
            RoadmapSnapshot = roadmapSnapshotJson,
            Title = request.Title.Trim(),
            Slug = slug,
            Content = SanitizeContent(request.Content),
            CoverMediaId = request.CoverMediaId,
            Status = BlogStatus.Published,
            PublishedAt = now,
            LikeCount = 0,
            CommentCount = 0,
            ViewCount = 0
        };

        if (request.ImageMediaIds is { Count: > 0 })
        {
            short sortOrder = 0;
            foreach (var mediaId in request.ImageMediaIds.Distinct())
            {
                blog.Images.Add(new BlogImage
                {
                    Blog = blog,
                    MediaId = mediaId,
                    SortOrder = sortOrder++
                });
            }
        }

        _blogRepository.Add(blog);
        await _unitOfWork.SaveChangesAsync(ct);

        var savedBlog = await _blogRepository.GetByIdWithDetailsAsync(blog.Id, ct) ?? blog;
        return MapToDetailDto(savedBlog, snapshotDto);
    }

    private async Task EnsureMediaFilesExistAsync(
        int userId,
        CreateBlogRequestDto request,
        CancellationToken ct)
    {
        var mediaIds = new HashSet<long>();
        if (request.CoverMediaId.HasValue)
        {
            mediaIds.Add(request.CoverMediaId.Value);
        }

        if (request.ImageMediaIds is { Count: > 0 })
        {
            foreach (var id in request.ImageMediaIds)
            {
                mediaIds.Add(id);
            }
        }

        if (mediaIds.Count == 0)
        {
            return;
        }

        var mediaFiles = await _mediaRepository.GetOwnedByIdsAsync(userId, mediaIds, ct);
        var foundIds = mediaFiles.Select(m => m.Id).ToHashSet();
        var failures = new List<ValidationFailure>();

        if (request.CoverMediaId is { } coverId && !foundIds.Contains(coverId))
        {
            failures.Add(new ValidationFailure(
                nameof(request.CoverMediaId),
                $"Ảnh bìa {coverId} không tồn tại hoặc không phải ảnh bạn đã tải lên."));
        }

        var missingImageIds = (request.ImageMediaIds ?? Enumerable.Empty<long>())
            .Where(id => !foundIds.Contains(id))
            .Distinct()
            .ToArray();

        if (missingImageIds.Length > 0)
        {
            failures.Add(new ValidationFailure(
                nameof(request.ImageMediaIds),
                $"Ảnh {string.Join(", ", missingImageIds)} không tồn tại hoặc không phải ảnh bạn đã tải lên."));
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }
    }

    private RoadmapSnapshotDto CreateRoadmapSnapshot(Domain.Entities.Roadmap roadmap)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var snapshotDays = roadmap.Days
            .OrderBy(d => d.DayNumber)
            .Select(d => new RoadmapSnapshotDayDto
            {
                DayNumber = d.DayNumber,
                Items = d.Items
                    .OrderBy(i => i.SortOrder)
                    .Select(i => new RoadmapSnapshotItemDto
                    {
                        MealSlot = FormatMealSlot(i.MealSlot),
                        DishId = i.DishId,
                        DishName = i.Dish?.Name ?? string.Empty,
                        Price = i.Dish?.Price ?? 0,
                        RestaurantId = i.Dish?.RestaurantId ?? 0,
                        RestaurantName = i.Dish?.Restaurant?.Name ?? string.Empty,
                        Address = FormatAddress(i.Dish?.Restaurant)
                    })
                    .ToList()
            })
            .ToList();

        var totalCost = snapshotDays.SelectMany(d => d.Items).Sum(i => i.Price);

        return new RoadmapSnapshotDto
        {
            RoadmapId = roadmap.Id,
            Title = roadmap.Title,
            StartDate = roadmap.StartDate,
            NumDays = roadmap.NumDays,
            TotalCost = totalCost,
            CapturedAt = now,
            Days = snapshotDays
        };
    }

    private static string FormatMealSlot(MealSlot slot) => slot switch
    {
        MealSlot.Breakfast => "breakfast",
        MealSlot.Lunch => "lunch",
        MealSlot.Dinner => "dinner",
        MealSlot.Snack => "snack",
        MealSlot.LateNight => "late_night",
        _ => slot.ToString().ToLowerInvariant()
    };

    private static string FormatAddress(Domain.Entities.Restaurant? restaurant)
    {
        if (restaurant == null)
        {
            return string.Empty;
        }

        var parts = new[] { restaurant.AddressLine, restaurant.Ward, restaurant.District, restaurant.ProvinceName }
            .Where(s => !string.IsNullOrWhiteSpace(s));

        return string.Join(", ", parts);
    }

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

        var baseSlug = Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), "[^a-z0-9]+", "-")
            .Trim('-');

        if (baseSlug.Length == 0)
        {
            baseSlug = "blog";
        }

        if (baseSlug.Length > 200)
        {
            baseSlug = baseSlug[..200].TrimEnd('-');
        }

        var slug = baseSlug;
        var suffix = 2;
        while (await _blogRepository.SlugExistsAsync(slug, ct))
        {
            slug = $"{baseSlug}-{suffix++}";
        }

        return slug;
    }

    private static string SanitizeContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var sanitized = Regex.Replace(content, @"<\s*(script|iframe|object|embed|applet|form|base)[^>]*>.*?<\s*/\s*\1\s*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        sanitized = Regex.Replace(sanitized, @"<\s*(script|iframe|object|embed|applet|form|base)[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"\s*on\w+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", string.Empty, RegexOptions.IgnoreCase);
        sanitized = Regex.Replace(sanitized, @"href\s*=\s*[""']?javascript:[^""'>]*[""']?", string.Empty, RegexOptions.IgnoreCase);
        return sanitized.Trim();
    }

    private static BlogDetailDto MapToDetailDto(Blog blog, RoadmapSnapshotDto? snapshotDto)
    {
        var content = blog.Content ?? string.Empty;
        var excerpt = content.Length <= ExcerptLength ? content.Trim() : content[..ExcerptLength].TrimEnd();

        RoadmapSnapshotDto? snapshot = snapshotDto;
        if (snapshot == null && !string.IsNullOrWhiteSpace(blog.RoadmapSnapshot))
        {
            try
            {
                snapshot = JsonSerializer.Deserialize<RoadmapSnapshotDto>(blog.RoadmapSnapshot, JsonOptions);
            }
            catch
            {
                // Ignore deserialization issues if snapshot is invalid
            }
        }

        var images = blog.Images?
            .OrderBy(i => i.SortOrder)
            .Select(i => i.Media?.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .ToList() ?? [];

        return new BlogDetailDto
        {
            Id = blog.Id,
            Title = blog.Title,
            Slug = blog.Slug,
            Excerpt = excerpt,
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
            PublishedAt = blog.PublishedAt,
            Content = blog.Content ?? string.Empty,
            Images = images,
            RoadmapId = blog.RoadmapId,
            RoadmapSnapshot = snapshot,
            LikedByMe = false,
            IsMine = true,
            UpdatedAt = blog.UpdatedAt == default ? blog.PublishedAt : blog.UpdatedAt
        };
    }
}
