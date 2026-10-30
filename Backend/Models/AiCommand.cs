using System.Text.Json.Serialization;

namespace SmartGreenhouse.Backend.Models;

public record AiCommand(
    [property: JsonPropertyName("pump_on")] bool PumpOn,
    [property: JsonPropertyName("fan_on")] bool FanOn,
    // Кулер радіатора фітолампи. Світло вмикає його на ESP32 й без команди;
    // true тут — активне охолодження теплиці разом із FanOn.
    [property: JsonPropertyName("led_fan_on")] bool LedFanOn,
    [property: JsonPropertyName("light_brightness")] int LightBrightness,
    [property: JsonPropertyName("soil_heater_power")] int SoilHeaterPower,
    [property: JsonPropertyName("air_heater_power")] int AirHeaterPower);
