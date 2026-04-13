using Microsoft.AspNetCore.Mvc;
using Themearr.API.Data;

namespace Themearr.API.Controllers;

[ApiController]
[Route("api/history")]
public class HistoryController(Database db) : ControllerBase
{
    [HttpGet]
    public IActionResult GetHistory() => Ok(db.GetThemeHistory());
}
