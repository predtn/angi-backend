using ANGI.Application.Common.Interfaces.UseCases.Moderation;
using ANGI.Application.DTOs.Moderation;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Moderation;

/// <summary>Exposes individual dish moderation to Mod and Admin accounts.</summary>
[ApiController]
[Route("api/v1/mod/dishes")]
[Authorize(Roles = "MOD,ADMIN")]
public sealed class ModDishesController(IUpdateDishModerationStatusUseCase update) : ControllerBase
{
    /// <summary>MOD-R4: hides or restores a dish while preventing restoration of removed menu items.</summary>
    [HttpPut("{dishId:int}/moderation-status")]
    public async Task<IActionResult> Update(int dishId, UpdateDishModerationStatusRequestDto request, CancellationToken ct) =>
        Ok(new ApiResponse<UpdateDishModerationStatusResponseDto> { Success = true, Data = await update.ExecuteAsync(dishId, request, ct) });
}
