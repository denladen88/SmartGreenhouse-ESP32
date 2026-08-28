namespace SmartGreenhouse.Backend.Models;

// Простий спільний ключ для мобільного застосунку — достатньо, бо API
// доступне лише в локальній Wi-Fi мережі (жодного публічного HTTPS/JWT наразі
// не потрібно, див. план мобільного застосунку).
public class ApiOptions
{
    public required string Key { get; set; }
}
