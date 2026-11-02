using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.UseCases.Administration;

/// <summary>Defines the use case for querying failed/dead outbox messages (OPS-01).</summary>
public interface IGetFailedOutboxMessagesUseCase
{
    /// <summary>Validates paging and filter parameters and returns a page of outbox messages.</summary>
    Task<Paged<OutboxMessageDto>> ExecuteAsync(GetOutboxMessagesRequestDto request, CancellationToken ct);
}
