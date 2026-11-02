using ANGI.Application.Common.Interfaces.Repositories.Moderation;
using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using FluentValidation;

namespace ANGI.Application.UseCases.Moderation.GetModBlogs;

/// <summary>Implements MOD-B1: lists blogs for moderation.</summary>
public sealed class GetModBlogsUseCase(
    IValidator<GetModBlogsRequestDto> validator,
    ModerationAccess access,
    IBlogModerationRepository repository) : IGetModBlogsUseCase
{
    /// <summary>Executes the MOD-B1 query with authorization, validation and filtering.</summary>
    public async Task<Paged<ModBlogCardDto>> ExecuteAsync(GetModBlogsRequestDto request, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        await access.RequireAsync(ct, "BLOG_MODERATE");

        return await repository.ListBlogsAsync(request, ct);
    }
}
