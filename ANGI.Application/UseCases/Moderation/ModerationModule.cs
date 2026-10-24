using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.UseCases.Moderation.GetVerificationReview;
using ANGI.Application.UseCases.Moderation.ListVerifications;
using ANGI.Application.UseCases.Moderation.ReviewVerification;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Moderation;

/// <summary>Centralizes Application-layer registrations for moderation features.</summary>
public static class ModerationModule
{
    /// <summary>Registers the verification moderation use cases for one HTTP request.</summary>
    public static IServiceCollection AddModerationUseCases(this IServiceCollection services)
    {
        services.AddScoped<IListVerificationsUseCase, ListVerificationsUseCase>();
        services.AddScoped<IGetVerificationReviewUseCase, GetVerificationReviewUseCase>();
        services.AddScoped<IReviewVerificationUseCase, ReviewVerificationUseCase>();
        return services;
    }
}
