namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the optional owner note accepted when submitting a menu for review.</summary>
public sealed class SubmitMenuSubmissionRequestDto
{
    public string? OwnerNote { get; set; }
}
