using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using SmartGreenhouse.Backend.Data;
using SmartGreenhouse.Backend.Models;

namespace SmartGreenhouse.Backend.Services;

public class MqttBackgroundService : BackgroundService, IMqttPublisher
{
    private readonly ILogger<MqttBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MqttOptions _options;
    private readonly IManagedMqttClient _mqttClient;

    public MqttBackgroundService(
        ILogger<MqttBackgroundService> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<MqttOptions> options)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _options = options.Value;

        var factory = new MqttFactory();
        _mqttClient = factory.CreateManagedMqttClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithClientId(_options.ClientId)
            .WithTcpServer(_options.Server, _options.Port);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            clientOptionsBuilder = clientOptionsBuilder.WithCredentials(_options.Username, _options.Password);
        }

        var managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
            .WithClientOptions(clientOptionsBuilder.Build())
            .Build();

        _mqttClient.ApplicationMessageReceivedAsync += HandleMessageReceivedAsync;

        // ВАЖЛИВО: підписуватись можна лише ПІСЛЯ StartAsync — виклик у зворотному
        // порядку (як було раніше) залишав підписку в дивному стані, через що
        // внутрішній reconnect-цикл ManagedMqttClient раз у раз "перепідтверджував"
        // її, і кожне єдине повідомлення з ESP32 в результаті оброблялось і
        // зберігалось у базу сотні разів поспіль (спостерігалось ~500x дублів на
        // один пакет телеметрії).
        await _mqttClient.StartAsync(managedOptions);
        await _mqttClient.SubscribeAsync(_options.Topic);

        _logger.LogInformation("MQTT client started, connecting to {Server}:{Port}, subscribed to {Topic}",
            _options.Server, _options.Port, _options.Topic);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    private async Task HandleMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        TelemetryMessage? telemetry;
        try
        {
            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment);
            telemetry = JsonSerializer.Deserialize<TelemetryMessage>(payload);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize telemetry payload");
            return;
        }

        if (telemetry is null)
        {
            return;
        }

        _logger.LogInformation(
            "Telemetry received: DeviceId={DeviceId} UptimeMs={UptimeMs} Temperature={TemperatureC} " +
            "Humidity={HumidityPct} Pressure={PressureHpa} Lux={Lux} SoilRaw={SoilRaw} SoilMoisturePct={SoilMoisturePct} " +
            "SoilTempC={SoilTempC}",
            telemetry.DeviceId, telemetry.UptimeMs,
            FormatOrNA(telemetry.TemperatureC, "C"),
            FormatOrNA(telemetry.HumidityPct, "%"),
            FormatOrNA(telemetry.PressureHpa, "hPa"),
            FormatOrNA(telemetry.Lux),
            telemetry.SoilRaw,
            FormatOrNA(telemetry.SoilMoisturePct, "%"),
            FormatOrNA(telemetry.SoilTempC, "C"));

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.Telemetries.Add(new TelemetryRecord
            {
                DeviceId = telemetry.DeviceId,
                UptimeMs = telemetry.UptimeMs,
                TemperatureC = telemetry.TemperatureC,
                HumidityPct = telemetry.HumidityPct,
                PressureHpa = telemetry.PressureHpa,
                Lux = telemetry.Lux,
                SoilRaw = telemetry.SoilRaw,
                SoilMoisturePct = telemetry.SoilMoisturePct,
                SoilTempC = telemetry.SoilTempC
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save telemetry record to the database");
        }
    }

    private static string FormatOrNA(double? value, string suffix = "") =>
        value.HasValue ? $"{value.Value:0.##}{suffix}" : "N/A";

    public async Task PublishAsync(string topic, string payload)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();

        await _mqttClient.EnqueueAsync(message);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _mqttClient.StopAsync();
        await base.StopAsync(cancellationToken);
    }
}
