namespace ANGI.Application.DTOs.Discovery;

/// <summary>Response data of IMP-01.</summary>
public sealed class RecordImpressionsResponseDto
{
    /// <summary>Always true: a reco failure is only logged.</summary>
    public bool Accepted { get; init; }
}
