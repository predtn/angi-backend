using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.Common.Models.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Application.UseCases.Validators.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Roadmap.Generate;

/// <summary>
/// Implements RM-10: queues one generation job per roadmap at a time. The background worker runs it
/// (ProcessRoadmapGenerationJobUseCase); the app follows it through RM-11.
/// </summary>
public sealed class GenerateRoadmapUseCase : IGenerateRoadmapUseCase
{
    public const decimal DefaultRadiusKm = 10;

    private readonly IValidator<GenerateRoadmapRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly IRoadmapGenerationRepository _generationRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes validation, persistence, current-user, and transaction dependencies.</summary>
    public GenerateRoadmapUseCase(
        IValidator<GenerateRoadmapRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        IRoadmapGenerationRepository generationRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _generationRepository = generationRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Locks the roadmap so two requests cannot both queue a job, then stores the job with its origin.</summary>
    public async Task<GenerateRoadmapResponseDto> ExecuteAsync(
        long roadmapId,
        GenerateRoadmapRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để tạo roadmap bằng AI.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var roadmap = await _roadmapRepository.LockAndGetTrackedAsync(roadmapId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");
        if (request.Scope != GenerateRoadmapRequestDtoValidator.ScopeFull &&
            roadmap.Days.All(day => day.Id != request.DayId))
        {
            throw new NotFoundException("DAY_NOT_FOUND", "Không tìm thấy ngày trong roadmap.");
        }

        if (await _generationRepository.HasActiveJobAsync(roadmapId, ct))
        {
            throw new ConflictException("GENERATION_IN_PROGRESS", "Roadmap đang được AI tạo, vui lòng chờ xong.");
        }

        var origin = ResolveOrigin(roadmap, request);
        var job = new RoadmapGenerationJob
        {
            RoadmapId = roadmapId,
            RequestedBy = userId,
            Scope = RoadmapGenerationValues.ParseScope(request.Scope),
            Input = RoadmapGenerationValues.Serialize(new RoadmapGenerationInput
            {
                Request = request,
                Origin = origin,
                RadiusKm = origin is null ? null : request.RadiusKm ?? DefaultRadiusKm
            }),
            Status = GenerationJobStatus.Queued
        };
        _generationRepository.AddJob(job);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new GenerateRoadmapResponseDto
        {
            JobId = job.Id,
            Status = RoadmapGenerationValues.ToApi(GenerationJobStatus.Queued)
        };
    }

    /// <summary>GPS sent with the request for a gps roadmap, the pinned point for a pinned one, otherwise none.</summary>
    private static RoadmapGenerationOrigin? ResolveOrigin(Domain.Entities.Roadmap roadmap, GenerateRoadmapRequestDto request) =>
        roadmap.OriginMode switch
        {
            RoadmapOriginMode.Gps when request.CurrentLatitude is { } latitude && request.CurrentLongitude is { } longitude =>
                new RoadmapGenerationOrigin(RoadmapGenerationValues.ToApi(RoadmapOriginMode.Gps), latitude, longitude),
            RoadmapOriginMode.Pinned when roadmap.OriginLatitude is { } latitude && roadmap.OriginLongitude is { } longitude =>
                new RoadmapGenerationOrigin(RoadmapGenerationValues.ToApi(RoadmapOriginMode.Pinned), latitude, longitude),
            _ => null
        };
}
