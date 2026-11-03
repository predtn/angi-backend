using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.GetFailedOutboxMessages;

/// <summary>Implements OPS-01 by filtering and paging outbox event messages.</summary>
public sealed class GetFailedOutboxMessagesUseCase : IGetFailedOutboxMessagesUseCase
{
    private readonly IValidator<GetOutboxMessagesRequestDto> _validator;
    private readonly IAdministrationOutboxRepository _repository;

    /// <summary>Initializes the use case with validation and outbox repository.</summary>
    public GetFailedOutboxMessagesUseCase(
        IValidator<GetOutboxMessagesRequestDto> validator,
        IAdministrationOutboxRepository repository)
    {
        _validator = validator;
        _repository = repository;
    }

    /// <summary>Validates OPS-01 query parameters and returns a paginated list of outbox messages.</summary>
    public async Task<Paged<OutboxMessageDto>> ExecuteAsync(
        GetOutboxMessagesRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var status = string.IsNullOrWhiteSpace(request.Status) ? "dead" : request.Status.Trim().ToLowerInvariant();
        if (status == "failed")
        {
            status = "dead";
        }

        var eventType = string.IsNullOrWhiteSpace(request.EventType) ? null : request.EventType.Trim().ToUpperInvariant();

        return await _repository.GetOutboxMessagesAsync(
            status,
            eventType,
            request.Page,
            request.PageSize,
            ct);
    }
}
