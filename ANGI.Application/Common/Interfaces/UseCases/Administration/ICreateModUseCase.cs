using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines ADM-M2 MOD account creation and invitation.</summary>
public interface ICreateModUseCase
{
    /// <summary>Creates a MOD and sends its password setup email.</summary>
    Task<AdminUserCardDto> ExecuteAsync(CreateModRequestDto request, CancellationToken ct);
}
