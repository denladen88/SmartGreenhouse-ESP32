using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Services;

public class AiAgronomistService : BackgroundService
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new() { PropertyNameCaseInsensitive = true };
    public const string PhotosDirectory = "Photos";

    private readonly ILogger<AiAgronomistService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GeminiOptions _geminiOptions;
    private readonly Esp32Options _esp32Options;
    private readonly MqttOptions _mqttOptions;
    private readonly AiAgronomistOptions _agronomistOptions;
    private readonly PlantOptions _plantOptions;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly HttpClient _httpClient;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Запускаємо перший цикл одразу при старті, а не чекаємо повний
        // PollIntervalMinutes (інакше після кожного рестарту сервісу пристрій
        // залишався б без свіжого рішення AI аж до години).
        await RunAnalysisCycleSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_agronomistOptions.PollIntervalMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunAnalysisCycleSafeAsync(stoppingToken);
        }
    }

    private async Task RunAnalysisCycleSafeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAnalysisCycleAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI Agronomist analysis cycle failed");
        }
    }

    private async Task RunAnalysisCycleAsync(CancellationToken stoppingToken)
    {
        var trendWindow = TimeSpan.FromMinutes(_agronomistOptions.TrendWindowMinutes);
        var trendBucket = TimeSpan.FromMinutes(_agronomistOptions.TrendBucketMinutes);
        var windowStart = DateTime.UtcNow - trendWindow;

        List<TelemetryRecord> recentRecords;
        List<AiDecisionRecord> recentDecisions;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            recentRecords = await db.Telemetries
                .Where(t => t.Timestamp >= windowStart)
                .OrderBy(t => t.Timestamp)
                .ToListAsync(stoppingToken);

            recentDecisions = await db.AiDecisions
                .OrderByDescending(d => d.Timestamp)
                .Take(_agronomistOptions.DecisionHistoryCount)
                .ToListAsync(stoppingToken);
        }
        recentDecisions.Reverse();

        if (recentRecords.Count == 0)
        {
            _logger.LogInformation("No telemetry recorded in the last {Window}, skipping AI analysis cycle", trendWindow);
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

        var decisionHistoryText = recentDecisions.Count == 0
            ? "(none yet)"
            : string.Join("\n", recentDecisions.Select(d =>
                $"{d.Timestamp:HH:mm} Pump={(d.PumpOn ? "On" : "Off")} Fan={(d.FanOn ? "On" : "Off")} " +
                $"Light={d.LightBrightness} — {d.Reason}"));

        var trendText = string.Join(
            "\n",
            trend.Select(t =>
                $"{t.Timestamp:HH:mm} Temp={FormatOrNA(t.TemperatureC, "C")} Humidity={FormatOrNA(t.HumidityPct, "%")} " +
                $"SoilMoisture={FormatOrNA(t.SoilMoisturePct, "%")} (raw diagnostic: {t.SoilRaw:0}) " +
                $"Lux={FormatOrNA(t.Lux)} Pressure={FormatOrNA(t.PressureHpa, "hPa")}"));

        _logger.LogInformation(
            "Trend for this cycle: {PointCount} points over the last {Window}:\n{TrendText}",
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

        string? photoFileName = null;
        if (hasPhoto)
        {
            try
            {
                Directory.CreateDirectory(PhotosDirectory);
                photoFileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
                await File.WriteAllBytesAsync(Path.Combine(PhotosDirectory, photoFileName), imageBytes, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save AI cycle photo to disk");
                photoFileName = null;
            }
        }
        else
        {
            _logger.LogInformation("No photo available from ESP32 camera this cycle (nighttime) — analyzing sensor trend only");
        }

        var plantProfile = string.IsNullOrWhiteSpace(_plantOptions.CareNotes)
            ? string.Empty
            : $"Plant profile — {_plantOptions.Name}: {_plantOptions.CareNotes}\n\n";

        var photoInstruction = hasPhoto
            ? "Analyze this plant photo together with the sensor trend below, using the plant profile to judge what is " +
              "actually normal or concerning for this specific species — not generic assumptions. "
            : "No photo was available this cycle (the camera doesn't capture at night, when ambient light is too low for a " +
              "useful frame) — base your analysis on the sensor trend and plant profile alone; leave PhotoDescription empty. ";

        var prompt =
            $"You are an AI Agronomist managing a greenhouse growing {_plantOptions.Name}. " +
            photoInstruction +
            "Pay attention to the RATE of change over time (e.g. how fast the soil is drying out or the temperature is rising), " +
            "not just the latest snapshot.\n\n" +
            $"Current local time: {DateTime.Now:yyyy-MM-dd HH:mm} ({DateTime.Now:dddd}).\n\n" +
            plantProfile +
            $"Recent past decisions, oldest to newest (for continuity across cycles — avoid flip-flopping the pump/fan on " +
            $"and off if the trend hasn't meaningfully changed since the last decision):\n{decisionHistoryText}\n\n" +
            "SoilMoisture is the primary soil signal: a calibrated 0-100% reading (100% = fully wet, 0% = fully dry). " +
            "The 'raw diagnostic' number next to it is the uncalibrated 0-4095 ADC value shown only for troubleshooting — " +
            "ignore it for moisture decisions. Do not apply a fixed cutoff like 'water if below X%': this sensor's calibration " +
            "drifts over time (its exposed copper contacts corrode under constant voltage, so the same soil moisture can read " +
            "differently after weeks of use, and it has already been recalibrated once). Instead, judge from the shape of the " +
            "trend: a value that is low and stable is fine, but a value falling steadily over the window is what indicates " +
            "watering is becoming necessary. Do not react to a single point; a real need to water shows up as a sustained " +
            "multi-point trend, not a one-off dip.\n\n" +
            $"Trend summary over the last {(int)trendWindow.TotalMinutes} minutes (Δ = change from earliest to latest reading; " +
            $"use this for at-a-glance direction, the detailed readings below show the full shape):\n{trendSummaryText}\n\n" +
            $"Detailed sensor trend, oldest to newest, {trend.Count} points over the last {(int)trendWindow.TotalMinutes} minutes " +
            $"('N/A' means that sensor had no reading in that time bucket):\n{trendText}\n\n" +
            "There is also a grow light (white, adjustable 0-255 brightness) you control directly — it is the ONLY light " +
            "source you can add; the Lux trend above is ambient light the plant is already getting. Use the current local " +
            "time together with the Lux trend to judge whether it's currently daytime or nighttime, and decide LightBrightness " +
            "accordingly: during the plant's normal daytime hours, if ambient Lux is low (e.g. an overcast day), supplement " +
            "with grow light brightness roughly proportional to how far short of a healthy level the ambient light is. " +
            "During nighttime hours, do NOT turn the grow light on just because ambient Lux is low — the plant needs a dark " +
            "rest period overnight like any normal day/night cycle, and keeping light on through the night is harmful, not " +
            "helpful. Avoid flip-flopping brightness sharply between consecutive cycles unless the trend genuinely changed.\n\n" +
            "Decide if we need to turn on the water pump or cooling fan, and what the grow light brightness should be. " +
            "Also describe what you see in the photo (plant condition, leaves, soil surface, anything notable). Reply " +
            "strictly in JSON format matching this schema: { \"PumpOn\": bool, \"FanOn\": bool, \"LightBrightness\": " +
            "int (0-255), \"Reason\": \"short explanation referencing the trend\", \"PhotoDescription\": \"what you see " +
            "in the photo\" } without markdown code blocks.";

        var parts = new List<object> { new { text = prompt } };
        if (hasPhoto)
        {
            parts.Add(new { inline_data = new { mime_type = "image/jpeg", data = base64Image } });
        }

        var requestBody = new
        {
            contents = new[]
            {
                new { parts = parts.ToArray() }
            },
            generationConfig = new
            {
                response_mime_type = "application/json"
            }
        };

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
            return;
        }

        var decision = JsonSerializer.Deserialize<AiDecision>(StripMarkdownFence(text), DecisionJsonOptions);

        if (decision is null)
        {
            _logger.LogWarning("Failed to parse AI decision from Gemini response: {Text}", text);
            return;
        }

        var lightBrightness = Math.Clamp(decision.LightBrightness, 0, 255);

        _logger.LogInformation(
            "AI Agronomist decision:\n" +
            "  Pump:   {Pump}\n" +
            "  Fan:    {Fan}\n" +
            "  Light:  {Light}\n" +
            "  Reason: {Reason}\n" +
            "  Photo:  {Photo}",
            decision.PumpOn ? "On" : "Off",
            decision.FanOn ? "On" : "Off",
            lightBrightness,
            decision.Reason,
            decision.PhotoDescription);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiDecisions.Add(new AiDecisionRecord
            {
                PumpOn = decision.PumpOn,
                FanOn = decision.FanOn,
                LightBrightness = lightBrightness,
                Reason = decision.Reason,
                PhotoDescription = decision.PhotoDescription,
                PhotoFileName = photoFileName
            });
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save AI decision record to the database");
        }

        var commandPayload = JsonSerializer.Serialize(new AiCommand(decision.PumpOn, decision.FanOn, lightBrightness));
        await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, commandPayload);

        _logger.LogInformation("Published command to {Topic}: {Payload}", _mqttOptions.CommandsTopic, commandPayload);
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

    private record GeminiGenerateContentResponse(
        [property: JsonPropertyName("candidates")] List<GeminiCandidate>? Candidates);

    private record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);

    private record GeminiContent(
        [property: JsonPropertyName("parts")] List<GeminiPart>? Parts);

    private record GeminiPart(
        [property: JsonPropertyName("text")] string? Text);
}
