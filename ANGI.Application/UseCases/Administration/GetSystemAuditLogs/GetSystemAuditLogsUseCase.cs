using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.GetSystemAuditLogs;

/// <summary>Implements ADM-A1 (F-64) to search system audit logs.</summary>
public sealed class GetSystemAuditLogsUseCase(
    IValidator<GetSystemAuditLogsRequestDto> validator,
    IAdministrationAuditLogRepository repository) : IGetSystemAuditLogsUseCase
{
    /// <inheritdoc />
    public async Task<Paged<AuditLogDto>> ExecuteAsync(GetSystemAuditLogsRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        return await repository.GetAuditLogsAsync(request, ct);
    }
}
