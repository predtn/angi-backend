namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the optional owner-facing approval note for MOD-M3.</summary>
public sealed class ApproveMenuSubmissionRequestDto
{
    public string? ReviewNote { get; set; }
}
