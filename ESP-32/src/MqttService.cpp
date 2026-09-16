#include "MqttService.h"
#include <Arduino.h>
#include <ArduinoJson.h>
#include <cstring>
#include <math.h>
#include "Config.h"
#include "Secrets.h"

void (*MqttService::_commandHandler)(const CommandData&) = nullptr;

MqttService::MqttService()
  : _mqttClient(_wifiClient),
    _reconnectTimer(MQTT_RECONNECT_INTERVAL_MS) {}

void MqttService::begin() {
  _mqttClient.setServer(MQTT_BROKER_IP, MQTT_BROKER_PORT);
  _mqttClient.setCallback(handleMessage);
  // 512 замість дефолтних 256: топік + заголовок MQTT з'їдають ~25 Б, а повна
  // телеметрія (усі валідні поля) підбирається до ~230 Б — за замовчуванням
  // publish() міг мовчки повертати false і губити зразок без ретраю.
  _mqttClient.setBufferSize(512);
  // Стеля busy-wait CONNACK у PubSubClient::connect(): з 15 с (дефолт) до 2 с.
  // connect() блокуючий і викликається з loop(), тож ця стеля напряму обмежує,
  // наскільки reconnect до недоступного брокера підвішує failsafe-таймери.
  _mqttClient.setSocketTimeout(2);
  // Те саме обмеження, але для самого TCP-connect (крок ДО CONNACK): якщо
  // брокер недоступний і мовчки не відповідає (а не одразу відхиляє
  // з'єднання), сокет-connect усередині WiFiClient може висіти довше за
  // setSocketTimeout(2) вище — той обмежує лише очікування CONNACK ПІСЛЯ
  // встановленого з'єднання. setTimeout() тут обмежує сам connect().
  _wifiClient.setTimeout(2000);
  _mqttClient.setKeepAlive(15);
}

void MqttService::onCommand(void (*handler)(const CommandData&)) {
  _commandHandler = handler;
}

void MqttService::handleMessage(char* topic, uint8_t* payload, unsigned int length) {
  if (strcmp(topic, MQTT_COMMANDS_TOPIC) != 0) {
    return;
  }

  JsonDocument doc;
  DeserializationError err = deserializeJson(doc, payload, length);
  if (err) {
    Serial.printf("[MQTT] Помилка розбору команди: %s\n", err.c_str());
    return;
  }

  CommandData cmd;
  // has* — чи ключ реально присутній у JSON (merge-семантика в обробнику).
  cmd.hasPump = !doc["pump_on"].isNull();
  cmd.hasFan = !doc["fan_on"].isNull();
  cmd.hasExhaustFan = !doc["exhaust_fan_on"].isNull();
  cmd.hasLight = !doc["light_brightness"].isNull();
  cmd.hasSoilHeater = !doc["soil_heater_power"].isNull();
  cmd.hasAirHeater = !doc["air_heater_power"].isNull();
  cmd.pumpOn = doc["pump_on"] | false;
  cmd.fanOn = doc["fan_on"] | false;
  cmd.exhaustFanOn = doc["exhaust_fan_on"] | false;
  cmd.lightBrightness = doc["light_brightness"] | (uint8_t)0;
  cmd.soilHeaterPower = doc["soil_heater_power"] | (uint8_t)0;
  cmd.airHeaterPower = doc["air_heater_power"] | (uint8_t)0;

  Serial.printf("[MQTT] Команда: pump_on=%d, fan_on=%d, exhaust_fan_on=%d, light_brightness=%d, soil_heater_power=%d, air_heater_power=%d\n",
                cmd.pumpOn, cmd.fanOn, cmd.exhaustFanOn, cmd.lightBrightness, cmd.soilHeaterPower, cmd.airHeaterPower);

  if (_commandHandler) {
    _commandHandler(cmd);
  }
}

void MqttService::reconnect() {
  Serial.print("[MQTT] Підключення до брокера...");

  // Last-Will: брокер сам опублікує retained "offline" у MQTT_STATUS_TOPIC, якщо
  // зв'язок обірветься негрейсфул (пропав живлення/Wi-Fi). Без волі брокер вічно
  // тримав би retained "online" з попереднього конекту, і бекенд не бачив би,
  // що пристрій зник.
  bool ok;
#if defined(MQTT_USERNAME) && defined(MQTT_PASSWORD)
  ok = _mqttClient.connect(DEVICE_ID, MQTT_USERNAME, MQTT_PASSWORD,
                           MQTT_STATUS_TOPIC, 1, true, "offline");
#else
  ok = _mqttClient.connect(DEVICE_ID, nullptr, nullptr,
                           MQTT_STATUS_TOPIC, 1, true, "offline");
#endif

  if (ok) {
    Serial.println(" готово.");
    _mqttClient.publish(MQTT_STATUS_TOPIC, "online", true);
    _mqttClient.subscribe(MQTT_COMMANDS_TOPIC);
  } else {
    Serial.printf(" помилка (rc=%d).\n", _mqttClient.state());
  }
}

bool MqttService::update(bool wifiUp) {
  if (!_mqttClient.connected()) {
    // Без Wi-Fi сокет-connect усе одно провалиться, але блокуюче: не чіпаємо
    // брокер, поки мережа не піднялась (публікація в main.cpp так само
    // захищена тим самим wifiUp). wifiUp приходить від network.update() цього
    // ж проходу циклу — не питаємо WiFi.status() тут вдруге.
    if (wifiUp && _reconnectTimer.elapsed()) {
      reconnect();
    }
    return _mqttClient.connected();
  }
  _mqttClient.loop();
  return true;
}

bool MqttService::isConnected() {
  return _mqttClient.connected();
}

void MqttService::publishTelemetry(const SensorData& data) {
  if (!_mqttClient.connected()) {
    return;
  }

  JsonDocument doc;
  doc["device_id"] = DEVICE_ID;

  // 64-бітний uptime: сирий millis() переповнюється в 0 на ~49.7 діб, і бекенд
  // бачив би стрибок часу назад. Акумулюємо переповнення локально.
  static uint32_t lastMs = 0;
  static uint64_t baseMs = 0;
  uint32_t nowMs = millis();
  if (nowMs < lastMs) {
    baseMs += 0x100000000ULL;
  }
  lastMs = nowMs;
  doc["uptime_ms"] = baseMs + nowMs;

  // Округлення float-ів: ArduinoJson друкує найкоротший round-trip запис, тобто
  // за повної мантиси ~9 символів на число — саме це роздувало payload під межу
  // MQTT-буфера. Для агрономії 2 знаки (0.01 °C/%/hPa) і 1 знак для lux — з запасом.
  if (data.climateValid) {
    doc["temperature_c"] = roundf(data.temperatureC * 100.0f) / 100.0f;
    doc["humidity_pct"] = roundf(data.humidityPct * 100.0f) / 100.0f;
    doc["pressure_hpa"] = roundf(data.pressureHpa * 100.0f) / 100.0f;
  }
  if (data.lightValid) {
    doc["lux"] = roundf(data.lux * 10.0f) / 10.0f;
  }
  doc["soil_raw"] = data.soilRaw;
  doc["soil_moisture_pct"] = roundf(data.soilMoisturePct * 10.0f) / 10.0f;
  // false = медіана вибірки біля межі діапазону з великим розкидом (ймовірно,
  // від'єднаний/обірваний зонд), і "вологість" рахувати не можна. Схему не
  // ламаємо — soil_raw/soil_moisture_pct шлемо завжди, бекенд сам вирішує.
  doc["soil_valid"] = data.soilValid;
  if (data.soilTempValid) {
    doc["soil_temp_c"] = roundf(data.soilTempC * 100.0f) / 100.0f;
  }

  char payload[512];
  size_t len = serializeJson(doc, payload, sizeof(payload));

  if (_mqttClient.publish(MQTT_TELEMETRY_TOPIC, payload, len)) {
    Serial.printf("[MQTT] Опубліковано (%u байт): %s\n", (unsigned)len, payload);
  } else {
    Serial.println("[MQTT] Помилка публікації!");
  }
}
