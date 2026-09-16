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

        // Той самий захист "здорового глузду", що й для AI-профілю
        // (AiAgronomistService) — не лише min<max, а й фізично розумні межі, бо ці
        // поля напряму рухають потужність нагрівачів у RunLocalControlAsync.
        // Обрізаємо ДО перевірки min<max — інакше пара (50, 60), обидва поза
        // межею, пройшла б перевірку порядку, а після обрізання стала б (45, 45).
        const double MinPlausibleTempC = 0.0;
        const double MaxPlausibleTempC = 45.0;

        var tempMinC = Math.Clamp(updated.TempMinC, MinPlausibleTempC, MaxPlausibleTempC);
        var tempMaxC = Math.Clamp(updated.TempMaxC, MinPlausibleTempC, MaxPlausibleTempC);
        var humidityMinPct = Math.Clamp(updated.HumidityMinPct, 0, 100);
        var humidityMaxPct = Math.Clamp(updated.HumidityMaxPct, 0, 100);
        var soilMoistureMinPct = Math.Clamp(updated.SoilMoistureMinPct, 0, 100);
        var soilMoistureMaxPct = Math.Clamp(updated.SoilMoistureMaxPct, 0, 100);
        var soilTempMinC = Math.Clamp(updated.SoilTempMinC, MinPlausibleTempC, MaxPlausibleTempC);
        var soilTempMaxC = Math.Clamp(updated.SoilTempMaxC, MinPlausibleTempC, MaxPlausibleTempC);

        // Локальний контролер довіряє цим межам напряму (наприклад, нагрівач
        // ґрунту вмикає просушку лише коли SoilTempMaxC > SoilTempMinC). Клієнти
        // валідують min<max самі, але API теж не має тихо приймати суперечливі
        // діапазони.
        var invalidPair = tempMinC > tempMaxC ? "temperature"
            : humidityMinPct > humidityMaxPct ? "humidity"
            : soilMoistureMinPct > soilMoistureMaxPct ? "soil moisture"
            : soilTempMinC >= soilTempMaxC ? "soil temperature"
            : null;
        if (invalidPair is not null)
        {
            return BadRequest($"Min value must be below max for {invalidPair}.");
        }

        profile.TempMinC = tempMinC;
        profile.TempMaxC = tempMaxC;
        profile.HumidityMinPct = humidityMinPct;
        profile.HumidityMaxPct = humidityMaxPct;
        profile.SoilMoistureMinPct = soilMoistureMinPct;
        profile.SoilMoistureMaxPct = soilMoistureMaxPct;
        profile.SoilTempMinC = soilTempMinC;
        profile.SoilTempMaxC = soilTempMaxC;
        profile.DailyLightHoursTarget = Math.Clamp(updated.DailyLightHoursTarget, 0, 24);
        profile.Notes = updated.Notes;
        profile.LastUpdatedUtc = DateTime.UtcNow;
        profile.LastUpdateReason = "Manual edit via mobile app";

        await _db.SaveChangesAsync(ct);
        return Ok(profile);
    }
}
