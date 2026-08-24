namespace SmartGreenhouse.Backend.Models;

public record AiDecision(bool PumpOn, bool FanOn, int LightBrightness, string Reason, string PhotoDescription);
