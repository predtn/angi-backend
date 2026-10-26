using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
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
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    public CreateRestaurantReportUseCase(
        IValidator<CreateRestaurantReportRequestDto> validator,
        IRestaurantRepository restaurantRepository,
        IReportRepository reportRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _restaurantRepository = restaurantRepository;
        _reportRepository = reportRepository;
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

        var restaurantExists = await _restaurantRepository.ExistsAsync(restaurantId, ct);
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

        var reasonCode = ParseReasonCode(request.ReasonCode);

        var report = new Report
        {
            ReporterId = userId,
            TargetType = ReportTargetType.Restaurant,
            RestaurantId = restaurantId,
            ReasonCode = reasonCode,
            Description = request.Description?.Trim(),
            Evidence = request.EvidenceMediaIds is { Count: > 0 }
                ? JsonSerializer.Serialize(request.EvidenceMediaIds)
                : "[]",
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
