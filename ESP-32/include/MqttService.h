#pragma once
#include <WiFiClient.h>
#include <PubSubClient.h>
#include "ActuatorService.h"
#include "NonBlockingTimer.h"
#include "SensorService.h"

// Вхідна команда з MQTT_COMMANDS_TOPIC (наприклад, від .NET-бекенду).
// Прапорці has* кажуть, чи поле реально було в JSON: обробник застосовує лише
// присутні поля (merge-семантика), тож часткова команда з одним ключем більше
// не занулює решту актуаторів. Бекенд (Backend/Models/AiCommand.cs) завжди шле
// всі 6 полів, тож для нього поведінка не змінюється.
struct CommandData {
  bool pumpOn = false;
  bool fanOn = false;
  bool exhaustFanOn = false;
  uint8_t lightBrightness = 0;  // 0-255, повністю замінює автоматику по BH1750
  uint8_t soilHeaterPower = 0;  // 0-255, потужність ШІМ підігріву ґрунту
  uint8_t airHeaterPower = 0;   // 0-255, потужність ШІМ підігріву повітря

  bool hasPump = false;
  bool hasFan = false;
  bool hasExhaustFan = false;
  bool hasLight = false;
  bool hasSoilHeater = false;
  bool hasAirHeater = false;
};

// Обгортка над PubSubClient: неблокуюче перепідключення, публікація
// телеметрії у форматі JSON, і підписка на вхідні команди актуаторів.
class MqttService {
public:
  MqttService();

  void begin();
  // Викликати кожен цикл loop(); wifiUp — результат network.update() ЦЬОГО Ж
  // проходу циклу, щоб не питати WiFi.status() вдруге за той самий стан.
  // Повертає isConnected() одразу після оновлення (той самий стан, що й
  // isConnected(), але без другого виклику _mqttClient.connected() у loop()).
  bool update(bool wifiUp);

  bool isConnected();
  void publishTelemetry(const SensorData& data, const ActuatorService& actuators);

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

  // Для діагностики розриву: чи були підключені на попередньому update(), і
  // коли підключились востаннє — щоб при розриві залогувати, скільки часу
  // з'єднання протрималось, а не лише сам факт розриву.
  bool _wasConnected = false;
  unsigned long _connectedSinceMs = 0;
};
