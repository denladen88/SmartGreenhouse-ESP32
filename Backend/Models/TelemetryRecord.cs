using Microsoft.EntityFrameworkCore;

namespace SmartGreenhouse.Backend.Models;

// Кожен запит локального контролера й аналізу профілю фільтрує/сортує за
// Timestamp; дедуп передоставки бере найсвіжіший запис пристрою. Індекс тримає
// це швидким, поки таблиця росте (навіть з DataRetentionService).
[Index(nameof(Timestamp))]
public class TelemetryRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DeviceId { get; set; } = string.Empty;
    public long UptimeMs { get; set; }
    public double? TemperatureC { get; set; }
    public double? HumidityPct { get; set; }
    public double? PressureHpa { get; set; }
    public double? Lux { get; set; }
    public int SoilRaw { get; set; }
    public double? SoilMoisturePct { get; set; }
    public bool SoilValid { get; set; } = true;
    public double? SoilTempC { get; set; }
    public long PumpRuntimeMs { get; set; }
    public long FanRuntimeMs { get; set; }
    public long ExhaustFanRuntimeMs { get; set; }
    public long LightRuntimeMs { get; set; }
    public long SoilHeaterRuntimeMs { get; set; }
    public long AirHeaterRuntimeMs { get; set; }
}
