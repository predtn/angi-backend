using ANGI.Application.DTOs.Account;

namespace ANGI.Application.Common.Interfaces.UseCases.Account;

/// <summary>Defines the contract for updating personal profile fields (ME-02).</summary>
public interface IUpdateMyProfileUseCase
{
    /// <summary>Executes ME-02 partial profile update for the currently logged-in user.</summary>
    Task<UserProfileDto> ExecuteAsync(UpdateProfileRequestDto request, CancellationToken ct);
}
