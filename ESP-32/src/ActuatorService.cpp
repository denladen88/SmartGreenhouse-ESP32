#include "ActuatorService.h"
#include <Arduino.h>
#include "Config.h"

ActuatorService::ActuatorService() {}

void ActuatorService::begin() {
  pinMode(PUMP_RELAY_PIN, OUTPUT);
  pinMode(FAN_PIN, OUTPUT);
  digitalWrite(PUMP_RELAY_PIN, LOW);
  digitalWrite(FAN_PIN, LOW);

  ledcSetup(LED_PWM_CHANNEL, LED_PWM_FREQ_HZ, LED_PWM_RESOLUTION_BITS);
  ledcAttachPin(LIGHT_PIN, LED_PWM_CHANNEL);
  ledcWrite(LED_PWM_CHANNEL, 0); // одразу гасимо

  ledcSetup(SOIL_HEATER_PWM_CHANNEL, SOIL_HEATER_PWM_FREQ_HZ, SOIL_HEATER_PWM_RESOLUTION_BITS);
  ledcAttachPin(SOIL_HEATER_PIN, SOIL_HEATER_PWM_CHANNEL);
  ledcWrite(SOIL_HEATER_PWM_CHANNEL, 0); // одразу гасимо
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

  // Аварійне вимкнення: якщо помпа увімкнена довше PUMP_MAX_RUNTIME_MS,
  // примусово гасимо її незалежно від того, хто й чому її увімкнув.
  if (_pumpOn && (millis() - _pumpStartMs >= PUMP_MAX_RUNTIME_MS)) {
    Serial.printf("[ПОМПА] УВАГА: перевищено безпечний час роботи (%lu мс) — аварійне вимкнення!\n",
                  PUMP_MAX_RUNTIME_MS);
    setPump(false);
  }

  // Той самий захист для вентилятора (FAN_MAX_RUNTIME_MS) — див. коментар у Config.h.
  if (_fanOn && (millis() - _fanStartMs >= FAN_MAX_RUNTIME_MS)) {
    Serial.printf("[ВЕНТИЛЯТОР] УВАГА: перевищено безпечний час роботи (%lu мс) — аварійне вимкнення!\n",
                  FAN_MAX_RUNTIME_MS);
    setFan(false);
  }

  // Той самий захист для ґрунтового нагрівача (SOIL_HEATER_MAX_RUNTIME_MS) — див. Config.h.
  if (_soilHeaterPower > 0 && (millis() - _soilHeaterStartMs >= SOIL_HEATER_MAX_RUNTIME_MS)) {
    Serial.printf("[НАГРІВАЧ]  УВАГА: перевищено безпечний час роботи (%lu мс) — аварійне вимкнення!\n",
                  SOIL_HEATER_MAX_RUNTIME_MS);
    setSoilHeater(0);
  }
}

void ActuatorService::setPump(bool on) {
  // Таймер стартує лише на переході OFF->ON, а не на кожен повторний виклик
  // з on=true — інакше повторні/підтверджувальні MQTT-команди "pump_on"
  // безкінечно відкладали б аварійне вимкнення, зводячи нанівець весь сенс
  // захисного ліміту PUMP_MAX_RUNTIME_MS.
  if (on && !_pumpOn) {
    _pumpStartMs = millis();
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
  _fanOn = on;
  digitalWrite(FAN_PIN, on ? HIGH : LOW);
}

void ActuatorService::setLight(uint8_t brightness) {
  _lightBrightness = brightness;
  ledcWrite(LED_PWM_CHANNEL, brightness);
}

void ActuatorService::setSoilHeater(uint8_t power) {
  // Той самий принцип, що й у вентилятора: таймер оновлюється на кожну
  // команду "увімкнено" (не лише перехід off->on), бо очікується
  // безперервна робота з періодичним підтвердженням від бекенда.
  if (power > 0) {
    _soilHeaterStartMs = millis();
  }
  _soilHeaterPower = power;
  ledcWrite(SOIL_HEATER_PWM_CHANNEL, power);
}
