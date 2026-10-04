using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Controllers;

[ApiController]
[Route("api/automation")]
public class AutomationController : ControllerBase
{
    private const int SustainedReadings = 2;
    private readonly AppDbContext _db;
    private readonly AiAgronomistOptions _options;

    public AutomationController(AppDbContext db, IOptions<AiAgronomistOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    [HttpGet("overview")]
    public async Task<ActionResult<AutomationOverview>> GetOverview(CancellationToken ct)
    {
        var profile = await _db.PlantProfiles
            .OrderByDescending(p => p.LastUpdatedUtc)
            .FirstOrDefaultAsync(ct);

        string Target(double? value, string suffix) => value is null ? "ціль AI ще не визначена" : $"{value:0.#}{suffix}";
        string MidpointTarget(double? min, double? max, string suffix) => min is null || max is null
            ? "ціль AI ще не визначена"
            : $"{min + (max - min) / 2.0:0.#}{suffix}";

        var rules = new List<AutomationRule>
        {
            new(
                "pump",
                "Насос поливу",
                "Повертає вологість ґрунту до середини безпечного діапазону.",
                $"Вологість нижча за {Target(profile?.SoilMoistureMinPct, "%")} і падає протягом {_options.SoilMoistureTrendWindowMinutes} хв. Аварійний виняток — {SustainedReadings} нульові показники поспіль.",
                $"Після досягнення середньої цілі {MidpointTarget(profile?.SoilMoistureMinPct, profile?.SoilMoistureMaxPct, "%")}; до неї подає короткі імпульси з безпечною паузою.",
                $"Не частіше одного разу на {_options.MinMinutesBetweenWaterings} хв; ESP32 додатково обмежує тривалість роботи помпи."),
            new(
                "light",
                "Фітолампа",
                "Добирає денну норму ефективного світла.",
                $"Поза нічним відпочинком, отримано менше {Target(profile?.DailyLightHoursTarget, " год/добу")}, а природне світло нижче {_options.GrowthLuxThreshold:0} lx. Яскравість пропорційна дефіциту lux.",
                $"Коли денну норму набрано, природне світло ≥ {_options.GrowthLuxThreshold:0} lx або настав нічний відпочинок.",
                $"Гарантований темний період: {_options.NightRestStartHour:00}:00–{_options.NightRestEndHour:00}:00; прошивка має власний таймер безпеки."),
            new(
                "soil-heater",
                "Нагрівач ґрунту",
                "Підігріває кореневу зону або мʼяко просушує надто вологий ґрунт.",
                $"Температура ґрунту нижча за {Target(profile?.SoilTempMinC, "°C")}; або останні {SustainedReadings} показники вологості вищі за {Target(profile?.SoilMoistureMaxPct, "%")}. Потужність пропорційна відхиленню.",
                $"На середній цілі: температура {MidpointTarget(profile?.SoilTempMinC, profile?.SoilTempMaxC, "°C")}, вологість {MidpointTarget(profile?.SoilMoistureMinPct, profile?.SoilMoistureMaxPct, "%")}. Верхня межа {Target(profile?.SoilTempMaxC, "°C")} завжди вимикає нагрів.",
                $"Жорстка межа температури та ліміт потужності {_options.SoilHeaterMaxPower}/255; ESP32 вимикає нагрівач без повторних команд."),
            new(
                "air-heater",
                "Нагрівач повітря",
                "Підігріває повітря або знижує відносну вологість без витяжки тепла назовні.",
                $"Останні {SustainedReadings} температури нижчі за {Target(profile?.TempMinC, "°C")}; або останні {SustainedReadings} значення вологості вищі за {Target(profile?.HumidityMaxPct, "%")}. Потужність пропорційна відхиленню.",
                $"На середній цілі: температура {MidpointTarget(profile?.TempMinC, profile?.TempMaxC, "°C")}, вологість {MidpointTarget(profile?.HumidityMinPct, profile?.HumidityMaxPct, "%")}. При активному охолодженні або на {Target(profile?.TempMaxC, "°C")} нагрів блокується.",
                $"Ліміт потужності {_options.AirHeaterMaxPower}/255; циркуляційний вентилятор вмикається разом із нагрівачем."),
            new(
                "exhaust",
                "Витяжка",
                "Виводить гаряче повітря з теплиці.",
                $"Останні {SustainedReadings} температури повітря вищі за {Target(profile?.TempMaxC, "°C")}.",
                $"Після охолодження до середини діапазону {MidpointTarget(profile?.TempMinC, profile?.TempMaxC, "°C")}.",
                "Стан корекції зберігається між циклами та після перезапуску бекенду."),
            new(
                "fan",
                "Циркуляційний вентилятор",
                "Розподіляє нагріте або охолоджуване повітря по теплиці.",
                "Автоматично працює разом із нагрівачем повітря або витяжкою.",
                "Коли нагрівач повітря і витяжка вимкнені.",
                "ESP32 апаратно гарантує обдув під час роботи повітряного нагрівача.")
        };

        return Ok(new AutomationOverview(
            "Актуаторами керує локальний контролер: вихід за min/max запускає корекцію до середини діапазону; AI лише оновлює самі діапазони.",
            $"Перерахунок після кожної нової телеметрії; резервне повторення команди кожні {_options.LocalControlIntervalMinutes} хв.",
            rules));
    }
}
