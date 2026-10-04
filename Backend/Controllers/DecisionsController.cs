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

    [HttpGet("watering/today")]
    public async Task<ActionResult<WateringTodaySummary>> GetTodayWateringSummary(CancellationToken ct = default)
    {
        // Кожен AiDecision із PumpOn=true є окремою опублікованою командою:
        // ESP32 перетворює її на один обмежений у часі імпульс помпи.
        // Межі дня рахуємо в локальному часі теплиці, як і добову норму світла.
        var localDate = DateTime.Today;
        var dayStartUtc = localDate.ToUniversalTime();
        var nextDayStartUtc = localDate.AddDays(1).ToUniversalTime();
        var wateringEvents = _db.AiDecisions.Where(d =>
            d.PumpOn && d.Timestamp >= dayStartUtc && d.Timestamp < nextDayStartUtc);

        var count = await wateringEvents.CountAsync(ct);
        var lastWateringUtc = await wateringEvents
            .OrderByDescending(d => d.Timestamp)
            .Select(d => (DateTime?)d.Timestamp)
            .FirstOrDefaultAsync(ct);

        return Ok(new WateringTodaySummary(
            DateOnly.FromDateTime(localDate),
            count,
            lastWateringUtc));
    }
}

public sealed record WateringTodaySummary(
    DateOnly LocalDate,
    int Count,
    DateTime? LastWateringUtc);
