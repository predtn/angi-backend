using System.Text.Json;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Maps restaurant verification aggregates to their shared API representation.</summary>
internal static class RestaurantVerificationMapper
{
    /// <summary>Maps a verification and signs each private document URL for the response.</summary>
    public static VerificationDto Map(
        RestaurantVerification verification,
        ICloudinaryService cloudinaryService)
    {
        return new VerificationDto
        {
            Id = verification.Id,
            RestaurantId = verification.RestaurantId,
            Status = ToApiValue(verification.Status),
            LegalName = verification.LegalName ?? string.Empty,
            BusinessLicenseNo = verification.BusinessLicenseNo ?? string.Empty,
            TaxCode = verification.TaxCode,
            OwnerNote = verification.OwnerNote,
            Documents = verification.Documents.Select(document => new VerificationDocumentDto
            {
                DocType = ToApiValue(document.DocType),
                MediaId = document.MediaId,
                Url = cloudinaryService.GetUrl(document.Media.StorageKey)
            }).ToArray(),
            PreviousId = verification.PreviousId,
            SubmittedAt = verification.SubmittedAt,
            ReviewedBy = verification.Reviewer is null
                ? null
                : new UserSummaryDto
                {
                    Id = verification.Reviewer.Id,
                    DisplayName = verification.Reviewer.DisplayName,
                    AvatarUrl = verification.Reviewer.AvatarMedia?.Url
                },
            ReviewedAt = verification.ReviewedAt,
            ReviewNote = verification.ReviewNote
        };
    }

    /// <summary>Converts a domain enum to the snake_case value documented by the API.</summary>
    private static string ToApiValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
