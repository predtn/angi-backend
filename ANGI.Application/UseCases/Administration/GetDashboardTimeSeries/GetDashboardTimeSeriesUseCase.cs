using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.GetDashboardTimeSeries;

/// <summary>Implements ADM-D2 by validating time-series parameters and loading chart metrics.</summary>
public sealed class GetDashboardTimeSeriesUseCase : IGetDashboardTimeSeriesUseCase
{
    private readonly IValidator<GetDashboardTimeSeriesRequestDto> _validator;
    private readonly IAdministrationDashboardRepository _repository;

    /// <summary>Initializes the use case with validator and repository.</summary>
    public GetDashboardTimeSeriesUseCase(
        IValidator<GetDashboardTimeSeriesRequestDto> validator,
        IAdministrationDashboardRepository repository)
    {
        _validator = validator;
        _repository = repository;
    }

    /// <summary>Validates ADM-D2 boundaries and returns time-series chart points.</summary>
    public async Task<DashboardTimeSeriesDto> ExecuteAsync(
        GetDashboardTimeSeriesRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var from = request.From!.Value;
        var to = request.To!.Value;

        if (from > to)
        {
            throw new BadRequestException("INVALID_DATE_RANGE", "startDate phải nhỏ hơn hoặc bằng endDate.");
        }

        var metric = request.Metric.Trim().ToLowerInvariant();
        var interval = string.IsNullOrWhiteSpace(request.Interval) ? "day" : request.Interval.Trim().ToLowerInvariant();

        var points = await _repository.GetTimeSeriesAsync(metric, from, to, interval, ct);

        return new DashboardTimeSeriesDto
        {
            Metric = metric,
            Interval = interval,
            Points = points
        };
    }
}
