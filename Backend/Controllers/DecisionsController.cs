using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Backend.Data;

namespace SmartGreenhouse.Backend.Controllers;

[ApiController]
[Route("api/decisions")]
public class DecisionsController : ControllerBase
{
    private readonly AppDbContext _db;

    public DecisionsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int count = 50, CancellationToken ct = default)
    {
        var records = await _db.AiDecisions
            .OrderByDescending(d => d.Timestamp)
            .Take(Math.Clamp(count, 1, 500))
            .ToListAsync(ct);
        return Ok(records);
    }
}
