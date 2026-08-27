#pragma once
#include <WiFiClient.h>
#include <PubSubClient.h>
#include "NonBlockingTimer.h"
#include "SensorService.h"

// Вхідна команда з MQTT_COMMANDS_TOPIC (наприклад, від .NET-бекенду).
struct CommandData {
  bool pumpOn = false;
  bool fanOn = false;
  uint8_t lightBrightness = 0;  // 0-255, повністю замінює автоматику по BH1750
  uint8_t soilHeaterPower = 0;  // 0-255, потужність ШІМ підігріву ґрунту
};

// Обгортка над PubSubClient: неблокуюче перепідключення, публікація
// телеметрії у форматі JSON, і підписка на вхідні команди актуаторів.
class MqttService {
public:
  MqttService();

  void begin();
  void update(); // викликати кожен цикл loop()

  bool isConnected();
  void publishTelemetry(const SensorData& data);

  // Реєструє обробник вхідних команд з MQTT_COMMANDS_TOPIC. Викликати до
  // begin(). PubSubClient вимагає звичайний вказівник на функцію (не
  // std::function), тож handler має бути вільною функцією або
  // лямбдою без захоплень.
  void onCommand(void (*handler)(const CommandData&));

private:
  void reconnect();

  // PubSubClient::setCallback() приймає лише вказівник на вільну/статичну
  // функцію (без this) — тому цей метод статичний, а не звичайний.
  // У проєкті існує рівно один екземпляр MqttService, тож глобальний
  // статичний стан тут безпечний.
  static void handleMessage(char* topic, uint8_t* payload, unsigned int length);
  static void (*_commandHandler)(const CommandData&);

  WiFiClient _wifiClient;
  PubSubClient _mqttClient;
  NonBlockingTimer _reconnectTimer;
};
