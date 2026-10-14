using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Holds the document and text rules shared by the OWN-06 and OWN-08 verification submissions.</summary>
internal static class RestaurantVerificationRules
{
    private static readonly IReadOnlyDictionary<string, VerificationDocumentType> DocumentTypes =
        new Dictionary<string, VerificationDocumentType>(StringComparer.Ordinal)
        {
            ["business_license"] = VerificationDocumentType.BusinessLicense,
            ["food_safety_cert"] = VerificationDocumentType.FoodSafetyCert,
            ["id_card"] = VerificationDocumentType.IdCard,
            ["storefront_photo"] = VerificationDocumentType.StorefrontPhoto,
            ["other"] = VerificationDocumentType.Other
        };

    /// <summary>Ensures every media id belongs to the caller and came from a private verification upload.</summary>
    public static IReadOnlyDictionary<long, MediaFile> EnsureVerificationMedia(
        IReadOnlyList<VerificationDocumentRequestDto> documents,
        IReadOnlyCollection<MediaFile> ownedMedia)
    {
        var mediaById = ownedMedia.ToDictionary(media => media.Id);
        var failures = new List<ValidationFailure>();
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            if (!mediaById.TryGetValue(document.MediaId, out var media) || !IsVerificationUpload(media.StorageKey))
            {
                failures.Add(new ValidationFailure(
                    $"Documents[{index}].MediaId",
                    "Tệp không tồn tại, không thuộc tài khoản của bạn hoặc không được tải lên cho hồ sơ xác minh."));
            }
        }

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return mediaById;
    }

    /// <summary>Attaches the requested documents to the verification, keeping the request order.</summary>
    public static void AddDocuments(
        RestaurantVerification verification,
        IEnumerable<VerificationDocumentRequestDto> documents)
    {
        foreach (var document in documents)
        {
            verification.Documents.Add(new RestaurantVerificationDocument
            {
                DocType = DocumentTypes[document.DocType!],
                // Only the id: the media rows were read untracked, so setting the navigation would make EF insert
                // them again (duplicate pk_media_files).
                MediaId = document.MediaId
            });
        }
    }

    /// <summary>Trims optional text and converts blank values to null.</summary>
    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Recognizes the private storage-key layout produced by MEDIA-01 for verification documents.</summary>
    private static bool IsVerificationUpload(string? storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return false;
        }

        var parts = storageKey.Split('/', 4, StringSplitOptions.None);
        return parts.Length == 4 &&
            parts[0] is "image" or "raw" &&
            parts[1] == "authenticated" &&
            parts[2] == "verification_doc" &&
            !string.IsNullOrWhiteSpace(parts[3]);
    }
}
