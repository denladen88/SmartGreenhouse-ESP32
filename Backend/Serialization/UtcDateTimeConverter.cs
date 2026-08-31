using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartGreenhouse.Backend.Serialization;

// EF Core + SQLite віддають DateTime назад із Kind=Unspecified (SQLite не має
// типу дати, тримає її текстом без зони). Стандартний System.Text.Json такий
// Kind серіалізує без 'Z', і клієнти (WebApp/MobileApp роблять new Date(...))
// читають UTC-момент як локальний час — звідси зсув історії/«Оновлено» на
// величину поточного зсуву зони.
//
// Усі DateTime у моделях — це DateTime.UtcNow, тож на запис примусово
// проставляємо Kind=Utc і віддаємо ISO 8601 із 'Z'; на читання так само
// вважаємо вхід UTC. System.Text.Json застосовує цей конвертер і до
// DateTime? автоматично.
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Utc);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(
            DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"));
}
