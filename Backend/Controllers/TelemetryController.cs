using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Backend.Data;

namespace SmartGreenhouse.Backend.Controllers;

[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly AppDbContext _db;

    public TelemetryController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest(CancellationToken ct)
    {
        var latest = await _db.Telemetries.OrderByDescending(t => t.Timestamp).FirstOrDefaultAsync(ct);
        return latest is null ? NotFound() : Ok(latest);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int minutes = 1440, CancellationToken ct = default)
    {
        var windowStart = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 43200));
        var records = await _db.Telemetries
            .Where(t => t.Timestamp >= windowStart)
            .OrderBy(t => t.Timestamp)
            .ToListAsync(ct);
        return Ok(records);
    }
}
