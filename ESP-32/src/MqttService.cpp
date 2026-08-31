#include "MqttService.h"
#include <Arduino.h>
#include <ArduinoJson.h>
#include <cstring>
#include "Config.h"
#include "Secrets.h"

void (*MqttService::_commandHandler)(const CommandData&) = nullptr;

MqttService::MqttService()
  : _mqttClient(_wifiClient),
    _reconnectTimer(MQTT_RECONNECT_INTERVAL_MS) {}

void MqttService::begin() {
  _mqttClient.setServer(MQTT_BROKER_IP, MQTT_BROKER_PORT);
  _mqttClient.setCallback(handleMessage);
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
  cmd.pumpOn = doc["pump_on"] | false;
  cmd.fanOn = doc["fan_on"] | false;
  cmd.lightBrightness = doc["light_brightness"] | (uint8_t)0;
  cmd.soilHeaterPower = doc["soil_heater_power"] | (uint8_t)0;
  cmd.airHeaterPower = doc["air_heater_power"] | (uint8_t)0;

  Serial.printf("[MQTT] Команда: pump_on=%d, fan_on=%d, light_brightness=%d, soil_heater_power=%d, air_heater_power=%d\n",
                cmd.pumpOn, cmd.fanOn, cmd.lightBrightness, cmd.soilHeaterPower, cmd.airHeaterPower);

  if (_commandHandler) {
    _commandHandler(cmd);
  }
}

void MqttService::reconnect() {
  Serial.print("[MQTT] Підключення до брокера...");

  bool ok;
#if defined(MQTT_USERNAME) && defined(MQTT_PASSWORD)
  ok = _mqttClient.connect(DEVICE_ID, MQTT_USERNAME, MQTT_PASSWORD);
#else
  ok = _mqttClient.connect(DEVICE_ID);
#endif

  if (ok) {
    Serial.println(" готово.");
    _mqttClient.publish(MQTT_STATUS_TOPIC, "online", true);
    _mqttClient.subscribe(MQTT_COMMANDS_TOPIC);
  } else {
    Serial.printf(" помилка (rc=%d).\n", _mqttClient.state());
  }
}

void MqttService::update() {
  if (!_mqttClient.connected()) {
    if (_reconnectTimer.elapsed()) {
      reconnect();
    }
    return;
  }
  _mqttClient.loop();
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
  doc["uptime_ms"] = millis();

  if (data.climateValid) {
    doc["temperature_c"] = data.temperatureC;
    doc["humidity_pct"] = data.humidityPct;
    doc["pressure_hpa"] = data.pressureHpa;
  }
  if (data.lightValid) {
    doc["lux"] = data.lux;
  }
  doc["soil_raw"] = data.soilRaw;
  doc["soil_moisture_pct"] = data.soilMoisturePct;
  if (data.soilTempValid) {
    doc["soil_temp_c"] = data.soilTempC;
  }

  char payload[256];
  size_t len = serializeJson(doc, payload, sizeof(payload));

  if (_mqttClient.publish(MQTT_TELEMETRY_TOPIC, payload, len)) {
    Serial.printf("[MQTT] Опубліковано (%u байт): %s\n", (unsigned)len, payload);
  } else {
    Serial.println("[MQTT] Помилка публікації!");
  }
}
