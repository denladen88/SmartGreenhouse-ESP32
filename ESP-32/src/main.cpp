#include <Arduino.h>
#include <ESPAsyncWebServer.h>
#include <WiFi.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <memory>
#include "esp_heap_caps.h"
#include "esp_system.h"
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

// Найважливіша діагностика "чому плата пішла в офлайн": якщо це не звичайний
// перезапуск (power-on), а PANIC/WDT/BROWNOUT — плата не просто втратила
// мережу, вона реально впала/перезавантажилась сама. Друкуємо одразу при
// старті, до begin() усіх сервісів, щоб не загубити рядок серед подальшого логу.
void logResetReason() {
  esp_reset_reason_t reason = esp_reset_reason();
  const char* text;
  switch (reason) {
    case ESP_RST_POWERON: text = "увімкнення живлення (нормальний старт)"; break;
    case ESP_RST_EXT: text = "зовнішній reset (кнопка/пін RESET)"; break;
    case ESP_RST_SW: text = "програмний reset (esp_restart())"; break;
    case ESP_RST_PANIC: text = "АВАРІЯ — прошивка впала (exception/panic)"; break;
    case ESP_RST_INT_WDT: text = "спрацював interrupt watchdog — щось блокувало переривання надто довго"; break;
    case ESP_RST_TASK_WDT: text = "спрацював task watchdog — loop() або задача зависла"; break;
    case ESP_RST_WDT: text = "спрацював інший watchdog"; break;
    case ESP_RST_DEEPSLEEP: text = "прокидання з deep sleep"; break;
    case ESP_RST_BROWNOUT: text = "BROWNOUT — просідання живлення (недостатньо струму від БЖ/USB, перевір проводку помпи/нагрівачів)"; break;
    case ESP_RST_SDIO: text = "reset через SDIO"; break;
    default: text = "невідома причина"; break;
  }
  Serial.printf("[BOOT] Причина рестарту: %s (код %d)\n", text, (int)reason);
  Serial.printf("[BOOT] Вільна internal-пам'ять: %u Б | PSRAM: %u Б\n",
                (unsigned)ESP.getFreeHeap(), (unsigned)ESP.getFreePsram());
}

void setup() {
  Serial.begin(115200);
  delay(2000);

  Serial.println("\n============================================");
  Serial.println("  SMART PLANT ESP32-S3: CONTROLLER STARTUP  ");
  Serial.println("============================================");
  logResetReason();

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
  // MQTT-reconnect до недоступного/повільного брокера) з'їв понад
  // LOOP_HANG_THRESHOLD_MS, ЖОДЕН failsafe-таймер в actuators.update() цей
  // час не працював — не лише помпин. Аварійно гасимо ВСІ актуатори негайно
  // (emergencyStopAll), а не тільки помпу: fan/light/обидва нагрівачі — той
  // самий millis()-таймер, що просто не встиг спрацювати вчасно. Бекенд
  // перекомандує актуальний стан наступним тіком. Ловить будь-яку причину
  // блокування loop(), не лише MQTT. Перша ітерація пропускається (lastLoopMs
  // ще 0 після довгого setup()). LOOP_HANG_THRESHOLD_MS (Config.h) навмисно
  // узгоджений із сумою MQTT_TCP_CONNECT_TIMEOUT_S+MQTT_CONNACK_TIMEOUT_S
  // (MqttService.cpp) — не піднімати один без перегляду іншого.
  static unsigned long lastLoopMs = 0;
  unsigned long nowMs = millis();
  unsigned long loopGap = nowMs - lastLoopMs;
  if (lastLoopMs != 0 && loopGap > LOOP_HANG_THRESHOLD_MS) {
    actuators.emergencyStopAll();
    Serial.printf("[SAFETY] loop() завис на %lu мс — усі актуатори аварійно вимкнено.\n", loopGap);
  }
  lastLoopMs = nowMs;

  bool wifiUp = network.update();
  sensors.update();  // неблокуюча вибірка ADC ґрунту + переперевіряння сенсорів
  bool mqttUp = mqtt.update(wifiUp);
  actuators.update(); // failsafe-перевірка кожного актуатора щоцикл, незалежно від таймерів

  // Стан актуатора важливіший за плановий 3-хвилинний інтервал сенсорів:
  // при фактичному on/off одразу шлемо телеметрію, щоб dashboard показував
  // безперервний runtime навіть для короткого імпульсу помпи.
  static uint32_t lastActuatorRevision = actuators.stateRevision();
  const uint32_t actuatorRevision = actuators.stateRevision();
  if (actuatorRevision != lastActuatorRevision) {
    lastActuatorRevision = actuatorRevision;
    mqttPublishTimer.expire();
  }

  // Перша телеметрія одразу після появи MQTT, а не через повний
  // MQTT_PUBLISH_INTERVAL_MS: на фронті "з'явився зв'язок" форсуємо тік таймера.
  static bool wasMqttUp = false;
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
    Serial.printf("[ҐРУНТ]    Raw ADC (GPIO%d): %d (медіана 5 хв; зараз %d) | Вологість: %.1f%% | valid=%d\n",
                  SOIL_ADC_PIN, lastSensorData.soilRaw, lastSensorData.soilRawCurrent,
                  lastSensorData.soilMoisturePct,
                  (int)lastSensorData.soilValid);
    if (lastSensorData.soilTempValid) {
      Serial.printf("[ҐРУНТ]    Температура: %.1f °C\n", lastSensorData.soilTempC);
    }

    // Тренд стабільності: падіння вільної пам'яті з часом (без відновлення)
    // означає витік (leak) і майбутній краш; слабкий/спадаючий RSSI —
    // ймовірну причину майбутнього WiFi-розриву (BEACON_TIMEOUT) ще до того,
    // як він станеться.
    static uint32_t minFreeHeap = UINT32_MAX;
    uint32_t freeHeap = ESP.getFreeHeap();
    if (freeHeap < minFreeHeap) {
      minFreeHeap = freeHeap;
    }
    Serial.printf("[СТАН]     Uptime: %lu с | RSSI: %s | Вільна пам'ять: %u Б (мін. за сеанс: %u Б)\n",
                  millis() / 1000,
                  wifiUp ? (String(WiFi.RSSI()) + " dBm").c_str() : "н/д (Wi-Fi відсутній)",
                  (unsigned)freeHeap, (unsigned)minFreeHeap);
  }

  if (mqttPublishTimer.elapsed()) {
    if (wifiUp) {
      mqtt.publishTelemetry(lastSensorData, actuators);
    } else {
      Serial.println("[MQTT] Пропуск публікації: немає Wi-Fi.");
    }
  }
}
