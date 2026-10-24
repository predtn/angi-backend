using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.UseCases.Moderation.ApproveMenuSubmission;
using ANGI.Application.UseCases.Moderation.ClaimReport;
using ANGI.Application.UseCases.Moderation.ExpireUserSuspensions;
using ANGI.Application.UseCases.Moderation.GetMenuSubmissionReview;
using ANGI.Application.UseCases.Moderation.GetModRestaurant;
using ANGI.Application.UseCases.Moderation.GetReport;
using ANGI.Application.UseCases.Moderation.GetVerificationReview;
using ANGI.Application.UseCases.Moderation.ListModMenuSubmissions;
using ANGI.Application.UseCases.Moderation.ListModRestaurants;
using ANGI.Application.UseCases.Moderation.ListReports;
using ANGI.Application.UseCases.Moderation.ListVerifications;
using ANGI.Application.UseCases.Moderation.RejectMenuSubmission;
using ANGI.Application.UseCases.Moderation.ResolveReport;
using ANGI.Application.UseCases.Moderation.ReviewVerification;
using ANGI.Application.UseCases.Moderation.UpdateDishModerationStatus;
using ANGI.Application.UseCases.Moderation.UpdateRestaurantModerationStatus;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Centralizes Application-layer registrations for moderation features.</summary>
public static class ModerationModule
{
    /// <summary>Registers the verification, menu (MOD-M1 to MOD-M4), restaurant and report moderation use cases for one HTTP request.</summary>
    public static IServiceCollection AddModerationUseCases(this IServiceCollection services)
    {
        services.AddScoped<IListVerificationsUseCase, ListVerificationsUseCase>();
        services.AddScoped<IGetVerificationReviewUseCase, GetVerificationReviewUseCase>();
        services.AddScoped<IReviewVerificationUseCase, ReviewVerificationUseCase>();
        services.AddScoped<IListModMenuSubmissionsUseCase, ListModMenuSubmissionsUseCase>();
        services.AddScoped<IGetMenuSubmissionReviewUseCase, GetMenuSubmissionReviewUseCase>();
        services.AddScoped<IApproveMenuSubmissionUseCase, ApproveMenuSubmissionUseCase>();
        services.AddScoped<IRejectMenuSubmissionUseCase, RejectMenuSubmissionUseCase>();
        services.AddScoped<ModerationAccess>();
        services.AddScoped<RestaurantModerationEffects>();
        services.AddScoped<IListModRestaurantsUseCase, ListModRestaurantsUseCase>();
        services.AddScoped<IGetModRestaurantUseCase, GetModRestaurantUseCase>();
        services.AddScoped<IUpdateRestaurantModerationStatusUseCase, UpdateRestaurantModerationStatusUseCase>();
        services.AddScoped<IUpdateDishModerationStatusUseCase, UpdateDishModerationStatusUseCase>();
        services.AddScoped<IResolveReportUseCase, ResolveReportUseCase>();
        services.AddScoped<IListReportsUseCase, ListReportsUseCase>();
        services.AddScoped<IGetReportUseCase, GetReportUseCase>();
        services.AddScoped<IClaimReportUseCase, ClaimReportUseCase>();
        services.AddScoped<IExpireUserSuspensionsUseCase, ExpireUserSuspensionsUseCase>();
        return services;
    }
}
