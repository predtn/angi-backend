using ANGI.Application.DTOs.Administration;
using FluentValidation;

namespace ANGI.Application.UseCases.Validators.Administration;

/// <summary>Validates ADM-D2 metric, time range, and aggregation interval parameters.</summary>
public sealed class GetDashboardTimeSeriesRequestDtoValidator : AbstractValidator<GetDashboardTimeSeriesRequestDto>
{
    private static readonly string[] AllowedMetrics =
    [
        "new_users",
        "new_restaurants",
        "roadmaps",
        "blogs",
        "reports",
        "feedbacks",
        "dish_rolls"
    ];

    private static readonly string[] AllowedIntervals = ["day", "week", "month"];

    /// <summary>Creates validation rules for metric, date range, and bucket interval.</summary>
    public GetDashboardTimeSeriesRequestDtoValidator()
    {
        RuleFor(request => request.Metric)
            .NotEmpty().WithMessage("metric không được để trống.")
            .Must(metric => string.IsNullOrWhiteSpace(metric) || AllowedMetrics.Contains(metric.Trim().ToLowerInvariant()))
            .WithMessage("metric không hợp lệ. Cho phép: new_users, new_restaurants, roadmaps, blogs, reports, feedbacks, dish_rolls.");

        RuleFor(request => request.From)
            .NotNull().WithMessage("from không được để trống.");

        RuleFor(request => request.To)
            .NotNull().WithMessage("to không được để trống.");

        RuleFor(request => request.Interval)
            .Must(interval => interval is null || AllowedIntervals.Contains(interval.Trim().ToLowerInvariant()))
            .WithMessage("interval không hợp lệ. Cho phép: day, week, month.");

        RuleFor(request => request)
            .Must(request => !(request.From.HasValue && request.To.HasValue) || request.From.Value <= request.To.Value)
            .WithMessage("startDate phải nhỏ hơn hoặc bằng endDate.")
            .Must(request =>
            {
                if (!request.From.HasValue || !request.To.HasValue) return true;
                var interval = request.Interval?.Trim().ToLowerInvariant() ?? "day";
                if (interval == "day")
                {
                    return (request.To.Value.DayNumber - request.From.Value.DayNumber) <= 366;
                }
                return true;
            })
            .WithMessage("Khoảng thời gian không được vượt quá 366 ngày khi interval=day.");
    }
}
