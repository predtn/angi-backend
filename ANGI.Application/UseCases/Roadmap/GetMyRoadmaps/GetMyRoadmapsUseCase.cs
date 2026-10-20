using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Roadmap;
using FluentValidation;

namespace ANGI.Application.UseCases.Roadmap.GetMyRoadmaps;

/// <summary>Coordinates validation and ownership scoping for RM-02.</summary>
public sealed class GetMyRoadmapsUseCase : IGetMyRoadmapsUseCase
{
    private readonly IValidator<GetMyRoadmapsRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;

    /// <summary>Initializes the dependencies required to list the current traveler's roadmaps.</summary>
    public GetMyRoadmapsUseCase(
        IValidator<GetMyRoadmapsRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
    }

    /// <summary>Validates paging, resolves the caller, and returns only that caller's non-deleted roadmaps.</summary>
    public async Task<Paged<RoadmapSummaryDto>> ExecuteAsync(
        GetMyRoadmapsRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để thực hiện thao tác này.");

        return await _roadmapRepository.GetByUserAsync(userId, request.Page, request.PageSize, ct);
    }
}
