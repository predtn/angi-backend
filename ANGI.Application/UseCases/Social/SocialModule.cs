using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.UseCases.Social.CreateBlog;
using ANGI.Application.UseCases.Social.DeleteBlog;
using ANGI.Application.UseCases.Social.EditBlog;
using ANGI.Application.UseCases.Social.GetBlogComments;
using ANGI.Application.UseCases.Social.GetBlogDetail;
using ANGI.Application.UseCases.Social.GetMyBlogs;
using ANGI.Application.UseCases.Social.GetPublicBlogs;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Social;

/// <summary>Registers Social use cases for the lifetime of an HTTP request.</summary>
public static class SocialModule
{
    /// <summary>Adds every Social use case to the Application dependency graph.</summary>
    public static IServiceCollection AddSocialUseCases(this IServiceCollection services)
    {
        services.AddScoped<IGetPublicBlogsUseCase, GetPublicBlogsUseCase>();
        services.AddScoped<ICreateBlogUseCase, CreateBlogUseCase>();
        services.AddScoped<IGetBlogDetailUseCase, GetBlogDetailUseCase>();
        services.AddScoped<IGetBlogCommentsUseCase, GetBlogCommentsUseCase>();
        services.AddScoped<IGetMyBlogsUseCase, GetMyBlogsUseCase>();
        services.AddScoped<IEditBlogUseCase, EditBlogUseCase>();
        services.AddScoped<IDeleteBlogUseCase, DeleteBlogUseCase>();
        return services;
    }
}
