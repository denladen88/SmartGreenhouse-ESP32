namespace SmartGreenhouse.Backend.Models;

public class AiDecisionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool PumpOn { get; set; }
    public bool FanOn { get; set; }
    public int LightBrightness { get; set; }
    public int SoilHeaterPower { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PhotoDescription { get; set; } = string.Empty;
    public string? PhotoFileName { get; set; }
}
