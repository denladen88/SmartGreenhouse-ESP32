using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Hubs;
using SmartGreenhouse.Backend.Models;
using SmartGreenhouse.Backend.Services;

namespace SmartGreenhouse.Backend.Controllers;

// Ручний override з мобільного застосунку. Публікує ту саму AiCommand, що й
// AiAgronomistService.RunLocalControlAsync, і так само логує AiDecisionRecord
// — щоб History-екран і GetLatestActuatorStateAsync лишались консистентними.
// Наступний тік локального контролера (LocalControlIntervalMinutes,
// типово 10 хв) природно перепише це рішення своїм — override не "залипає".
[ApiController]
[Route("api/commands")]
public class CommandsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMqttPublisher _mqttPublisher;
    private readonly MqttOptions _mqttOptions;
    private readonly AiAgronomistOptions _agronomistOptions;
    private readonly IHubContext<TelemetryHub> _hub;

    public CommandsController(
        AppDbContext db,
        IMqttPublisher mqttPublisher,
        IOptions<MqttOptions> mqttOptions,
        IOptions<AiAgronomistOptions> agronomistOptions,
        IHubContext<TelemetryHub> hub)
    {
        _db = db;
        _mqttPublisher = mqttPublisher;
        _mqttOptions = mqttOptions.Value;
        _agronomistOptions = agronomistOptions.Value;
        _hub = hub;
    }

    // [FromBody] JsonElement замість [FromBody] AiCommand: AiCommand — record із
    // не-nullable полями, тож JSON-біндер підставляв би default (false/0) для
    // будь-якого пропущеного ключа — застарілий клієнт, що не знає про поле
    // exhaust_fan_on (додане цим diff'ом), мовчки вимкнув би витяжку замість
    // того, щоб лишити її як є. Читаємо сирий JSON і для кожного пропущеного
    // ключа підставляємо останній відомий стан актуатора — той самий
    // has*-принцип часткового оновлення, що вже є у прошивки (MqttService.h
    // CommandData), лише реалізований тут через "відсутній ключ = не чіпати".
    [HttpPost]
    public async Task<IActionResult> Post([FromBody] JsonElement body, CancellationToken ct)
    {
        var latest = await _db.AiDecisions
            .OrderByDescending(d => d.Timestamp)
            .FirstOrDefaultAsync(ct);

        bool pumpOn = ReadBool(body, "pump_on", latest?.PumpOn ?? false);
        bool fanOn = ReadBool(body, "fan_on", latest?.FanOn ?? false);
        bool exhaustFanOn = ReadBool(body, "exhaust_fan_on", latest?.ExhaustFanOn ?? false);
        int lightBrightness = ReadInt(body, "light_brightness", latest?.LightBrightness ?? 0);
        int soilHeaterPower = ReadInt(body, "soil_heater_power", latest?.SoilHeaterPower ?? 0);
        int airHeaterPower = ReadInt(body, "air_heater_power", latest?.AirHeaterPower ?? 0);

        // Ті самі апаратні стелі, що застосовує RunLocalControlAsync до
        // AI/локальних рішень (SoilHeaterMaxPower/AirHeaterMaxPower) — ручний
        // override не мав жодного шляху, яким користувач/застарілий клієнт міг
        // би обійти апаратно ще не перевірену повну потужність нагрівача.
        soilHeaterPower = Math.Clamp(soilHeaterPower, 0, _agronomistOptions.SoilHeaterMaxPower);
        airHeaterPower = Math.Clamp(airHeaterPower, 0, _agronomistOptions.AirHeaterMaxPower);

        var command = new AiCommand(pumpOn, fanOn, exhaustFanOn, lightBrightness, soilHeaterPower, airHeaterPower);

        var record = new AiDecisionRecord
        {
            PumpOn = command.PumpOn,
            FanOn = command.FanOn,
            ExhaustFanOn = command.ExhaustFanOn,
            LightBrightness = command.LightBrightness,
            SoilHeaterPower = command.SoilHeaterPower,
            AirHeaterPower = command.AirHeaterPower,
            Source = "ManualOverride",
            PumpReason = "Стан встановлено вручну з екрана керування.",
            FanReason = "Стан встановлено вручну з екрана керування.",
            ExhaustFanReason = "Стан встановлено вручну з екрана керування.",
            LightReason = "Яскравість встановлено вручну з екрана керування.",
            SoilHeaterReason = "Потужність встановлено вручну з екрана керування.",
            AirHeaterReason = "Потужність встановлено вручну з екрана керування.",
            Reason = "Ручне керування із застосунку. Наступний цикл автоматики знову застосує правила.",
            PhotoDescription = string.Empty,
            PhotoFileName = null
        };

        _db.AiDecisions.Add(record);
        await _db.SaveChangesAsync(ct);

        await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, JsonSerializer.Serialize(command));
        await _hub.Clients.All.SendAsync("DecisionReceived", record, ct);

        return Ok(record);
    }

    private static bool ReadBool(JsonElement body, string key, bool fallback) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement body, string key, int fallback) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetInt32()
            : fallback;
}
