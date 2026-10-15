using ANGI.Application.Common.Interfaces.UseCases.Discovery;
using ANGI.Application.DTOs.Discovery;
using ANGI.WebApi.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ANGI.WebApi.Controllers.Discovery;

/// <summary>Exposes the home page data (HOME-01).</summary>
[ApiController]
[Route("api/v1/home")]
public sealed class HomeController : ControllerBase
{
    private readonly IGetHomeUseCase _getHomeUseCase;

    /// <summary>Initializes the HTTP boundary with the home use case.</summary>
    public HomeController(IGetHomeUseCase getHomeUseCase)
    {
        _getHomeUseCase = getHomeUseCase;
    }

    /// <summary>Receives HOME-01 and returns the trending, top restaurant, featured blog and recommended blocks.</summary>
    [AllowAnonymous]
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<GetHomeResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get([FromQuery] GetHomeRequestDto request, CancellationToken ct)
    {
        var result = await _getHomeUseCase.ExecuteAsync(request, ct);
        return Ok(new ApiResponse<GetHomeResponseDto> { Success = true, Data = result });
    }
}
