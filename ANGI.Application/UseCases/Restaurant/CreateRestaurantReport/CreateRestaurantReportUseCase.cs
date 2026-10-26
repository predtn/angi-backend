using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Restaurant.CreateRestaurantReport;

/// <summary>Implements REP-01 to report a restaurant.</summary>
public sealed class CreateRestaurantReportUseCase : ICreateRestaurantReportUseCase
{
    private readonly IValidator<CreateRestaurantReportRequestDto> _validator;
    private readonly IRestaurantRepository _restaurantRepository;
    private readonly IReportRepository _reportRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    public CreateRestaurantReportUseCase(
        IValidator<CreateRestaurantReportRequestDto> validator,
        IRestaurantRepository restaurantRepository,
        IReportRepository reportRepository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _restaurantRepository = restaurantRepository;
        _reportRepository = reportRepository;
        _mediaRepository = mediaRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    public async Task<CreateRestaurantReportResponseDto> ExecuteAsync(
        long restaurantId,
        CreateRestaurantReportRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để báo cáo nhà hàng.");

        // Only a restaurant the traveler can see (REST-02: verified and visible) can be reported.
        var restaurantExists = await _restaurantRepository.ExistsAndActiveAsync(restaurantId, ct);
        if (!restaurantExists)
        {
            throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
        }

        var hasPendingReport = await _reportRepository.HasPendingReportAsync(
            userId, ReportTargetType.Restaurant, restaurantId, ct);

        if (hasPendingReport)
        {
            throw new ConflictException("REPORT_ALREADY_PENDING", "Bạn đã có báo cáo về nhà hàng này đang chờ xử lý.");
        }

        var evidenceIds = request.EvidenceMediaIds?.Distinct().ToArray() ?? [];
        if (evidenceIds.Length > 0 &&
            (await _mediaRepository.GetOwnedByIdsAsync(userId, evidenceIds, ct)).Count != evidenceIds.Length)
        {
            // Evidence URLs are signed for the Mod, so only the reporter's own uploads may be referenced.
            throw new BadRequestException("VALIDATION_FAILED", "Bằng chứng chỉ được là ảnh do chính bạn tải lên.");
        }

        var reasonCode = ParseReasonCode(request.ReasonCode);

        var report = new Report
        {
            ReporterId = userId,
            TargetType = ReportTargetType.Restaurant,
            RestaurantId = restaurantId,
            ReasonCode = reasonCode,
            Description = request.Description?.Trim(),
            Evidence = JsonSerializer.Serialize(evidenceIds),
            Status = ReportStatus.Pending
        };

        _reportRepository.Add(report);
        await _unitOfWork.SaveChangesAsync(ct);

        return new CreateRestaurantReportResponseDto
        {
            ReportId = report.Id,
            Status = "pending"
        };
    }

    private static ReportReasonCode ParseReasonCode(string reasonCode)
    {
        return reasonCode.ToLowerInvariant() switch
        {
            "wrong_info" => ReportReasonCode.WrongInfo,
            "closed" => ReportReasonCode.Closed,
            "spam" => ReportReasonCode.Spam,
            "offensive" => ReportReasonCode.Offensive,
            "hygiene" => ReportReasonCode.Hygiene,
            "scam" => ReportReasonCode.Scam,
            "other" => ReportReasonCode.Other,
            _ => throw new BadRequestException("VALIDATION_FAILED", "Lý do báo cáo không hợp lệ.")
        };
    }
}
