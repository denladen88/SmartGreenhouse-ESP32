namespace SmartGreenhouse.Backend.Models;

public class MqttOptions
{
    public required string Server { get; set; }
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "smartgreenhouse-backend";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string Topic { get; set; } = "smartplant/telemetry";
    public string CommandsTopic { get; set; } = "smartplant/commands";
}
