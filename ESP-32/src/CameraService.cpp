#include "CameraService.h"
#include "esp_camera.h"

// Піни камери OV3660
#define PWDN_GPIO_NUM     -1
#define RESET_GPIO_NUM    -1
#define XCLK_GPIO_NUM     15
#define SIOD_GPIO_NUM      4
#define SIOC_GPIO_NUM      5

#define Y9_GPIO_NUM       16
#define Y8_GPIO_NUM       17
#define Y7_GPIO_NUM       18
#define Y6_GPIO_NUM       12
#define Y5_GPIO_NUM       10
#define Y4_GPIO_NUM        8
#define Y3_GPIO_NUM        9
#define Y2_GPIO_NUM       11
#define VSYNC_GPIO_NUM     6
#define HREF_GPIO_NUM      7
#define PCLK_GPIO_NUM     13

bool CameraService::begin() {
  camera_config_t config = {};
  config.ledc_channel = LEDC_CHANNEL_0;
  config.ledc_timer = LEDC_TIMER_0;
  config.pin_d0 = Y2_GPIO_NUM;
  config.pin_d1 = Y3_GPIO_NUM;
  config.pin_d2 = Y4_GPIO_NUM;
  config.pin_d3 = Y5_GPIO_NUM;
  config.pin_d4 = Y6_GPIO_NUM;
  config.pin_d5 = Y7_GPIO_NUM;
  config.pin_d6 = Y8_GPIO_NUM;
  config.pin_d7 = Y9_GPIO_NUM;
  config.pin_xclk = XCLK_GPIO_NUM;
  config.pin_pclk = PCLK_GPIO_NUM;
  config.pin_vsync = VSYNC_GPIO_NUM;
  config.pin_href = HREF_GPIO_NUM;
  config.pin_sccb_sda = SIOD_GPIO_NUM;
  config.pin_sccb_scl = SIOC_GPIO_NUM;
  config.pin_pwdn = PWDN_GPIO_NUM;
  config.pin_reset = RESET_GPIO_NUM;
  config.xclk_freq_hz = 20000000;
  config.frame_size = FRAMESIZE_SVGA;
  config.pixel_format = PIXFORMAT_JPEG;
  config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
  config.fb_location = CAMERA_FB_IN_PSRAM;
  config.jpeg_quality = 12;
  config.fb_count = 2;

  esp_err_t err = esp_camera_init(&config);
  _ready = (err == ESP_OK);
  return _ready;
}

int CameraService::captureFrameSize() {
  if (!_ready) {
    return -1;
  }

  camera_fb_t *fb = esp_camera_fb_get();
  if (!fb) {
    return -1;
  }
  int len = (int)fb->len;
  esp_camera_fb_return(fb);
  return len;
}

bool CameraService::captureJpeg(const uint8_t** buf, size_t* len) {
  if (!_ready || _pendingFb != nullptr) {
    return false; // попередній кадр ще не звільнено через releaseFrame()
  }

  camera_fb_t* fb = esp_camera_fb_get();
  if (!fb) {
    return false;
  }

  _pendingFb = fb;
  *buf = fb->buf;
  *len = fb->len;
  return true;
}

void CameraService::releaseFrame() {
  if (_pendingFb != nullptr) {
    esp_camera_fb_return(static_cast<camera_fb_t*>(_pendingFb));
    _pendingFb = nullptr;
  }
}
