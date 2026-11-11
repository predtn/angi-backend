using ANGI.Application.DTOs.Discovery;

namespace ANGI.Application.Common.Interfaces.UseCases.Discovery;

/// <summary>Builds the home page blocks (HOME-01).</summary>
public interface IGetHomeUseCase
{
    Task<GetHomeResponseDto> ExecuteAsync(GetHomeRequestDto request, CancellationToken ct);
}
