using ANGI.Application.Common.Exceptions;
using ANGI.Application.Common.Interfaces.Repositories.Restaurant;
using ANGI.Application.Common.Interfaces.Services.Auth;
using ANGI.Application.Common.Interfaces.UseCases.Restaurant;
using ANGI.Application.DTOs.Restaurant;

namespace ANGI.Application.UseCases.Restaurant.GetMenuSubmission;

/// <summary>Implements OWN-13 by comparing a submission with the current menu.</summary>
public sealed class GetMenuSubmissionUseCase : IGetMenuSubmissionUseCase
{
    private readonly IRestaurantMenuRepository _repository;
    private readonly ICurrentUserService _currentUser;

    /// <summary>Initializes the owned submission detail query.</summary>
    public GetMenuSubmissionUseCase(IRestaurantMenuRepository repository, ICurrentUserService currentUser)
    {
        _repository = repository;
        _currentUser = currentUser;
    }

    /// <summary>Loads items, derives their change type, and finds removed current dishes.</summary>
    public async Task<MenuSubmissionDetailDto> ExecuteAsync(long submissionId, CancellationToken ct)
    {
        var ownerId = _currentUser.UserId
            ?? throw new UnauthorizedException("UNAUTHORIZED", "Bạn cần đăng nhập để xem bản menu.");
        var submission = await _repository.GetSubmissionDetailsAsync(ownerId, submissionId, ct)
            ?? throw new NotFoundException("SUBMISSION_NOT_FOUND", "Không tìm thấy bản menu.");
        return RestaurantMenuMapper.MapSubmissionDetail(submission);
    }
}
