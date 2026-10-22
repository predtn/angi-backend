using ANGI.Application.Common.Interfaces.UseCases.Social;
using ANGI.Application.UseCases.Social.CreateBlog;
using ANGI.Application.UseCases.Social.CreateBlogComment;
using ANGI.Application.UseCases.Social.DeleteBlogComment;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Application.UseCases.Social;

/// <summary>Registers Social use cases for the lifetime of an HTTP request.</summary>
public static class SocialModule
{
    /// <summary>Adds every Social use case to the Application dependency graph.</summary>
    public static IServiceCollection AddSocialUseCases(this IServiceCollection services)
    {
        services.AddScoped<ICreateBlogUseCase, CreateBlogUseCase>();
        services.AddScoped<ICreateBlogCommentUseCase, CreateBlogCommentUseCase>();
        services.AddScoped<IDeleteBlogCommentUseCase, DeleteBlogCommentUseCase>();
        return services;
    }
}
