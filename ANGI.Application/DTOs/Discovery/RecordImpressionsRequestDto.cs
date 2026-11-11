namespace ANGI.Application.DTOs.Discovery;

/// <summary>Request body of IMP-01: dishes that were really shown (≥ 50 % on screen for ≥ 1 s).</summary>
public sealed class RecordImpressionsRequestDto
{
    /// <summary>The requestId of DISH-01, HOME-01, RM-06 or DISH-03.</summary>
    public Guid? RequestId { get; set; }

    /// <summary>1..200 dish ids.</summary>
    public List<int>? DishIds { get; set; }

    /// <summary>When they were shown; the server time when omitted.</summary>
    public DateTime? ShownAt { get; set; }
}
