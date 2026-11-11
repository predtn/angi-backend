using System.Text.Json;
using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.DTOs.Social;
using ANGI.Domain.Entities;
using ANGI.Domain.Enums;
using FluentValidation;

namespace ANGI.Application.UseCases.Social.ReportBlog;

/// <summary>Implements REP-02 to report a blog.</summary>
public class ReportBlogUseCase : IReportBlogUseCase, ICreateReportUseCase
{
    private readonly IValidator<ReportBlogRequestDto> _validator;
    private readonly IBlogRepository _blogRepository;
    private readonly IReportRepository _reportRepository;
    private readonly IMediaRepository _mediaRepository;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUnitOfWork _unitOfWork;

    public ReportBlogUseCase(
        IValidator<ReportBlogRequestDto> validator,
        IBlogRepository blogRepository,
        IReportRepository reportRepository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
    {
        _validator = validator;
        _blogRepository = blogRepository;
        _reportRepository = reportRepository;
        _mediaRepository = mediaRepository;
        _currentUserService = currentUserService;
        _unitOfWork = unitOfWork;
    }

    public async Task<CreateReportResponseDto> ExecuteAsync(
        long blogId,
        ReportBlogRequestDto request,
        CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var userId = _currentUserService.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để báo cáo.");

        var blog = await _blogRepository.GetByIdAsync(blogId, ct)
            ?? throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");

        if (blog.Status != BlogStatus.Published)
        {
            throw new NotFoundException("BLOG_NOT_FOUND", "Không tìm thấy bài viết.");
        }

        if (blog.AuthorId == userId)
        {
            throw new BadRequestException("CANNOT_REPORT_OWN_CONTENT", "Bạn không thể tự báo cáo nội dung của mình.");
        }

        var hasPendingReport = await _reportRepository.HasPendingReportAsync(
            userId, ReportTargetType.Blog, blogId, ct);

        if (hasPendingReport)
        {
            throw new ConflictException("REPORT_ALREADY_PENDING", "Bạn đã có báo cáo về bài viết này đang chờ xử lý.");
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
            TargetType = ReportTargetType.Blog,
            BlogId = blogId,
            ReasonCode = reasonCode,
            Description = request.Description?.Trim(),
            Evidence = JsonSerializer.Serialize(evidenceIds),
            Status = ReportStatus.Pending
        };

        _reportRepository.Add(report);
        await _unitOfWork.SaveChangesAsync(ct);

        return new CreateReportResponseDto
        {
            ReportId = report.Id,
            Status = "pending"
        };
    }

    private static ReportReasonCode ParseReasonCode(string reasonCode)
    {
        return reasonCode.ToLowerInvariant() switch
        {
            "spam" => ReportReasonCode.Spam,
            "offensive" => ReportReasonCode.Offensive,
            "wrong_info" => ReportReasonCode.WrongInfo,
            "scam" => ReportReasonCode.Scam,
            "other" => ReportReasonCode.Other,
            _ => throw new BadRequestException("VALIDATION_FAILED", "Lý do báo cáo không hợp lệ.")
        };
    }
}
