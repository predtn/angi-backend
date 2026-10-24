using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.Services;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.Common.Models.Moderation;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.ListReports;

/// <summary>Implements MOD-RP1 without leaking reports across target-specific permission boundaries.</summary>
public sealed class ListReportsUseCase(IValidator<ListReportsRequestDto> validator, ModerationAccess access,
    IRestaurantModerationRepository repository, ICloudinaryService cloudinary) : IListReportsUseCase
{
    /// <summary>Validates the query, authorizes its target and maps a batch-loaded report page.</summary>
    public async Task<Paged<ReportDto>> ExecuteAsync(ListReportsRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, request.TargetType == "restaurant" ? "REPORT_RESTAURANT_HANDLE" : "REPORT_SOCIAL_HANDLE");
        var page = await repository.ListReportsAsync(request, ct);
        var displays = await repository.GetReportDisplaysAsync(page.Items, ct);
        return new Paged<ReportDto>
        {
            Items = page.Items.Select(report => ModerationMapper.Report(report,
                displays[new ReportTarget(report.TargetType, (report.RestaurantId ?? report.BlogId)!.Value)], cloudinary)).ToArray(),
            Page = page.Page, PageSize = page.PageSize, TotalItems = page.TotalItems, TotalPages = page.TotalPages
        };
    }
}
