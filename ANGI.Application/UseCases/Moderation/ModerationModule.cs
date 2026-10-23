using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.UseCases.Moderation.ListModMenuSubmissions;
using ANGI.Application.UseCases.Moderation.GetMenuSubmissionReview;
using ANGI.Application.UseCases.Moderation.ApproveMenuSubmission;
using ANGI.Application.UseCases.Moderation.RejectMenuSubmission;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Registers menu moderation use cases.</summary>
public static class ModerationModule
{
    /// <summary>Adds the four MOD-M1 through MOD-M4 operations to the application container.</summary>
    public static IServiceCollection AddModerationUseCases(this IServiceCollection services)
    {
        services.AddScoped<IListModMenuSubmissionsUseCase, ListModMenuSubmissionsUseCase>();
        services.AddScoped<IGetMenuSubmissionReviewUseCase, GetMenuSubmissionReviewUseCase>();
        services.AddScoped<IApproveMenuSubmissionUseCase, ApproveMenuSubmissionUseCase>();
        services.AddScoped<IRejectMenuSubmissionUseCase, RejectMenuSubmissionUseCase>();
        return services;
    }
}
