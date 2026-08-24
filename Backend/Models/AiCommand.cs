using System.Text.Json.Serialization;

namespace SmartGreenhouse.Backend.Models;

public record AiCommand(
    [property: JsonPropertyName("pump_on")] bool PumpOn,
    [property: JsonPropertyName("fan_on")] bool FanOn);
