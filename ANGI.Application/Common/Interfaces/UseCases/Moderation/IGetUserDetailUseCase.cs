using ANGI.Application.DTOs.Moderation;

namespace ANGI.Application.Common.Interfaces.UseCases.Moderation;

/// <summary>Defines the MOD-U2 operation to get user details for moderation.</summary>
public interface IGetUserDetailUseCase
{
    /// <summary>Executes the get detail query with moderation authorization.</summary>
    Task<ModUserDetailDto> ExecuteAsync(int userId, CancellationToken ct);
}
