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
    [property: JsonPropertyName("soil_moisture_pct")] double? SoilMoisturePct);
