using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Administration;
using ANGI.Application.DTOs.Account;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.DTOs.Moderation;
using ANGI.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ANGI.Infrastructure.Persistences.Repositories.Administration;

/// <summary>Implements read-only audit_logs queries for administrators.</summary>
public sealed class AdministrationAuditLogRepository(ANGIContext context) : IAdministrationAuditLogRepository
{
    /// <inheritdoc />
    public async Task<Paged<AuditLogDto>> GetAuditLogsAsync(GetSystemAuditLogsRequestDto request, CancellationToken ct)
    {
        var query = context.AuditLogs.AsNoTracking().AsQueryable();

        if (request.ActorId.HasValue) query = query.Where(a => a.ActorId == request.ActorId);
        if (request.SubjectUserId.HasValue) query = query.Where(a => a.SubjectUserId == request.SubjectUserId);
        if (request.EntityId.HasValue) query = query.Where(a => a.EntityId == request.EntityId);

        if (!string.IsNullOrWhiteSpace(request.ActorRole))
        {
            var role = request.ActorRole.Trim().ToUpperInvariant();
            query = query.Where(a => a.ActorRole == role);
        }

        if (!string.IsNullOrWhiteSpace(request.Action))
        {
            var action = request.Action.Trim().ToUpperInvariant();
            query = query.Where(a => a.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            var entityType = request.EntityType.Trim().ToLowerInvariant();
            query = query.Where(a => a.EntityType == entityType);
        }

        if (request.From.HasValue)
        {
            var fromUtc = ToUtc(request.From.Value);
            query = query.Where(a => a.CreatedAt >= fromUtc);
        }

        if (request.To.HasValue)
        {
            var toUtc = ToUtc(request.To.Value);
            if (toUtc.TimeOfDay == TimeSpan.Zero)
            {
                var toExclusive = toUtc.AddDays(1);
                query = query.Where(a => a.CreatedAt < toExclusive);
            }
            else
            {
                query = query.Where(a => a.CreatedAt <= toUtc);
            }
        }

        var totalItems = await query.LongCountAsync(ct);
        var totalPages = (int)Math.Ceiling((double)totalItems / request.PageSize);
        var offset = ((long)request.Page - 1) * request.PageSize;

        IReadOnlyList<AuditLogDto> items = [];
        if (totalItems > 0 && offset < totalItems)
        {
            items = await query
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Skip((int)offset)
                .Take(request.PageSize)
                .Select(a => new AuditLogDto
                {
                    Id = a.Id,
                    Actor = a.ActorId.HasValue
                        ? new AuditActorDto
                        {
                            Id = a.ActorId.Value,
                            DisplayName = a.Actor != null ? a.Actor.DisplayName : string.Empty,
                            Role = a.ActorRole
                        }
                        : null,
                    Action = a.Action,
                    EntityType = a.EntityType,
                    EntityId = a.EntityId,
                    SubjectUser = a.SubjectUser != null
                        ? new UserSummaryDto
                        {
                            Id = a.SubjectUser.Id,
                            DisplayName = a.SubjectUser.DisplayName,
                            AvatarUrl = a.SubjectUser.AvatarMedia != null ? a.SubjectUser.AvatarMedia.Url : null
                        }
                        : null,
                    CreatedAt = a.CreatedAt
                })
                .ToListAsync(ct);
        }

        return new Paged<AuditLogDto>
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalItems = totalItems,
            TotalPages = totalPages
        };
    }

    /// <inheritdoc />
    public async Task<AuditLogDetailRecord?> GetAuditLogByIdAsync(long auditLogId, CancellationToken ct)
    {
        var row = await context.AuditLogs
            .AsNoTracking()
            .Where(a => a.Id == auditLogId)
            .Select(a => new
            {
                a.Id,
                a.ActorId,
                ActorName = a.Actor != null ? a.Actor.DisplayName : null,
                a.ActorRole,
                a.Action,
                a.EntityType,
                a.EntityId,
                SubjectId = a.SubjectUser != null ? (int?)a.SubjectUser.Id : null,
                SubjectName = a.SubjectUser != null ? a.SubjectUser.DisplayName : null,
                SubjectAvatar = a.SubjectUser != null && a.SubjectUser.AvatarMedia != null ? a.SubjectUser.AvatarMedia.Url : null,
                a.CreatedAt,
                a.OldValues,
                a.NewValues,
                a.IpAddress,
                a.UserAgent
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new AuditLogDetailRecord
        {
            Id = row.Id,
            Actor = row.ActorId.HasValue
                ? new AuditActorDto { Id = row.ActorId.Value, DisplayName = row.ActorName ?? string.Empty, Role = row.ActorRole }
                : null,
            Action = row.Action,
            EntityType = row.EntityType,
            EntityId = row.EntityId,
            SubjectUser = row.SubjectId.HasValue
                ? new UserSummaryDto { Id = row.SubjectId.Value, DisplayName = row.SubjectName ?? string.Empty, AvatarUrl = row.SubjectAvatar }
                : null,
            CreatedAt = row.CreatedAt,
            OldValues = row.OldValues,
            NewValues = row.NewValues,
            IpAddress = row.IpAddress?.ToString(),
            UserAgent = row.UserAgent
        };
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
