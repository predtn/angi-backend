using System.ComponentModel.DataAnnotations;

namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Contains the optional owner note accepted when submitting a menu for review.</summary>
public sealed class SubmitMenuSubmissionRequestDto
{
    [MaxLength(1000)]
    public string? OwnerNote { get; set; }
}
