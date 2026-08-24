#pragma once
#include <cstdint>
#include <Adafruit_NeoPixel.h>

// Керує помпою (реле), вентилятором (реле) та grow-світлом (адресована
// 24V RGB+TW стрічка, 5 каналів на піксель).
class ActuatorService {
public:
  ActuatorService();

  void begin();

  // Викликати щоцикл loop(): перевіряє захисні таймери помпи й вентилятора
  // (PUMP_MAX_RUNTIME_MS, FAN_MAX_RUNTIME_MS) і примусово вимикає їх при
  // перевищенні.
  void update();

  void setPump(bool on);
  void setFan(bool on);
  void setLight(bool on);

  bool isPumpOn() const { return _pumpOn; }
  bool isFanOn() const { return _fanOn; }
  bool isLightOn() const { return _lightOn; }

private:
  bool _pumpOn = false;
  bool _fanOn = false;
  bool _lightOn = false;

  unsigned long _pumpStartMs = 0; // millis() моменту останнього вмикання помпи
  unsigned long _fanStartMs = 0;  // millis() моменту останнього вмикання вентилятора

  Adafruit_NeoPixel _strip;
};
