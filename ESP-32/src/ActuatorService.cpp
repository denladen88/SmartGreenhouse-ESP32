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
}

void ActuatorService::update() {
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
  // Той самий патерн, що й у setPump(): таймер стартує лише на переході
  // OFF->ON, щоб повторні "fan_on" команди не відкладали аварійне вимкнення.
  if (on && !_fanOn) {
    _fanStartMs = millis();
  }
  _fanOn = on;
  digitalWrite(FAN_PIN, on ? HIGH : LOW);
}

void ActuatorService::setLight(uint8_t brightness) {
  _lightBrightness = brightness;
  ledcWrite(LED_PWM_CHANNEL, brightness);
}
