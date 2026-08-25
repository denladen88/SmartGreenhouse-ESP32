using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Services;

// Два незалежні цикли: RunProfileSupervisionLoopAsync раз на добу (або
// позачергово, при стійкій аномалії) питає Gemini і повністю переписує
// PlantProfile; RunLocalControlLoopAsync щохвилини LocalControlIntervalMinutes
// сам вирішує pump/fan/light простими правилами, спираючись на цей профіль —
// жодного звернення до AI на кожен тік актуаторів.
public class AiAgronomistService : BackgroundService
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new() { PropertyNameCaseInsensitive = true };
    public const string PhotosDirectory = "Photos";

    // Скільки послідовних не-null точок треба, щоб довіряти "стійкому" тренду
    // (і в DetectSustainedAnomalyAsync, і в локальному правилі вентилятора) —
    // одна точка може бути шумом, кілька поспіль — уже сигнал.
    private const int MinSustainedReadings = 2;

    private readonly ILogger<AiAgronomistService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GeminiOptions _geminiOptions;
    private readonly Esp32Options _esp32Options;
    private readonly MqttOptions _mqttOptions;
    private readonly AiAgronomistOptions _agronomistOptions;
    private readonly PlantOptions _plantOptions;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly HttpClient _httpClient;

    // Коли востаннє реально відбувся аналіз профілю (плановий чи позачерговий) —
    // від цього моменту рахуються і ProfileAnalysisIntervalMinutes, і MinMinutesBetweenCycles.
    // Належить виключно RunProfileSupervisionLoopAsync — локальний контролер його не чіпає.
    private DateTime? _lastProfileAnalysisUtc;

    public AiAgronomistService(
        ILogger<AiAgronomistService> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<GeminiOptions> geminiOptions,
        IOptions<Esp32Options> esp32Options,
        IOptions<MqttOptions> mqttOptions,
        IOptions<AiAgronomistOptions> agronomistOptions,
        IOptions<PlantOptions> plantOptions,
        IMqttPublisher mqttPublisher,
        IHttpClientFactory httpClientFactory)
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await Task.WhenAll(
            RunProfileSupervisionLoopAsync(stoppingToken),
            RunLocalControlLoopAsync(stoppingToken));

    // ---- Профіль: раз на добу (або раніше, при аномалії) Gemini переглядає все і переписує PlantProfile ----

    private async Task RunProfileSupervisionLoopAsync(CancellationToken stoppingToken)
    {
        // Перший аналіз одразу при старті — він же й бутстрап, якщо профілю для
        // цієї рослини в базі ще немає.
        await RunProfileAnalysisSafeAsync(stoppingToken, earlyTriggerReason: null);
        _lastProfileAnalysisUtc = DateTime.UtcNow;

        // Тік коротший за ProfileAnalysisIntervalMinutes: на кожному перевіряємо,
        // чи не пора або плановий перегляд (минув ProfileAnalysisIntervalMinutes),
        // або позачерговий через стійку аномалію (минуло хоча б MinMinutesBetweenCycles
        // і DetectSustainedAnomalyAsync каже, що щось стійко вийшло за межі профілю).
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_agronomistOptions.LocalControlIntervalMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var elapsed = DateTime.UtcNow - (_lastProfileAnalysisUtc ?? DateTime.MinValue);
            if (elapsed >= TimeSpan.FromMinutes(_agronomistOptions.ProfileAnalysisIntervalMinutes))
            {
                await RunProfileAnalysisSafeAsync(stoppingToken, earlyTriggerReason: null);
                _lastProfileAnalysisUtc = DateTime.UtcNow;
                continue;
            }

            if (elapsed < TimeSpan.FromMinutes(_agronomistOptions.MinMinutesBetweenCycles))
            {
                continue;
            }

            string? anomalyReason;
            try
            {
                anomalyReason = await DetectSustainedAnomalyAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anomaly detection failed");
                anomalyReason = null;
            }

            if (anomalyReason is not null)
            {
                _logger.LogWarning("Sustained anomaly detected, running an early profile analysis: {Reason}", anomalyReason);
                await RunProfileAnalysisSafeAsync(stoppingToken, anomalyReason);
                _lastProfileAnalysisUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task RunProfileAnalysisSafeAsync(CancellationToken stoppingToken, string? earlyTriggerReason)
    {
        try
        {
            await RunProfileAnalysisAsync(stoppingToken, earlyTriggerReason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Agronomist profile analysis failed");
        }
    }

    // Дивиться на телеметрію за останні SustainedExcursionMinutes і шукає метрику
    // (Temp/Humidity/SoilMoisture — Lux свідомо не перевіряємо тут, він не
    // безпековий показник на кшталт перегріву/посухи/вологісного грибка), усі
    // не-null точки якої за вікно лежать поза межами PlantProfile. Це лише
    // сигнал "проаналізувати профіль раніше" — жодних рішень тут не приймається.
    private async Task<string?> DetectSustainedAnomalyAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = await db.PlantProfiles.FirstOrDefaultAsync(p => p.PlantName == _plantOptions.Name, stoppingToken);
        if (profile is null)
        {
            return null;
        }

        var windowStart = DateTime.UtcNow - TimeSpan.FromMinutes(_agronomistOptions.SustainedExcursionMinutes);
        var records = await db.Telemetries
            .Where(t => t.Timestamp >= windowStart)
            .OrderBy(t => t.Timestamp)
            .ToListAsync(stoppingToken);

        return CheckExcursion("Temperature", records.Select(t => t.TemperatureC), profile.TempMinC, profile.TempMaxC, "C")
            ?? CheckExcursion("Humidity", records.Select(t => t.HumidityPct), profile.HumidityMinPct, profile.HumidityMaxPct, "%")
            ?? CheckExcursion("SoilMoisture", records.Select(t => t.SoilMoisturePct), profile.SoilMoistureMinPct,
                profile.SoilMoistureMaxPct, "%");
    }

    private string? CheckExcursion(string metricName, IEnumerable<double?> values, double min, double max, string suffix)
    {
        var points = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (points.Count < MinSustainedReadings)
        {
            return null;
        }

        var allBelow = points.All(v => v < min);
        var allAbove = points.All(v => v > max);
        if (!allBelow && !allAbove)
        {
            return null;
        }

        return $"{metricName} has been {(allBelow ? "below" : "above")} the ideal range ({min:0.#}-{max:0.#}{suffix}) for " +
            $"all {points.Count} readings over the last {_agronomistOptions.SustainedExcursionMinutes} minutes (ranged " +
            $"{points.Min():0.#}-{points.Max():0.#}{suffix}).";
    }

    private async Task RunProfileAnalysisAsync(CancellationToken stoppingToken, string? earlyTriggerReason)
    {
        var trendWindow = TimeSpan.FromMinutes(_agronomistOptions.TrendWindowMinutes);
        var trendBucket = TimeSpan.FromMinutes(_agronomistOptions.TrendBucketMinutes);
        var windowStart = DateTime.UtcNow - trendWindow;

        List<TelemetryRecord> recentRecords;
        List<AiDecisionRecord> recentDecisions;
        PlantProfile? profile;
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

            profile = await db.PlantProfiles.FirstOrDefaultAsync(p => p.PlantName == _plantOptions.Name, stoppingToken);
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
            SummarizeMetric("Lux", trend, t => t.Lux, ""),
            SummarizeMetric("Pressure", trend, t => t.PressureHpa, "hPa"),
        }.Where(l => l is not null));

        var trendText = string.Join(
            "\n",
            trend.Select(t =>
                $"{t.Timestamp:MM-dd HH:mm} Temp={FormatOrNA(t.TemperatureC, "C")} Humidity={FormatOrNA(t.HumidityPct, "%")} " +
                $"SoilMoisture={FormatOrNA(t.SoilMoisturePct, "%")} (raw diagnostic: {t.SoilRaw:0}) " +
                $"Lux={FormatOrNA(t.Lux)} Pressure={FormatOrNA(t.PressureHpa, "hPa")}"));

        var actuatorHistoryText = SummarizeActuatorHistory(recentDecisions);

        _logger.LogInformation(
            "Trend for this profile analysis: {PointCount} points over the last {Window}:\n{TrendText}",
            trend.Count, trendWindow, trendText);

        byte[] imageBytes;
        try
        {
            imageBytes = await _httpClient.GetByteArrayAsync(_esp32Options.CameraUrl, stoppingToken);
            _logger.LogInformation("Downloaded {ByteCount} bytes from ESP32 camera at {CameraUrl}",
                imageBytes.Length, _esp32Options.CameraUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture image from ESP32-CAM at {CameraUrl}", _esp32Options.CameraUrl);
            return;
        }

        // ESP32 повертає 204 без тіла вночі (замало світла для корисного кадру) —
        // GetByteArrayAsync у такому разі не кидає виняток, а віддає порожній масив.
        var hasPhoto = imageBytes.Length > 0;
        var base64Image = hasPhoto ? Convert.ToBase64String(imageBytes) : null;

        var photoInstruction = hasPhoto
            ? "Analyze this plant photo together with the sensor trend below, using it to judge what is actually normal or " +
              "concerning for this specific species — not generic assumptions. "
            : "No photo was available this time (the camera doesn't capture at night, when ambient light is too low for a " +
              "useful frame) — base your review on the sensor trend and history alone. ";

        var currentProfileParagraph = profile is not null
            ? $"You previously set this profile (last updated {profile.LastUpdatedUtc:yyyy-MM-dd HH:mm} UTC, reason: " +
              $"{profile.LastUpdateReason}):\n" +
              $"- Temperature: {profile.TempMinC:0.#}-{profile.TempMaxC:0.#}C\n" +
              $"- Humidity: {profile.HumidityMinPct:0.#}-{profile.HumidityMaxPct:0.#}%\n" +
              $"- SoilMoisture: {profile.SoilMoistureMinPct:0.#}-{profile.SoilMoistureMaxPct:0.#}%\n" +
              $"- DailyLightHoursTarget: {profile.DailyLightHoursTarget:0.#}h\n" +
              $"- Notes: {profile.Notes}\n\n" +
              "Reassess based on everything below — the trend, and the actuator history (what the automated local rules " +
              "actually did while using these ranges). Keep values that are still working, adjust ones that aren't. The soil " +
              "moisture sensor's calibration drifts over time (its exposed copper contacts corrode under constant voltage), " +
              "so if the watering history looks wrong for how the plant actually looks in the photo (watering too often/too " +
              "rarely relative to visible plant health), nudge SoilMoistureMinPct/MaxPct to compensate rather than leaving " +
              "them stale.\n\n"
            : $"This is the first time a profile is being set for {_plantOptions.Name}. Grower's notes: " +
              $"{(string.IsNullOrWhiteSpace(_plantOptions.CareNotes) ? "(none provided)" : _plantOptions.CareNotes)}\n\n";

        var earlyTriggerParagraph = earlyTriggerReason is not null
            ? $"NOTE: this review is running earlier than the normal {_agronomistOptions.ProfileAnalysisIntervalMinutes}-" +
              $"minute schedule because a sensor reading has been persistently outside the current profile range: " +
              $"{earlyTriggerReason}\n\n"
            : string.Empty;

        var prompt =
            $"You are an AI Agronomist responsible for setting the ideal growing parameters for a greenhouse growing " +
            $"{_plantOptions.Name}. " + photoInstruction +
            $"Current local time: {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).\n\n" +
            earlyTriggerParagraph +
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
            "range from the trend shape and actuator history above, not just a snapshot), and how many hours of effective " +
            "light (sun and/or grow light combined) it needs per day. These ranges will be used directly by simple automated " +
            "rules — not by you — to control the pump, fan, and grow light until your next review, so make them realistic " +
            "operating ranges, not aspirational extremes. Reply strictly in JSON matching this schema: { \"TempMinC\": " +
            "number, \"TempMaxC\": number, \"HumidityMinPct\": number, \"HumidityMaxPct\": number, \"SoilMoistureMinPct\": " +
            "number, \"SoilMoistureMaxPct\": number, \"DailyLightHoursTarget\": number, \"Notes\": \"short rationale, " +
            "referencing what changed since last time if applicable\" } without markdown code blocks.";

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

        _logger.LogInformation(
            "AI profile analysis: Temp {TempMin}-{TempMax}C, Humidity {HumMin}-{HumMax}%, SoilMoisture {SoilMin}-{SoilMax}%, " +
            "DailyLight {Light}h. Notes: {Notes}",
            analysis.TempMinC, analysis.TempMaxC, analysis.HumidityMinPct, analysis.HumidityMaxPct,
            analysis.SoilMoistureMinPct, analysis.SoilMoistureMaxPct, analysis.DailyLightHoursTarget, analysis.Notes);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tracked = await db.PlantProfiles.FirstOrDefaultAsync(p => p.PlantName == _plantOptions.Name, stoppingToken);
            if (tracked is null)
            {
                tracked = new PlantProfile { PlantName = _plantOptions.Name };
                db.PlantProfiles.Add(tracked);
            }

            tracked.TempMinC = analysis.TempMinC;
            tracked.TempMaxC = analysis.TempMaxC;
            tracked.HumidityMinPct = analysis.HumidityMinPct;
            tracked.HumidityMaxPct = analysis.HumidityMaxPct;
            tracked.SoilMoistureMinPct = analysis.SoilMoistureMinPct;
            tracked.SoilMoistureMaxPct = analysis.SoilMoistureMaxPct;
            tracked.DailyLightHoursTarget = analysis.DailyLightHoursTarget;
            tracked.Notes = analysis.Notes;
            tracked.LastUpdatedUtc = DateTime.UtcNow;
            tracked.LastUpdateReason = earlyTriggerReason is null ? "Scheduled daily review" : $"Early review: {earlyTriggerReason}";

            await db.SaveChangesAsync(stoppingToken);
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
                last.Sample.LightBrightness == d.LightBrightness)
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
            $"Fan={(s.Sample.FanOn ? "On" : "Off")} Light={s.Sample.LightBrightness} — {s.Sample.Reason}"));
    }

    private record ActuatorSegment(DateTime Start, DateTime End, AiDecisionRecord Sample, int Count);

    // ---- Локальний контролер: щохвилини LocalControlIntervalMinutes вирішує pump/fan/light сам, без AI ----

    private async Task RunLocalControlLoopAsync(CancellationToken stoppingToken)
    {
        await RunLocalControlSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_agronomistOptions.LocalControlIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunLocalControlSafeAsync(stoppingToken);
        }
    }

    private async Task RunLocalControlSafeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunLocalControlAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local actuator control tick failed");
        }
    }

    private async Task RunLocalControlAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = await db.PlantProfiles.FirstOrDefaultAsync(p => p.PlantName == _plantOptions.Name, stoppingToken);
        if (profile is null)
        {
            _logger.LogInformation(
                "No PlantProfile yet for {PlantName} — waiting for the first AI profile analysis before controlling actuators",
                _plantOptions.Name);
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

        var todayStartUtc = DateTime.Now.Date.ToUniversalTime();
        var todayLightRecords = await db.Telemetries
            .Where(t => t.Timestamp >= todayStartUtc)
            .OrderBy(t => t.Timestamp)
            .Select(t => new TelemetryRecord { Timestamp = t.Timestamp, Lux = t.Lux })
            .ToListAsync(stoppingToken);

        var latestLux = await db.Telemetries
            .OrderByDescending(t => t.Timestamp)
            .Select(t => t.Lux)
            .FirstOrDefaultAsync(stoppingToken);

        // Вентилятор: усі останні MinSustainedReadings точки вище максимуму — не одна.
        var fanOn = recentTemps.Count >= MinSustainedReadings && recentTemps.All(t => t > profile.TempMaxC);
        var fanReason = fanOn
            ? $"Temp {string.Join("/", recentTemps.Select(t => t.ToString("0.#")))}C > max {profile.TempMaxC:0.#}C " +
              $"({recentTemps.Count} readings) -> On"
            : $"Temp within {profile.TempMaxC:0.#}C max -> Off";

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

        // Світло: тільки в "денні" години, коли ambient Lux не дотягує до порогу і
        // денна норма годин світла ще не вибрана.
        var lightHoursSoFarToday = EstimateLightHours(todayLightRecords, _agronomistOptions.GrowthLuxThreshold);
        var hour = DateTime.Now.Hour;
        var isDaytime = hour >= _agronomistOptions.DaytimeStartHour && hour < _agronomistOptions.DaytimeEndHour;
        var ambientLux = latestLux ?? 0;

        int lightBrightness;
        string lightReason;
        if (!isDaytime)
        {
            lightBrightness = 0;
            lightReason = $"Outside daytime hours ({_agronomistOptions.DaytimeStartHour}-{_agronomistOptions.DaytimeEndHour}) -> Off";
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
            lightBrightness = (int)Math.Clamp(shortfall * 255, 0, 255);
            lightReason = $"Daytime, ambient {ambientLux:0}lx < {_agronomistOptions.GrowthLuxThreshold:0}lx threshold, " +
                $"{lightHoursSoFarToday:0.#}h/{profile.DailyLightHoursTarget:0.#}h today -> {lightBrightness}";
        }

        var reason = $"{fanReason}; {pumpReason}; {lightReason}";

        db.AiDecisions.Add(new AiDecisionRecord
        {
            PumpOn = pumpOn,
            FanOn = fanOn,
            LightBrightness = lightBrightness,
            Reason = reason,
            PhotoDescription = string.Empty,
            PhotoFileName = null
        });
        await db.SaveChangesAsync(stoppingToken);

        // Публікуємо щотіку незалежно від того, чи змінилось рішення — саме на
        // це покладаються FAN_MAX_RUNTIME_MS/помпові failsafe-таймери на ESP32,
        // які без повторної команди самі гасять актуатор.
        var commandPayload = JsonSerializer.Serialize(new AiCommand(pumpOn, fanOn, lightBrightness));
        await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, commandPayload);

        _logger.LogInformation("Local control decision: Pump={Pump} Fan={Fan} Light={Light} — {Reason}",
            pumpOn ? "On" : "Off", fanOn ? "On" : "Off", lightBrightness, reason);
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

    // Підсумовує, скільки годин сьогодні Lux тримався на рівні "ефективного" ростового
    // світла (сонце і/або grow light разом — BH1750 бачить обидва джерела). Кожен
    // інтервал між сусідніми точками рахується як "освітлений", якщо його стартова
    // точка була вище порогу; інтервал обрізається до 15 хв, щоб простій пристрою
    // (Wi-Fi/MQTT відвалились на години) не зарахувався як багатогодинне освітлення.
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
        double DailyLightHoursTarget,
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
