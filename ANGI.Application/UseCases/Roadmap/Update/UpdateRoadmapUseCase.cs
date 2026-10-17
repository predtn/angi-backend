using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Roadmap;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Roadmap;
using ANGI.Application.DTOs.Roadmap;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;
using FluentValidation.Results;

namespace ANGI.Application.UseCases.Roadmap.Update;

/// <summary>Implements RM-04 as an ownership-scoped, atomic partial update.</summary>
public sealed class UpdateRoadmapUseCase : IUpdateRoadmapUseCase
{
    private readonly IValidator<UpdateRoadmapRequestDto> _validator;
    private readonly IRoadmapRepository _roadmapRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Initializes validation, identity, transaction, and persistence dependencies.</summary>
    public UpdateRoadmapUseCase(
        IValidator<UpdateRoadmapRequestDto> validator,
        IRoadmapRepository roadmapRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _roadmapRepository = roadmapRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    /// <summary>Applies RM-04, safely resizes empty days, and returns recalculated detail data.</summary>
    public async Task<RoadmapDetailDto> ExecuteAsync(
        long roadmapId,
        UpdateRoadmapRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để cập nhật roadmap.");
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        var roadmap = await _roadmapRepository.LockAndGetTrackedAsync(roadmapId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");

        ApplyScalarChanges(roadmap, request);
        ApplyOriginChanges(roadmap, request);
        ResizeDays(roadmap, request.NumDaysSpecified ? request.NumDays!.Value : roadmap.NumDays);

        await _unitOfWork.SaveChangesAsync(ct);
        var itemIds = roadmap.Days.SelectMany(day => day.Items).Select(item => item.Id).ToArray();
        var feedback = await _roadmapRepository.GetFeedbackByItemIdsAsync(userId, itemIds, ct);
        var result = RoadmapMapper.Map(roadmap, feedback);
        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>Applies supplied non-origin overview values and normalizes the title.</summary>
    private static void ApplyScalarChanges(Domain.Entities.Roadmap roadmap, UpdateRoadmapRequestDto request)
    {
        if (request.TitleSpecified)
        {
            roadmap.Title = request.Title!.Trim();
        }

        if (request.StartDateSpecified)
        {
            roadmap.StartDate = request.StartDate;
        }

        if (request.BudgetAmountSpecified)
        {
            roadmap.BudgetAmount = request.BudgetAmount;
        }
    }

    /// <summary>Resolves the effective origin and enforces the pinned coordinate invariant.</summary>
    private static void ApplyOriginChanges(Domain.Entities.Roadmap roadmap, UpdateRoadmapRequestDto request)
    {
        var mode = request.OriginModeSpecified ? ParseOriginMode(request.OriginMode!) : roadmap.OriginMode;
        if (mode != RoadmapOriginMode.Pinned)
        {
            var failures = new List<ValidationFailure>();
            if (request.OriginLatitudeSpecified && request.OriginLatitude.HasValue)
            {
                failures.Add(new ValidationFailure(nameof(request.OriginLatitude),
                    "Vĩ độ phải để trống khi điểm xuất phát không phải pinned."));
            }

            if (request.OriginLongitudeSpecified && request.OriginLongitude.HasValue)
            {
                failures.Add(new ValidationFailure(nameof(request.OriginLongitude),
                    "Kinh độ phải để trống khi điểm xuất phát không phải pinned."));
            }

            if (request.OriginLabelSpecified && !string.IsNullOrWhiteSpace(request.OriginLabel))
            {
                failures.Add(new ValidationFailure(nameof(request.OriginLabel),
                    "Tên điểm xuất phát phải để trống khi điểm xuất phát không phải pinned."));
            }

            if (failures.Count > 0)
            {
                throw new ValidationException(failures);
            }

            roadmap.OriginMode = mode;
            roadmap.OriginLatitude = null;
            roadmap.OriginLongitude = null;
            roadmap.OriginLabel = null;
            return;
        }

        var latitude = request.OriginLatitudeSpecified ? request.OriginLatitude : roadmap.OriginLatitude;
        var longitude = request.OriginLongitudeSpecified ? request.OriginLongitude : roadmap.OriginLongitude;
        var pinnedFailures = new List<ValidationFailure>();
        if (!latitude.HasValue)
        {
            pinnedFailures.Add(new ValidationFailure(nameof(request.OriginLatitude),
                "Vĩ độ là bắt buộc khi điểm xuất phát là pinned."));
        }

        if (!longitude.HasValue)
        {
            pinnedFailures.Add(new ValidationFailure(nameof(request.OriginLongitude),
                "Kinh độ là bắt buộc khi điểm xuất phát là pinned."));
        }

        if (pinnedFailures.Count > 0)
        {
            throw new ValidationException(pinnedFailures);
        }

        roadmap.OriginMode = mode;
        roadmap.OriginLatitude = latitude;
        roadmap.OriginLongitude = longitude;
        if (request.OriginLabelSpecified)
        {
            roadmap.OriginLabel = NormalizeOptional(request.OriginLabel);
        }
    }

    /// <summary>Adds consecutive empty days or removes only trailing days that contain no items.</summary>
    private static void ResizeDays(Domain.Entities.Roadmap roadmap, short targetNumDays)
    {
        if (targetNumDays < roadmap.NumDays)
        {
            var removedDays = roadmap.Days.Where(day => day.DayNumber > targetNumDays).ToArray();
            if (removedDays.Any(day => day.Items.Count > 0))
            {
                throw new ConflictException("DAY_NOT_EMPTY", "Không thể giảm số ngày vì ngày bị cắt vẫn còn món.");
            }

            foreach (var day in removedDays)
            {
                roadmap.Days.Remove(day);
            }
        }
        else if (targetNumDays > roadmap.NumDays)
        {
            for (var dayNumber = roadmap.NumDays + 1; dayNumber <= targetNumDays; dayNumber++)
            {
                roadmap.Days.Add(new RoadmapDay
                {
                    DayNumber = (short)dayNumber,
                    Roadmap = roadmap
                });
            }
        }

        roadmap.NumDays = targetNumDays;
    }

    /// <summary>Parses a validated public origin value into its domain enum.</summary>
    private static RoadmapOriginMode ParseOriginMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "pinned" => RoadmapOriginMode.Pinned,
        "gps" => RoadmapOriginMode.Gps,
        _ => RoadmapOriginMode.None
    };

    /// <summary>Trims optional text and converts blank values to null.</summary>
    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
