using System.Text.Json;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Enums;
using ANGI.Infrastructure.Persistences;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Administration;

/// <summary>Implements outbox query persistence against core.outbox_messages.</summary>
public sealed class AdministrationOutboxRepository : IAdministrationOutboxRepository
{
    private readonly ANGIContext _context;

    /// <summary>Initializes the repository with the persistence context.</summary>
    public AdministrationOutboxRepository(ANGIContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<Paged<OutboxMessageDto>> GetOutboxMessagesAsync(
        string status,
        string? eventType,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var targetStatus = status.ToLowerInvariant() switch
        {
            "pending" => OutboxStatus.Pending,
            "processing" => OutboxStatus.Processing,
            "done" => OutboxStatus.Done,
            "dead" or "failed" => OutboxStatus.Dead,
            _ => OutboxStatus.Dead
        };

        var query = _context.OutboxMessages.AsNoTracking()
            .Where(m => m.Status == targetStatus);

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            var normalized = eventType.Trim();
            if (Enum.TryParse<OutboxEventType>(normalized, true, out var parsedEventType)
                || Enum.TryParse<OutboxEventType>(normalized.Replace("_", ""), true, out parsedEventType))
            {
                query = query.Where(m => m.EventType == parsedEventType);
            }
        }

        var totalItems = await query.LongCountAsync(ct);
        var totalPages = pageSize > 0 ? (int)Math.Ceiling((double)totalItems / pageSize) : 0;

        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new
            {
                m.Id,
                m.EventUuid,
                m.EventType,
                m.AggregateKey,
                m.Payload,
                m.Status,
                m.AttemptCount,
                m.LastError,
                m.CreatedAt,
                m.NextAttemptAt
            })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            object payloadObj;
            try
            {
                payloadObj = JsonSerializer.Deserialize<JsonElement>(r.Payload);
            }
            catch
            {
                payloadObj = r.Payload;
            }

            return new OutboxMessageDto
            {
                Id = r.Id,
                EventUuid = r.EventUuid,
                EventType = JsonNamingPolicy.SnakeCaseUpper.ConvertName(r.EventType.ToString()),
                AggregateKey = r.AggregateKey,
                Payload = payloadObj,
                Status = r.Status.ToString().ToLowerInvariant(),
                AttemptCount = r.AttemptCount,
                LastError = r.LastError,
                CreatedAt = r.CreatedAt,
                NextAttemptAt = r.NextAttemptAt
            };
        }).ToList();

        return new Paged<OutboxMessageDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }
}
