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
        // STEP-01: Return null early when mediaId has no value.
        if (!mediaId.HasValue)
        {
            return null;
        }

        // STEP-02: Prepare media through GetOwnedByIdsAsync().SingleOrDefault.
        var media = (await mediaRepository.GetOwnedByIdsAsync(ownerId, [mediaId.Value], ct))
            .SingleOrDefault();
        // STEP-03: Throw ValidationException when media is missing, media.MimeType is blank, AllowedDishImageMimeTypes does not contain media.MimeType.Trim() or 2 more condition(s).
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

        // STEP-04: Return media.
        return media;
    }

    /// <summary>Ensures every tag id in the request exists in the tag catalog.</summary>
    public static async Task ValidateTagIdsAsync(
        IRestaurantMenuRepository repository,
        IReadOnlyCollection<short> tagIds,
        CancellationToken ct)
    {
        // STEP-01: Stop here when tagIds.Count is 0.
        if (tagIds.Count == 0)
        {
            return;
        }

        // STEP-02: Load existingIds through repository.GetExistingTagIdsAsync.
        var existingIds = await repository.GetExistingTagIdsAsync(tagIds, ct);
        // STEP-03: Prepare missingIds through tagIds.Except().ToArray.
        var missingIds = tagIds.Except(existingIds).ToArray();
        // STEP-04: Throw ValidationException when missingIds.Length > 0.
        if (missingIds.Length > 0)
        {
            throw new ValidationException([
                new ValidationFailure("TagIds", $"Tag {string.Join(", ", missingIds)} không tồn tại.")
            ]);
        }
    }
}
