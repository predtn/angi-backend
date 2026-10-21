using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models.Audit;
using ANGI.Application.DTOs.Moderation;
using ANGI.Application.DTOs.Restaurant;
using ANGI.Application.UseCases.Restaurant;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.ReviewVerification;

/// <summary>Implements the atomic MOD-V3 restaurant verification decision workflow.</summary>
public sealed class ReviewVerificationUseCase : IReviewVerificationUseCase
{
    private readonly IValidator<ReviewVerificationRequestDto> _validator;
    private readonly IVerificationReviewRepository _repository;
    private readonly ICurrentUserService _currentUserService;
    private readonly ICloudinaryService _cloudinaryService;
    private readonly IAuditLogService _auditLogService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes validation, authorization, persistence, audit, media, and time boundaries.</summary>
    public ReviewVerificationUseCase(
        IValidator<ReviewVerificationRequestDto> validator,
        IVerificationReviewRepository repository,
        ICurrentUserService currentUserService,
        ICloudinaryService cloudinaryService,
        IAuditLogService auditLogService,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _validator = validator;
        _repository = repository;
        _currentUserService = currentUserService;
        _cloudinaryService = cloudinaryService;
        _auditLogService = auditLogService;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    /// <summary>Locks a pending submission, applies MOD-V3, and saves its notification and audit atomically.</summary>
    public async Task<VerificationDto> ExecuteAsync(
        long verificationId,
        ReviewVerificationRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);
        var reviewerId = await ModerationPermissionGuard.RequireAsync(_currentUserService, _repository, ct);
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        var verification = await _repository.LockForDecisionAsync(verificationId, ct)
            ?? throw new NotFoundException("VERIFICATION_NOT_FOUND", "Không tìm thấy hồ sơ xác minh.");
        if (verification.Status != VerificationStatus.Pending)
        {
            throw new ConflictException("VERIFICATION_ALREADY_REVIEWED", "Hồ sơ xác minh đã được xử lý.");
        }

        var oldRestaurantStatus = verification.Restaurant.VerificationStatus;
        var decision = request.Decision.ToLowerInvariant();
        var note = string.IsNullOrWhiteSpace(request.ReviewNote) ? null : request.ReviewNote.Trim();
        ApplyDecision(verification, decision, reviewerId, note, _timeProvider.GetUtcNow().UtcDateTime);
        await _repository.LoadReviewerAsync(verification, ct);

        _repository.AddNotification(CreateNotification(verification, decision, note));
        AddAudit(verification, oldRestaurantStatus, decision, note);
        await _unitOfWork.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return RestaurantVerificationMapper.Map(verification, _cloudinaryService);
    }

    /// <summary>Applies the documented submission and restaurant status transitions.</summary>
    private static void ApplyDecision(
        RestaurantVerification verification,
        string decision,
        int reviewerId,
        string? note,
        DateTime reviewedAt)
    {
        verification.Status = decision switch
        {
            "approve" => VerificationStatus.Approved,
            "reject" => VerificationStatus.Rejected,
            _ => VerificationStatus.NeedsMoreInfo
        };
        verification.Restaurant.VerificationStatus = decision == "approve"
            ? RestaurantVerificationStatus.Verified
            : RestaurantVerificationStatus.Rejected;
        verification.ReviewedBy = reviewerId;
        verification.ReviewedAt = reviewedAt;
        verification.ReviewNote = note;
    }

    /// <summary>Creates the owner notification corresponding to the selected decision.</summary>
    private static Domain.Entities.Notification CreateNotification(
        RestaurantVerification verification,
        string decision,
        string? note)
    {
        var (type, title, body) = decision switch
        {
            "approve" => ("VERIFICATION_APPROVED", "Hồ sơ xác minh đã được duyệt", "Nhà hàng của bạn đã được xác minh."),
            "reject" => ("VERIFICATION_REJECTED", "Hồ sơ xác minh bị từ chối", note),
            _ => ("VERIFICATION_NEEDS_MORE_INFO", "Hồ sơ xác minh cần bổ sung", note)
        };

        return new Domain.Entities.Notification
        {
            RecipientId = verification.Restaurant.OwnerId,
            Type = type,
            Title = title,
            Body = body,
            Data = JsonSerializer.Serialize(new
            {
                restaurantId = verification.RestaurantId,
                verificationId = verification.Id
            })
        };
    }

    /// <summary>Adds the one documented restaurant verification audit row to the current unit of work.</summary>
    private void AddAudit(
        RestaurantVerification verification,
        RestaurantVerificationStatus oldRestaurantStatus,
        string decision,
        string? note)
    {
        _auditLogService.Add(new AuditLogEntry
        {
            Action = decision == "approve"
                ? AuditActions.RestaurantVerified
                : AuditActions.RestaurantVerificationRejected,
            EntityType = AuditEntityTypes.Restaurant,
            EntityId = verification.RestaurantId,
            SubjectUserId = verification.Restaurant.OwnerId,
            OldValues = new
            {
                VerificationStatus = oldRestaurantStatus,
                SubmissionStatus = VerificationStatus.Pending
            },
            NewValues = new
            {
                VerificationStatus = verification.Restaurant.VerificationStatus,
                SubmissionStatus = verification.Status,
                Reason = note
            }
        });
    }
}
