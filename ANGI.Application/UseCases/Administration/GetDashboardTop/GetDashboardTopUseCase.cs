using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.GetDashboardTop;

/// <summary>Implements ADM-D3 by validating top parameters and loading top-ranked items.</summary>
public sealed class GetDashboardTopUseCase : IGetDashboardTopUseCase
{
    private readonly IValidator<GetDashboardTopRequestDto> _validator;
    private readonly IAdministrationDashboardRepository _repository;

    /// <summary>Initializes the use case with validator and repository.</summary>
    public GetDashboardTopUseCase(
        IValidator<GetDashboardTopRequestDto> validator,
        IAdministrationDashboardRepository repository)
    {
        _validator = validator;
        _repository = repository;
    }

    /// <summary>Validates ADM-D3 input and returns ranked items.</summary>
    public async Task<DashboardTopDto> ExecuteAsync(
        GetDashboardTopRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var type = request.Type.Trim().ToLowerInvariant();
        var limit = request.Limit ?? 10;

        var items = await _repository.GetTopAsync(type, limit, ct);

        return new DashboardTopDto
        {
            Type = type,
            Items = items
        };
    }
}
