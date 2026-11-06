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
        // STEP-01: Validate the request.
        await validator.ValidateAndThrowAsync(request, ct);
        // STEP-02: Check the caller's permission through access.RequireAsync.
        await access.RequireAsync(ct, request.TargetType == "restaurant" ? "REPORT_RESTAURANT_HANDLE" : "REPORT_SOCIAL_HANDLE");
        // STEP-03: Load page through repository.ListReportsAsync and displays through repository.GetReportDisplaysAsync.
        var page = await repository.ListReportsAsync(request, ct);
        var displays = await repository.GetReportDisplaysAsync(page.Items, ct);
        // STEP-04: Return a new Paged<ReportDto>.
        return new Paged<ReportDto>
        {
            Items = page.Items.Select(report => ModerationMapper.Report(report,
                displays[new ReportTarget(report.TargetType, (report.RestaurantId ?? report.BlogId)!.Value)], cloudinary)).ToArray(),
            Page = page.Page, PageSize = page.PageSize, TotalItems = page.TotalItems, TotalPages = page.TotalPages
        };
    }
}
