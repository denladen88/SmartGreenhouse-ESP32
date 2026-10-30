namespace SmartGreenhouse.Backend.Models;

public class GeminiOptions
{
    public required string ApiKey { get; set; }
    public string Model { get; set; } = "gemini-3.5-flash";
}
