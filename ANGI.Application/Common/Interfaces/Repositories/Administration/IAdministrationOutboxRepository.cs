using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;

namespace ANGI.Application.Common.Interfaces.Repositories.Administration;

/// <summary>Provides read queries for outbox event messages in core.outbox_messages.</summary>
public interface IAdministrationOutboxRepository
{
    /// <summary>Returns a paginated list of outbox messages filtered by status and optional event type.</summary>
    Task<Paged<OutboxMessageDto>> GetOutboxMessagesAsync(
        string status,
        string? eventType,
        int page,
        int pageSize,
        CancellationToken ct);
}
