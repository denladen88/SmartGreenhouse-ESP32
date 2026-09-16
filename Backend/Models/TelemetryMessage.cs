using System.Text.Json.Serialization;

namespace SmartGreenhouse.Backend.Models;

public record TelemetryMessage(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("uptime_ms")] long UptimeMs,
    [property: JsonPropertyName("temperature_c")] double? TemperatureC,
    [property: JsonPropertyName("humidity_pct")] double? HumidityPct,
    [property: JsonPropertyName("pressure_hpa")] double? PressureHpa,
    [property: JsonPropertyName("lux")] double? Lux,
    [property: JsonPropertyName("soil_raw")] int SoilRaw,
    [property: JsonPropertyName("soil_moisture_pct")] double? SoilMoisturePct,
    // Nullable, не bool: старіша прошивка (до цього поля) просто не шле цей
    // ключ. null тут означає "невідомо", і трактується як valid=true нижче —
    // інакше телеметрія від ще не перепрошитого пристрою мовчки виглядала б
    // завжди невалідною й вимкнула б керування поливом/просушкою до рефлешу.
    [property: JsonPropertyName("soil_valid")] bool? SoilValid,
    [property: JsonPropertyName("soil_temp_c")] double? SoilTempC);
