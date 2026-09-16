#include <Arduino.h>
#include <ESPAsyncWebServer.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include "esp_heap_caps.h"
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

// Оновлюється з даних BH1750 раз на цикл читання сенсорів (SENSOR_READ_INTERVAL_MS).
// Використовується в обробнику /capture (не віддавати фото бекенду вночі — все
// одно чорний кадр). Якщо сенсор освітленості недоступний, лишаємо попереднє
// значення — не блокуємо камеру назавжди через збій BH1750. atomic: пишеться в
// задачі loop(), читається в задачі async_tcp (обробник /capture).
std::atomic<bool> isNight{false};

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
    //
    // Merge-семантика: застосовуємо лише поля, реально присутні в JSON
    // (cmd.hasX). Бекенд (Backend/Models/AiCommand.cs) завжди шле всі 5, тож
    // для нього поведінка не змінилась; ручна часткова команда (напр. лише
    // {"pump_on":true}) більше не занулює решту актуаторів.
    if (cmd.hasPump) {
      actuators.setPump(cmd.pumpOn);
    }
    // Вентилятор і повітряний нагрівач — окремі керуючі сигнали одного
    // фізичного блоку "вентилятор+PTC-радіатор": достатньо передати fan_on
    // як є, ActuatorService сам гарантує, що вентилятор лишається увімкненим,
    // поки нагрівач активний (див. ActuatorService::setAirHeater/isFanOn) —
    // незалежно від джерела команди (локальний контролер чи ручне керування
    // із застосунку).
    if (cmd.hasFan) {
      actuators.setFan(cmd.fanOn);
    }
    if (cmd.hasExhaustFan) {
      actuators.setExhaustFan(cmd.exhaustFanOn);
    }
    if (cmd.hasLight) {
      actuators.setLight(cmd.lightBrightness);
    }
    if (cmd.hasSoilHeater) {
      actuators.setSoilHeater(cmd.soilHeaterPower);
    }
    if (cmd.hasAirHeater) {
      actuators.setAirHeater(cmd.airHeaterPower);
    }
  });
  mqtt.begin();

  server.on("/capture", HTTP_GET, [](AsyncWebServerRequest* request) {
    if (isNight) {
      request->send(204); // ніч — фото немає (без тіла відповіді)
      return;
    }

    if (!camera.isReady()) {
      camera.retryIfDown(); // раз на 30с пробує переініціалізацію
      request->send(503, "text/plain", "Camera not ready");
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
    //
    // Копія — у PSRAM (heap_caps_malloc), не в internal heap: JPEG важить
    // сотні КБ, а під час роботи Wi-Fi internal heap тісний. new[] без
    // перевірки на невдачу давав би memcpy у nullptr / abort().
    uint8_t* raw = static_cast<uint8_t*>(heap_caps_malloc(len, MALLOC_CAP_SPIRAM));
    if (raw == nullptr) {
      camera.releaseFrame();
      request->send(503, "text/plain", "Out of memory");
      return;
    }
    std::shared_ptr<uint8_t> copy(raw, heap_caps_free);
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

  // Перше читання сенсорів — у першій же ітерації loop(), а не через повний
  // SENSOR_READ_INTERVAL_MS (лічильник NonBlockingTimer інакше стартує з 0).
  sensorReadTimer.expire();
}

void loop() {
  // Детектор зависання: якщо якийсь виклик нижче (найімовірніше — блокуючий
  // MQTT-reconnect до недоступного брокера) з'їв понад ~3 с, failsafe-таймери
  // помпи в actuators.update() цей час не працювали. Аварійно гасимо помпу
  // негайно — бекенд перекомандує наступним тіком. Ловить будь-яку причину
  // блокування loop(), не лише MQTT. Перша ітерація пропускається (lastLoopMs
  // ще 0 після довгого setup()).
  static unsigned long lastLoopMs = 0;
  unsigned long nowMs = millis();
  unsigned long loopGap = nowMs - lastLoopMs;
  if (lastLoopMs != 0 && loopGap > 3000 && actuators.isPumpOn()) {
    actuators.setPump(false);
    Serial.printf("[SAFETY] loop() завис на %lu мс — помпу аварійно вимкнено.\n", loopGap);
  }
  lastLoopMs = nowMs;

  network.update();
  sensors.update();  // неблокуюча вибірка ADC ґрунту + переперевіряння сенсорів
  mqtt.update();
  actuators.update(); // failsafe-перевірка помпи щоцикл, незалежно від таймерів

  // Перша телеметрія одразу після появи MQTT, а не через повний
  // MQTT_PUBLISH_INTERVAL_MS: на фронті "з'явився зв'язок" форсуємо тік таймера.
  static bool wasMqttUp = false;
  bool mqttUp = mqtt.isConnected();
  if (mqttUp && !wasMqttUp) {
    mqttPublishTimer.expire();
  }
  wasMqttUp = mqttUp;

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
    Serial.printf("[ҐРУНТ]    Raw ADC (GPIO%d): %d | Вологість: %.1f%% | valid=%d\n",
                  SOIL_ADC_PIN, lastSensorData.soilRaw, lastSensorData.soilMoisturePct,
                  (int)lastSensorData.soilValid);
    if (lastSensorData.soilTempValid) {
      Serial.printf("[ҐРУНТ]    Температура: %.1f °C\n", lastSensorData.soilTempC);
    }
  }

  if (mqttPublishTimer.elapsed()) {
    if (network.isConnected()) {
      mqtt.publishTelemetry(lastSensorData);
    } else {
      Serial.println("[MQTT] Пропуск публікації: немає Wi-Fi.");
    }
  }
}
