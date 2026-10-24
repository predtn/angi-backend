using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Roadmap.Create;

/// <summary>Implements RM-01 by creating a roadmap aggregate with empty days.</summary>
public sealed class CreateRoadmapUseCase : ICreateRoadmapUseCase
{
    private readonly IValidator<CreateRoadmapRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes validation, identity, and persistence dependencies.</summary>
    public CreateRoadmapUseCase(
        IValidator<CreateRoadmapRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Validates RM-01, creates consecutive empty days, and returns the full detail.</summary>
    public async Task<RoadmapDetailDto> ExecuteAsync(CreateRoadmapRequestDto request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để tạo roadmap.");
        var originMode = ParseOriginMode(request.OriginMode);
        var roadmap = new Domain.Entities.Roadmap
        {
            UserId = userId,
            Title = request.Title.Trim(),
            StartDate = request.StartDate,
            NumDays = request.NumDays,
            BudgetAmount = request.BudgetAmount,
            OriginMode = originMode,
            OriginLatitude = originMode == RoadmapOriginMode.Pinned ? request.OriginLatitude : null,
            OriginLongitude = originMode == RoadmapOriginMode.Pinned ? request.OriginLongitude : null,
            OriginLabel = originMode == RoadmapOriginMode.Pinned ? NormalizeOptional(request.OriginLabel) : null
        };
        for (short dayNumber = 1; dayNumber <= request.NumDays; dayNumber++)
        {
            roadmap.Days.Add(new RoadmapDay { DayNumber = dayNumber, Roadmap = roadmap });
        }

        _roadmapRepository.Add(roadmap);
        await _unitOfWork.SaveChangesAsync(ct);
        return RoadmapMapper.Map(roadmap);
    }

    /// <summary>Parses the optional public origin value, whose validation already guarantees validity.</summary>
    private static RoadmapOriginMode ParseOriginMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pinned" => RoadmapOriginMode.Pinned,
        "gps" => RoadmapOriginMode.Gps,
        _ => RoadmapOriginMode.None
    };

    /// <summary>Trims optional text and converts blank values to null.</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
