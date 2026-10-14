namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Represents the verification status and history for OWN-07.</summary>
public sealed class GetVerificationStatusResponseDto
{
    /// <summary>Current verification status of the restaurant (unverified, pending, verified, rejected).</summary>
    public string VerificationStatus { get; set; } = string.Empty;

    /// <summary>Verification submission history, ordered from newest to oldest.</summary>
    public IReadOnlyList<VerificationDto> Items { get; set; } = Array.Empty<VerificationDto>();
}
