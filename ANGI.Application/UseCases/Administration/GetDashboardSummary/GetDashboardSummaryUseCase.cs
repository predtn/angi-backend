using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.GetDashboardSummary;

/// <summary>Implements ADM-D1 by validating date parameters and retrieving dashboard summary metrics.</summary>
public sealed class GetDashboardSummaryUseCase : IGetDashboardSummaryUseCase
{
    private readonly IValidator<GetDashboardSummaryRequestDto> _validator;
    private readonly IAdministrationDashboardRepository _repository;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes the use case with validation, persistence, and time services.</summary>
    public GetDashboardSummaryUseCase(
        IValidator<GetDashboardSummaryRequestDto> validator,
        IAdministrationDashboardRepository repository,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _repository = repository;
        _timeProvider = timeProvider;
    }

    /// <summary>Validates ADM-D1 input and returns the aggregate summary DTO.</summary>
    public async Task<DashboardSummaryDto> ExecuteAsync(
        GetDashboardSummaryRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var to = request.To ?? today;
        var from = request.From ?? to.AddDays(-30);

        if (from > to)
        {
            throw new BadRequestException("INVALID_DATE_RANGE", "startDate phải nhỏ hơn hoặc bằng endDate.");
        }

        return await _repository.GetSummaryAsync(from, to, ct);
    }
}
