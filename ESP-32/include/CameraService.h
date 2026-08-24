#pragma once
#include <cstdint>
#include <cstddef>

// esp_camera.h навмисно НЕ підключається тут: він конфліктує з типом sensor_t
// з Adafruit_Sensor.h, якщо потрапляє в ту саму одиницю компіляції. Тому цей
// заголовок лишається "тонким" — деталі esp_camera.h живуть лише в CameraService.cpp.
class CameraService {
public:
  // Ініціалізує камеру OV3660. Повертає true в разі успіху.
  bool begin();

  // Захоплює кадр і повертає його розмір у байтах, або -1 у разі помилки.
  int captureFrameSize();

  // Захоплює JPEG-кадр для віддачі назовні (напр. веб-сервером). У разі
  // успіху заповнює buf/len і повертає true; кадр обов'язково звільнити
  // через releaseFrame() одразу після використання (buf стає невалідним).
  bool captureJpeg(const uint8_t** buf, size_t* len);
  void releaseFrame();

private:
  bool _ready = false;
  void* _pendingFb = nullptr; // camera_fb_t*, тип навмисно прихований (див. коментар вище)
};
