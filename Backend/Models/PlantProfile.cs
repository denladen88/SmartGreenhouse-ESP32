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
    // ґрунт не нижче цього порогу пропорційним ШІМ.
    public double SoilTempMinC { get; set; }

    // Верхня межа температури кореневої зони: локальне правило підігріву обриває
    // нагрівач, щойно SoilTempC її досягає. Потрібна, бо нагрівач тепер вмикається
    // не лише для добору SoilTempMinC, а й щоб просушити перезволожений ґрунт
    // (SoilMoisturePct стійко вище SoilMoistureMaxPct) — без цієї стелі "сушіння
    // теплом" могло б перегріти корені. Потужність просушки лінійно спадає до нуля
    // на останніх SoilDryingCeilingTaperC °C перед стелею, тож підхід до неї
    // м'який, а не різкий обрив. 0 (чи <= SoilTempMinC) = профіль ще не задав межу,
    // тоді просушка вимкнена, а нагрів працює лише за старим правилом дефіциту.
    public double SoilTempMaxC { get; set; }

    public double DailyLightHoursTarget { get; set; }

    public string Notes { get; set; } = string.Empty;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    public string LastUpdateReason { get; set; } = string.Empty;
}
