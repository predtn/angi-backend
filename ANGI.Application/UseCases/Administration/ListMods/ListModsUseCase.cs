using ANGI.Application.Common.Interfaces.Repositories.Administration;
using ANGI.Application.Common.Interfaces.UseCases.Administration;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Administration;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Administration.ListMods;

/// <summary>Implements ADM-M1 by filtering and paging MOD accounts.</summary>
public sealed class ListModsUseCase : IListModsUseCase
{
    private readonly IValidator<ListModsRequestDto> _validator;
    private readonly IAdministrationModRepository _repository;

    /// <summary>Initializes the MOD list workflow with validation and persistence.</summary>
    public ListModsUseCase(
        IValidator<ListModsRequestDto> validator,
        IAdministrationModRepository repository)
    {
        _validator = validator;
        _repository = repository;
    }

    /// <summary>Validates ADM-M1 input and returns its mapped page.</summary>
    public async Task<Paged<AdminUserCardDto>> ExecuteAsync(
        ListModsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var status = request.Status switch
        {
            "active" => UserStatus.Active,
            "deactivated" => UserStatus.Deactivated,
            _ => (UserStatus?)null
        };
        var page = await _repository.GetModsAsync(
            string.IsNullOrWhiteSpace(request.Q) ? null : request.Q.Trim(),
            status,
            request.Page,
            request.PageSize,
            ct);
        return AdministrationModMapper.ToPagedDto(page);
    }
}
