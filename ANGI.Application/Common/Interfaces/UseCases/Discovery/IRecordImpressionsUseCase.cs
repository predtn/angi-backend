using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Forwards the dishes a Traveler really saw to reco (IMP-01 → RECO-06).</summary>
public interface IRecordImpressionsUseCase
{
    /// <summary>Always accepted once valid; a reco failure is only logged.</summary>
    Task<RecordImpressionsResponseDto> ExecuteAsync(RecordImpressionsRequestDto request, CancellationToken ct);
}
