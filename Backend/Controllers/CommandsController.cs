using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
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
    private readonly IHubContext<TelemetryHub> _hub;

    public CommandsController(
        AppDbContext db,
        IMqttPublisher mqttPublisher,
        IOptions<MqttOptions> mqttOptions,
        IHubContext<TelemetryHub> hub)
    {
        _db = db;
        _mqttPublisher = mqttPublisher;
        _mqttOptions = mqttOptions.Value;
        _hub = hub;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] AiCommand command, CancellationToken ct)
    {
        var record = new AiDecisionRecord
        {
            PumpOn = command.PumpOn,
            FanOn = command.FanOn,
            LightBrightness = command.LightBrightness,
            SoilHeaterPower = command.SoilHeaterPower,
            Reason = "Manual override via mobile app",
            PhotoDescription = string.Empty,
            PhotoFileName = null
        };

        _db.AiDecisions.Add(record);
        await _db.SaveChangesAsync(ct);

        await _mqttPublisher.PublishAsync(_mqttOptions.CommandsTopic, System.Text.Json.JsonSerializer.Serialize(command));
        await _hub.Clients.All.SendAsync("DecisionReceived", record, ct);

        return Ok(record);
    }
}
