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
// поточної рослини профілю ще нема) питає Gemini і повністю переписує
// PlantProfile; RunLocalControl* сам вирішує pump/fan/light/heater простими
// правилами, спираючись на цей профіль — жодного звернення до AI на кожен тік
// актуаторів.
public class AiAgronomistService : BackgroundService
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new() { PropertyNameCaseInsensitive = true };
    public const string PhotosDirectory = "Photos";

    // Плановий (щоденний) огляд вимагає свіжого фото; bootstrap для нової рослини
    // — ні (краще профіль на самих сенсорах, ніж бездіяльні актуатори до полудня).
    private enum ProfileReviewKind { ScheduledDaily, Bootstrap }

    // Скільки послідовних не-null точок треба, щоб довіряти "стійкому" тренду в
    // локальних правилах (вентилятор, помпа, просушка ґрунту) — одна точка може
    // бути шумом, кілька поспіль — уже сигнал.
    private const int MinSustainedReadings = 2;

    private readonly ILogger<AiAgronomistService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GeminiOptions _geminiOptions;
    private readonly Esp32Options _esp32Options;
    private readonly MqttOptions _mqttOptions;
    private readonly AiAgronomistOptions _agronomistOptions;
    private readonly PlantOptions _plantOptions;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly HttpClient _httpClient;        // Gemini (45с — фото + prompt)
    private readonly HttpClient _cameraHttpClient;  // ESP32-CAM (15с — щоб мертва камера не тримала слот 45с)
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly TelemetrySignal _telemetrySignal;

    // Локальна дата, коли востаннє СТАРТУВАВ плановий (ScheduledDaily) огляд —
    // байдуже, чи він дописав профіль. Гасить тісний повторний прогін, коли
    // плановий огляд завершився без фото і нічого не записав (інакше цикл одразу
    // побачив би "після полудня, сьогодні ще не було" і запустився знову).
    // Належить виключно RunProfileSupervisionLoopAsync.
    private DateOnly? _lastScheduledAttemptLocalDate;

    // Локальний контролер тепер будиться з двох джерел — fallback-таймера і
    // сигналу про нову телеметрію. Семафор серіалізує їх (один тік за раз), а
    // _lastLocalControlUtc гасить здвоєний прогін, коли обидва спрацювали разом
    // (щоб не писати дубль AiDecisionRecord і не публікувати команду двічі).
    private readonly SemaphoreSlim _localControlGate = new(1, 1);
    private DateTime _lastLocalControlUtc = DateTime.MinValue;

    public AiAgronomistService(
        ILogger<AiAgronomistService> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<GeminiOptions> geminiOptions,
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
        _geminiOptions = geminiOptions.Value;
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

        await RunProfileAnalysisSafeAsync(ct, ProfileReviewKind.Bootstrap, $"Initial profile: {reason}");
    }

    // ---- Профіль: рівно раз на добу о DailyAnalysisHour Gemini переглядає все і переписує PlantProfile ----

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
                await RunProfileAnalysisSafeAsync(stoppingToken, ProfileReviewKind.Bootstrap,
                    "Initial profile (no profile on startup)");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                // "Навздогін": бекенд підняли вже після DailyAnalysisHour, а
                // сьогоднішній плановий огляд ще не стартував у цьому процесі
                // (_lastScheduledAttemptLocalDate) і профіль сьогодні після
                // полудня не оновлювався (перевірка в БД — переживає рестарт).
                // Тоді не чекаємо повну добу до наступного полудня.
                var today = DateOnly.FromDateTime(DateTime.Now);
                var noonPassed = DateTime.Now.Hour >= _agronomistOptions.DailyAnalysisHour;
                var catchUp = noonPassed
                    && _lastScheduledAttemptLocalDate != today
                    && !await AlreadyReviewedSinceTodayNoonAsync(stoppingToken);

                if (!catchUp)
                {
                    await Task.Delay(TimeUntilNextNoon(), stoppingToken);
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _lastScheduledAttemptLocalDate = DateOnly.FromDateTime(DateTime.Now);
                await RunProfileAnalysisSafeAsync(stoppingToken, ProfileReviewKind.ScheduledDaily, "Scheduled daily review");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
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

    // Чи профіль поточної рослини вже оновлювався сьогодні після DailyAnalysisHour
    // — тобто плановий огляд (або ручна правка через застосунок) цього дня вже
    // стався. Переживає рестарт бекенду, на відміну від
    // _lastScheduledAttemptLocalDate.
    private async Task<bool> AlreadyReviewedSinceTodayNoonAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plantName = ResolvePlantName(await GetCurrentPlantingAsync(db, ct));
        var todayNoonUtc = DateTime.Now.Date.AddHours(_agronomistOptions.DailyAnalysisHour).ToUniversalTime();
        var lastUpdatedUtc = await db.PlantProfiles
            .Where(p => p.PlantName == plantName)
            .Select(p => (DateTime?)p.LastUpdatedUtc)
            .FirstOrDefaultAsync(ct);
        return lastUpdatedUtc is { } u && u >= todayNoonUtc;
    }

    private async Task RunProfileAnalysisSafeAsync(
        CancellationToken stoppingToken, ProfileReviewKind kind, string lastUpdateReason)
    {
        try
        {
            await RunProfileAnalysisAsync(stoppingToken, kind, lastUpdateReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Agronomist profile analysis failed");
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

        if (imageBytes.Length == 0)
        {
            var previousDecision = await GetLatestActuatorStateAsync(stoppingToken);

            _logger.LogInformation("No photo (likely night per ESP32) — forcing grow light on for a proper shot and retrying");
            await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, JsonSerializer.Serialize(
                new AiCommand(previousDecision.PumpOn, previousDecision.FanOn, 255, previousDecision.SoilHeaterPower,
                    previousDecision.AirHeaterPower)));

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
                new AiCommand(previousDecision.PumpOn, previousDecision.FanOn, previousDecision.LightBrightness,
                    previousDecision.SoilHeaterPower, previousDecision.AirHeaterPower)));
        }

        if (imageBytes.Length == 0)
        {
            return null;
        }

        _logger.LogInformation("Downloaded {ByteCount} bytes from ESP32 camera at {CameraUrl}",
            imageBytes.Length, _esp32Options.CameraUrl);
        return imageBytes;
    }

    private async Task RunProfileAnalysisAsync(
        CancellationToken stoppingToken, ProfileReviewKind kind, string lastUpdateReason)
    {
        var requirePhoto = kind == ProfileReviewKind.ScheduledDaily;
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
            return;
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

        // Плановий (щоденний) огляд не виконується без свіжого фото: якщо камера
        // недоступна / затемно, добираємо кадр кожні PhotoRetryIntervalMinutes,
        // поки від старту огляду не мине PhotoRetryWindowMinutes, після чого цей
        // день пропускаємо (профіль лишається без змін). Bootstrap фото не
        // вимагає — краще профіль на самих сенсорах, ніж бездіяльні актуатори.
        var photoDeadlineUtc = DateTime.UtcNow + TimeSpan.FromMinutes(_agronomistOptions.PhotoRetryWindowMinutes);
        var imageBytes = await TryCapturePhotoAsync(stoppingToken);
        while (imageBytes is null && requirePhoto && DateTime.UtcNow < photoDeadlineUtc)
        {
            _logger.LogInformation(
                "Scheduled review needs a photo but none available yet — retrying in {Interval} min",
                _agronomistOptions.PhotoRetryIntervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(_agronomistOptions.PhotoRetryIntervalMinutes), stoppingToken);
            imageBytes = await TryCapturePhotoAsync(stoppingToken);
        }

        if (imageBytes is null && requirePhoto)
        {
            _logger.LogWarning(
                "No photo from ESP32 camera within {Window} min of the scheduled review — skipping today's profile " +
                "analysis, profile left unchanged", _agronomistOptions.PhotoRetryWindowMinutes);
            return;
        }

        var hasPhoto = imageBytes is { Length: > 0 };
        var base64Image = hasPhoto ? Convert.ToBase64String(imageBytes!) : null;

        var photoInstruction = hasPhoto
            ? "Analyze this plant photo together with the sensor trend below, using it to judge what is actually normal or " +
              "concerning for this specific species — not generic assumptions. "
            : "No photo was available this time (the camera doesn't capture at night, when ambient light is too low for a " +
              "useful frame) — base your review on the sensor trend and history alone. ";

        // Контекст посадки йде В КОЖНОМУ огляді (не лише в першому), щоб Gemini
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

        var prompt =
            $"You are an AI Agronomist responsible for setting the ideal growing parameters for a greenhouse growing " +
            $"{ResolvePlantName(planting)}. " + photoInstruction +
            $"Current local time: {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).\n\n" +
            plantingContextParagraph +
            currentProfileParagraph +
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
            "you — to control the pump, fan, grow light, an air heater, and a soil heating mat until your next review. " +
            "The fan is cooling only: it turns on when air temperature is sustained above TempMaxC. The air heater runs " +
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
            "markdown code blocks.";

        var parts = new List<object> { new { text = prompt } };
        if (hasPhoto)
        {
            parts.Add(new { inline_data = new { mime_type = "image/jpeg", data = base64Image } });
        }

        var requestBody = new
        {
            contents = new[] { new { parts = parts.ToArray() } },
            generationConfig = new { response_mime_type = "application/json" }
        };

        var text = await CallGeminiAsync(requestBody, stoppingToken);
        if (text is null)
        {
            return;
        }

        var analysis = JsonSerializer.Deserialize<PlantProfileAnalysisResponse>(StripMarkdownFence(text), DecisionJsonOptions);
        if (analysis is null)
        {
            _logger.LogWarning("Failed to parse PlantProfile analysis response: {Text}", text);
            return;
        }

        // Захист від некоректної відповіді: якщо AI повернув SoilTempMaxC <= SoilTempMinC,
        // це або вимкнуло б добір температури, або зняло стелю просушки — обидва
        // небезпечні. Підставляємо мінімум + запас на повну потужність і логуємо.
        var soilTempMaxC = analysis.SoilTempMaxC > analysis.SoilTempMinC
            ? analysis.SoilTempMaxC
            : analysis.SoilTempMinC + _agronomistOptions.SoilHeaterFullPowerDeficitC;
        if (soilTempMaxC != analysis.SoilTempMaxC)
        {
            _logger.LogWarning(
                "AI returned SoilTempMaxC {Returned}C <= SoilTempMinC {Min}C — clamping to {Clamped}C",
                analysis.SoilTempMaxC, analysis.SoilTempMinC, soilTempMaxC);
        }

        _logger.LogInformation(
            "AI profile analysis: Temp {TempMin}-{TempMax}C, Humidity {HumMin}-{HumMax}%, SoilMoisture {SoilMin}-{SoilMax}%, " +
            "SoilTemp {SoilTempMin}-{SoilTempMax}C, DailyLight {Light}h. GrowthStage: {GrowthStage}. Notes: {Notes}",
            analysis.TempMinC, analysis.TempMaxC, analysis.HumidityMinPct, analysis.HumidityMaxPct,
            analysis.SoilMoistureMinPct, analysis.SoilMoistureMaxPct, analysis.SoilTempMinC, soilTempMaxC,
            analysis.DailyLightHoursTarget, analysis.GrowthStage, analysis.Notes);

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

            tracked.TempMinC = analysis.TempMinC;
            tracked.TempMaxC = analysis.TempMaxC;
            tracked.HumidityMinPct = analysis.HumidityMinPct;
            tracked.HumidityMaxPct = analysis.HumidityMaxPct;
            tracked.SoilMoistureMinPct = analysis.SoilMoistureMinPct;
            tracked.SoilMoistureMaxPct = analysis.SoilMoistureMaxPct;
            tracked.SoilTempMinC = analysis.SoilTempMinC;
            tracked.SoilTempMaxC = soilTempMaxC;
            tracked.DailyLightHoursTarget = analysis.DailyLightHoursTarget;
            tracked.GrowthStage = analysis.GrowthStage ?? string.Empty;
            tracked.Notes = analysis.Notes;
            tracked.LastUpdatedUtc = DateTime.UtcNow;
            tracked.LastUpdateReason = lastUpdateReason;

            await db.SaveChangesAsync(stoppingToken);
            await _hub.Clients.All.SendAsync("PlantProfileReceived", tracked, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save PlantProfile");
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
            $"Fan={(s.Sample.FanOn ? "On" : "Off")} Light={s.Sample.LightBrightness} " +
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

        var soilWindowStart = DateTime.UtcNow - TimeSpan.FromMinutes(_agronomistOptions.SoilMoistureTrendWindowMinutes);
        var soilPoints = await db.Telemetries
            .Where(t => t.Timestamp >= soilWindowStart && t.SoilMoisturePct != null)
            .OrderBy(t => t.Timestamp)
            .Select(t => t.SoilMoisturePct!.Value)
            .ToListAsync(stoppingToken);

        var recentTemps = await db.Telemetries
            .Where(t => t.TemperatureC != null)
            .OrderByDescending(t => t.Timestamp)
            .Take(MinSustainedReadings)
            .Select(t => t.TemperatureC!.Value)
            .ToListAsync(stoppingToken);

        var recentHumidity = await db.Telemetries
            .Where(t => t.HumidityPct != null)
            .OrderByDescending(t => t.Timestamp)
            .Take(MinSustainedReadings)
            .Select(t => t.HumidityPct!.Value)
            .ToListAsync(stoppingToken);

        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();
        var todayLightRecords = await db.Telemetries
            .Where(t => t.Timestamp >= todayStartUtc)
            .OrderBy(t => t.Timestamp)
            .Select(t => new TelemetryRecord { Timestamp = t.Timestamp, Lux = t.Lux })
            .ToListAsync(stoppingToken);

        var todayLightDecisions = await db.AiDecisions
            .Where(d => d.Timestamp >= todayStartUtc)
            .OrderBy(d => d.Timestamp)
            .ToListAsync(stoppingToken);

        var latestLux = await db.Telemetries
            .OrderByDescending(t => t.Timestamp)
            .Select(t => t.Lux)
            .FirstOrDefaultAsync(stoppingToken);

        var latestSoilTemp = await db.Telemetries
            .Where(t => t.SoilTempC != null)
            .OrderByDescending(t => t.Timestamp)
            .Select(t => t.SoilTempC)
            .FirstOrDefaultAsync(stoppingToken);

        // Вентилятор: ТІЛЬКИ охолодження повітря. Вмикається, коли останні
        // MinSustainedReadings замірів температури всі вище PlantProfile.TempMaxC
        // (стійкий перегрів, а не один випадковий стрибок), і працює далі з
        // гістерезисом — доки повітря не охолоне до (TempMaxC - FanHysteresisC).
        // Без цього "мертвого діапазону" реле смикало б туди-сюди щоразу, коли
        // температура тремтить рівно біля стелі.
        //
        // Вологість більше НЕ керує вентилятором (прибрано на прохання) — тепер
        // це суто температурний прилад на охолодження. Підігрів повітря буде
        // окремим ШІМ-актуатором (як грілка ґрунту), а не цим реле: вентилятор
        // фізично гріти не може, лише ганяти повітря.
        var fanWasOn = await db.AiDecisions
            .OrderByDescending(d => d.Timestamp)
            .Select(d => (bool?)d.FanOn)
            .FirstOrDefaultAsync(stoppingToken) ?? false;

        var latestTemp = recentTemps.Count > 0 ? (double?)recentTemps[0] : null;
        var tempSustainedHigh = recentTemps.Count >= MinSustainedReadings &&
            recentTemps.All(t => t > profile.TempMaxC);
        var fanReleaseTempC = profile.TempMaxC - _agronomistOptions.FanHysteresisC;

        // Для повітряного нагрівача (нижче): стійко холодне повітря — усі останні
        // MinSustainedReadings замірів нижче TempMinC; стійко волога — усі
        // останні заміри RH вище HumidityMaxPct (і сама межа реально задана
        // профілем, HumidityMaxPct > 0 — інакше режим осушення вимкнено, як
        // hasCeiling у ґрунтового нагрівача).
        var tempSustainedLow = recentTemps.Count >= MinSustainedReadings &&
            recentTemps.All(t => t < profile.TempMinC);
        var humiditySustainedHigh = profile.HumidityMaxPct > 0 &&
            recentHumidity.Count >= MinSustainedReadings &&
            recentHumidity.All(h => h > profile.HumidityMaxPct);

        bool fanOn;
        string fanReason;
        if (tempSustainedHigh)
        {
            fanOn = true;
            fanReason = $"Temp {string.Join("/", recentTemps.Select(t => t.ToString("0.#")))}C > max " +
                $"{profile.TempMaxC:0.#}C ({recentTemps.Count} readings) -> On (cooling)";
        }
        else if (fanWasOn && latestTemp is { } stillWarm && stillWarm > fanReleaseTempC)
        {
            fanOn = true;
            fanReason = $"Temp {stillWarm:0.#}C still above release {fanReleaseTempC:0.#}C " +
                $"(max {profile.TempMaxC:0.#}C - hysteresis {_agronomistOptions.FanHysteresisC:0.#}C) -> On (cooling)";
        }
        else
        {
            fanOn = false;
            fanReason = latestTemp is { } coolEnough
                ? $"Temp {coolEnough:0.#}C <= release {fanReleaseTempC:0.#}C (max {profile.TempMaxC:0.#}C) -> Off"
                : "No air temperature readings -> Off";
        }

        // Помпа: вологість спадає і вже нижче мінімуму, плюс не поливали нещодавно
        // (запобіжник від кореневої гнилі базиліка — див. Plant:CareNotes).
        var soilDeclining = soilPoints.Count >= MinSustainedReadings && soilPoints[0] - soilPoints[^1] > 1.0;
        var soilBelowMin = soilPoints.Count > 0 && soilPoints[^1] < profile.SoilMoistureMinPct;

        var lastWateringUtc = await db.AiDecisions
            .Where(d => d.PumpOn)
            .OrderByDescending(d => d.Timestamp)
            .Select(d => (DateTime?)d.Timestamp)
            .FirstOrDefaultAsync(stoppingToken);
        var wateringCooldownElapsed = lastWateringUtc is null ||
            DateTime.UtcNow - lastWateringUtc.Value >= TimeSpan.FromMinutes(_agronomistOptions.MinMinutesBetweenWaterings);

        var pumpOn = soilBelowMin && soilDeclining && wateringCooldownElapsed;
        var pumpReason = !soilBelowMin
            ? $"SoilMoisture {(soilPoints.Count > 0 ? soilPoints[^1].ToString("0.#") : "N/A")}% >= min " +
              $"{profile.SoilMoistureMinPct:0.#}% -> Off"
            : !soilDeclining
                ? "SoilMoisture low but not declining over window -> Off"
                : !wateringCooldownElapsed
                    ? $"SoilMoisture low and declining but watered within last {_agronomistOptions.MinMinutesBetweenWaterings}" +
                      "min -> Off (cooldown)"
                    : $"SoilMoisture {soilPoints[^1]:0.#}% < min {profile.SoilMoistureMinPct:0.#}% and declining -> On";

        // Світло: рахуємо ГОДИНИ, коли рослина реально отримувала світло — і від
        // сонця (ambient Lux >= порогу), і від самого grow light (коли він був
        // увімкнений). Ці два джерела не перетинаються за побудовою: підсвітка
        // вмикається лише тоді, коли ambient нижче порогу, тож подвійний підрахунок
        // неможливий. Без цього grow light міг світити годинами, а лічильник
        // денної норми майже не зрушувався б (grow light рідко піднімає покази
        // BH1750 до GrowthLuxThreshold).
        var lightHoursSoFarToday = EstimateLightHours(todayLightRecords, _agronomistOptions.GrowthLuxThreshold) +
            EstimateGrowLightHours(todayLightDecisions);

        var hour = DateTime.Now.Hour;
        var isNightRest = _agronomistOptions.NightRestStartHour > _agronomistOptions.NightRestEndHour
            ? hour >= _agronomistOptions.NightRestStartHour || hour < _agronomistOptions.NightRestEndHour
            : hour >= _agronomistOptions.NightRestStartHour && hour < _agronomistOptions.NightRestEndHour;
        var ambientLux = latestLux ?? 0;

        int lightBrightness;
        string lightReason;
        if (isNightRest)
        {
            lightBrightness = 0;
            lightReason = $"Night rest period ({_agronomistOptions.NightRestStartHour}-{_agronomistOptions.NightRestEndHour}) -> Off";
        }
        else if (lightHoursSoFarToday >= profile.DailyLightHoursTarget)
        {
            lightBrightness = 0;
            lightReason = $"Daily light target already met ({lightHoursSoFarToday:0.#}h/{profile.DailyLightHoursTarget:0.#}h) -> Off";
        }
        else if (ambientLux >= _agronomistOptions.GrowthLuxThreshold)
        {
            lightBrightness = 0;
            lightReason = $"Ambient {ambientLux:0}lx >= {_agronomistOptions.GrowthLuxThreshold:0}lx threshold, sufficient -> Off";
        }
        else
        {
            var shortfall = (_agronomistOptions.GrowthLuxThreshold - ambientLux) / _agronomistOptions.GrowthLuxThreshold;
            lightBrightness = (int)Math.Round(Math.Clamp(shortfall * 255, 0, 255));
            lightReason = $"Outside night rest, ambient {ambientLux:0}lx < {_agronomistOptions.GrowthLuxThreshold:0}lx threshold, " +
                $"{lightHoursSoFarToday:0.#}h/{profile.DailyLightHoursTarget:0.#}h today -> {lightBrightness}";
        }

        // Підігрів ґрунту працює у ДВОХ режимах, обидва пропорційним ШІМ (без
        // on/off-стрибків):
        //   1) добір температури — лінійне наростання від 0 (на SoilTempMinC) до
        //      255 (дефіцит SoilHeaterFullPowerDeficitC і більше);
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
            soilPoints.All(p => p > profile.SoilMoistureMaxPct);
        var hasCeiling = profile.SoilTempMaxC > profile.SoilTempMinC;

        int soilHeaterPower;
        string soilHeaterReason;
        if (latestSoilTemp is not { } soilTemp)
        {
            soilHeaterPower = 0;
            soilHeaterReason = "No soil temperature sensor connected yet -> Off";
        }
        else if (hasCeiling && soilTemp >= profile.SoilTempMaxC)
        {
            // Стеля завжди виграє — байдуже, гріли б ми для добору чи для просушки.
            soilHeaterPower = 0;
            soilHeaterReason = $"SoilTemp {soilTemp:0.#}C >= max {profile.SoilTempMaxC:0.#}C -> Off (ceiling)";
        }
        else if (soilTemp < profile.SoilTempMinC)
        {
            var deficit = profile.SoilTempMinC - soilTemp;
            soilHeaterPower = (int)Math.Round(Math.Clamp(deficit / _agronomistOptions.SoilHeaterFullPowerDeficitC, 0, 1) * 255);
            soilHeaterReason = $"SoilTemp {soilTemp:0.#}C < min {profile.SoilTempMinC:0.#}C (deficit {deficit:0.#}C) -> {soilHeaterPower}";
        }
        else if (soilWet && hasCeiling)
        {
            var latestSoil = soilPoints[^1];
            var moistureFactor = Math.Clamp(
                (latestSoil - profile.SoilMoistureMaxPct) / _agronomistOptions.SoilDryingFullPowerExcessPct, 0, 1);
            var tempFactor = Math.Clamp(
                (profile.SoilTempMaxC - soilTemp) / _agronomistOptions.SoilDryingCeilingTaperC, 0, 1);
            soilHeaterPower = (int)Math.Round(Math.Min(moistureFactor, tempFactor) * 255);
            soilHeaterReason = soilHeaterPower > 0
                ? $"SoilMoisture {latestSoil:0.#}% > max {profile.SoilMoistureMaxPct:0.#}% " +
                  $"(excess {latestSoil - profile.SoilMoistureMaxPct:0.#}%), SoilTemp {soilTemp:0.#}/{profile.SoilTempMaxC:0.#}C " +
                  $"-> drying at {soilHeaterPower}"
                : $"SoilMoisture {latestSoil:0.#}% > max {profile.SoilMoistureMaxPct:0.#}% but SoilTemp {soilTemp:0.#}C " +
                  $"near max {profile.SoilTempMaxC:0.#}C, easing off -> Off";
        }
        else
        {
            soilHeaterPower = 0;
            soilHeaterReason = $"SoilTemp {soilTemp:0.#}C in range, SoilMoisture within {profile.SoilMoistureMaxPct:0.#}% max -> Off";
        }

        // Повітряний нагрівач: ДВА режими, обидва пропорційним ШІМ, дзеркалять
        // грілку ґрунту (RunLocalControlAsync вище):
        //   1) добір температури — коли повітря стійко нижче TempMinC, потужність
        //      лінійно 0..255 на дефіциті AirHeaterFullPowerDeficitC;
        //   2) осушення — коли RH стійко вище HumidityMaxPct, підігрів піднімає
        //      температуру => падає відносна вологість і конденсат не осідає на
        //      листі. Потужність = мінімум двох лінійних факторів: наскільки RH
        //      над ціллю (humidityFactor) і скільки лишилось "запасу" під стелею
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
            airHeaterReason = "No air temperature readings -> Off";
        }
        else if (airTemp >= profile.TempMaxC)
        {
            airHeaterPower = 0;
            airHeaterReason = $"AirTemp {airTemp:0.#}C >= max {profile.TempMaxC:0.#}C -> Off (ceiling)";
        }
        else if (tempSustainedLow)
        {
            var deficit = profile.TempMinC - airTemp;
            airHeaterPower = (int)Math.Round(Math.Clamp(deficit / _agronomistOptions.AirHeaterFullPowerDeficitC, 0, 1) * 255);
            airHeaterReason = $"AirTemp {string.Join("/", recentTemps.Select(t => t.ToString("0.#")))}C < min " +
                $"{profile.TempMinC:0.#}C (deficit {deficit:0.#}C) -> {airHeaterPower}";
        }
        else if (humiditySustainedHigh)
        {
            var latestHumidity = recentHumidity[0];
            var humidityFactor = Math.Clamp(
                (latestHumidity - profile.HumidityMaxPct) / _agronomistOptions.AirHeaterDryingFullPowerExcessPct, 0, 1);
            var headroomFactor = Math.Clamp(
                (profile.TempMaxC - airTemp) / _agronomistOptions.AirHeaterDryingCeilingTaperC, 0, 1);
            airHeaterPower = (int)Math.Round(Math.Min(humidityFactor, headroomFactor) * 255);
            airHeaterReason = airHeaterPower > 0
                ? $"Humidity {latestHumidity:0.#}% > max {profile.HumidityMaxPct:0.#}% " +
                  $"(excess {latestHumidity - profile.HumidityMaxPct:0.#}%), AirTemp {airTemp:0.#}/{profile.TempMaxC:0.#}C " +
                  $"-> drying at {airHeaterPower}"
                : $"Humidity {latestHumidity:0.#}% > max {profile.HumidityMaxPct:0.#}% but AirTemp {airTemp:0.#}C " +
                  $"near max {profile.TempMaxC:0.#}C, easing off -> Off";
        }
        else
        {
            airHeaterPower = 0;
            airHeaterReason = $"AirTemp {airTemp:0.#}C in range, Humidity within {profile.HumidityMaxPct:0.#}% max -> Off";
        }

        // Тимчасова апаратна стеля: хоч би що вирішили правила вище, не пускаємо
        // повітряний нагрівач вище AirHeaterMaxPower (перевірка нагрівача/БЖ на
        // тривалий повний режим).
        if (airHeaterPower > _agronomistOptions.AirHeaterMaxPower)
        {
            airHeaterReason += $" [capped -> {_agronomistOptions.AirHeaterMaxPower}]";
            airHeaterPower = _agronomistOptions.AirHeaterMaxPower;
        }

        var reason = $"{fanReason}; {pumpReason}; {lightReason}; {soilHeaterReason}; {airHeaterReason}";

        var decisionRecord = new AiDecisionRecord
        {
            PumpOn = pumpOn,
            FanOn = fanOn,
            LightBrightness = lightBrightness,
            SoilHeaterPower = soilHeaterPower,
            AirHeaterPower = airHeaterPower,
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
            pumpOn ? "On" : "Off", fanOn ? "On" : "Off", lightBrightness, soilHeaterPower, airHeaterPower, reason);
    }

    // ---- Спільне ----

    // Спільна логіка виклику Gemini (retry/backoff на 429/5xx, парсинг тексту з
    // відповіді) — використовується профільним аналізом.
    private async Task<string?> CallGeminiAsync(object requestBody, CancellationToken stoppingToken)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_geminiOptions.Model}:generateContent";

        // Gemini occasionally returns 503/429 when its servers are overloaded — these are
        // transient on Google's side, not a problem with our request, so retry a few times
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
            request.Headers.Add("x-goog-api-key", _geminiOptions.ApiKey);

            _logger.LogInformation("Dispatching request to Gemini model {Model} (attempt {Attempt}/{MaxAttempts})",
                _geminiOptions.Model, attempt, maxAttempts);

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
                    "Gemini request threw {ExceptionType} (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}",
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
                "Gemini request failed with {StatusCode} (attempt {Attempt}/{MaxAttempts}), retrying in {Delay}",
                response.StatusCode, attempt, maxAttempts, retryDelay);
            response.Dispose();
            await Task.Delay(retryDelay, stoppingToken);
            retryDelay *= 2;
        }

        var rawBody = await response.Content.ReadAsStringAsync(stoppingToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Gemini request failed with {StatusCode}, response body: {Body}",
                response.StatusCode, rawBody);
        }

        response.EnsureSuccessStatusCode();
        response.Dispose();
        _logger.LogDebug("Raw Gemini response: {RawBody}", rawBody);

        var geminiResponse = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(rawBody);
        var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("Gemini returned an empty response");
            return null;
        }

        return text;
    }

    // Підсумовує, скільки годин сьогодні ambient Lux (природне світло) сам по собі
    // тримався на рівні "ефективного" ростового освітлення. На практиці grow light
    // навіть на високій яскравості рідко піднімає покази BH1750 до GrowthLuxThreshold
    // — тому години активної підсвітки рахує окремо EstimateGrowLightHours, а тут
    // лише природне світло. Кожен інтервал між сусідніми точками рахується як
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
            if (!current.Lux.HasValue || current.Lux.Value < luxThreshold)
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

    private record GeminiGenerateContentResponse(
        [property: JsonPropertyName("candidates")] List<GeminiCandidate>? Candidates);

    private record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);

    private record GeminiContent(
        [property: JsonPropertyName("parts")] List<GeminiPart>? Parts);

    private record GeminiPart(
        [property: JsonPropertyName("text")] string? Text);
}
