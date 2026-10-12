using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Centralizes validation shared by owner menu item commands.</summary>
internal static class RestaurantMenuRules
{
    private static readonly IReadOnlySet<string> AllowedDishImageMimeTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg",
            "image/png",
            "image/webp"
        };

    /// <summary>Converts a validated snake_case dish form to its domain value.</summary>
    public static DishForm ParseDishForm(string value) => value.ToLowerInvariant() switch
    {
        "soup" => DishForm.Soup,
        "dry" => DishForm.Dry,
        _ => DishForm.Other
    };

    /// <summary>Rejects writes against menu submissions that are no longer drafts.</summary>
    public static void EnsureEditable(MenuSubmission submission)
    {
        if (submission.Status != MenuSubmissionStatus.Draft)
        {
            throw new ConflictException(
                "SUBMISSION_NOT_EDITABLE",
                "Bản menu không còn ở trạng thái bản nháp để chỉnh sửa.");
        }
    }

    /// <summary>Loads an optional owner media record or raises field-level validation.</summary>
    public static async Task<MediaFile?> GetOwnedMediaAsync(
        IMediaRepository mediaRepository,
        int ownerId,
        long? mediaId,
        CancellationToken ct)
    {
        if (!mediaId.HasValue)
        {
            return null;
        }

        var media = (await mediaRepository.GetOwnedByIdsAsync(ownerId, [mediaId.Value], ct))
            .SingleOrDefault();
        if (media is null ||
            string.IsNullOrWhiteSpace(media.MimeType) ||
            !AllowedDishImageMimeTypes.Contains(media.MimeType.Trim()) ||
            string.IsNullOrWhiteSpace(media.StorageKey) ||
            !media.StorageKey.StartsWith("image/upload/dish/", StringComparison.Ordinal))
        {
            throw new ValidationException([
                new ValidationFailure(
                    "CoverMediaId",
                    "Ảnh món không tồn tại hoặc không phải ảnh bạn đã tải lên.")
            ]);
        }

        return media;
    }

    /// <summary>Ensures every tag id in the request exists in the tag catalog.</summary>
    public static async Task ValidateTagIdsAsync(
        IRestaurantMenuRepository repository,
        IReadOnlyCollection<short> tagIds,
        CancellationToken ct)
    {
        if (tagIds.Count == 0)
        {
            return;
        }

        var existingIds = await repository.GetExistingTagIdsAsync(tagIds, ct);
        var missingIds = tagIds.Except(existingIds).ToArray();
        if (missingIds.Length > 0)
        {
            throw new ValidationException([
                new ValidationFailure("TagIds", $"Tag {string.Join(", ", missingIds)} không tồn tại.")
            ]);
        }
    }
}
