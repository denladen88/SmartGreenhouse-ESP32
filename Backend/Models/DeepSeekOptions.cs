namespace SmartGreenhouse.Backend.Models;

// DeepSeek API (OpenAI-сумісний /chat/completions). Ключ має формат "sk-...".
// Model має приймати зображення (input_modalities містить "image"), інакше
// щоденний огляд з фото падатиме з 400 — deepseek-flash приймає, а
// deepseek-v4-pro лише текст.
public class DeepSeekOptions
{
    public required string ApiKey { get; set; }
    public string Model { get; set; } = "deepseek-flash";
    public string BaseUrl { get; set; } = "https://api.deepseek.com";
}
