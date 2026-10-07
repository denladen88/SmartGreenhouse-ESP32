#pragma once
#include <atomic>
#include <cstdint>
#include <functional>
#include "esp_timer.h"

// Керує помпою (реле), вентилятором циркуляції (реле), витяжкою (реле,
// незалежна), grow-світлом (один діод через LED-драйвер струму, ШІМ-сигнал
// димування) та нагрівачами ґрунту й повітря (окремі ШІМ-канали через
// MOSFET-модулі — див. Config.h).
// Вентилятор циркуляції і повітряний нагрівач — один фізичний блок
// (вентилятор+PTC-радіатор): вентилятор лишається увімкненим, поки нагрівач
// активний, незалежно від останньої явної команди setFan() — див.
// applyFanOutput(). Витяжка (setExhaustFan) ні з чим не пов'язана.
class ActuatorService {
public:
  ActuatorService();

  void begin();

  // Викликати щоцикл loop(): перевіряє захисні таймери помпи, вентилятора,
  // світла та нагрівачів ґрунту й повітря (PUMP_MAX_RUNTIME_MS,
  // FAN_MAX_RUNTIME_MS, LIGHT_MAX_RUNTIME_MS, SOIL_HEATER_MAX_RUNTIME_MS,
  // AIR_HEATER_MAX_RUNTIME_MS) і примусово вимикає їх при перевищенні.
  void update();

  void setPump(bool on);
  void setFan(bool on);               // явний запит на вентиляцію; фактичний пін — див. isFanOn()
  void setExhaustFan(bool on);        // витяжка; незалежна від setFan()/setAirHeater()
  void setLight(uint8_t brightness); // 0 = вимкнено; обрізається до LIGHT_MAX_BRIGHTNESS (25% ШІМ), а не 255
  void setSoilHeater(uint8_t power);  // 0 = вимкнено; обрізається до SOIL_HEATER_MAX_POWER
  void setAirHeater(uint8_t power);   // 0 = вимкнено; обрізається до AIR_HEATER_MAX_POWER; ненульова потужність тримає вентилятор увімкненим

  // Аварійне вимкнення ВСЬОГО одразу, незалежно від власних failsafe-таймерів
  // кожного актуатора — для випадків, коли loop() сам щойно завис на секунди
  // (детектор зависання в main.cpp) і немає гарантії, коли наступний штатний
  // update() встигне перевірити кожен таймер окремо. Бекенд перекомандує
  // актуальний стан наступним тіком.
  void emergencyStopAll();

  bool isPumpOn() const { return _pumpOn; }
  bool isFanOn() const { return _fanRequested || _airHeaterPower > 0; } // фактичний стан FAN_PIN, не лише останній setFan()
  bool isExhaustFanOn() const { return _exhaustFanOn; }
  bool isLightOn() const { return _lightBrightness > 0; }
  bool isSoilHeaterOn() const { return _soilHeaterPower > 0; }
  bool isAirHeaterOn() const { return _airHeaterPower > 0; }

  // Фактичний час БЕЗПЕРЕРВНОЇ роботи, а не час від останнього MQTT-
  // підтвердження. Нуль означає, що актуатор зараз вимкнений. esp_timer
  // використовується замість millis(), щоб лічильник не скидався кожні ~49 діб.
  uint64_t pumpRuntimeMs() const;
  uint64_t fanRuntimeMs() const;
  uint64_t exhaustFanRuntimeMs() const;
  uint64_t lightRuntimeMs() const;
  uint64_t soilHeaterRuntimeMs() const;
  uint64_t airHeaterRuntimeMs() const;

  // Змінюється тільки при фактичному переході on/off. main.cpp використовує
  // ревізію, щоб негайно опублікувати новий стан, не чекаючи планового тіку.
  uint32_t stateRevision() const { return _stateRevision.load(); }

private:
  void applyFanOutput(); // пише FAN_PIN = isFanOn(); викликати після зміни _fanRequested або _airHeaterPower
  void updateFanRuntime(bool wasOn);
  static uint64_t monotonicMs();
  static uint64_t continuousRuntimeMs(bool active, uint64_t startMs);

  // Незалежне від loop() апаратне вимкнення помпи через PUMP_RUN_DURATION_MS.
  // Причина: MqttService::reconnect() — блокуючий виклик з loop(), і його
  // найгірший випадок (TCP-connect + очікування CONNACK) може тривати довше,
  // ніж PUMP_MAX_RUNTIME_MS (5с) — на цей час checkFailsafe() у update()
  // просто не встигає викликатись. esp_timer виконує callback у власній
  // FreeRTOS-задачі (esp_timer task), незалежній від задачі loop(), тож помпа
  // гарантовано вимкнеться вчасно навіть якщо loop() застряг у мережевому
  // блокуючому виклику. update()/checkFailsafe() лишається як другий,
  // резервний рівень захисту (на випадок, якщо сам таймер не зміг стартувати).
  static void pumpFailsafeCallback(void* arg);
  esp_timer_handle_t _pumpFailsafeTimer = nullptr;

  // Спільна форма для 6 практично ідентичних перевірок у update() (лише поле
  // active, таймер startMs, ліміт maxRuntimeMs, лейбл логу й "вимикач"
  // різняться) — щоб додавання нового актуатора не означало копіювати ще один
  // 5-рядковий блок і ризикувати одруком у назві таймера/константи.
  void checkFailsafe(bool active, unsigned long startMs, unsigned long maxRuntimeMs,
                      const char* label, const std::function<void()>& off);

  // Спільна форма для setLight/setSoilHeater/setAirHeater: обрізати до
  // апаратної стелі, освіжити failsafe-таймер на ненульовому запиті, записати
  // ШІМ. stateField/startMs — посилання на конкретне приватне поле
  // (_lightBrightness/_lightStartMs тощо) цього актуатора.
  void applyClampedPwm(uint8_t requested, uint8_t maxValue, int pwmChannel,
                        unsigned long& confirmationMs, uint64_t& continuousStartMs,
                        uint8_t& stateField, const char* label);

  std::atomic<bool> _pumpOn{false}; // читається/пишеться і з loop() (задача Arduino), і з callback'у _pumpFailsafeTimer (окрема FreeRTOS-задача esp_timer)
  std::atomic<uint32_t> _stateRevision{0};
  bool _fanRequested = false; // останній явний setFan(); НЕ обов'язково фактичний стан піна — див. isFanOn()
  bool _exhaustFanOn = false;
  uint8_t _lightBrightness = 0;
  uint8_t _soilHeaterPower = 0;
  uint8_t _airHeaterPower = 0;

  unsigned long _pumpStartMs = 0;        // millis() моменту останнього вмикання помпи
  unsigned long _fanStartMs = 0;         // millis() моменту останнього явного запиту вентиляції
  unsigned long _exhaustFanStartMs = 0;  // millis() моменту останнього підтвердження витяжки
  unsigned long _lightStartMs = 0;       // millis() моменту останнього підтвердження світла
  unsigned long _soilHeaterStartMs = 0;  // millis() моменту останнього підтвердження нагрівача ґрунту
  unsigned long _airHeaterStartMs = 0;   // millis() моменту останнього підтвердження нагрівача повітря

  uint64_t _pumpContinuousStartMs = 0;
  uint64_t _fanContinuousStartMs = 0;
  uint64_t _exhaustFanContinuousStartMs = 0;
  uint64_t _lightContinuousStartMs = 0;
  uint64_t _soilHeaterContinuousStartMs = 0;
  uint64_t _airHeaterContinuousStartMs = 0;
};
