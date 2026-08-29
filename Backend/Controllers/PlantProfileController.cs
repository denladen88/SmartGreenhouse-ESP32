using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Controllers;

[ApiController]
[Route("api/plant-profile")]
public class PlantProfileController : ControllerBase
{
    private readonly AppDbContext _db;

    public PlantProfileController(AppDbContext db)
    {
        _db = db;
    }

    // Профілів по одному на кожну назву рослини (природний ключ PlantName), тож
    // "поточний" — найсвіжіше оновлений, узгоджено з тим, як AiAgronomistService
    // засіює новий рядок при кожній новій посадці (див. PlantingController).
    [HttpGet]
    public async Task<IActionResult> GetCurrent(CancellationToken ct)
    {
        var profile = await _db.PlantProfiles.OrderByDescending(p => p.LastUpdatedUtc).FirstOrDefaultAsync(ct);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPut]
    public async Task<IActionResult> UpdateCurrent([FromBody] PlantProfile updated, CancellationToken ct)
    {
        var profile = await _db.PlantProfiles.OrderByDescending(p => p.LastUpdatedUtc).FirstOrDefaultAsync(ct);
        if (profile is null)
        {
            return NotFound();
        }

        // Локальний контролер довіряє цим межам напряму (наприклад, нагрівач
        // ґрунту вмикає просушку лише коли SoilTempMaxC > SoilTempMinC). Клієнти
        // валідують min<max самі, але API теж не має тихо приймати суперечливі
        // діапазони.
        var invalidPair = updated.TempMinC > updated.TempMaxC ? "temperature"
            : updated.HumidityMinPct > updated.HumidityMaxPct ? "humidity"
            : updated.SoilMoistureMinPct > updated.SoilMoistureMaxPct ? "soil moisture"
            : updated.SoilTempMinC >= updated.SoilTempMaxC ? "soil temperature"
            : null;
        if (invalidPair is not null)
        {
            return BadRequest($"Min value must be below max for {invalidPair}.");
        }

        profile.TempMinC = updated.TempMinC;
        profile.TempMaxC = updated.TempMaxC;
        profile.HumidityMinPct = updated.HumidityMinPct;
        profile.HumidityMaxPct = updated.HumidityMaxPct;
        profile.SoilMoistureMinPct = updated.SoilMoistureMinPct;
        profile.SoilMoistureMaxPct = updated.SoilMoistureMaxPct;
        profile.SoilTempMinC = updated.SoilTempMinC;
        profile.SoilTempMaxC = updated.SoilTempMaxC;
        profile.DailyLightHoursTarget = updated.DailyLightHoursTarget;
        profile.Notes = updated.Notes;
        profile.LastUpdatedUtc = DateTime.UtcNow;
        profile.LastUpdateReason = "Manual edit via mobile app";

        await _db.SaveChangesAsync(ct);
        return Ok(profile);
    }
}
