using ANGI.Application.DTOs.Account;

namespace ANGI.Application.Common.Interfaces.UseCases.Account;

/// <summary>Defines the contract for retrieving the personal profile of the currently logged-in user (ME-01).</summary>
public interface IGetMyProfileUseCase
{
    /// <summary>Executes ME-01 using caller identity from the current token.</summary>
    Task<UserProfileDto> ExecuteAsync(CancellationToken ct);
}
