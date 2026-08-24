#pragma once
#include <cstdint>

// Керує помпою (реле), вентилятором (реле) та grow-світлом (Cold+Warm White
// канали 24V стрічки, обидва разом через один ШІМ-сигнал — див. Config.h).
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
  void setLight(uint8_t brightness); // 0 = вимкнено, 255 = максимальна яскравість

  bool isPumpOn() const { return _pumpOn; }
  bool isFanOn() const { return _fanOn; }
  bool isLightOn() const { return _lightBrightness > 0; }

private:
  bool _pumpOn = false;
  bool _fanOn = false;
  uint8_t _lightBrightness = 0;

  unsigned long _pumpStartMs = 0; // millis() моменту останнього вмикання помпи
  unsigned long _fanStartMs = 0;  // millis() моменту останнього вмикання вентилятора
};
