using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.UseCases.Restaurant.Browse;
using ANGI.Application.UseCases.Restaurant.GetOwnerProfile;
using ANGI.Application.UseCases.Restaurant.GetOwnerMenu;
using ANGI.Application.UseCases.Restaurant.UpdateDishAvailability;
using ANGI.Application.UseCases.Restaurant.CreateMenuSubmission;
using ANGI.Application.UseCases.Restaurant.ListMenuSubmissions;
using ANGI.Application.UseCases.Restaurant.GetMenuSubmission;
using ANGI.Application.UseCases.Restaurant.AddMenuSubmissionItem;
using ANGI.Application.UseCases.Restaurant.UpdateMenuSubmissionItem;
using ANGI.Application.UseCases.Restaurant.DeleteMenuSubmissionItem;
using ANGI.Application.UseCases.Restaurant.CancelMenuSubmission;
using ANGI.Application.UseCases.Restaurant.SubmitMenuSubmission;
using ANGI.Application.UseCases.Restaurant.SubmitVerification;
using ANGI.Application.UseCases.Restaurant.GetRestaurantDetail;
using ANGI.Application.UseCases.Restaurant.GetRestaurantMenu;
using ANGI.Application.UseCases.Restaurant.GetRestaurantReviews;
using ANGI.Application.UseCases.Restaurant.Register;
using ANGI.Application.UseCases.Restaurant.UpdateBusinessHours;
using ANGI.Application.UseCases.Restaurant.UpdateOperatingStatus;
using ANGI.Application.UseCases.Restaurant.UpdateOwnerProfile;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Restaurant;

/// <summary>Centralizes Application-layer registration for the Restaurant module.</summary>
public static class RestaurantModule
{
    /// <summary>Registers restaurant use cases for the lifetime of an HTTP request.</summary>
    public static IServiceCollection AddRestaurantUseCases(this IServiceCollection services)
    {
        services.AddScoped<IRegisterRestaurantUseCase, RegisterRestaurantUseCase>();
        services.AddScoped<IGetOwnerRestaurantUseCase, GetOwnerRestaurantUseCase>();
        services.AddScoped<IUpdateOwnerRestaurantUseCase, UpdateOwnerRestaurantUseCase>();
        services.AddScoped<IUpdateBusinessHoursUseCase, UpdateBusinessHoursUseCase>();
        services.AddScoped<IUpdateOperatingStatusUseCase, UpdateOperatingStatusUseCase>();
        services.AddScoped<IGetOwnerMenuUseCase, GetOwnerMenuUseCase>();
        services.AddScoped<IUpdateDishAvailabilityUseCase, UpdateDishAvailabilityUseCase>();
        services.AddScoped<ICreateMenuSubmissionUseCase, CreateMenuSubmissionUseCase>();
        services.AddScoped<IListMenuSubmissionsUseCase, ListMenuSubmissionsUseCase>();
        services.AddScoped<IGetMenuSubmissionUseCase, GetMenuSubmissionUseCase>();
        services.AddScoped<IAddMenuSubmissionItemUseCase, AddMenuSubmissionItemUseCase>();
        services.AddScoped<IUpdateMenuSubmissionItemUseCase, UpdateMenuSubmissionItemUseCase>();
        services.AddScoped<IDeleteMenuSubmissionItemUseCase, DeleteMenuSubmissionItemUseCase>();
        services.AddScoped<ICancelMenuSubmissionUseCase, CancelMenuSubmissionUseCase>();
        services.AddScoped<ISubmitMenuSubmissionUseCase, SubmitMenuSubmissionUseCase>();
        services.AddScoped<ISubmitVerificationUseCase, SubmitVerificationUseCase>();
        services.AddScoped<IBrowseRestaurantsUseCase, BrowseRestaurantsUseCase>();
        services.AddScoped<IGetRestaurantDetailUseCase, GetRestaurantDetailUseCase>();
        services.AddScoped<IGetRestaurantMenuUseCase, GetRestaurantMenuUseCase>();
        services.AddScoped<IGetRestaurantReviewsUseCase, GetRestaurantReviewsUseCase>();
        return services;
    }
}
