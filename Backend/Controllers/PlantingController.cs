using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;
using SmartGreenhouse.Backend.Services;

namespace SmartGreenhouse.Backend.Controllers;

public record PlantingRequest(string PlantName, string SoilType, DateTime PlantedDateUtc, string? Notes);

// Онбординг нової посадки — замінює правку appsettings.json:Plant + перезапуск
// Backend на POST з мобільного застосунку. Див. розділ "Ініціалізація нової
// посадки" в плані мобільного застосунку.
[ApiController]
[Route("api/planting")]
public class PlantingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AiAgronomistService _aiAgronomist;

    public PlantingController(AppDbContext db, AiAgronomistService aiAgronomist)
    {
        _db = db;
        _aiAgronomist = aiAgronomist;
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent(CancellationToken ct)
    {
        var current = await _db.Plantings.OrderByDescending(p => p.CreatedUtc).FirstOrDefaultAsync(ct);
        return current is null ? NotFound() : Ok(current);
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] PlantingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PlantName))
        {
            return BadRequest("PlantName is required");
        }

        var planting = new Planting
        {
            PlantName = request.PlantName.Trim(),
            SoilType = request.SoilType?.Trim() ?? string.Empty,
            PlantedDateUtc = request.PlantedDateUtc,
            Notes = request.Notes?.Trim() ?? string.Empty
        };

        _db.Plantings.Add(planting);
        await _db.SaveChangesAsync(ct);

        // Не чекаємо на Gemini (може тривати десятки секунд з ретраями) в межах
        // цього HTTP-запиту — застосунок отримує відповідь одразу, перший
        // AI-профіль з'явиться за кілька хвилин у фоні.
        _ = _aiAgronomist.TriggerImmediateProfileAnalysisAsync($"New planting started via mobile app: {planting.PlantName}");

        return Ok(planting);
    }
}
