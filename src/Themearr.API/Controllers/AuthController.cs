using Microsoft.AspNetCore.Mvc;
using Themearr.API.Services;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IConfiguration config) : ControllerBase
{
    [HttpPost("verify")]
    public IActionResult Verify([FromBody] VerifyRequest req)
    {
        var ok = ApiAuthMiddleware.Matches(config, req.Token ?? "");
        if (!ok) return Unauthorized(new { ok = false, detail = "Invalid token" });
        return Ok(new { ok = true });
    }
}

public record VerifyRequest(string? Token);
