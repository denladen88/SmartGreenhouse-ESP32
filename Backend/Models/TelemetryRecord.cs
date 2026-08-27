namespace SmartGreenhouse.Backend.Models;

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
    public double? SoilTempC { get; set; }
}
