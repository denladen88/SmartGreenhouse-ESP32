#pragma once
#include <Arduino.h>

// Періодичний таймер на базі millis(), що не блокує loop().
class NonBlockingTimer {
public:
  explicit NonBlockingTimer(unsigned long intervalMs) : _intervalMs(intervalMs) {}

  // Повертає true не частіше, ніж раз на intervalMs, і одразу зсуває відлік.
  bool elapsed() {
    unsigned long now = millis();
    if (now - _lastTrigger >= _intervalMs) {
      _lastTrigger = now;
      return true;
    }
    return false;
  }

  void reset() { _lastTrigger = millis(); }

private:
  unsigned long _intervalMs;
  unsigned long _lastTrigger = 0;
};
