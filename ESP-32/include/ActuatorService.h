#pragma once
#include <cstdint>

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

  // Викликати щоцикл loop(): перевіряє захисні таймери помпи, вентилятора та
  // нагрівачів ґрунту й повітря (PUMP_MAX_RUNTIME_MS, FAN_MAX_RUNTIME_MS,
  // SOIL_HEATER_MAX_RUNTIME_MS, AIR_HEATER_MAX_RUNTIME_MS) і примусово вимикає
  // їх при перевищенні.
  void update();

  void setPump(bool on);
  void setFan(bool on);               // явний запит на вентиляцію; фактичний пін — див. isFanOn()
  void setExhaustFan(bool on);        // витяжка; незалежна від setFan()/setAirHeater()
  void setLight(uint8_t brightness); // 0 = вимкнено; обрізається до LIGHT_MAX_BRIGHTNESS (800 мА), а не 255
  void setSoilHeater(uint8_t power);  // 0 = вимкнено, 255 = максимальна потужність
  void setAirHeater(uint8_t power);   // 0 = вимкнено, 255 = максимальна потужність; ненульова потужність тримає вентилятор увімкненим

  bool isPumpOn() const { return _pumpOn; }
  bool isFanOn() const { return _fanRequested || _airHeaterPower > 0; } // фактичний стан FAN_PIN, не лише останній setFan()
  bool isExhaustFanOn() const { return _exhaustFanOn; }
  bool isLightOn() const { return _lightBrightness > 0; }
  bool isSoilHeaterOn() const { return _soilHeaterPower > 0; }
  bool isAirHeaterOn() const { return _airHeaterPower > 0; }

private:
  void applyFanOutput(); // пише FAN_PIN = isFanOn(); викликати після зміни _fanRequested або _airHeaterPower

  bool _pumpOn = false;
  bool _fanRequested = false; // останній явний setFan(); НЕ обов'язково фактичний стан піна — див. isFanOn()
  bool _exhaustFanOn = false;
  uint8_t _lightBrightness = 0;
  uint8_t _soilHeaterPower = 0;
  uint8_t _airHeaterPower = 0;

  unsigned long _pumpStartMs = 0;        // millis() моменту останнього вмикання помпи
  unsigned long _fanStartMs = 0;         // millis() моменту останнього явного запиту вентиляції
  unsigned long _exhaustFanStartMs = 0;  // millis() моменту останнього підтвердження витяжки
  unsigned long _soilHeaterStartMs = 0;  // millis() моменту останнього підтвердження нагрівача ґрунту
  unsigned long _airHeaterStartMs = 0;   // millis() моменту останнього підтвердження нагрівача повітря
};
