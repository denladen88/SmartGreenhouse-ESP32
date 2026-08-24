namespace SmartGreenhouse.Backend.Services;

public interface IMqttPublisher
{
    Task PublishAsync(string topic, string payload);
}
