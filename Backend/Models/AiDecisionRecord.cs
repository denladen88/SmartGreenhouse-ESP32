using Microsoft.EntityFrameworkCore;

namespace SmartGreenhouse.Backend.Models;

// Часто читається як OrderByDescending(Timestamp) (останній стан актуаторів,
// час останнього поливу, історія рішень за вікно) — індексуємо.
[Index(nameof(Timestamp))]
public class AiDecisionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool PumpOn { get; set; }
    public bool FanOn { get; set; }
    public bool ExhaustFanOn { get; set; }
    public int LightBrightness { get; set; }
    public int SoilHeaterPower { get; set; }
    public int AirHeaterPower { get; set; }

    // Джерело й окремі пояснення дають UI структуровану відповідь на питання
    // "чому саме цей актуатор увімкнений/вимкнений". Загальний Reason лишається
    // для сумісності зі старими клієнтами та історією.
    public string Source { get; set; } = "LocalController";
    public string PumpReason { get; set; } = string.Empty;
    public string FanReason { get; set; } = string.Empty;
    public string ExhaustFanReason { get; set; } = string.Empty;
    public string LightReason { get; set; } = string.Empty;
    public string SoilHeaterReason { get; set; } = string.Empty;
    public string AirHeaterReason { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
    public string PhotoDescription { get; set; } = string.Empty;
    public string? PhotoFileName { get; set; }
}
