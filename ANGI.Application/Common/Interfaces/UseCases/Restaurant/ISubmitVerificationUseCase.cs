using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.Common.Interfaces.UseCases.Restaurant;

/// <summary>Defines the OWN-06 restaurant verification submission workflow.</summary>
public interface ISubmitVerificationUseCase
{
    /// <summary>Creates the authenticated owner's first verification submission.</summary>
    Task<VerificationDto> ExecuteAsync(SubmitVerificationRequestDto request, CancellationToken ct);
}
