#include "ActuatorService.h"
#include <Arduino.h>
#include "Config.h"

ActuatorService::ActuatorService() {}

void ActuatorService::pumpFailsafeCallback(void* arg) {
  // Виконується у власній задачі esp_timer, НЕ в задачі loop() — саме тому
  // це надійно спрацює, навіть якщо loop() зараз застряг у блокуючому
  // MqttService::reconnect(). Пишемо пін і атомарний прапорець напряму, без
  // виклику setPump(false) (щоб не чіпати _pumpStartMs з чужої задачі).
  digitalWrite(PUMP_RELAY_PIN, LOW);
  static_cast<ActuatorService*>(arg)->_pumpOn = false;
}

void ActuatorService::begin() {
  const esp_timer_create_args_t pumpTimerArgs = {
      .callback = &ActuatorService::pumpFailsafeCallback,
      .arg = this,
      .dispatch_method = ESP_TIMER_TASK,
      .name = "pump_failsafe",
  };
  if (esp_timer_create(&pumpTimerArgs, &_pumpFailsafeTimer) != ESP_OK) {
    Serial.println("[ПОМПА] УВАГА: не вдалось створити апаратний таймер захисту — лишається лише резервний перевірка в update().");
    _pumpFailsafeTimer = nullptr;
  }

  pinMode(PUMP_RELAY_PIN, OUTPUT);
  pinMode(FAN_PIN, OUTPUT);
  pinMode(EXHAUST_FAN_PIN, OUTPUT);
  digitalWrite(PUMP_RELAY_PIN, LOW);
  digitalWrite(FAN_PIN, LOW);
  digitalWrite(EXHAUST_FAN_PIN, LOW);

  ledcSetup(LED_PWM_CHANNEL, LED_PWM_FREQ_HZ, LED_PWM_RESOLUTION_BITS);
  ledcAttachPin(LIGHT_PIN, LED_PWM_CHANNEL);
  ledcWrite(LED_PWM_CHANNEL, 0); // одразу гасимо

  ledcSetup(SOIL_HEATER_PWM_CHANNEL, SOIL_HEATER_PWM_FREQ_HZ, SOIL_HEATER_PWM_RESOLUTION_BITS);
  ledcAttachPin(SOIL_HEATER_PIN, SOIL_HEATER_PWM_CHANNEL);
  ledcWrite(SOIL_HEATER_PWM_CHANNEL, 0); // одразу гасимо

  ledcSetup(AIR_HEATER_PWM_CHANNEL, AIR_HEATER_PWM_FREQ_HZ, AIR_HEATER_PWM_RESOLUTION_BITS);
  ledcAttachPin(AIR_HEATER_PIN, AIR_HEATER_PWM_CHANNEL);
  ledcWrite(AIR_HEATER_PWM_CHANNEL, 0); // одразу гасимо
}

void ActuatorService::update() {
  // Штатне вимкнення: кожне ввімкнення помпи — фіксований імпульс
  // PUMP_RUN_DURATION_MS (один "постріл" поливу). _pumpStartMs виставляється в
  // setPump() на переході OFF->ON, тож повторні pump_on під час роботи імпульс
  // не подовжують.
  if (_pumpOn && (millis() - _pumpStartMs >= PUMP_RUN_DURATION_MS)) {
    Serial.printf("[ПОМПА] Імпульс поливу завершено (%lu мс).\n", PUMP_RUN_DURATION_MS);
    setPump(false);
  }

  // Той самий захист для помпи (аварійне вимкнення понад PUMP_MAX_RUNTIME_MS,
  // незалежно від того, хто й чому її увімкнув), вентилятора, витяжки, світла й обох
  // нагрівачів (*_MAX_RUNTIME_MS — див. Config.h), лише через спільний
  // checkFailsafe() замість шести окремо виписаних копій.
  checkFailsafe(_pumpOn, _pumpStartMs, PUMP_MAX_RUNTIME_MS, "ПОМПА", [this] { setPump(false); });
  checkFailsafe(_fanRequested, _fanStartMs, FAN_MAX_RUNTIME_MS, "ВЕНТИЛЯТОР", [this] { setFan(false); });
  checkFailsafe(_exhaustFanOn, _exhaustFanStartMs, EXHAUST_FAN_MAX_RUNTIME_MS, "ВИТЯЖКА", [this] { setExhaustFan(false); });
  checkFailsafe(_lightBrightness > 0, _lightStartMs, LIGHT_MAX_RUNTIME_MS, "СВІТЛО", [this] { setLight(0); });
  checkFailsafe(_soilHeaterPower > 0, _soilHeaterStartMs, SOIL_HEATER_MAX_RUNTIME_MS, "НАГРІВАЧ", [this] { setSoilHeater(0); });
  checkFailsafe(_airHeaterPower > 0, _airHeaterStartMs, AIR_HEATER_MAX_RUNTIME_MS, "НАГРІВАЧ ПОВІТРЯ", [this] { setAirHeater(0); });
}

void ActuatorService::checkFailsafe(bool active, unsigned long startMs, unsigned long maxRuntimeMs,
                                     const char* label, const std::function<void()>& off) {
  if (active && (millis() - startMs >= maxRuntimeMs)) {
    Serial.printf("[%s] УВАГА: перевищено безпечний час роботи (%lu мс) — аварійне вимкнення!\n", label, maxRuntimeMs);
    off();
  }
}

void ActuatorService::emergencyStopAll() {
  setPump(false);
  setFan(false);
  setExhaustFan(false);
  setLight(0);
  setSoilHeater(0);
  setAirHeater(0);
}

void ActuatorService::setPump(bool on) {
  // Таймер стартує лише на переході OFF->ON, а не на кожен повторний виклик
  // з on=true — інакше повторні/підтверджувальні MQTT-команди "pump_on"
  // безкінечно відкладали б аварійне вимкнення, зводячи нанівець весь сенс
  // захисного ліміту PUMP_MAX_RUNTIME_MS.
  if (on && !_pumpOn) {
    _pumpStartMs = millis();
    // Апаратний таймер — головний захист (спрацює навіть якщо loop() застряг
    // довше PUMP_RUN_DURATION_MS у блокуючому MQTT-reconnect, див. коментар
    // біля _pumpFailsafeTimer в ActuatorService.h); checkFailsafe() у
    // update() лишається резервним другим рівнем.
    if (_pumpFailsafeTimer) {
      esp_timer_start_once(_pumpFailsafeTimer, (int64_t)PUMP_RUN_DURATION_MS * 1000);
    }
  } else if (!on && _pumpFailsafeTimer) {
    // Вимикаємо явно (штатний postrél завершився в update(), або emergencyStopAll) —
    // скасовуємо ще не спрацьований таймер, щоб він не "вимкнув" вже вимкнену
    // помпу на наступному циклі ввімкнення.
    esp_timer_stop(_pumpFailsafeTimer);
  }
  _pumpOn = on;
  digitalWrite(PUMP_RELAY_PIN, on ? HIGH : LOW);
}

void ActuatorService::setFan(bool on) {
  // На відміну від помпи (короткий "постріл" на цикл рішення, де повторне
  // підтвердження мало б безкінечно відкладати вимкнення), вентилятор
  // задумано як безперервну роботу, поки умова тримається — локальний
  // контролер на бекенді підтверджує рішення щотіку (кожні ~10 хв),
  // незалежно від того, змінилось воно чи ні. Тож тут таймер навмисно
  // оновлюється на КОЖНУ команду "on", а не лише на перехід OFF->ON:
  // FAN_MAX_RUNTIME_MS стає не "макс. безперервна робота", а "макс. час
  // БЕЗ підтвердження від бекенда" — вентилятор гаситься, лише якщо бекенд
  // реально замовк і не надіслав жодної команди довше цього ліміту.
  if (on) {
    _fanStartMs = millis();
  }
  _fanRequested = on;
  applyFanOutput();
}

void ActuatorService::setExhaustFan(bool on) {
  // Та сама Pattern B, що й у вентилятора циркуляції: безперервна робота,
  // поки бекенд підтверджує рішення щотіку — таймер оновлюється на кожен
  // виклик з on=true, не лише на переході OFF->ON. На відміну від setFan(),
  // тут немає жодного зв'язку з іншими актуаторами — просте незалежне реле.
  if (on) {
    _exhaustFanStartMs = millis();
  }
  _exhaustFanOn = on;
  digitalWrite(EXHAUST_FAN_PIN, on ? HIGH : LOW);
}

void ActuatorService::setLight(uint8_t brightness) {
  applyClampedPwm(brightness, LIGHT_MAX_BRIGHTNESS, LED_PWM_CHANNEL, _lightStartMs, _lightBrightness, "СВІТЛО");
}

void ActuatorService::setSoilHeater(uint8_t power) {
  applyClampedPwm(power, SOIL_HEATER_MAX_POWER, SOIL_HEATER_PWM_CHANNEL, _soilHeaterStartMs, _soilHeaterPower, "ҐРУНТ. НАГРІВАЧ");
}

void ActuatorService::setAirHeater(uint8_t power) {
  applyClampedPwm(power, AIR_HEATER_MAX_POWER, AIR_HEATER_PWM_CHANNEL, _airHeaterStartMs, _airHeaterPower, "ПОВІТР. НАГРІВАЧ");
  // Обдув без вентилятора не має сенсу — тепло застоюється біля елемента,
  // датчик його не бачить. Вентилятор і нагрівач — один фізичний блок.
  applyFanOutput();
}

void ActuatorService::applyClampedPwm(uint8_t requested, uint8_t maxValue, int pwmChannel,
                                       unsigned long& startMs, uint8_t& stateField, const char* label) {
  // Апаратний захист (LIGHT_MAX_BRIGHTNESS/SOIL_HEATER_MAX_POWER/AIR_HEATER_MAX_POWER
  // у Config.h) — не довіряємо, що бекенд чи ручний override завжди пришле
  // безпечне значення.
  if (requested > maxValue) {
    Serial.printf("[%s] Запит %d обрізано до безпечного максимуму %d.\n", label, requested, maxValue);
    requested = maxValue;
  }
  // Той самий принцип, що й у вентилятора/витяжки: таймер оновлюється на
  // кожну команду "увімкнено" (не лише перехід off->on), бо очікується
  // безперервна робота з періодичним підтвердженням від бекенда.
  if (requested > 0) {
    startMs = millis();
  }
  stateField = requested;
  ledcWrite(pwmChannel, requested);
}

void ActuatorService::applyFanOutput() {
  digitalWrite(FAN_PIN, isFanOn() ? HIGH : LOW);
}
