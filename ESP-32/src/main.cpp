#include <Arduino.h>
#include <ESPAsyncWebServer.h>
#include <algorithm>
#include <cstring>
#include <memory>
#include "Config.h"
#include "NonBlockingTimer.h"
#include "SensorService.h"
#include "CameraService.h"
#include "ActuatorService.h"
#include "NetworkService.h"
#include "MqttService.h"

SensorService sensors;
CameraService camera;
ActuatorService actuators;
NetworkService network;
MqttService mqtt;
AsyncWebServer server(80);

NonBlockingTimer sensorReadTimer(SENSOR_READ_INTERVAL_MS);
NonBlockingTimer mqttPublishTimer(MQTT_PUBLISH_INTERVAL_MS);

// Оновлюється з даних BH1750 щоцикл читання сенсорів; використовується і в
// loop() (пропустити діагностичний кадр), і в обробнику /capture (не
// віддавати фото бекенду вночі). Якщо сенсор освітленості недоступний,
// лишаємо попереднє значення — не блокуємо камеру назавжди через збій BH1750.
bool isNight = false;

// Останнє зчитане показання сенсорів — читаємо й логуємо частіше
// (SENSOR_READ_INTERVAL_MS), ніж публікуємо в MQTT (MQTT_PUBLISH_INTERVAL_MS),
// тож публікація бере останній збережений результат, а не читає повторно.
SensorData lastSensorData;

void setup() {
  Serial.begin(115200);
  delay(2000);

  Serial.println("\n============================================");
  Serial.println("  SMART PLANT ESP32-S3: CONTROLLER STARTUP  ");
  Serial.println("============================================");

  sensors.begin();

  if (camera.begin()) {
    Serial.println("[CAM] Камера готова.");
  } else {
    Serial.println("[CAM] Помилка ініціалізації камери!");
  }

  actuators.begin();

  network.begin();

  mqtt.onCommand([](const CommandData& cmd) {
    // ActuatorService::setPump()/setFan() і так мають незалежні failsafe-
    // таймери (перевіряються в actuators.update() щоцикл loop()) — MQTT-
    // команда просто вмикає/вимикає той самий метод, що й уся інша логіка,
    // тож захист спрацює автоматично незалежно від того, чи прийде ще
    // якась команда з мережі.
    actuators.setPump(cmd.pumpOn);
    actuators.setFan(cmd.fanOn);
    actuators.setLight(cmd.lightBrightness);
  });
  mqtt.begin();

  server.on("/capture", HTTP_GET, [](AsyncWebServerRequest* request) {
    if (isNight) {
      request->send(204); // ніч — фото немає (без тіла відповіді)
      return;
    }

    const uint8_t* buf;
    size_t len;
    if (!camera.captureJpeg(&buf, &len)) {
      request->send(500, "text/plain", "Camera capture failed");
      return;
    }

    // Копіюємо кадр в окрему пам'ять і одразу звільняємо буфер камери:
    // асинхронна відправка може тривати кілька циклів loop() вже після
    // виходу з цього лямбда-обробника, а camera_fb_t не можна тримати
    // зайнятим весь цей час (заблокує наступний захват кадру). shared_ptr,
    // захоплений колбеком нижче, звільнить копію сам, коли ESPAsyncWebServer
    // реально завершить передачу — незалежно від того, скільки це триватиме.
    std::shared_ptr<uint8_t[]> copy(new uint8_t[len]);
    memcpy(copy.get(), buf, len);
    camera.releaseFrame();

    AsyncWebServerResponse* response = request->beginResponse(
        "image/jpeg", len,
        [copy, len](uint8_t* dest, size_t maxLen, size_t index) -> size_t {
          if (index >= len) {
            return 0;
          }
          size_t chunk = std::min(maxLen, len - index);
          memcpy(dest, copy.get() + index, chunk);
          return chunk;
        });
    request->send(response);
  });
  server.begin();
  Serial.println("[WEB] HTTP-сервер запущено на порту 80 (/capture).");
}

void loop() {
  network.update();
  mqtt.update();
  actuators.update(); // failsafe-перевірка помпи щоцикл, незалежно від таймерів

  if (sensorReadTimer.elapsed()) {
    lastSensorData = sensors.read();

    Serial.println("\n--- [SENSOR READ] ---");
    if (lastSensorData.climateValid) {
      Serial.printf("[КЛІМАТ]  Темп: %.2f °C | Вологість: %.2f %% | Тиск: %.2f hPa\n",
                    lastSensorData.temperatureC, lastSensorData.humidityPct, lastSensorData.pressureHpa);
    }
    if (lastSensorData.lightValid) {
      Serial.printf("[СВІТЛО]   Освітленість: %.1f Lux\n", lastSensorData.lux);
      isNight = lastSensorData.lux < NIGHT_LUX_THRESHOLD;
    }
    Serial.printf("[ҐРУНТ]    Raw ADC (GPIO%d): %d | Вологість: %.1f%%\n",
                  SOIL_ADC_PIN, lastSensorData.soilRaw, lastSensorData.soilMoisturePct);
  }

  if (mqttPublishTimer.elapsed()) {
    if (isNight) {
      Serial.println("[КАМЕРА]   Ніч — кадр не знімається.");
    } else {
      int frameLen = camera.captureFrameSize();
      if (frameLen >= 0) {
        Serial.printf("[КАМЕРА]   Кадр OK (%d байт)\n", frameLen);
      } else {
        Serial.println("[КАМЕРА]   Помилка захоплення!");
      }
    }

    if (network.isConnected()) {
      mqtt.publishTelemetry(lastSensorData);
    } else {
      Serial.println("[MQTT] Пропуск публікації: немає Wi-Fi.");
    }
  }
}
