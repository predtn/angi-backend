namespace ANGI.Application.DTOs.Moderation;

/// <summary>Represents the MOD-V3 decision made for a pending verification submission.</summary>
public sealed class ReviewVerificationRequestDto
{
    public string Decision { get; set; } = null!;
    public string? ReviewNote { get; set; }
}
