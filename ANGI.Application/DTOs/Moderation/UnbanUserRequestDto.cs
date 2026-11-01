namespace ANGI.Application.DTOs.Moderation;

/// <summary>Request body for MOD-U4: unbanning or lifting suspension of a user account.</summary>
public class UnbanUserRequestDto
{
    public string LiftReason { get; set; } = string.Empty;
}
