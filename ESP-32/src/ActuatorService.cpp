#include "ActuatorService.h"
#include <Arduino.h>
#include "Config.h"

namespace {
// Adafruit_NeoPixel вміє пакувати через свій зручний API (fill()/Color())
// лише 3 (RGB) або 4 (RGBW) байти на "піксель" — наша стрічка фізично має
// 5 байтів на піксель (R,G,B,Cold White,Warm White), чого бібліотека
// напряму не підтримує. Обхідний шлях: виділяємо буфер у форматі RGBW
// (4 "віртуальних" байти/піксель) з запасом, достатнім щоб вмістити
// NUM_LEDS*5 реальних байтів, і пишемо в нього напряму через getPixels(),
// повністю ігноруючи межі "віртуальних" пікселів — show() лише передає
// весь буфер байт-за-байтом по протоколу, форматування має значення тільки
// для setPixelColor()/Color()/fill().
constexpr int BYTES_PER_PHYSICAL_LED = 5;
constexpr uint16_t VIRTUAL_PIXEL_COUNT = (NUM_LEDS * BYTES_PER_PHYSICAL_LED + 3) / 4;
}  // namespace

ActuatorService::ActuatorService()
  // НЕПІДТВЕРДЖЕНО: точна модель чипа стрічки невідома (немає маркування/
  // даташиту). Припускаємо WS2812-сумісний біт-таймінг (800кГц) — поширений
  // серед одно-провідних адресованих ІС, але не гарантований для 24V
  // 5-канальних RGB+TW чипів. Порядок байтів R,G,B,Cold,Warm у setLight()
  // теж здогадка. Якщо нічого не засвітиться чи кольори вийдуть неправильні —
  // це перші дві речі, які варто підозрювати й пробувати міняти.
  : _strip(VIRTUAL_PIXEL_COUNT, LIGHT_PIN, NEO_GRBW + NEO_KHZ800) {}

void ActuatorService::begin() {
  pinMode(PUMP_RELAY_PIN, OUTPUT);
  pinMode(FAN_PIN, OUTPUT);
  digitalWrite(PUMP_RELAY_PIN, LOW);
  digitalWrite(FAN_PIN, LOW);

  _strip.begin();
  _strip.show(); // одразу гасимо всі пікселі
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

void ActuatorService::setLight(bool on) {
  _lightOn = on;

  if (on) {
    uint8_t* buf = _strip.getPixels();
    for (int led = 0; led < NUM_LEDS; led++) {
      uint8_t* px = buf + led * BYTES_PER_PHYSICAL_LED;
      px[0] = 255; // R
      px[1] = 255; // G
      px[2] = 255; // B
      px[3] = 255; // Cold White
      px[4] = 255; // Warm White
    }
  } else {
    _strip.clear(); // просто зануляє весь буфер — коректно незалежно від формату
  }
  _strip.show();
}
