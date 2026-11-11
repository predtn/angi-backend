namespace ANGI.Application.DTOs.Restaurant;

/// <summary>Identifies the draft or pending menu that prevents opening another submission.</summary>
public sealed class SubmissionAlreadyOpenErrorDto
{
    public long Id { get; set; }
}
