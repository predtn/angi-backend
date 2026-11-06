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
        // STEP-01: Validate the request.
        await _validator.ValidateAndThrowAsync(request, ct);

        // STEP-02: Prepare userId.
        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để cập nhật roadmap.");
        // STEP-03: Open the database transaction.
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // STEP-04: Prepare roadmap, then call ApplyScalarChanges, ApplyOriginChanges and ResizeDays.
        var roadmap = await _roadmapRepository.LockAndGetTrackedAsync(roadmapId, userId, ct)
            ?? throw new NotFoundException("ROADMAP_NOT_FOUND", "Không tìm thấy roadmap.");

        ApplyScalarChanges(roadmap, request);
        ApplyOriginChanges(roadmap, request);
        ResizeDays(roadmap, request.NumDaysSpecified ? request.NumDays!.Value : roadmap.NumDays);

        // STEP-05: Save the pending changes.
        await _unitOfWork.SaveChangesAsync(ct);
        // STEP-06: Prepare itemIds, then load feedback through roadmapRepository.GetFeedbackByItemIdsAsync.
        var itemIds = roadmap.Days.SelectMany(day => day.Items).Select(item => item.Id).ToArray();
        var feedback = await _roadmapRepository.GetFeedbackByItemIdsAsync(userId, itemIds, ct);
        // STEP-07: Prepare result through RoadmapMapper.Map.
        var result = RoadmapMapper.Map(roadmap, feedback);
        // STEP-08: Commit the transaction.
        await transaction.CommitAsync(ct);
        // STEP-09: Return result.
        return result;
    }

    /// <summary>Applies supplied non-origin overview values and normalizes the title.</summary>
    private static void ApplyScalarChanges(Domain.Entities.Roadmap roadmap, UpdateRoadmapRequestDto request)
    {
        // STEP-01: When request.TitleSpecified is true, set roadmap.Title.
        if (request.TitleSpecified)
        {
            roadmap.Title = request.Title!.Trim();
        }

        // STEP-02: When request.StartDateSpecified is true, set roadmap.StartDate.
        if (request.StartDateSpecified)
        {
            roadmap.StartDate = request.StartDate;
        }

        // STEP-03: When request.BudgetAmountSpecified is true, set roadmap.BudgetAmount.
        if (request.BudgetAmountSpecified)
        {
            roadmap.BudgetAmount = request.BudgetAmount;
        }
    }

    /// <summary>Resolves the effective origin and enforces the pinned coordinate invariant.</summary>
    private static void ApplyOriginChanges(Domain.Entities.Roadmap roadmap, UpdateRoadmapRequestDto request)
    {
        // STEP-01: Prepare mode from ParseOriginMode.
        var mode = request.OriginModeSpecified ? ParseOriginMode(request.OriginMode!) : roadmap.OriginMode;
        // STEP-02: When mode is not RoadmapOriginMode.Pinned, run the branch below.
        if (mode != RoadmapOriginMode.Pinned)
        {
            // STEP-03: Prepare failures as List<ValidationFailure>.
            var failures = new List<ValidationFailure>();
            // STEP-04: When request.OriginLatitudeSpecified is true and request.OriginLatitude has a value, call failures.Add.
            if (request.OriginLatitudeSpecified && request.OriginLatitude.HasValue)
            {
                failures.Add(new ValidationFailure(nameof(request.OriginLatitude),
                    "Vĩ độ phải để trống khi điểm xuất phát không phải pinned."));
            }

            // STEP-05: When request.OriginLongitudeSpecified is true and request.OriginLongitude has a value, call failures.Add.
            if (request.OriginLongitudeSpecified && request.OriginLongitude.HasValue)
            {
                failures.Add(new ValidationFailure(nameof(request.OriginLongitude),
                    "Kinh độ phải để trống khi điểm xuất phát không phải pinned."));
            }

            // STEP-06: When request.OriginLabelSpecified is true and request.OriginLabel has a value, call failures.Add.
            if (request.OriginLabelSpecified && !string.IsNullOrWhiteSpace(request.OriginLabel))
            {
                failures.Add(new ValidationFailure(nameof(request.OriginLabel),
                    "Tên điểm xuất phát phải để trống khi điểm xuất phát không phải pinned."));
            }

            // STEP-07: Throw ValidationException when failures.Count > 0.
            if (failures.Count > 0)
            {
                throw new ValidationException(failures);
            }

            // STEP-08: Update roadmap: OriginMode, OriginLatitude, OriginLongitude and OriginLabel.
            roadmap.OriginMode = mode;
            roadmap.OriginLatitude = null;
            roadmap.OriginLongitude = null;
            roadmap.OriginLabel = null;
            // STEP-09: Finish the method.
            return;
        }

        // STEP-10: Prepare latitude, longitude and pinnedFailures as List<ValidationFailure>.
        var latitude = request.OriginLatitudeSpecified ? request.OriginLatitude : roadmap.OriginLatitude;
        var longitude = request.OriginLongitudeSpecified ? request.OriginLongitude : roadmap.OriginLongitude;
        var pinnedFailures = new List<ValidationFailure>();
        // STEP-11: When latitude has no value, call pinnedFailures.Add.
        if (!latitude.HasValue)
        {
            pinnedFailures.Add(new ValidationFailure(nameof(request.OriginLatitude),
                "Vĩ độ là bắt buộc khi điểm xuất phát là pinned."));
        }

        // STEP-12: When longitude has no value, call pinnedFailures.Add.
        if (!longitude.HasValue)
        {
            pinnedFailures.Add(new ValidationFailure(nameof(request.OriginLongitude),
                "Kinh độ là bắt buộc khi điểm xuất phát là pinned."));
        }

        // STEP-13: Throw ValidationException when pinnedFailures.Count > 0.
        if (pinnedFailures.Count > 0)
        {
            throw new ValidationException(pinnedFailures);
        }

        // STEP-14: Update roadmap: OriginMode, OriginLatitude and OriginLongitude.
        roadmap.OriginMode = mode;
        roadmap.OriginLatitude = latitude;
        roadmap.OriginLongitude = longitude;
        // STEP-15: When request.OriginLabelSpecified is true, set roadmap.OriginLabel.
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
