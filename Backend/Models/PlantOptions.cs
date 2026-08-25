namespace SmartGreenhouse.Backend.Models;

// CareNotes/DailyLightHoursTarget — це "насіння" для першого аналізу профілю
// (AiAgronomistService.RunProfileAnalysisAsync, коли PlantProfile для цього
// Name ще не існує). Далі профіль щодня повністю переписує сам Gemini, а
// CareNotes лишається лише додатковим контекстом-нагадуванням у промпті.
// DailyLightHoursTarget використовується як fallback, поки жодного профілю
// ще не створено.
public class PlantOptions
{
    public string Name { get; set; } = "unspecified plant";
    public string CareNotes { get; set; } = string.Empty;
    public double DailyLightHoursTarget { get; set; } = 6.0;
}
