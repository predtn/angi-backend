using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.Common.Models;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes the restaurant moderation endpoints to Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/restaurants")]
[Authorize(Roles = "MOD,ADMIN")]
public sealed class ModRestaurantsController(IListModRestaurantsUseCase list, IGetModRestaurantUseCase detail,
    IUpdateRestaurantModerationStatusUseCase update) : ControllerBase
{
    /// <summary>MOD-R1: lists restaurants using moderator filters and pagination.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListModRestaurantsRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<Paged<ModRestaurantCardDto>> { Success = true, Data = await list.ExecuteAsync(request, ct) });

    /// <summary>MOD-R2: returns the restaurant profile, owner, menu, reports and recent audit entries.</summary>
    [HttpGet("{restaurantId:long}")]
    public async Task<IActionResult> Get(long restaurantId, CancellationToken ct) =>
        Ok(new ApiResponse<ModRestaurantDetailDto> { Success = true, Data = await detail.ExecuteAsync(restaurantId, ct) });

    /// <summary>MOD-R3: changes restaurant visibility with an explanation and optional report reference.</summary>
    [HttpPut("{restaurantId:long}/moderation-status")]
    public async Task<IActionResult> Update(long restaurantId, UpdateRestaurantModerationStatusRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<UpdateRestaurantModerationStatusResponseDto> { Success = true, Data = await update.ExecuteAsync(restaurantId, request, ct) });
}
