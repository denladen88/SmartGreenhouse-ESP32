namespace SmartGreenhouse.Backend.Models;

public class AiAgronomistOptions
{
    // Локальна година доби (0-23), о якій Gemini "по плану" переглядає фото+тренд
    // і переписує ідеальний профіль рослини (PlantProfile) — рівно один раз на
    // добу. Рішеннями актуаторів між цими оглядами керує локальний контролер
    // (AiAgronomistService.RunLocalControlAsync), не AI.
    public int DailyAnalysisHour { get; set; } = 12;

    // Плановий огляд НЕ виконується без свіжого фото. Якщо о DailyAnalysisHour
    // камера не віддала кадр (офлайн / затемно), пробуємо ще раз кожні
    // PhotoRetryIntervalMinutes, поки від старту огляду не мине
    // PhotoRetryWindowMinutes — після чого цей день пропускається (профіль
    // лишається без змін). Для запуску о 12:00 вікно 120 хв = "до 14:00".
    public int PhotoRetryIntervalMinutes { get; set; } = 15;
    public int PhotoRetryWindowMinutes { get; set; } = 120;

    public int TrendWindowMinutes { get; set; } = 1440;
    public int TrendBucketMinutes { get; set; } = 60;

    // Скільки згрупованих сегментів історії актуаторів (SummarizeActuatorHistory)
    // показувати Gemini в промпті.
    public int DecisionHistoryCount { get; set; } = 5;

    // Lux рівень, що вважається "ефективним" ростовим світлом (сонце і/або
    // grow light разом) — використовується і для підрахунку годин світла за
    // добу, і локальним правилом підсвітки як поріг "замало ambient light".
    // Орієнтовне значення, може знадобитись відкалібрувати під реальні покази
    // BH1750 у вашій теплиці.
    public double GrowthLuxThreshold { get; set; } = 1000.0;

    // FALLBACK-таймер локального контролера актуаторів. Основний шлях подієвий:
    // RunLocalControlSignalLoopAsync реагує на кожну нову телеметрію одразу. Цей
    // таймер лишається як підстрахування — щоб команда актуаторам
    // підтверджувалась навіть коли телеметрія замовкла (на це покладаються
    // FAN/SOIL_HEATER_MAX_RUNTIME_MS=15 хв у прошивці; 10 хв дає запас поверх них).
    public int LocalControlIntervalMinutes { get; set; } = 10;

    // Вікно для локального правила помпи: шукаємо спадний тренд вологості
    // ґрунту саме в цих межах.
    public int SoilMoistureTrendWindowMinutes { get; set; } = 30;

    // Запобіжник від перезапуску поливу щотіку локального контролера, поки
    // ґрунт лишається сухим — базилік дуже вразливий до кореневої гнилі від
    // перезволоження (див. Plant:CareNotes).
    public int MinMinutesBetweenWaterings { get; set; } = 60;

    // Гарантований нічний "відпочинок" без підсвітки, незалежно від того,
    // скільки годин світла ще недобрано за добу — рослині шкідливо тримати
    // світло цілодобово. Може огортати північ (StartHour > EndHour, як у
    // дефолті 23->5). ПОЗА цим вікном контролер намагається добрати денну
    // норму (PlantProfile.DailyLightHoursTarget), а не зупиняється по
    // жорсткому "денному" годиннику — інакше при хмарному дні чи короткому
    // денному вікні норма просто ніколи не набирається.
    public int NightRestStartHour { get; set; } = 23;
    public int NightRestEndHour { get; set; } = 5;

    // Наскільки нижче PlantProfile.SoilTempMinC (у °C) має впасти ґрунт, щоб
    // локальне правило підігріву вивело нагрівач на повну потужність (255) —
    // між 0 і цим дефіцитом потужність зростає пропорційно, не різким on/off.
    public double SoilHeaterFullPowerDeficitC { get; set; } = 5.0;

    // Наскільки SoilMoisturePct має перевищувати PlantProfile.SoilMoistureMaxPct
    // (у процентних пунктах), щоб режим просушки ґрунту нагрівачем ішов на повній
    // потужності. Між 0 і цим перевищенням потужність зростає лінійно; на цілі
    // (перевищення 0) — нагрівач сам гасне.
    public double SoilDryingFullPowerExcessPct { get; set; } = 15.0;

    // Скільки °C "запасу" під PlantProfile.SoilTempMaxC, на яких потужність
    // просушки лінійно спадає до 0 — щоб температура кореневої зони не вганялась у
    // жорстку стелю, а м'яко до неї підходила. Сам жорсткий обрив на SoilTempMaxC
    // лишається (це остання лінія захисту разом із failsafe-таймером прошивки).
    public double SoilDryingCeilingTaperC { get; set; } = 5.0;

    // Гістерезис вентилятора охолодження (°C). Вентилятор вмикається, коли
    // температура повітря стійко вище PlantProfile.TempMaxC, і працює, доки не
    // охолодить до (TempMaxC - FanHysteresisC). Цей "мертвий діапазон" не дає
    // реле смикатися, коли температура тремтить рівно біля стелі.
    public double FanHysteresisC { get; set; } = 1.0;

    // Наскільки нижче PlantProfile.TempMinC (у °C) має впасти повітря, щоб
    // локальне правило підігріву вивело повітряний нагрівач на повну потужність
    // (255) — між 0 і цим дефіцитом потужність зростає пропорційно, не різким
    // on/off. Аналог SoilHeaterFullPowerDeficitC для ґрунтового.
    public double AirHeaterFullPowerDeficitC { get; set; } = 5.0;

    // Тимчасова апаратна стеля потужності повітряного нагрівача (0..255) для
    // локального контролера. 255 = без обмеження. Тримаємо нижче, поки нагрівач і
    // блок живлення не перевірено на тривалий режим на повній потужності.
    public int AirHeaterMaxPower { get; set; } = 255;

    // Наскільки відносна вологість повітря має перевищувати
    // PlantProfile.HumidityMaxPct (у процентних пунктах), щоб режим осушення
    // повітряним нагрівачем ішов на повній потужності. Між 0 і цим перевищенням
    // потужність зростає лінійно; на цілі — нагрівач сам гасне. Аналог
    // SoilDryingFullPowerExcessPct.
    public double AirHeaterDryingFullPowerExcessPct { get; set; } = 15.0;

    // Скільки °C "запасу" під PlantProfile.TempMaxC, на яких потужність осушення
    // повітряним нагрівачем лінійно спадає до 0 — щоб температура повітря не
    // вганялась у жорстку стелю, а м'яко до неї підходила. Тісніше за ґрунтовий
    // аналог (SoilDryingCeilingTaperC=5): повітря реагує на нагрів помітно
    // швидше за ґрунт. Сам жорсткий обрив на TempMaxC лишається.
    public double AirHeaterDryingCeilingTaperC { get; set; } = 3.0;
}
