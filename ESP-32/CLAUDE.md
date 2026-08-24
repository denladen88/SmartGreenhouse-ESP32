# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Firmware for an ESP32-S3 based plant monitoring/watering controller ("SmartPlant S3"), built with PlatformIO + Arduino framework. It reads climate/light/soil sensors, captures camera frames, drives a pump/fan/LED, and publishes telemetry over MQTT. Serial log output and in-code comments are in Ukrainian.

## Build / flash / monitor

This is a PlatformIO project (not npm/cmake). Use the `pio` CLI:

```
pio run                    # build
pio run -t upload          # build and flash
pio run -t monitor         # serial monitor (115200 baud)
pio run -t upload -t monitor
pio check                  # static analysis (cppcheck via PlatformIO)
```

There is no test suite (`test/` only contains PlatformIO's placeholder README) and no lint config beyond `pio check`.

The single build environment is `esp32-s3-devkitc-1` (see [platformio.ini](platformio.ini)) — an N16R8 module (16MB flash / 8MB octal PSRAM), so `board_build.arduino.memory_type = qio_opi` and `-DBOARD_HAS_PSRAM` matter for anything touching the camera frame buffer or large allocations.

## Required local config (not in git)

[include/Secrets.h](include/Secrets.h) holds Wi-Fi and MQTT broker credentials and must be filled in locally before the firmware will connect to anything — it's a placeholder file with `YOUR_WIFI_SSID` etc. and is explicitly excluded from the public repo per the comment at its top. Optional MQTT auth is enabled by uncommenting `MQTT_USERNAME`/`MQTT_PASSWORD` `#define`s in that file, which gate an `#if defined(...)` branch in [src/MqttService.cpp](src/MqttService.cpp).

All tunable constants (pins, I2C addresses, intervals, MQTT topics, device ID) live in [include/Config.h](include/Config.h) — check there before hardcoding a pin or interval elsewhere.

## Architecture

Class headers live in `/include`, implementations in `/src` (`ClassName.h` + `ClassName.cpp`, matching names). `src/main.cpp` wires together five independent service classes, each owning one piece of hardware/connectivity, and drives them from a single non-blocking `loop()`:

- **SensorService** — I2C sensors (BME280 climate, BH1750 lux) plus one analog soil-moisture read. `begin()` probes both I2C addresses for the BME280 and tolerates either sensor being absent (`SensorData` has per-group `*Valid` flags rather than failing hard).
- **CameraService** — wraps `esp_camera.h` for an OV3660 camera. Its header deliberately does **not** include `esp_camera.h` (only forward-declares a thin class interface) because `esp_camera.h`'s `sensor_t` collides with `Adafruit_Sensor.h`'s type of the same name if both land in one translation unit — keep camera internals confined to CameraService.cpp and never pull `esp_camera.h` into a file that also (transitively) includes `Adafruit_Sensor.h`. It also claims LEDC channel 0 / timer 0 directly, which is why `LED_PWM_CHANNEL` in Config.h is set to 4 to avoid conflicting.
- **ActuatorService** — pump relay + fan as digital outputs, LED brightness via `ledcWrite` PWM.
- **NetworkService** — Wi-Fi station connect with non-blocking reconnect-on-drop.
- **MqttService** — wraps PubSubClient; non-blocking reconnect and JSON telemetry publish (ArduinoJson, one consolidated payload every `SENSOR_READ_INTERVAL_MS`) to `MQTT_TELEMETRY_TOPIC`, retained "online" status to `MQTT_STATUS_TOPIC` on connect. Also subscribes to `MQTT_COMMANDS_TOPIC` on connect and dispatches parsed `pump_on`/`fan_on` JSON to a handler registered via `onCommand()` — this is how the sibling `Backend` project's AI Agronomist decisions reach `ActuatorService` (see `main.cpp`'s `mqtt.onCommand(...)` lambda). `PubSubClient::setCallback()` only accepts a plain function pointer, so `MqttService::handleMessage` is static and there's a static `_commandHandler` — safe only because the project has exactly one `MqttService` instance.

An `ESPAsyncWebServer` (port 80) is also started directly in `main.cpp` (not its own service class), exposing `GET /capture` which returns the current camera JPEG — this is the endpoint the Backend's AI Agronomist cycle polls to get a photo alongside sensor trends.

**Non-blocking loop pattern**: every periodic action (sensor read, camera capture, Wi-Fi/MQTT reconnect) is gated by a `NonBlockingTimer` ([include/NonBlockingTimer.h](include/NonBlockingTimer.h)), a tiny `millis()`-based interval helper. `loop()` never calls `delay()`; new periodic behavior should follow the same pattern — construct a `NonBlockingTimer` with the desired interval and check `.elapsed()` each pass rather than blocking.

Service classes generally follow the same shape: a `begin()` for one-time init, and either an `update()` (Network/Mqtt, called every loop) or an on-demand method (Sensors/Camera/Actuators, called from timers or external commands). The pump has an independent failsafe: `ActuatorService::update()` (called every loop) force-shuts it off after `PUMP_MAX_RUNTIME_MS`, regardless of whether it was turned on locally or via an MQTT command.
