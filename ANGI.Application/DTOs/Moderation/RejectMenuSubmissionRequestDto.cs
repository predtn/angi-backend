namespace ANGI.Application.DTOs.Moderation;

/// <summary>Contains the required owner-facing rejection reason for MOD-M4.</summary>
public sealed class RejectMenuSubmissionRequestDto
{
    public string ReviewNote { get; set; } = string.Empty;
}
