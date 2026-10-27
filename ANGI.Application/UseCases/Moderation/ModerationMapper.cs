using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.DTOs.Social;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Maps moderation aggregates into their documented API contracts without leaking private account data.</summary>
internal static class ModerationMapper
{
    /// <summary>Maps a report detail without adding moderator-only fields to the public target contract.</summary>
    internal static ReportDetailDto ReportDetail(Report report, ReportDisplayData display, ICloudinaryService cloudinary,
        object target, IReadOnlyList<ReportDto> related)
    {
        var source = Report(report, display, cloudinary);
        return new ReportDetailDto
        {
            Id = source.Id, TargetType = source.TargetType, TargetId = source.TargetId, TargetName = source.TargetName,
            ReasonCode = source.ReasonCode, Description = source.Description, EvidenceUrls = source.EvidenceUrls,
            Reporter = source.Reporter, Status = source.Status, HandledBy = source.HandledBy, HandledAt = source.HandledAt,
            ResolutionAction = source.ResolutionAction, ResolutionNote = source.ResolutionNote,
            ReportCountOnTarget = source.ReportCountOnTarget, CreatedAt = source.CreatedAt, Target = target, RelatedReports = related
        };
    }

    /// <summary>Maps a reported restaurant to RestaurantCard, including its current Vietnam business hours.</summary>
    internal static RestaurantCardDto ReportRestaurant(Domain.Entities.Restaurant restaurant, TimeProvider clock) => new()
    {
        Id = restaurant.Id, Name = restaurant.Name, Slug = restaurant.Slug, CoverUrl = restaurant.CoverMedia?.Url,
        AddressLine = restaurant.AddressLine, ProvinceName = restaurant.ProvinceName,
        Latitude = restaurant.Latitude, Longitude = restaurant.Longitude, PriceLevel = restaurant.PriceLevel,
        RatingAvg = restaurant.RatingAvg, RatingCount = restaurant.RatingCount, OperatingStatus = Api(restaurant.OperatingStatus),
        IsOpenNow = OwnerRestaurantMapper.IsOpenNow(restaurant.BusinessHours, clock)
    };

    /// <summary>Maps the reported blog and its stored immutable roadmap snapshot without incrementing views.</summary>
    internal static BlogDetailDto ReportBlog(Blog blog, int callerId) => new()
    {
        Id = blog.Id, Title = blog.Title, Slug = blog.Slug,
        Excerpt = blog.Content.Length <= 200 ? blog.Content.Trim() : blog.Content[..200].TrimEnd(),
        CoverUrl = blog.CoverMedia?.Url, Author = Summary(blog.Author), HasRoadmap = blog.RoadmapSnapshot is not null,
        LikeCount = blog.LikeCount, CommentCount = blog.CommentCount, ViewCount = blog.ViewCount, PublishedAt = blog.PublishedAt,
        Content = blog.Content, Images = blog.Images.OrderBy(image => image.SortOrder).ThenBy(image => image.MediaId)
            .Select(image => image.Media.Url).ToArray(),
        RoadmapId = blog.RoadmapId, RoadmapSnapshot = blog.RoadmapSnapshot is null ? null
            : JsonSerializer.Deserialize<RoadmapSnapshotDto>(blog.RoadmapSnapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        LikedByMe = blog.Likes.Any(like => like.UserId == callerId), IsMine = blog.AuthorId == callerId, UpdatedAt = blog.UpdatedAt
    };

    /// <summary>Converts a domain enum to its exact snake_case API representation.</summary>
    internal static string Api<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>Maps the projected restaurant and its open report count into a list card.</summary>
    internal static ModRestaurantCardDto Card(ModRestaurantListItem item, TimeProvider clock)
    {
        var restaurant = item.Restaurant;
        return new ModRestaurantCardDto
        {
            Id = restaurant.Id, Name = restaurant.Name, Slug = restaurant.Slug,
            CoverUrl = restaurant.CoverMedia?.Url, AddressLine = restaurant.AddressLine,
            ProvinceName = restaurant.ProvinceName,
            Latitude = restaurant.Latitude, Longitude = restaurant.Longitude, PriceLevel = restaurant.PriceLevel,
            RatingAvg = restaurant.RatingAvg, RatingCount = restaurant.RatingCount,
            OperatingStatus = Api(restaurant.OperatingStatus),
            IsOpenNow = OwnerRestaurantMapper.IsOpenNow(restaurant.BusinessHours, clock),
            VerificationStatus = Api(restaurant.VerificationStatus), ModerationStatus = Api(restaurant.ModerationStatus),
            PendingReportCount = item.PendingReportCount
        };
    }

    /// <summary>Maps the complete restaurant profile and its moderator-only associated data.</summary>
    internal static ModRestaurantDetailDto Detail(Domain.Entities.Restaurant restaurant,
        IReadOnlyList<ReportDto> reports, IReadOnlyList<AuditLog> audit, ICloudinaryService cloudinary, TimeProvider clock)
    {
        var source = OwnerRestaurantMapper.Map(restaurant, cloudinary, clock);
        return new ModRestaurantDetailDto
        {
            Id = source.Id, Name = source.Name, Slug = source.Slug, CoverUrl = source.CoverUrl,
            AddressLine = source.AddressLine, ProvinceName = source.ProvinceName,
            Latitude = source.Latitude, Longitude = source.Longitude, PriceLevel = source.PriceLevel,
            RatingAvg = source.RatingAvg, RatingCount = source.RatingCount, OperatingStatus = source.OperatingStatus,
            IsOpenNow = source.IsOpenNow, DistanceKm = source.DistanceKm, Description = source.Description,
            Phone = source.Phone, Email = source.Email, Website = source.Website, Ward = source.Ward,
            Images = source.Images, BusinessHours = source.BusinessHours, Menu = source.Menu, MyReview = source.MyReview,
            VerificationStatus = source.VerificationStatus, ModerationStatus = source.ModerationStatus,
            LatestVerification = source.LatestVerification, OpenMenuSubmission = source.OpenMenuSubmission,
            CreatedAt = source.CreatedAt, UpdatedAt = source.UpdatedAt,
            Owner = AdminUser(restaurant.Owner), Dishes = RestaurantMenuMapper.MapOwnerMenu(restaurant).Dishes,
            Reports = reports, RecentAudit = audit.Select(Audit).ToArray()
        };
    }

    /// <summary>Maps an owner account to the administrative card without password or token fields.</summary>
    private static AdminUserCardDto AdminUser(User user) => new()
    {
        Id = user.Id, Email = user.Email, DisplayName = user.DisplayName, AvatarUrl = user.AvatarMedia?.Url,
        Role = user.Role.Code, Status = Api(user.Status), SuspendedUntil = user.SuspendedUntil,
        LastLoginAt = user.LastLoginAt, CreatedAt = user.CreatedAt
    };

    /// <summary>Maps the public identity used by reporter, handler and audit subject fields.</summary>
    private static UserSummaryDto Summary(User user) => new()
    {
        Id = user.Id, DisplayName = user.DisplayName, AvatarUrl = user.AvatarMedia?.Url
    };

    /// <summary>Maps a report using signed evidence URLs and the target's current open-report count.</summary>
    internal static ReportDto Report(Report report, ReportDisplayData display, ICloudinaryService cloudinary) => new()
    {
        Id = report.Id, TargetType = Api(report.TargetType), TargetId = report.RestaurantId ?? report.BlogId!.Value,
        TargetName = display.TargetName, ReasonCode = Api(report.ReasonCode), Description = report.Description,
        EvidenceUrls = (JsonSerializer.Deserialize<long[]>(report.Evidence) ?? [])
            .Where(display.Evidence.ContainsKey).Select(id => cloudinary.GetUrl(display.Evidence[id].StorageKey)).ToArray(),
        Reporter = display.Users.TryGetValue(report.ReporterId, out var reporter) ? Summary(reporter) : new UserSummaryDto { Id = report.ReporterId },
        Status = Api(report.Status), HandledBy = report.HandledBy.HasValue && display.Users.TryGetValue(report.HandledBy.Value, out var handler)
            ? Summary(handler) : null,
        HandledAt = report.HandledAt, ResolutionAction = report.ResolutionAction.HasValue ? Api(report.ResolutionAction.Value) : null,
        ResolutionNote = report.ResolutionNote, ReportCountOnTarget = display.OpenReportCount, CreatedAt = report.CreatedAt
    };

    /// <summary>Maps an audit list entry, preserving the actor's historical role.</summary>
    private static AuditLogDto Audit(AuditLog log) => new()
    {
        Id = log.Id, Actor = log.ActorId.HasValue ? new AuditActorDto
        {
            Id = log.ActorId.Value, DisplayName = log.Actor?.DisplayName ?? string.Empty, Role = log.ActorRole
        } : null,
        Action = log.Action, EntityType = log.EntityType, EntityId = log.EntityId,
        SubjectUser = log.SubjectUser is null ? null : Summary(log.SubjectUser), CreatedAt = log.CreatedAt
    };
}
