using System.Threading.Channels;

namespace SmartGreenhouse.Backend.Services;

// Однонаправлений сигнал "прийшла нова телеметрія" від MqttBackgroundService до
// локального контролера AiAgronomistService. Обидва — singleton, тож прямий
// виклик створив би цикл у DI (AiAgronomistService вже залежить від
// MqttBackgroundService через IMqttPublisher); цей клас розриває залежність.
//
// Канал обмежений одним елементом із FullMode=DropWrite: сплеск повідомлень
// "склеюється" в одне очікуване пробудження (немає сенсу переганяти контролер
// N разів поспіль на одному й тому ж стані БД), але одне вже надіслане
// сповіщення не губиться, поки читач зайнятий попереднім тіком.
public sealed class TelemetrySignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    // Викликається з callback-потоку MQTT-клієнта після збереження TelemetryRecord.
    public void Notify() => _channel.Writer.TryWrite(0);

    // Завершується, щойно є хоч одне непрочитане сповіщення.
    public ValueTask<byte> WaitAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
