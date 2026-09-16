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
        // (AiAgronomistService), тепер спільний через PlantProfileRangeGuard — не
        // лише min<max, а й фізично розумні межі, бо ці поля напряму рухають
        // потужність нагрівачів у RunLocalControlAsync. Обрізаємо ДО перевірки
        // min<max — інакше пара (50, 60), обидва поза межею, пройшла б перевірку
        // порядку, а після обрізання стала б (45, 45). На відміну від
        // AiAgronomistService (яка самокоригує невпорядковану пару — відхилити
        // AI-відповідь нема кому), тут, де є людина на іншому кінці HTTP-запиту,
        // невпорядкована пара відхиляється нижче через BadRequest.
        var tempMinC = PlantProfileRangeGuard.ClampTempC(updated.TempMinC);
        var tempMaxC = PlantProfileRangeGuard.ClampTempC(updated.TempMaxC);
        var humidityMinPct = PlantProfileRangeGuard.ClampPct(updated.HumidityMinPct);
        var humidityMaxPct = PlantProfileRangeGuard.ClampPct(updated.HumidityMaxPct);
        var soilMoistureMinPct = PlantProfileRangeGuard.ClampPct(updated.SoilMoistureMinPct);
        var soilMoistureMaxPct = PlantProfileRangeGuard.ClampPct(updated.SoilMoistureMaxPct);
        var soilTempMinC = PlantProfileRangeGuard.ClampTempC(updated.SoilTempMinC);
        var soilTempMaxC = PlantProfileRangeGuard.ClampTempC(updated.SoilTempMaxC);

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
