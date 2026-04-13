using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/youtube-auth")]
public class YouTubeAuthController(YouTubeAuthService auth) : ControllerBase
{
    [HttpGet]
    public IActionResult GetStatus() => Ok(auth.GetStatus());

    [HttpPost("start")]
    public IActionResult Start()
    {
        auth.StartFlow();
        return Accepted();
    }

    [HttpDelete]
    public IActionResult Revoke()
    {
        auth.Revoke();
        return Ok(new { authenticated = false });
    }
}
