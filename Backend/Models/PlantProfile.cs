namespace SmartGreenhouse.Backend.Models;

// Структурований, "живий" профіль ідеальних параметрів рослини — на відміну від
// вільного тексту PlantOptions.CareNotes (який лише одноразово засіває перший
// профіль), цей запис щодня (або позачергово, при стійкій аномалії) повністю
// переписує Gemini (AiAgronomistService.RunProfileAnalysisAsync). Актуатори між
// цими переглядами керуються локально (RunLocalControlAsync) саме за цими
// значеннями. PlantName — природний ключ: зміна Plant:Name в конфігурації
// природно породжує новий профіль замість тихого перевикористання старого.
public class PlantProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PlantName { get; set; } = string.Empty;

    public double TempMinC { get; set; }
    public double TempMaxC { get; set; }
    public double HumidityMinPct { get; set; }
    public double HumidityMaxPct { get; set; }

    // Локальне правило поливу реально спирається на ці межі (у поєднанні зі
    // спадним трендом, не саму лише точку) — тому компенсація дрейфу датчика
    // ґрунту (корозія контактів з часом) лягає на Gemini: щодня переглядаючи
    // фото/історію, він підлаштовує ці числа, а не на жорстке ігнорування
    // порогу локальним правилом (див. AiAgronomistService.RunLocalControlAsync).
    public double SoilMoistureMinPct { get; set; }
    public double SoilMoistureMaxPct { get; set; }

    // Локальне правило підігріву (AiAgronomistService.RunLocalControlAsync) тримає
    // ґрунт не нижче цього порогу пропорційним ШІМ (немає верхньої межі — нагрівач
    // лише додає тепло, не охолоджує, тож перегріву від власної роботи не буває).
    public double SoilTempMinC { get; set; }

    public double DailyLightHoursTarget { get; set; }

    public string Notes { get; set; } = string.Empty;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public string LastUpdateReason { get; set; } = string.Empty;
}
