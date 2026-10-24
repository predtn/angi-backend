using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines ADM-M3 MOD account detail retrieval.</summary>
public interface IGetModDetailUseCase
{
    /// <summary>Returns account, permission, and handled-work details for one MOD.</summary>
    Task<ModDetailDto> ExecuteAsync(int userId, CancellationToken ct);
}
