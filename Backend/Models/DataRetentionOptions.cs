namespace SmartGreenhouse.Backend.Models;

public class DataRetentionOptions
{
    // Скільки діб історії телеметрії/рішень тримати. AI-тренд дивиться лише на
    // останні 24 год (TrendWindowMinutes), тож 90 діб — з великим запасом для
    // перегляду історії в застосунку.
    public int RetentionDays { get; set; } = 90;

    // Як часто проходити прибирання.
    public int SweepIntervalHours { get; set; } = 24;
}
