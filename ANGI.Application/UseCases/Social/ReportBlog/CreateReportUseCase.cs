using ANGI.Application.Common.Interfaces.Repositories;
using ANGI.Application.Common.Interfaces.Repositories.Media;
using ANGI.Application.Common.Interfaces.Repositories.Social;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.DTOs.Social;
using FluentValidation;

namespace ANGI.Application.UseCases.Social.ReportBlog;

/// <summary>Implements REP-02 to create a report for a blog (Clean Architecture use case).</summary>
public sealed class CreateReportUseCase : ReportBlogUseCase
{
    public CreateReportUseCase(
        IValidator<ReportBlogRequestDto> validator,
        IBlogRepository blogRepository,
        IReportRepository reportRepository,
        IMediaRepository mediaRepository,
        ICurrentUserService currentUserService,
        IUnitOfWork unitOfWork)
        : base(validator, blogRepository, reportRepository, mediaRepository, currentUserService, unitOfWork)
    {
    }
}
