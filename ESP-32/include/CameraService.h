#pragma once
#include <cstdint>
#include <cstddef>
#include "NonBlockingTimer.h"

// esp_camera.h навмисно НЕ підключається тут: він конфліктує з типом sensor_t
// з Adafruit_Sensor.h, якщо потрапляє в ту саму одиницю компіляції. Тому цей
// заголовок лишається "тонким" — деталі esp_camera.h живуть лише в CameraService.cpp.
class CameraService {
public:
  // Ініціалізує камеру OV3660. Повертає true в разі успіху.
  bool begin();

  bool isReady() const { return _ready; }

  // Якщо камера не піднялась (esp_camera_init() впав на старті) — раз на 30 с
  // пробує повну переініціалізацію. Викликати перед captureJpeg(), коли
  // isReady() == false. До цього фікса збій ініціалізації був назавжди.
  void retryIfDown();

  // Захоплює JPEG-кадр для віддачі назовні (напр. веб-сервером). У разі
  // успіху заповнює buf/len і повертає true; кадр обов'язково звільнити
  // через releaseFrame() одразу після використання (buf стає невалідним).
  bool captureJpeg(const uint8_t** buf, size_t* len);
  void releaseFrame();

private:
  bool _ready = false;
  void* _pendingFb = nullptr; // camera_fb_t*, тип навмисно прихований (див. коментар вище)
  NonBlockingTimer _retryTimer{30000UL};
};
