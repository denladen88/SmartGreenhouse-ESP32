namespace SmartGreenhouse.Backend.Models;

// БД-заміна статичного PlantOptions/appsettings.json:Plant — заводиться через
// онбординг-екран мобільного застосунку (POST /api/planting), коли рослину
// фізично посадили чи замінили. Останній запис (за CreatedUtc) визначає
// "поточну" рослину для AiAgronomistService — той самий природний ключ
// (PlantName), яким досі керував PlantProfile.PlantName.
public class Planting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string PlantName { get; set; } = string.Empty;
    public string SoilType { get; set; } = string.Empty;
    public DateTime PlantedDateUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
