using Microsoft.AspNetCore.SignalR;

namespace SmartGreenhouse.Backend.Hubs;

// Лише прийом підключень — сервер сам розсилає події ("TelemetryReceived",
// "DecisionReceived") через IHubContext<TelemetryHub> з MqttBackgroundService
// та AiAgronomistService; клієнти нічого на хаб не викликають.
public class TelemetryHub : Hub
{
}
