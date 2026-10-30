using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Hubs;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Services;

// Два незалежні цикли: RunProfileSupervisionLoopAsync рівно раз на добу о
// AiAgronomistOptions.DailyAnalysisHour (плюс одноразовий bootstrap, якщо для
// поточної рослини профілю ще нема) питає DeepSeek і повністю переписує
// PlantProfile; RunLocalControl* сам вирішує pump/fan/light/heater простими
// правилами, спираючись на цей профіль — жодного звернення до AI на кожен тік
// актуаторів.
public class AiAgronomistService : BackgroundService
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new() { PropertyNameCaseInsensitive = true };
    public const string PhotosDirectory = "Photos";

    // Скільки послідовних не-null точок треба, щоб довіряти "стійкому" тренду в
    // локальних правилах (вентилятор, помпа, просушка ґрунту) — одна точка може
    // бути шумом, кілька поспіль — уже сигнал.
    private const int MinSustainedReadings = 2;

    // Запас (у °C/percentage points), на який піднімається max, якщо AI-профіль
    // повернув пару, що після незалежного обрізання до PlantProfileRangeGuard
    // виявилась невпорядкованою (min >= max) — див. RunProfileAnalysisAsync.
    private const double TempOrderMarginC = 2.0;
    private const double HumidityOrderMarginPct = 10.0;
    private const double SoilMoistureOrderMarginPct = 10.0;

    private static double Midpoint(double min, double max) => min + (max - min) / 2.0;

    private static int ProportionalPower(double deviation, double fullPowerDeviation)
    {
        if (deviation <= 0)
        {
            return 0;
        }

        var ratio = fullPowerDeviation > 0 ? deviation / fullPowerDeviation : 1.0;
        return Math.Max(1, (int)Math.Round(Math.Clamp(ratio, 0, 1) * 255));
    }

    private static int LimitedProportionalPower(double deviation, double fullPowerDeviation,
        double safetyHeadroom, double safetyTaper)
    {
        if (deviation <= 0 || safetyHeadroom <= 0)
        {
            return 0;
        }

        var deviationFactor = fullPowerDeviation > 0
            ? Math.Clamp(deviation / fullPowerDeviation, 0, 1)
            : 1.0;
        var safetyFactor = safetyTaper > 0
            ? Math.Clamp(safetyHeadroom / safetyTaper, 0, 1)
            : 1.0;
        var factor = Math.Min(deviationFactor, safetyFactor);
        return factor > 0 ? Math.Max(1, (int)Math.Round(factor * 255)) : 0;
    }

    private static int ApplyRuntimePowerBoost(int basePower, long runtimeMs, int maxPower,
        int boostAfterMinutes, int boostStepMinutes, int boostStepPower)
    {
        if (basePower <= 0 || maxPower <= basePower || runtimeMs < 0 ||
            boostAfterMinutes <= 0 || boostStepMinutes <= 0 || boostStepPower <= 0)
        {
            return Math.Clamp(basePower, 0, Math.Max(0, maxPower));
        }

        var boostAfterMs = TimeSpan.FromMinutes(boostAfterMinutes).TotalMilliseconds;
        if (runtimeMs < boostAfterMs)
        {
            return Math.Min(basePower, maxPower);
        }

        var boostStepMs = TimeSpan.FromMinutes(boostStepMinutes).TotalMilliseconds;
        var steps = 1L + (long)((runtimeMs - boostAfterMs) / boostStepMs);
        var boosted = (long)basePower + steps * boostStepPower;
        return (int)Math.Min(boosted, maxPower);
    }

    private static string FormatRuntime(long runtimeMs)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(0, runtimeMs));
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} год {duration.Minutes} хв"
            : $"{Math.Max(1, duration.Minutes)} хв";
    }

    private readonly ILogger<AiAgronomistService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DeepSeekOptions _deepSeekOptions;
    private readonly Esp32Options _esp32Options;
    private readonly MqttOptions _mqttOptions;
    private readonly AiAgronomistOptions _agronomistOptions;
    private readonly PlantOptions _plantOptions;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly HttpClient _httpClient;        // DeepSeek (120с — фото + prompt + reasoning)
    private readonly HttpClient _cameraHttpClient;  // ESP32-CAM (15с — щоб мертва камера не тримала слот 45с)
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly TelemetrySignal _telemetrySignal;

    // Локальний контролер тепер будиться з двох джерел — fallback-таймера і
    // сигналу про нову телеметрію. Семафор серіалізує їх (один тік за раз), а
    // _lastLocalControlUtc гасить здвоєний прогін, коли обидва спрацювали разом
    // (щоб не писати дубль AiDecisionRecord і не публікувати команду двічі).
    private readonly SemaphoreSlim _localControlGate = new(1, 1);

    // Один AI-огляд профілю за раз: плановий, bootstrap і ручний
    // (TryStartManualReview) інакше могли б паралельно двічі смикнути камеру
    // й перезаписати профіль один одному.
    private readonly SemaphoreSlim _profileAnalysisGate = new(1, 1);
    private DateTime _lastLocalControlUtc = DateTime.MinValue;

    // TEMPORARY STRESS TEST — set back to false when done. Forces every
    // actuator except the pump fully on regardless of the rules below, so
    // fans/light/heaters stay under continuous load for a burn-in test.
    // Pump stays governed by the normal rule (never forced on) so it doesn't
    // run unattended for the duration of the test.
    private static readonly bool StressTestForceActuatorsOn = false;

    public AiAgronomistService(
        ILogger<AiAgronomistService> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<DeepSeekOptions> deepSeekOptions,
        IOptions<Esp32Options> esp32Options,
        IOptions<MqttOptions> mqttOptions,
        IOptions<AiAgronomistOptions> agronomistOptions,
        IOptions<PlantOptions> plantOptions,
        IMqttPublisher mqttPublisher,
        IHttpClientFactory httpClientFactory,
        IHubContext<TelemetryHub> hub,
        TelemetrySignal telemetrySignal)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _deepSeekOptions = deepSeekOptions.Value;
        _esp32Options = esp32Options.Value;
        _mqttOptions = mqttOptions.Value;
        _agronomistOptions = agronomistOptions.Value;
        _plantOptions = plantOptions.Value;
        _mqttPublisher = mqttPublisher;
        _httpClient = httpClientFactory.CreateClient(nameof(AiAgronomistService));
        _cameraHttpClient = httpClientFactory.CreateClient("Esp32Camera");
        _hub = hub;
        _telemetrySignal = telemetrySignal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await Task.WhenAll(
            RunProfileSupervisionLoopAsync(stoppingToken),
            RunLocalControlTimerLoopAsync(stoppingToken),
            RunLocalControlSignalLoopAsync(stoppingToken));

    // Дозволяє зовнішньому виклику (PlantingController, коли завели нову посадку
    // через застосунок) попросити НЕГАЙНИЙ bootstrap-аналіз профілю, не чекаючи
    // до наступного DailyAnalysisHour — інакше актуатори лишались би
    // бездіяльними, поки для нової рослини ще немає PlantProfile
    // (RunLocalControlAsync просто виходить, якщо профілю немає). No-op, якщо
    // профіль для поточної рослини вже є: щоденний огляд його й так перегляне.
    public async Task TriggerImmediateProfileAnalysisAsync(string reason, CancellationToken ct = default)
    {
        if (await HasProfileForCurrentPlantAsync(ct))
        {
            _logger.LogInformation(
                "Profile already exists for the current plant — skipping bootstrap analysis ({Reason})", reason);
            return;
        }

        await RunProfileAnalysisSafeAsync(ct, $"Initial profile: {reason}");
    }

    // ---- Профіль: щодня після DailyAnalysisHour DeepSeek переглядає все і переписує PlantProfile ----

    private async Task RunProfileSupervisionLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Одноразовий bootstrap: якщо для поточної рослини профілю ще немає
            // (свіжа БД або нова посадка зі зміненою назвою), не змушуємо
            // актуатори чекати до полудня — робимо аналіз одразу, фото не
            // вимагаємо.
            if (!await HasProfileForCurrentPlantAsync(stoppingToken))
            {
                await RunProfileAnalysisSafeAsync(stoppingToken,
                    "Initial profile (no profile on startup)");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var noonPassed = DateTime.Now.Hour >= _agronomistOptions.DailyAnalysisHour;
                var reviewedToday = noonPassed && await AlreadyAiReviewedSinceTodayNoonAsync(stoppingToken);

                if (!noonPassed || reviewedToday)
                {
                    await Task.Delay(TimeUntilNextNoon(), stoppingToken);
                    continue;
                }

                // Після планового часу повторюємо до успішного запису профілю.
                // Це покриває тимчасову відсутність телеметрії, помилки DeepSeek,
                // невалідний JSON і помилки БД. Фото для успіху не потрібне.
                var succeeded = await RunProfileAnalysisSafeAsync(stoppingToken, "Scheduled daily review");
                if (!succeeded)
                {
                    var retryMinutes = Math.Max(1, _agronomistOptions.DailyAnalysisRetryMinutes);
                    _logger.LogWarning(
                        "Scheduled daily AI review did not complete — retrying in {RetryMinutes} minutes",
                        retryMinutes);
                    await Task.Delay(TimeSpan.FromMinutes(retryMinutes), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    // Гарантований темний період NightRestStartHour–NightRestEndHour за локальним
    // годинником; підтримує інтервал через північ (напр. 23–5).
    private bool IsNightRest()
    {
        var hour = DateTime.Now.Hour;
        return _agronomistOptions.NightRestStartHour > _agronomistOptions.NightRestEndHour
            ? hour >= _agronomistOptions.NightRestStartHour || hour < _agronomistOptions.NightRestEndHour
            : hour >= _agronomistOptions.NightRestStartHour && hour < _agronomistOptions.NightRestEndHour;
    }

    // Час до найближчого настання DailyAnalysisHour за локальним годинником. У
    // день переходу на літній/зимовий час похибка ≤ 1 год і самокоригується на
    // наступній ітерації.
    private TimeSpan TimeUntilNextNoon()
    {
        var now = DateTime.Now;
        var todayNoon = now.Date.AddHours(_agronomistOptions.DailyAnalysisHour);
        var nextNoon = now < todayNoon ? todayNoon : todayNoon.AddDays(1);
        return nextNoon - now;
    }

    private async Task<bool> HasProfileForCurrentPlantAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plantName = ResolvePlantName(await GetCurrentPlantingAsync(db, ct));
        return await db.PlantProfiles.AnyAsync(p => p.PlantName == plantName, ct);
    }

    // Чи саме AI-плановий огляд (не ручна правка і не bootstrap) вже успішно
    // записав профіль сьогодні після DailyAnalysisHour. Переживає рестарт.
    private async Task<bool> AlreadyAiReviewedSinceTodayNoonAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plantName = ResolvePlantName(await GetCurrentPlantingAsync(db, ct));
        var todayNoonUtc = DateTime.Now.Date.AddHours(_agronomistOptions.DailyAnalysisHour).ToUniversalTime();
        var lastUpdatedUtc = await db.PlantProfiles
            .Where(p => p.PlantName == plantName && p.LastUpdateReason == "Scheduled daily review")
            .Select(p => (DateTime?)p.LastUpdatedUtc)
            .FirstOrDefaultAsync(ct);
        return lastUpdatedUtc is { } u && u >= todayNoonUtc;
    }

    // Позачерговий огляд на запит користувача (AiReviewController). Не чекає на
    // завершення — DeepSeek думає до кількох хвилин; результат прийде клієнтам
    // через SignalR "PlantProfileReceived". false — інший огляд (плановий,
    // bootstrap чи попередній ручний) ще триває, тож другий паралельно не
    // запускаємо. LastUpdateReason "Manual review" не зараховується як щоденний
    // плановий огляд (AlreadyAiReviewedSinceTodayNoonAsync), тож той відбудеться
    // за розкладом як завжди.
    public bool TryStartManualReview(string? growerComment)
    {
        if (!_profileAnalysisGate.Wait(0))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunProfileAnalysisUngatedAsync(CancellationToken.None, "Manual review", growerComment);
            }
            finally
            {
                _profileAnalysisGate.Release();
            }
        });
        return true;
    }

    private async Task<bool> RunProfileAnalysisSafeAsync(CancellationToken stoppingToken, string lastUpdateReason)
    {
        await _profileAnalysisGate.WaitAsync(stoppingToken);
        try
        {
            return await RunProfileAnalysisUngatedAsync(stoppingToken, lastUpdateReason, null);
        }
        finally
        {
            _profileAnalysisGate.Release();
        }
    }

    private async Task<bool> RunProfileAnalysisUngatedAsync(CancellationToken stoppingToken, string lastUpdateReason,
        string? growerComment)
    {
        try
        {
            return await RunProfileAnalysisAsync(stoppingToken, lastUpdateReason, growerComment);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Agronomist profile analysis failed");
            return false;
        }
    }

    // Найсвіжіша посадка з мобільного застосунку (PlantingController) визначає
    // "поточну" рослину; якщо жодної ще не заведено (свіжа БД без онбордингу),
    // відкочуємось на статичний Plant:Name з appsettings.json — той самий засів,
    // що був єдиним джерелом до появи Planting.
    private async Task<Planting?> GetCurrentPlantingAsync(AppDbContext db, CancellationToken ct) =>
        await db.Plantings.OrderByDescending(p => p.CreatedUtc).FirstOrDefaultAsync(ct);

    private string ResolvePlantName(Planting? planting) =>
        string.IsNullOrWhiteSpace(planting?.PlantName) ? _plantOptions.Name : planting.PlantName;

    // Останній реально опублікований стан актуаторів (з AiDecisions — байдуже,
    // від локального контролера чи від будь-чого іншого) — щоб примусове
    // вмикання світла для нічного фото не зачепило pump/fan і щоб потім було
    // куди повертати світло назад.
    private async Task<(bool PumpOn, bool FanOn, int LightBrightness, int SoilHeaterPower, int AirHeaterPower)> GetLatestActuatorStateAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var latest = await db.AiDecisions.OrderByDescending(d => d.Timestamp).FirstOrDefaultAsync(stoppingToken);
        return latest is null
            ? (false, false, 0, 0, 0)
            : (latest.PumpOn, latest.FanOn, latest.LightBrightness, latest.SoilHeaterPower, latest.AirHeaterPower);
    }

    // Одна повна спроба отримати кадр з ESP32-CAM. Повертає null, якщо камера
    // недоступна або віддала порожньо навіть після примусової підсвітки.
    //
    // ESP32 повертає 204 без тіла, якщо сам вважає, що зараз ніч (замало Lux для
    // корисного кадру) — GetByteArrayAsync у такому разі не кидає виняток, а
    // віддає порожній масив. Тоді примусово вмикаємо підсвітку на максимум,
    // чекаємо, поки прошивка це помітить (isNight оновлюється раз на
    // SENSOR_READ_INTERVAL_MS=60с — чекаємо з запасом), і пробуємо ще раз. Після
    // спроби одразу повертаємо світло (і pump/fan/heater) до стану, який реально
    // був до цього, а не лишаємо ввімкненим до наступного тіку локального контролера.
    private async Task<byte[]?> TryCapturePhotoAsync(CancellationToken stoppingToken)
    {
        byte[] imageBytes;
        try
        {
            imageBytes = await _cameraHttpClient.GetByteArrayAsync(_esp32Options.CameraUrl, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture image from ESP32-CAM at {CameraUrl}", _esp32Options.CameraUrl);
            return null;
        }

        // Під час нічного відпочинку лампу заради фото не вмикаємо: локальний
        // контролер на першу ж телеметрію після ввімкнення гасить її назад (ніч),
        // тож лампа лише блимала, а кадру однаково не було — і так на кожну
        // повторну спробу щоденного огляду. Огляд без фото дозволений.
        if (imageBytes.Length == 0 && IsNightRest())
        {
            _logger.LogInformation("No photo (night per ESP32) during night rest — skipping forced grow light, reviewing without photo");
            return null;
        }

        if (imageBytes.Length == 0)
        {
            var previousDecision = await GetLatestActuatorStateAsync(stoppingToken);

            _logger.LogInformation("No photo (likely night per ESP32) — forcing grow light on for a proper shot and retrying");
            await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, JsonSerializer.Serialize(
                new AiCommand(previousDecision.PumpOn, previousDecision.FanOn, 255,
                    previousDecision.SoilHeaterPower, previousDecision.AirHeaterPower)));

            await Task.Delay(TimeSpan.FromSeconds(65), stoppingToken);

            try
            {
                imageBytes = await _cameraHttpClient.GetByteArrayAsync(_esp32Options.CameraUrl, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retry photo capture after forcing light on failed");
                imageBytes = Array.Empty<byte>();
            }

            await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, JsonSerializer.Serialize(
                new AiCommand(previousDecision.PumpOn, previousDecision.FanOn,
                    previousDecision.LightBrightness, previousDecision.SoilHeaterPower, previousDecision.AirHeaterPower)));
        }

        if (imageBytes.Length == 0)
        {
            return null;
        }

        _logger.LogInformation("Downloaded {ByteCount} bytes from ESP32 camera at {CameraUrl}",
            imageBytes.Length, _esp32Options.CameraUrl);
        return imageBytes;
    }

    private async Task<bool> RunProfileAnalysisAsync(CancellationToken stoppingToken, string lastUpdateReason,
        string? growerComment)
    {
        var trendWindow = TimeSpan.FromMinutes(_agronomistOptions.TrendWindowMinutes);
        var trendBucket = TimeSpan.FromMinutes(_agronomistOptions.TrendBucketMinutes);
        var windowStart = DateTime.UtcNow - trendWindow;

        List<TelemetryRecord> recentRecords;
        List<AiDecisionRecord> recentDecisions;
        PlantProfile? profile;
        Planting? planting;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            recentRecords = await db.Telemetries
                .Where(t => t.Timestamp >= windowStart)
                .OrderBy(t => t.Timestamp)
                .ToListAsync(stoppingToken);

            recentDecisions = await db.AiDecisions
                .Where(d => d.Timestamp >= windowStart)
                .OrderBy(d => d.Timestamp)
                .ToListAsync(stoppingToken);

            planting = await GetCurrentPlantingAsync(db, stoppingToken);
            var plantName = ResolvePlantName(planting);
            profile = await db.PlantProfiles.FirstOrDefaultAsync(p => p.PlantName == plantName, stoppingToken);
        }

        if (recentRecords.Count == 0)
        {
            _logger.LogInformation("No telemetry recorded in the last {Window}, skipping profile analysis", trendWindow);
            return false;
        }

        var trend = DownsampleTrend(recentRecords, trendBucket);

        var trendSummaryText = string.Join("\n", new[]
        {
            SummarizeMetric("Temp", trend, t => t.TemperatureC, "C"),
            SummarizeMetric("Humidity", trend, t => t.HumidityPct, "%"),
            SummarizeMetric("SoilMoisture", trend, t => t.SoilMoisturePct, "%"),
            SummarizeMetric("SoilTemp", trend, t => t.SoilTempC, "C"),
            SummarizeMetric("Lux", trend, t => t.Lux, ""),
            SummarizeMetric("Pressure", trend, t => t.PressureHpa, "hPa"),
        }.Where(l => l is not null));

        var trendText = string.Join(
            "\n",
            trend.Select(t =>
                $"{t.Timestamp:MM-dd HH:mm} Temp={FormatOrNA(t.TemperatureC, "C")} Humidity={FormatOrNA(t.HumidityPct, "%")} " +
                $"SoilMoisture={FormatOrNA(t.SoilMoisturePct, "%")} (raw diagnostic: {t.SoilRaw:0}) " +
                $"SoilTemp={FormatOrNA(t.SoilTempC, "C")} " +
                $"Lux={FormatOrNA(t.Lux)} Pressure={FormatOrNA(t.PressureHpa, "hPa")}"));

        var actuatorHistoryText = SummarizeActuatorHistory(recentDecisions);

        _logger.LogInformation(
            "Trend for this profile analysis: {PointCount} points over the last {Window}:\n{TrendText}",
            trend.Count, trendWindow, trendText);

        // Фото покращує оцінку стадії росту, але не є обов'язковим: недоступна
        // камера не повинна блокувати щоденне оновлення профілю за телеметрією.
        var imageBytes = await TryCapturePhotoAsync(stoppingToken);
        var hasPhoto = imageBytes is { Length: > 0 };
        var base64Image = hasPhoto ? Convert.ToBase64String(imageBytes!) : null;

        var photoInstruction = hasPhoto
            ? "Analyze this plant photo together with the sensor trend below, using it to judge what is actually normal or " +
              "concerning for this specific species — not generic assumptions. "
            : "No photo was available this time (the camera doesn't capture at night, when ambient light is too low for a " +
              "useful frame) — base your review on the sensor trend and history alone. ";

        // Контекст посадки йде В КОЖНОМУ огляді (не лише в першому), щоб модель
        // міг судити про етап розвитку за віком рослини, а не лише за фото.
        var daysSincePlanting = planting is not null
            ? Math.Max(0, (int)(DateTime.UtcNow - planting.PlantedDateUtc).TotalDays)
            : (int?)null;
        var plantingContextParagraph = planting is not null
            ? $"Planting: {ResolvePlantName(planting)}, planted {planting.PlantedDateUtc:yyyy-MM-dd} ({daysSincePlanting} " +
              $"days ago), grown in {(string.IsNullOrWhiteSpace(planting.SoilType) ? "unspecified soil" : planting.SoilType)}. " +
              $"Grower's notes: {(string.IsNullOrWhiteSpace(planting.Notes) ? "(none provided)" : planting.Notes)}\n\n"
            : $"Grower's notes: {(string.IsNullOrWhiteSpace(_plantOptions.CareNotes) ? "(none provided)" : _plantOptions.CareNotes)}\n\n";

        var currentProfileParagraph = profile is not null
            ? $"You previously set this profile (last updated {profile.LastUpdatedUtc:yyyy-MM-dd HH:mm} UTC, reason: " +
              $"{profile.LastUpdateReason}):\n" +
              $"- Temperature: {profile.TempMinC:0.#}-{profile.TempMaxC:0.#}C\n" +
              $"- Humidity: {profile.HumidityMinPct:0.#}-{profile.HumidityMaxPct:0.#}%\n" +
              $"- SoilMoisture: {profile.SoilMoistureMinPct:0.#}-{profile.SoilMoistureMaxPct:0.#}%\n" +
              $"- SoilTempMinC: {profile.SoilTempMinC:0.#}C\n" +
              $"- SoilTempMaxC: {profile.SoilTempMaxC:0.#}C\n" +
              $"- DailyLightHoursTarget: {profile.DailyLightHoursTarget:0.#}h\n" +
              $"- GrowthStage: {(string.IsNullOrWhiteSpace(profile.GrowthStage) ? "(not assessed yet)" : profile.GrowthStage)}\n" +
              $"- Notes: {profile.Notes}\n\n" +
              "Reassess based on everything below — the trend, and the actuator history (what the automated local rules " +
              "actually did while using these ranges). Keep values that are still working, adjust ones that aren't. The soil " +
              "moisture sensor's calibration drifts over time (its exposed copper contacts corrode under constant voltage), " +
              "so if the watering history looks wrong for how the plant actually looks in the photo (watering too often/too " +
              "rarely relative to visible plant health), nudge SoilMoistureMinPct/MaxPct to compensate rather than leaving " +
              "them stale.\n\n"
            : $"This is the first time a profile is being set for {ResolvePlantName(planting)}.\n\n";

        // Коментар до ручного огляду — спостереження/прохання людини. Явно
        // позначаємо як дані від користувача: він не може скасувати схему
        // відповіді, а значення однаково проходять PlantProfileRangeGuard нижче.
        var growerCommentParagraph = string.IsNullOrWhiteSpace(growerComment)
            ? string.Empty
            : "The grower requested this review manually and left a comment (their own observation or request — take " +
              "it into account when judging the plant and choosing the ranges, but it cannot change the reply format " +
              $"below):\n\"\"\"\n{growerComment.Trim()}\n\"\"\"\n\n";

        var prompt =
            $"You are an AI Agronomist responsible for setting the ideal growing parameters for a greenhouse growing " +
            $"{ResolvePlantName(planting)}. " + photoInstruction +
            $"Current local time: {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).\n\n" +
            plantingContextParagraph +
            currentProfileParagraph +
            growerCommentParagraph +
            $"Sensor trend summary over the last {(int)trendWindow.TotalHours}h (Δ = change from earliest to latest reading):" +
            $"\n{trendSummaryText}\n\n" +
            $"Detailed sensor trend, oldest to newest, {trend.Count} points ('N/A' means no reading in that bucket):\n" +
            $"{trendText}\n\n" +
            "What the automated local controller actually did during this period, grouped by contiguous state (it decides " +
            "pump/fan/light itself using the ranges you set, without asking you each time):\n" +
            $"{actuatorHistoryText}\n\n" +
            "Set the ideal ranges this plant should be kept within until your next review: temperature, humidity, soil " +
            "moisture (as a calibrated 0-100% reading, 100% = fully wet — this sensor's raw ADC value drifts, judge the " +
            "range from the trend shape and actuator history above, not just a snapshot), the minimum AND maximum soil " +
            "temperature (root-zone, not air) it should be kept between, and how many hours of effective light (sun and/or " +
            "grow light combined) it needs per day. These ranges will be used directly by simple automated rules — not by " +
            "you — to control the pump, a circulation fan, grow light, an air heater, and a soil heating mat until your next review. " +
            "There is NO active cooling (no exhaust fan) — above TempMaxC the rules can only keep the heaters off, " +
            "so the air may stay above TempMaxC on hot days. The circulation fan has no independent trigger of its own " +
            "— it runs automatically whenever the air heater is active (it's the heater's only airflow) and is off " +
            "otherwise, so it isn't something your ranges control directly. The air heater runs " +
            "in two modes, both proportional (no on/off jumps): temperature make-up whenever air temperature is sustained " +
            "below TempMinC (power ramps up with the deficit), AND (separately) dehumidification whenever air humidity is " +
            "sustained above HumidityMaxPct — warming the air drives relative humidity down and keeps condensation off " +
            "the leaves, with power modulated by how far humidity is over HumidityMaxPct and tapering to zero as air " +
            "temperature approaches TempMaxC, plus a hard cut at TempMaxC. So TempMinC AND HumidityMaxPct both directly " +
            "drive the air heater (HumidityMaxPct is a live control setpoint now, not just an alert threshold), and " +
            "TempMaxC is a live ceiling for it — keep TempMaxC a few C above TempMinC with real headroom. The soil " +
            "heater runs in two modes — proportional to the deficit whenever soil temperature is below SoilTempMinC, AND " +
            "(separately) power-modulated whenever soil moisture is sustained above SoilMoistureMaxPct, applying bottom " +
            "heat to dry an over-wet root zone and stave off root rot, easing off as moisture falls back to " +
            "SoilMoistureMaxPct and as soil temperature rises toward SoilTempMaxC, with a hard cut at SoilTempMaxC. So " +
            "SoilTempMaxC is a live control setpoint (keep it a few C above SoilTempMinC with real headroom, never at or " +
            "below it) and SoilMoistureMaxPct now drives an actuator, not just an alert. Make all ranges realistic " +
            "operating targets, not aspirational extremes. Also " +
            "assess the plant's current phenological growth stage from the photo, the days since planting, and the trend " +
            "(e.g. seedling, vegetative, flowering, fruiting, senescing) and take it into account when choosing the ranges. " +
            "Reply strictly in JSON matching this schema: { \"TempMinC\": number, \"TempMaxC\": number, " +
            "\"HumidityMinPct\": number, \"HumidityMaxPct\": number, \"SoilMoistureMinPct\": number, " +
            "\"SoilMoistureMaxPct\": number, \"SoilTempMinC\": number, \"SoilTempMaxC\": number, " +
            "\"DailyLightHoursTarget\": number, \"GrowthStage\": \"current growth stage plus a few words on how you can " +
            "tell\", \"Notes\": \"short rationale, referencing what changed since last time if applicable\" } without " +
            "markdown code blocks. Write the GrowthStage and Notes values in Ukrainian so they can be shown directly " +
            "to the grower.";

        // OpenAI-сумісний формат: фото — data-URL у частині image_url.
        // response_format json_object вимагає слова "JSON" у промпті (воно там є).
        var content = new List<object> { new { type = "text", text = prompt } };
        if (hasPhoto)
        {
            content.Add(new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{base64Image}" } });
        }

        var requestBody = new
        {
            model = _deepSeekOptions.Model,
            messages = new[] { new { role = "user", content = content.ToArray() } },
            response_format = new { type = "json_object" }
        };

        var text = await CallDeepSeekAsync(requestBody, stoppingToken);
        if (text is null)
        {
            return false;
        }

        var analysis = JsonSerializer.Deserialize<PlantProfileAnalysisResponse>(StripMarkdownFence(text), DecisionJsonOptions);
        if (analysis is null)
        {
            _logger.LogWarning("Failed to parse PlantProfile analysis response: {Text}", text);
            return false;
        }

        // Захист від некоректної/маніпульованої відповіді: навіть у межах min<max
        // екстремальне значення (наприклад, 200C) небезпечне, бо ці пороги напряму
        // рухають потужність нагрівачів у RunLocalControlAsync. PlantProfileRangeGuard
        // обрізає кожен кінець пари НЕЗАЛЕЖНО до фізично розумного діапазону, а
        // потім перевіряє min < max ЗА ВЖЕ ОБРІЗАНИМИ значеннями (не сире проти
        // обрізаного — саме ця розбіжність раніше ховала баг: пара (46, 47),
        // обидві вище стелі 45°C, обрізалась в (45, 45), а порівняння "сире 47 >
        // обрізане 45" пропускало це як впорядковану пару). Той самий guard
        // використовує ручне редагування в PlantProfileController — там
        // невпорядкована пара відхиляється (BadRequest), тут, де відхилити
        // нема кому, вона самокоригується підняттям max на запас.
        var (tempMinC, tempMaxC, tempReordered) =
            PlantProfileRangeGuard.ClampOrderedTempC(analysis.TempMinC, analysis.TempMaxC, TempOrderMarginC);
        var (humidityMinPct, humidityMaxPct, humidityReordered) =
            PlantProfileRangeGuard.ClampOrderedPct(analysis.HumidityMinPct, analysis.HumidityMaxPct, HumidityOrderMarginPct);
        var (soilMoistureMinPct, soilMoistureMaxPct, soilMoistureReordered) =
            PlantProfileRangeGuard.ClampOrderedPct(analysis.SoilMoistureMinPct, analysis.SoilMoistureMaxPct, SoilMoistureOrderMarginPct);
        var (soilTempMinC, soilTempMaxC, soilTempReordered) = PlantProfileRangeGuard.ClampOrderedTempC(
            analysis.SoilTempMinC, analysis.SoilTempMaxC, _agronomistOptions.SoilHeaterFullPowerDeficitC);
        var dailyLightHoursTarget = Math.Clamp(analysis.DailyLightHoursTarget, 0, 24);

        if (tempReordered || humidityReordered || soilMoistureReordered || soilTempReordered)
        {
            _logger.LogWarning(
                "AI returned an out-of-order/out-of-range min-max pair (Temp={TempReordered}, Humidity={HumidityReordered}, " +
                "SoilMoisture={SoilMoistureReordered}, SoilTemp={SoilTempReordered}) — reordered with a margin. Raw values: " +
                "Temp {RawTempMin}-{RawTempMax}C, Humidity {RawHumMin}-{RawHumMax}%, SoilMoisture {RawSoilMin}-{RawSoilMax}%, " +
                "SoilTemp {RawSoilTempMin}-{RawSoilTempMax}C",
                tempReordered, humidityReordered, soilMoistureReordered, soilTempReordered,
                analysis.TempMinC, analysis.TempMaxC, analysis.HumidityMinPct, analysis.HumidityMaxPct,
                analysis.SoilMoistureMinPct, analysis.SoilMoistureMaxPct, analysis.SoilTempMinC, analysis.SoilTempMaxC);
        }

        _logger.LogInformation(
            "AI profile analysis: Temp {TempMin}-{TempMax}C, Humidity {HumMin}-{HumMax}%, SoilMoisture {SoilMin}-{SoilMax}%, " +
            "SoilTemp {SoilTempMin}-{SoilTempMax}C, DailyLight {Light}h. GrowthStage: {GrowthStage}. Notes: {Notes}",
            tempMinC, tempMaxC, humidityMinPct, humidityMaxPct,
            soilMoistureMinPct, soilMoistureMaxPct, soilTempMinC, soilTempMaxC,
            dailyLightHoursTarget, analysis.GrowthStage, analysis.Notes);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var plantName = ResolvePlantName(planting);
            var tracked = await db.PlantProfiles.FirstOrDefaultAsync(p => p.PlantName == plantName, stoppingToken);
            if (tracked is null)
            {
                tracked = new PlantProfile { PlantName = plantName };
                db.PlantProfiles.Add(tracked);
            }

            tracked.TempMinC = tempMinC;
            tracked.TempMaxC = tempMaxC;
            tracked.HumidityMinPct = humidityMinPct;
            tracked.HumidityMaxPct = humidityMaxPct;
            tracked.SoilMoistureMinPct = soilMoistureMinPct;
            tracked.SoilMoistureMaxPct = soilMoistureMaxPct;
            tracked.SoilTempMinC = soilTempMinC;
            tracked.SoilTempMaxC = soilTempMaxC;
            tracked.DailyLightHoursTarget = dailyLightHoursTarget;
            tracked.GrowthStage = analysis.GrowthStage ?? string.Empty;
            tracked.Notes = analysis.Notes;
            tracked.LastAiPrompt = prompt;
            tracked.LastAiResponse = text;
            tracked.LastAiHadPhoto = hasPhoto;
            tracked.LastAiReviewedUtc = DateTime.UtcNow;
            tracked.LastUpdatedUtc = DateTime.UtcNow;
            tracked.LastUpdateReason = lastUpdateReason;

            await db.SaveChangesAsync(stoppingToken);
            await _hub.Clients.All.SendAsync("PlantProfileReceived", tracked, stoppingToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save PlantProfile");
            return false;
        }
    }

    // Групує хронологічний список рішень у сегменти з однаковим станом
    // (Pump/Fan/Light) — щоденний промпт інакше потонув би в ~144 рядках
    // (по одному на кожен LocalControlIntervalMinutes-тік).
    private string SummarizeActuatorHistory(List<AiDecisionRecord> chronologicalDecisions)
    {
        if (chronologicalDecisions.Count == 0)
        {
            return "(none yet)";
        }

        var segments = new List<ActuatorSegment>();
        foreach (var d in chronologicalDecisions)
        {
            var last = segments.Count > 0 ? segments[^1] : null;
            if (last is not null && last.Sample.PumpOn == d.PumpOn && last.Sample.FanOn == d.FanOn &&
                last.Sample.LightBrightness == d.LightBrightness && last.Sample.SoilHeaterPower == d.SoilHeaterPower &&
                last.Sample.AirHeaterPower == d.AirHeaterPower)
            {
                segments[^1] = last with { End = d.Timestamp, Count = last.Count + 1 };
            }
            else
            {
                segments.Add(new ActuatorSegment(d.Timestamp, d.Timestamp, d, 1));
            }
        }

        return string.Join("\n", segments.TakeLast(_agronomistOptions.DecisionHistoryCount).Select(s =>
            $"{s.Start:MM-dd HH:mm}-{s.End:HH:mm} ({s.Count}x) Pump={(s.Sample.PumpOn ? "On" : "Off")} " +
            $"Fan={(s.Sample.FanOn ? "On" : "Off")} " +
            $"Light={s.Sample.LightBrightness} " +
            $"SoilHeater={s.Sample.SoilHeaterPower} AirHeater={s.Sample.AirHeaterPower} — {s.Sample.Reason}"));
    }

    private record ActuatorSegment(DateTime Start, DateTime End, AiDecisionRecord Sample, int Count);

    // ---- Локальний контролер: вирішує pump/fan/light/heater сам, без AI ----
    //
    // Два джерела пробудження:
    //   * RunLocalControlSignalLoopAsync — на кожну нову телеметрію (швидкий шлях,
    //     реакція в ту ж секунду);
    //   * RunLocalControlTimerLoopAsync — fallback раз на LocalControlIntervalMinutes,
    //     щоб команда актуаторам підтверджувалась навіть коли телеметрія замовкла
    //     (на це покладаються FAN/SOIL_HEATER_MAX_RUNTIME_MS-таймери прошивки).
    // Обидва йдуть через один семафор у RunLocalControlSafeAsync.

    private async Task RunLocalControlTimerLoopAsync(CancellationToken stoppingToken)
    {
        await RunLocalControlSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_agronomistOptions.LocalControlIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunLocalControlSafeAsync(stoppingToken);
        }
    }

    private async Task RunLocalControlSignalLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await _telemetrySignal.WaitAsync(stoppingToken);
                await RunLocalControlSafeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private async Task RunLocalControlSafeAsync(CancellationToken stoppingToken)
    {
        await _localControlGate.WaitAsync(stoppingToken);
        try
        {
            // Здвоєне пробудження (таймер + сигнал майже одночасно) — другий прогін
            // нічого не додасть, лише дубль-запис і дубль-команда. 5с достатньо:
            // телеметрія приходить не частіше ніж раз на кілька хвилин.
            if (DateTime.UtcNow - _lastLocalControlUtc < TimeSpan.FromSeconds(5))
            {
                return;
            }

            await RunLocalControlAsync(stoppingToken);
            _lastLocalControlUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local actuator control tick failed");
        }
        finally
        {
            _localControlGate.Release();
        }
    }

    private async Task RunLocalControlAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var plantName = ResolvePlantName(await GetCurrentPlantingAsync(db, stoppingToken));
        var profile = await db.PlantProfiles.FirstOrDefaultAsync(p => p.PlantName == plantName, stoppingToken);
        if (profile is null)
        {
            _logger.LogInformation(
                "No PlantProfile yet for {PlantName} — waiting for the first AI profile analysis before controlling actuators",
                plantName);
            return;
        }

        var recoveryState = await db.ControlRecoveryStates
            .SingleOrDefaultAsync(s => s.PlantName == plantName, stoppingToken);
        if (recoveryState is null)
        {
            recoveryState = new ControlRecoveryState { PlantName = plantName };
            db.ControlRecoveryStates.Add(recoveryState);
        }

        var soilWindowStart = DateTime.UtcNow - TimeSpan.FromMinutes(_agronomistOptions.SoilMoistureTrendWindowMinutes);
        // t.SoilValid: прошивка позначає ним показник, який ймовірно від сенсора,
        // що відвалився/обірваний (див. SensorService::read у ESP-32) — без цього
        // фільтра garbage-значення потрапляли б у тренд поливу/просушки нарівні з
        // реальними замірами.
        var soilPoints = await db.Telemetries
            .Where(t => t.Timestamp >= soilWindowStart && t.SoilMoisturePct != null && t.SoilValid)
            .OrderBy(t => t.Timestamp)
            .Select(t => t.SoilMoisturePct!.Value)
            .ToListAsync(stoppingToken);
        var recentSoilReadings = await db.Telemetries
            .OrderByDescending(t => t.Timestamp)
            .Take(MinSustainedReadings)
            .Select(t => new { t.SoilMoisturePct, t.SoilValid })
            .ToListAsync(stoppingToken);

        // TemperatureC і HumidityPct завжди приходять з одного зчитування BME280
        // (SensorService::read встановлює обидва разом під climateValid, ніколи
        // окремо), тож один запит з тим самим Where/Take/OrderBy, звідки беремо
        // обидва стовпці, дає ідентичний результат двом окремим запитам нижче —
        // без зайвого round trip до SQLite.
        var recentClimate = await db.Telemetries
            .Where(t => t.TemperatureC != null)
            .OrderByDescending(t => t.Timestamp)
            .Take(MinSustainedReadings)
            .Select(t => new { Temp = t.TemperatureC!.Value, Humidity = t.HumidityPct!.Value })
            .ToListAsync(stoppingToken);
        var recentTemps = recentClimate.Select(t => t.Temp).ToList();
        var recentHumidity = recentClimate.Select(t => t.Humidity).ToList();

        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();
        var todayLightRecords = await db.Telemetries
            .Where(t => t.Timestamp >= todayStartUtc)
            .OrderBy(t => t.Timestamp)
            .Select(t => new TelemetryRecord { Timestamp = t.Timestamp, Lux = t.Lux, LightRuntimeMs = t.LightRuntimeMs })
            .ToListAsync(stoppingToken);

        var todayLightDecisions = await db.AiDecisions
            .Where(d => d.Timestamp >= todayStartUtc)
            .OrderBy(d => d.Timestamp)
            .ToListAsync(stoppingToken);

        var recentLightTelemetry = await db.Telemetries
            .OrderByDescending(t => t.Timestamp)
            .Take(100)
            .Select(t => new { t.Timestamp, t.Lux, t.LightRuntimeMs })
            .ToListAsync(stoppingToken);
        var latestLux = recentLightTelemetry.FirstOrDefault()?.Lux;

        // Чистий (без фітолампи) вимір природного світла: лампа вимкнена і в
        // цьому записі, і в попередньому. Одного запису мало — після вимкнення
        // ESP32 одразу шле телеметрію зі старим показом BH1750, знятим ще при
        // увімкненій лампі.
        var cleanLuxIndex = Enumerable.Range(0, Math.Max(0, recentLightTelemetry.Count - 1))
            .Cast<int?>()
            .FirstOrDefault(i => recentLightTelemetry[i!.Value].Lux is not null &&
                recentLightTelemetry[i.Value].LightRuntimeMs == 0 &&
                recentLightTelemetry[i.Value + 1].LightRuntimeMs == 0);
        var cleanLux = cleanLuxIndex is { } ci ? recentLightTelemetry[ci].Lux : null;
        var cleanLuxAgeMinutes = cleanLuxIndex is { } ai
            ? (DateTime.UtcNow - recentLightTelemetry[ai].Timestamp).TotalMinutes
            : double.PositiveInfinity;

        var latestSoilTemp = await db.Telemetries
            .Where(t => t.SoilTempC != null)
            .OrderByDescending(t => t.Timestamp)
            .Select(t => t.SoilTempC)
            .FirstOrDefaultAsync(stoppingToken);

        var latestActuatorRuntimes = await db.Telemetries
            .OrderByDescending(t => t.Timestamp)
            .Select(t => new { t.SoilHeaterRuntimeMs, t.AirHeaterRuntimeMs })
            .FirstOrDefaultAsync(stoppingToken);
        var soilHeaterRuntimeMs = latestActuatorRuntimes?.SoilHeaterRuntimeMs ?? 0;
        var airHeaterRuntimeMs = latestActuatorRuntimes?.AirHeaterRuntimeMs ?? 0;

        var latestTemp = recentTemps.Count > 0 ? (double?)recentTemps[0] : null;
        var latestHumidity = recentHumidity.Count > 0 ? (double?)recentHumidity[0] : null;
        var airTempTargetC = profile.TempMaxC > profile.TempMinC
            ? Midpoint(profile.TempMinC, profile.TempMaxC)
            : profile.TempMinC;
        var hasHumidityRange = profile.HumidityMaxPct > profile.HumidityMinPct;
        var airHumidityTargetPct = hasHumidityRange
            ? Midpoint(profile.HumidityMinPct, profile.HumidityMaxPct)
            : profile.HumidityMaxPct;

        var tempSustainedLow = recentTemps.Count >= MinSustainedReadings &&
            recentTemps.All(t => t < profile.TempMinC);
        var humiditySustainedHigh = hasHumidityRange &&
            recentHumidity.Count >= MinSustainedReadings &&
            recentHumidity.All(h => h > profile.HumidityMaxPct);

        // Межа лише запускає корекцію. Після запуску стан зберігається в БД і
        // актуатор працює до середини дозволеного діапазону, а не вимикається
        // одразу після повернення за min/max.
        if (latestTemp is { } currentAirTemp)
        {
            recoveryState.AirHeatingActive = currentAirTemp < airTempTargetC &&
                (recoveryState.AirHeatingActive || tempSustainedLow);
        }

        if (!hasHumidityRange)
        {
            recoveryState.AirDryingActive = false;
        }
        else if (latestHumidity is { } currentHumidity)
        {
            recoveryState.AirDryingActive = currentHumidity > airHumidityTargetPct &&
                (recoveryState.AirDryingActive || humiditySustainedHigh);
        }

        // Помпа: щонайменше два останні показники нижче мінімуму, вологість
        // спадає і не поливали нещодавно. Подвійне підтвердження не дозволяє
        // одному випадковому стрибку резистивного зонда запустити полив.
        // (запобіжник від кореневої гнилі базиліка — див. Plant:CareNotes).
        // Окремий аварійний випадок — щонайменше два останні валідні показники
        // дорівнюють 0%: нижче значення вже фізично не може впасти, тому вимога
        // спадного тренду назавжди заблокувала б полив повністю сухого ґрунту.
        // Cooldown нижче однаково діє і для цього випадку.
        var soilDeclining = soilPoints.Count >= MinSustainedReadings && soilPoints[0] - soilPoints[^1] > 1.0;
        var hasValidLatestSoilReading = recentSoilReadings.Count > 0 &&
            recentSoilReadings[0].SoilValid && recentSoilReadings[0].SoilMoisturePct.HasValue;
        var latestSoilMoisturePct = hasValidLatestSoilReading
            ? recentSoilReadings[0].SoilMoisturePct
            : null;
        var soilBelowMin = latestSoilMoisturePct < profile.SoilMoistureMinPct;
        var soilSustainedBelowMin = recentSoilReadings.Count >= MinSustainedReadings &&
            recentSoilReadings.All(p => p.SoilValid &&
                p.SoilMoisturePct.HasValue && p.SoilMoisturePct.Value < profile.SoilMoistureMinPct);
        var soilPersistentlyAtZero = recentSoilReadings.Count >= MinSustainedReadings &&
            recentSoilReadings.All(p => p.SoilValid &&
                p.SoilMoisturePct.HasValue && p.SoilMoisturePct.Value <= 0.0);
        var soilMoistureTargetPct = profile.SoilMoistureMaxPct > profile.SoilMoistureMinPct
            ? Midpoint(profile.SoilMoistureMinPct, profile.SoilMoistureMaxPct)
            : profile.SoilMoistureMinPct;

        var lastWateringUtc = await db.AiDecisions
            .Where(d => d.PumpOn)
            .OrderByDescending(d => d.Timestamp)
            .Select(d => (DateTime?)d.Timestamp)
            .FirstOrDefaultAsync(stoppingToken);
        var wateringCooldownElapsed = lastWateringUtc is null ||
            DateTime.UtcNow - lastWateringUtc.Value >= TimeSpan.FromMinutes(_agronomistOptions.MinMinutesBetweenWaterings);

        var hasWateringDemand = soilSustainedBelowMin && (soilDeclining || soilPersistentlyAtZero);
        if (latestSoilMoisturePct is { } currentSoilMoisturePct)
        {
            recoveryState.SoilWateringActive = currentSoilMoisturePct < soilMoistureTargetPct &&
                (recoveryState.SoilWateringActive || hasWateringDemand);
        }

        var pumpOn = recoveryState.SoilWateringActive && latestSoilMoisturePct is not null && wateringCooldownElapsed;
        string pumpReason;
        if (latestSoilMoisturePct is null)
        {
            pumpReason = "Останній показник вологості ґрунту невалідний або відсутній — полив заблоковано.";
        }
        else if (recoveryState.SoilWateringActive && !wateringCooldownElapsed)
        {
            pumpReason = $"Полив триває до середньої цілі {soilMoistureTargetPct:0.#}%, але після попереднього імпульсу " +
                $"ще не минуло {_agronomistOptions.MinMinutesBetweenWaterings} хв — пауза безпеки.";
        }
        else if (pumpOn)
        {
            pumpReason = $"Вологість {latestSoilMoisturePct:0.#}%: активний цикл поливу до середньої цілі " +
                $"{soilMoistureTargetPct:0.#}% — увімкнено короткий імпульс.";
        }
        else if (soilBelowMin && !soilSustainedBelowMin)
        {
            pumpReason = $"Вологість {latestSoilMoisturePct:0.#}% нижча за мінімум, але потрібно " +
                $"{MinSustainedReadings} послідовні телеметрії нижче межі — чекаємо підтвердження.";
        }
        else if (soilBelowMin && !soilDeclining && !soilPersistentlyAtZero)
        {
            pumpReason = $"Вологість {latestSoilMoisturePct:0.#}% нижча за мінімум, але не падає протягом " +
                $"{_agronomistOptions.SoilMoistureTrendWindowMinutes} хв — чекаємо підтвердження тренду.";
        }
        else
        {
            pumpReason = $"Полив неактивний: {latestSoilMoisturePct:0.#}%. Новий цикл почнеться нижче " +
                $"{profile.SoilMoistureMinPct:0.#}% і завершиться на цілі {soilMoistureTargetPct:0.#}%.";
        }

        // Світло: рахуємо ГОДИНИ, коли рослина реально отримувала світло — і від
        // сонця (ambient Lux >= порогу), і від самого grow light (коли він був
        // увімкнений). Ці два джерела не перетинаються за побудовою: підсвітка
        // вмикається лише тоді, коли ambient нижче порогу, тож подвійний підрахунок
        // неможливий. Без цього grow light міг світити годинами, а лічильник
        // денної норми майже не зрушувався б (grow light рідко піднімає покази
        // BH1750 до GrowthLuxThreshold).
        var lightHoursSoFarToday = EstimateLightHours(todayLightRecords, _agronomistOptions.GrowthLuxThreshold) +
            EstimateGrowLightHours(todayLightDecisions);

        var isNightRest = IsNightRest();
        // Без BH1750 (Lux == null) поведінка як раніше — вважаємо, що темно.
        // AmbientLuxRecheckMinutes = 0: датчик екранований від лампи, тож її
        // власний внесок у показ малий і його достатньо брати як є.
        var ambientRecheckEnabled = _agronomistOptions.AmbientLuxRecheckMinutes > 0;
        var needsAmbientRecheck = ambientRecheckEnabled && latestLux is not null &&
            cleanLuxAgeMinutes > _agronomistOptions.AmbientLuxRecheckMinutes;
        var ambientLux = ambientRecheckEnabled ? cleanLux ?? latestLux ?? 0 : latestLux ?? 0;

        int lightBrightness;
        string lightReason;
        if (isNightRest)
        {
            lightBrightness = 0;
            lightReason = $"Нічний відпочинок {_agronomistOptions.NightRestStartHour:00}:00–{_agronomistOptions.NightRestEndHour:00}:00 — світло вимкнене.";
        }
        else if (lightHoursSoFarToday >= profile.DailyLightHoursTarget)
        {
            lightBrightness = 0;
            lightReason = $"Денну норму світла вже набрано: {lightHoursSoFarToday:0.#}/{profile.DailyLightHoursTarget:0.#} год — світло вимкнене.";
        }
        else if (needsAmbientRecheck)
        {
            lightBrightness = 0;
            lightReason = "Фітолампу тимчасово вимкнено на один цикл телеметрії, щоб виміряти природне світло " +
                $"без її впливу (перевірка щонайменше раз на {_agronomistOptions.AmbientLuxRecheckMinutes} хв).";
        }
        else if (ambientLux >= _agronomistOptions.GrowthLuxThreshold)
        {
            lightBrightness = 0;
            lightReason = $"Природного світла достатньо: {ambientLux:0} lx ≥ {_agronomistOptions.GrowthLuxThreshold:0} lx — лампа вимкнена.";
        }
        else
        {
            var shortfall = (_agronomistOptions.GrowthLuxThreshold - ambientLux) / _agronomistOptions.GrowthLuxThreshold;
            lightBrightness = (int)Math.Round(Math.Clamp(shortfall * 255, 0, 255));
            lightReason = $"Природне світло {ambientLux:0} lx нижче {_agronomistOptions.GrowthLuxThreshold:0} lx, " +
                $"сьогодні набрано {lightHoursSoFarToday:0.#}/{profile.DailyLightHoursTarget:0.#} год — яскравість {lightBrightness}/255.";
        }

        // Підігрів ґрунту працює у ДВОХ режимах, обидва пропорційним ШІМ (без
        // on/off-стрибків):
        //   1) добір температури — SoilTempMinC запускає цикл, який триває до
        //      середини SoilTempMinC/MaxC; потужність пропорційна відстані до неї;
        //   2) просушка перезволоженого ґрунту — коли SoilMoisturePct стійко вище
        //      SoilMoistureMaxPct, підігрів прискорює випаровування з кореневої
        //      зони (базилік гине насамперед від гнилі при мокрому ґрунті — див.
        //      Plant:CareNotes). Потужність = мінімум двох лінійних факторів:
        //      наскільки волого над ціллю (moistureFactor) і скільки лишилось
        //      "запасу" під стелею SoilTempMaxC (tempFactor). Тож підігрів сам
        //      стихає і коли ґрунт підсох до цілі, і коли температура підійшла до
        //      стелі — на самій SoilTempMaxC жорсткий обрив.
        //
        // Раніше режим просушки вже пробували (тоді фіксованою потужністю, поки не
        // було DS18B20) і прибрали: нагрівач сам зсував показник вологості (сушив
        // ґрунт / грів резистивний зонд) швидше, ніж 10-хв цикл встигав відпрацювати
        // — рішення забруднювало власні вхідні дані. Цей ефект нікуди не подівся,
        // але пропорційне згасання (замість різкого on/off) не дає контуру
        // "розгойдатись", а стеля SoilTempMaxC обмежує найгірший випадок.
        //
        // hasCeiling: якщо профіль ще не задав SoilTempMaxC (0 чи <= SoilTempMinC),
        // поводимось як раніше — тільки добір температури, просушка вимкнена.
        var soilWet = soilPoints.Count >= MinSustainedReadings &&
            soilPoints.TakeLast(MinSustainedReadings).All(p => p > profile.SoilMoistureMaxPct);
        var hasCeiling = profile.SoilTempMaxC > profile.SoilTempMinC;
        var soilTempTargetC = hasCeiling
            ? Midpoint(profile.SoilTempMinC, profile.SoilTempMaxC)
            : profile.SoilTempMinC;

        if (latestSoilTemp is { } currentSoilTemp)
        {
            recoveryState.SoilHeatingActive = currentSoilTemp < soilTempTargetC &&
                (recoveryState.SoilHeatingActive || currentSoilTemp < profile.SoilTempMinC);
        }

        if (!hasCeiling)
        {
            recoveryState.SoilDryingActive = false;
        }
        else if (soilPoints.Count > 0)
        {
            recoveryState.SoilDryingActive = soilPoints[^1] > soilMoistureTargetPct &&
                (recoveryState.SoilDryingActive || soilWet);
        }

        int soilHeaterPower;
        string soilHeaterReason;
        if (latestSoilTemp is not { } soilTemp)
        {
            soilHeaterPower = 0;
            soilHeaterReason = "Немає показників датчика температури ґрунту — нагрівач вимкнений.";
        }
        else if (hasCeiling && soilTemp >= profile.SoilTempMaxC)
        {
            // Стеля завжди виграє — байдуже, гріли б ми для добору чи для просушки.
            soilHeaterPower = 0;
            soilHeaterReason = $"Температура ґрунту {soilTemp:0.#}°C досягла жорсткої межі " +
                $"{profile.SoilTempMaxC:0.#}°C — нагрівач тимчасово вимкнений.";
        }
        else
        {
            var proportionalHeatingPower = recoveryState.SoilHeatingActive
                ? ProportionalPower(soilTempTargetC - soilTemp, _agronomistOptions.SoilHeaterFullPowerDeficitC)
                : 0;
            var heatingPower = ApplyRuntimePowerBoost(
                proportionalHeatingPower,
                soilHeaterRuntimeMs,
                _agronomistOptions.SoilHeaterMaxPower,
                _agronomistOptions.HeaterBoostAfterMinutes,
                _agronomistOptions.HeaterBoostStepMinutes,
                _agronomistOptions.HeaterBoostStepPower);
            var dryingPower = recoveryState.SoilDryingActive && soilPoints.Count > 0 && hasCeiling
                ? LimitedProportionalPower(
                    soilPoints[^1] - soilMoistureTargetPct,
                    _agronomistOptions.SoilDryingFullPowerExcessPct,
                    profile.SoilTempMaxC - soilTemp,
                    _agronomistOptions.SoilDryingCeilingTaperC)
                : 0;
            soilHeaterPower = Math.Max(heatingPower, dryingPower);

            var activeReasons = new List<string>();
            if (heatingPower > 0)
            {
                activeReasons.Add($"підігрів ґрунту {soilTemp:0.#}→{soilTempTargetC:0.#}°C ({heatingPower}/255)");
                if (heatingPower > proportionalHeatingPower)
                {
                    activeReasons.Add($"підсилення після {FormatRuntime(soilHeaterRuntimeMs)} безперервної роботи " +
                        $"({proportionalHeatingPower}→{heatingPower}/255)");
                }
            }
            if (dryingPower > 0)
            {
                activeReasons.Add($"просушка {soilPoints[^1]:0.#}→{soilMoistureTargetPct:0.#}% ({dryingPower}/255)");
            }

            soilHeaterReason = activeReasons.Count > 0
                ? $"Активний цикл до середини діапазону: {string.Join("; ", activeReasons)}. Команда {soilHeaterPower}/255."
                : recoveryState.SoilDryingActive
                    ? $"Просушка до {soilMoistureTargetPct:0.#}% ще активна, але температурний захист зараз не дозволяє нагрів."
                    : $"Нагрівач неактивний. Новий цикл температури почнеться нижче {profile.SoilTempMinC:0.#}°C, " +
                      $"просушки — вище {profile.SoilMoistureMaxPct:0.#}%.";
        }

        // Тимчасова апаратна стеля: хоч би що вирішили правила вище, не пускаємо
        // ґрунтовий нагрівач вище SoilHeaterMaxPower (аналог перевірки нижче для
        // повітряного нагрівача).
        if (soilHeaterPower > _agronomistOptions.SoilHeaterMaxPower)
        {
            soilHeaterReason += $" Потужність обмежено безпечним максимумом {_agronomistOptions.SoilHeaterMaxPower}/255.";
            soilHeaterPower = _agronomistOptions.SoilHeaterMaxPower;
        }

        // Повітряний нагрівач: ДВА режими, обидва пропорційним ШІМ, дзеркалять
        // грілку ґрунту (RunLocalControlAsync вище):
        //   1) добір температури — TempMinC запускає цикл до середини
        //      TempMinC/MaxC, потужність пропорційна відстані до цієї цілі;
        //   2) осушення — коли RH стійко вище HumidityMaxPct, підігрів піднімає
        //      температуру => падає відносна вологість і конденсат не осідає на
        //      листі. Потужність = мінімум двох лінійних факторів: наскільки RH
        //      над серединою діапазону і скільки лишилось "запасу" під стелею
        //      TempMaxC (headroomFactor). Тож нагрів сам стихає і коли повітря
        //      підсохло до цілі, і коли температура підійшла до стелі; на самій
        //      TempMaxC — жорсткий обрив.
        // Стеля TempMaxC завжди виграє. З вентилятором (охолодження) не
        // конфліктує: той вмикається лише на СТІЙКОМУ перегріві вище TempMaxC, а
        // осушення тут згасає ще на підході до неї (headroomFactor -> 0).
        int airHeaterPower;
        string airHeaterReason;
        if (latestTemp is not { } airTemp)
        {
            airHeaterPower = 0;
            airHeaterReason = "Немає показників температури повітря — нагрівач вимкнений.";
        }
        else if (airTemp >= profile.TempMaxC)
        {
            airHeaterPower = 0;
            airHeaterReason = $"Температура повітря {airTemp:0.#}°C досягла жорсткої межі {profile.TempMaxC:0.#}°C — нагрівач вимкнений.";
        }
        else
        {
            var proportionalHeatingPower = recoveryState.AirHeatingActive
                ? ProportionalPower(airTempTargetC - airTemp, _agronomistOptions.AirHeaterFullPowerDeficitC)
                : 0;
            var heatingPower = ApplyRuntimePowerBoost(
                proportionalHeatingPower,
                airHeaterRuntimeMs,
                _agronomistOptions.AirHeaterMaxPower,
                _agronomistOptions.HeaterBoostAfterMinutes,
                _agronomistOptions.HeaterBoostStepMinutes,
                _agronomistOptions.HeaterBoostStepPower);
            var dryingPower = recoveryState.AirDryingActive && latestHumidity is { } humidity
                ? LimitedProportionalPower(
                    humidity - airHumidityTargetPct,
                    _agronomistOptions.AirHeaterDryingFullPowerExcessPct,
                    profile.TempMaxC - airTemp,
                    _agronomistOptions.AirHeaterDryingCeilingTaperC)
                : 0;
            airHeaterPower = Math.Max(heatingPower, dryingPower);

            var activeReasons = new List<string>();
            if (heatingPower > 0)
            {
                activeReasons.Add($"підігрів повітря {airTemp:0.#}→{airTempTargetC:0.#}°C ({heatingPower}/255)");
                if (heatingPower > proportionalHeatingPower)
                {
                    activeReasons.Add($"підсилення після {FormatRuntime(airHeaterRuntimeMs)} безперервної роботи " +
                        $"({proportionalHeatingPower}→{heatingPower}/255)");
                }
            }
            if (dryingPower > 0 && latestHumidity is { } activeHumidity)
            {
                activeReasons.Add($"осушення {activeHumidity:0.#}→{airHumidityTargetPct:0.#}% ({dryingPower}/255)");
            }

            airHeaterReason = activeReasons.Count > 0
                ? $"Активний цикл до середини діапазону: {string.Join("; ", activeReasons)}. Команда {airHeaterPower}/255."
                : recoveryState.AirDryingActive
                    ? $"Осушення до {airHumidityTargetPct:0.#}% ще активне, але температурний захист зараз не дозволяє нагрів."
                    : $"Нагрівач неактивний. Новий цикл температури почнеться нижче {profile.TempMinC:0.#}°C, " +
                      $"осушення — вище {profile.HumidityMaxPct:0.#}%.";
        }

        // Тимчасова апаратна стеля: хоч би що вирішили правила вище, не пускаємо
        // повітряний нагрівач вище AirHeaterMaxPower (перевірка нагрівача/БЖ на
        // тривалий повний режим).
        if (airHeaterPower > _agronomistOptions.AirHeaterMaxPower)
        {
            airHeaterReason += $" Потужність обмежено безпечним максимумом {_agronomistOptions.AirHeaterMaxPower}/255.";
            airHeaterPower = _agronomistOptions.AirHeaterMaxPower;
        }

        // Вентилятор циркуляції працює лише разом із повітряним нагрівачем —
        // гріти повітря без обдуву немає сенсу (гаряче повітря стоїть біля
        // елемента, датчик його не "бачить", тепло не розходиться по обʼєму).
        // ESP32 однаково гарантує це на своєму боці
        // (ActuatorService::applyFanOutput), незалежно від цього прапорця.
        var fanOn = airHeaterPower > 0;
        var fanReason = fanOn
            ? $"Увімкнений для розподілу тепла від нагрівача повітря ({airHeaterPower}/255)."
            : "Нагрівач повітря не працює — циркуляція вимкнена.";

        var reason = $"{fanReason}; {pumpReason}; {lightReason}; {soilHeaterReason}; {airHeaterReason}";

        if (StressTestForceActuatorsOn)
        {
            // TEMPORARY STRESS TEST override — see StressTestForceActuatorsOn.
            // Pump is intentionally excluded: pumpOn keeps whatever the normal
            // rule above decided (i.e. stays off unless that rule says water).
            fanOn = true;
            lightBrightness = 255;
            soilHeaterPower = _agronomistOptions.SoilHeaterMaxPower;
            airHeaterPower = _agronomistOptions.AirHeaterMaxPower;
            reason = $"[STRESS TEST: all actuators forced on except pump] {reason}";
        }

        recoveryState.UpdatedUtc = DateTime.UtcNow;

        var decisionRecord = new AiDecisionRecord
        {
            PumpOn = pumpOn,
            FanOn = fanOn,
            LightBrightness = lightBrightness,
            SoilHeaterPower = soilHeaterPower,
            AirHeaterPower = airHeaterPower,
            Source = StressTestForceActuatorsOn ? "StressTest" : "LocalController",
            PumpReason = pumpReason,
            FanReason = fanReason,
            LightReason = lightReason,
            SoilHeaterReason = soilHeaterReason,
            AirHeaterReason = airHeaterReason,
            Reason = reason,
            PhotoDescription = string.Empty,
            PhotoFileName = null
        };
        db.AiDecisions.Add(decisionRecord);
        await db.SaveChangesAsync(stoppingToken);
        await _hub.Clients.All.SendAsync("DecisionReceived", decisionRecord, stoppingToken);

        // Публікуємо щотіку незалежно від того, чи змінилось рішення — саме на
        // це покладаються FAN_MAX_RUNTIME_MS/помпові/нагрівача failsafe-таймери на
        // ESP32, які без повторної команди самі гасять актуатор.
        var commandPayload = JsonSerializer.Serialize(
            new AiCommand(pumpOn, fanOn, lightBrightness, soilHeaterPower, airHeaterPower));
        await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, commandPayload);

        _logger.LogInformation(
            "Local control decision: Pump={Pump} Fan={Fan} Light={Light} SoilHeater={SoilHeater} AirHeater={AirHeater} — {Reason}",
            pumpOn ? "On" : "Off", fanOn ? "On" : "Off", lightBrightness, soilHeaterPower,
            airHeaterPower, reason);
    }

    // ---- Спільне ----

    // Спільна логіка виклику DeepSeek (retry/backoff на 429/5xx, парсинг тексту з
    // відповіді) — використовується профільним аналізом.
    private async Task<string?> CallDeepSeekAsync(object requestBody, CancellationToken stoppingToken)
    {
        var url = $"{_deepSeekOptions.BaseUrl.TrimEnd('/')}/chat/completions";

        // DeepSeek occasionally returns 503/429 when its servers are overloaded — these are
        // transient on the provider's side, not a problem with our request, so retry a few times
        // with backoff before giving up and letting the cycle fail for real.
        const int maxAttempts = 3;
        var retryDelay = TimeSpan.FromSeconds(5);
        HttpResponseMessage response;
        for (int attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _deepSeekOptions.ApiKey);

            _logger.LogInformation("Dispatching request to DeepSeek model {Model} (attempt {Attempt}/{MaxAttempts})",
                _deepSeekOptions.Model, attempt, maxAttempts);

            try
            {
                response = await _httpClient.SendAsync(request, stoppingToken);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) &&
                !stoppingToken.IsCancellationRequested && attempt < maxAttempts)
            {
                // Network-level failure (timeout, connection reset) before we even got a response —
                // just as retryable as a transient 5xx, but SendAsync throws instead of returning one.
                _logger.LogWarning(ex,
                    "DeepSeek request threw {ExceptionType} (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}",
                    ex.GetType().Name, attempt, maxAttempts, retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay *= 2;
                continue;
            }

            var isTransientError = !response.IsSuccessStatusCode &&
                (int)response.StatusCode is 429 or 500 or 502 or 503 or 504;
            if (!isTransientError || attempt >= maxAttempts)
            {
                break;
            }

            _logger.LogWarning(
                "DeepSeek request failed with {StatusCode} (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}",
                response.StatusCode, attempt, maxAttempts, retryDelay);
            response.Dispose();
            await Task.Delay(retryDelay, stoppingToken);
            retryDelay *= 2;
        }

        var rawBody = await response.Content.ReadAsStringAsync(stoppingToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("DeepSeek request failed with {StatusCode}, response body: {Body}",
                response.StatusCode, rawBody);
        }

        response.EnsureSuccessStatusCode();
        response.Dispose();
        _logger.LogDebug("Raw DeepSeek response: {RawBody}", rawBody);

        // reasoning_content (хід думок моделі) свідомо не читаємо — JSON-відповідь лише в content.
        var completion = JsonSerializer.Deserialize<ChatCompletionResponse>(rawBody);
        var text = completion?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("DeepSeek returned an empty response");
            return null;
        }

        return text;
    }

    // Підсумовує, скільки годин сьогодні ambient Lux (природне світло) сам по собі
    // тримався на рівні "ефективного" ростового освітлення. Точки, зняті при
    // увімкненій фітолампі (LightRuntimeMs > 0), пропускаються: без обмеження
    // яскравості лампа сама піднімає BH1750 вище порогу, і ці години рахувались
    // би двічі — тут і в EstimateGrowLightHours, що рахує години підсвітки окремо. Кожен інтервал між сусідніми точками рахується як
    // "освітлений", якщо його стартова точка була вище порогу; інтервал обрізається
    // до 15 хв, щоб простій пристрою (Wi-Fi/MQTT відвалились на години) не
    // зарахувався як багатогодинне освітлення.
    private static double EstimateLightHours(List<TelemetryRecord> chronologicalRecords, double luxThreshold)
    {
        var cap = TimeSpan.FromMinutes(15);
        double hours = 0;
        for (int i = 0; i < chronologicalRecords.Count - 1; i++)
        {
            var current = chronologicalRecords[i];
            if (current.LightRuntimeMs > 0 || !current.Lux.HasValue || current.Lux.Value < luxThreshold)
            {
                continue;
            }

            var gap = chronologicalRecords[i + 1].Timestamp - current.Timestamp;
            if (gap < TimeSpan.Zero)
            {
                continue;
            }

            hours += (gap > cap ? cap : gap).TotalHours;
        }

        return hours;
    }

    // Той самий принцип, що й EstimateLightHours, але за історією рішень
    // локального контролера: рахує години, коли grow light сам був увімкнений
    // (LightBrightness > 0). За побудовою правила світла ці інтервали не
    // перетинаються з інтервалами EstimateLightHours (підсвітка вмикається лише
    // коли ambient нижче порогу), тож суму двох можна брати без подвійного обліку.
    private static double EstimateGrowLightHours(List<AiDecisionRecord> chronologicalDecisions)
    {
        var cap = TimeSpan.FromMinutes(15);
        double hours = 0;
        for (int i = 0; i < chronologicalDecisions.Count - 1; i++)
        {
            var current = chronologicalDecisions[i];
            if (current.LightBrightness <= 0)
            {
                continue;
            }

            var gap = chronologicalDecisions[i + 1].Timestamp - current.Timestamp;
            if (gap < TimeSpan.Zero)
            {
                continue;
            }

            hours += (gap > cap ? cap : gap).TotalHours;
        }

        return hours;
    }

    private static List<TrendPoint> DownsampleTrend(List<TelemetryRecord> chronologicalRecords, TimeSpan bucketSize)
    {
        return chronologicalRecords
            .GroupBy(t => new DateTime(t.Timestamp.Ticks / bucketSize.Ticks * bucketSize.Ticks, DateTimeKind.Utc))
            .Select(bucket => new TrendPoint(
                bucket.Key,
                AverageOrNull(bucket.Select(t => t.TemperatureC)),
                AverageOrNull(bucket.Select(t => t.HumidityPct)),
                bucket.Average(t => t.SoilRaw),
                AverageOrNull(bucket.Select(t => t.SoilMoisturePct)),
                AverageOrNull(bucket.Select(t => t.SoilTempC)),
                AverageOrNull(bucket.Select(t => t.Lux)),
                AverageOrNull(bucket.Select(t => t.PressureHpa))))
            .ToList();
    }

    private static double? AverageOrNull(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count > 0 ? present.Average() : null;
    }

    private static string FormatOrNA(double? value, string suffix = "") =>
        value.HasValue ? $"{value.Value:0.##}{suffix}" : "N/A";

    private static string? SummarizeMetric(string label, List<TrendPoint> trend, Func<TrendPoint, double?> selector, string suffix)
    {
        var points = trend.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (points.Count == 0)
        {
            return null;
        }

        var start = points.First();
        var end = points.Last();
        var delta = end - start;
        var sign = delta >= 0 ? "+" : "";
        return $"{label}: {start:0.##}{suffix} -> {end:0.##}{suffix} (Δ{sign}{delta:0.##}{suffix}, min {points.Min():0.##}{suffix}, max {points.Max():0.##}{suffix})";
    }

    private record TrendPoint(
        DateTime Timestamp,
        double? TemperatureC,
        double? HumidityPct,
        double SoilRaw,
        double? SoilMoisturePct,
        double? SoilTempC,
        double? Lux,
        double? PressureHpa);

    private static string StripMarkdownFence(string text)
    {
        text = text.Trim();
        if (!text.StartsWith('`'))
        {
            return text;
        }

        text = text.Trim('`').Trim();
        if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..].Trim();
        }

        return text;
    }

    private record PlantProfileAnalysisResponse(
        double TempMinC,
        double TempMaxC,
        double HumidityMinPct,
        double HumidityMaxPct,
        double SoilMoistureMinPct,
        double SoilMoistureMaxPct,
        double SoilTempMinC,
        double SoilTempMaxC,
        double DailyLightHoursTarget,
        string GrowthStage,
        string Notes);

    private record ChatCompletionResponse(
        [property: JsonPropertyName("choices")] List<ChatCompletionChoice>? Choices);

    private record ChatCompletionChoice(
        [property: JsonPropertyName("message")] ChatCompletionMessage? Message);

    private record ChatCompletionMessage(
        [property: JsonPropertyName("content")] string? Content);
}
