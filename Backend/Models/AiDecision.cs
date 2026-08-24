namespace SmartGreenhouse.Backend.Models;

public record AiDecision(bool PumpOn, bool FanOn, string Reason, string PhotoDescription);
