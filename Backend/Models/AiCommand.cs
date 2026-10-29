using System.Text.Json.Serialization;

namespace SmartGreenhouse.Backend.Models;

public record AiCommand(
    [property: JsonPropertyName("pump_on")] bool PumpOn,
    [property: JsonPropertyName("fan_on")] bool FanOn,
    [property: JsonPropertyName("light_brightness")] int LightBrightness,
    [property: JsonPropertyName("soil_heater_power")] int SoilHeaterPower,
    [property: JsonPropertyName("air_heater_power")] int AirHeaterPower);
