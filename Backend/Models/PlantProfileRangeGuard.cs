namespace SmartGreenhouse.Backend.Models;

// Спільний "захист здорового глузду" для меж PlantProfile: той самий фізично
// розумний діапазон (0-45°C, 0-100%) і той самий порядок дій (обрізати кожен
// кінець незалежно, ПОТІМ перевірити min < max за вже обрізаними значеннями),
// незалежно від того, хто сформував пару — AI-аналіз (AiAgronomistService) чи
// ручне редагування (PlantProfileController). Обидва джерела напряму рухають
// потужність нагрівачів/вентиляторів у RunLocalControlAsync, тож розбіжність
// між ними тут — це не просто дублювання коду, а діра в безпеці.
public static class PlantProfileRangeGuard
{
    public const double MinPlausibleTempC = 0.0;
    public const double MaxPlausibleTempC = 45.0;

    public static double ClampTempC(double value) => Math.Clamp(value, MinPlausibleTempC, MaxPlausibleTempC);

    public static double ClampPct(double value) => Math.Clamp(value, 0.0, 100.0);

    // Обрізає min і max НЕЗАЛЕЖНО (кожен може вилазити за межу з різних причин),
    // і лише ПОТІМ порівнює вже обрізані значення між собою — порівняння сирого
    // значення з одного боку проти обрізаного з іншого якраз і ховало баг: пара
    // (46, 47), обидва вище стелі 45°C, обрізається в (45, 45), і порівняння
    // "сире 47 > обрізане 45" пропускає це як нібито впорядковану пару. Якщо
    // після обрізання min >= max, піднімаємо max на marginC (не займаючи min) —
    // потрібен якийсь робочий діапазон, а не порожній.
    public static (double Min, double Max, bool WasReordered) ClampOrderedTempC(double rawMin, double rawMax, double marginC)
    {
        var min = ClampTempC(rawMin);
        var max = ClampTempC(rawMax);
        var reordered = max <= min;
        if (reordered)
        {
            max = Math.Min(min + marginC, MaxPlausibleTempC);
        }
        return (min, max, reordered);
    }

    public static (double Min, double Max, bool WasReordered) ClampOrderedPct(double rawMin, double rawMax, double marginPct)
    {
        var min = ClampPct(rawMin);
        var max = ClampPct(rawMax);
        var reordered = max <= min;
        if (reordered)
        {
            max = Math.Min(min + marginPct, 100.0);
        }
        return (min, max, reordered);
    }
}
