# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Firmware for an ESP32-S3 based plant monitoring/watering controller ("SmartPlant S3"), built with PlatformIO + Arduino framework. It reads climate/light/soil sensors (including a DS18B20 soil-temperature probe), captures camera frames, drives a pump, two fans, a grow-light, and two heaters, and publishes telemetry over MQTT. Serial log output and in-code comments are in Ukrainian.

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

`docs/` holds physical-build reference material, kept in sync with `Config.h` — update it when pins or hardware change:
- [docs/wiring-diagram.html](docs/wiring-diagram.html) — open in a browser; a simple block overview plus a detailed wiring diagram (every GPIO mapped to its sensor/actuator module and external device, power domains, which pins are off-limits).
- [docs/pinout.md](docs/pinout.md) — the same GPIO-to-device mapping as plain text, no diagram.
- [docs/hardware.md](docs/hardware.md) — flat list of the physical components used (sensors, camera, actuators, driver/relay modules, power).

## Architecture

Class headers live in `/include`, implementations in `/src` (`ClassName.h` + `ClassName.cpp`, matching names). `src/main.cpp` wires together five independent service classes, each owning one piece of hardware/connectivity, and drives them from a single non-blocking `loop()`:

- **SensorService** — I2C sensors (BME280 climate, BH1750 lux), one analog soil-moisture read, and a DS18B20 soil-temperature probe (OneWire, via `DallasTemperature`). `begin()` probes both I2C addresses for the BME280 and tolerates any sensor being absent (`SensorData` has per-group `*Valid` flags rather than failing hard). `update()` (called every `loop()`) does two things `read()` itself can't: a non-blocking ring-buffer sample of the soil ADC (20ms/tick across `kSoilSamples`=15, so `read()` never blocks on a `delay()`-based median filter) and, every 5 minutes, a re-probe of any sensor that was missing at `begin()` or has since dropped off its bus — so a sensor plugged in or reconnected after boot is picked up without a restart.
- **CameraService** — wraps `esp_camera.h` for an OV3660 camera. Its header deliberately does **not** include `esp_camera.h` (only forward-declares a thin class interface) because `esp_camera.h`'s `sensor_t` collides with `Adafruit_Sensor.h`'s type of the same name if both land in one translation unit — keep camera internals confined to CameraService.cpp and never pull `esp_camera.h` into a file that also (transitively) includes `Adafruit_Sensor.h`. It also claims LEDC channel 0 / timer 0 directly, which is why `LED_PWM_CHANNEL` in Config.h is set to 2 (timer 1) to avoid conflicting. If `esp_camera_init()` fails at boot, `isReady()` returns false and `retryIfDown()` (call before `captureJpeg()`) retries the full init once every 30s — previously an init failure at boot was permanent until reflash.
- **ActuatorService** — six actuators, each with its own setter and a failsafe timer checked every `loop()` in `update()`: pump (`setPump`, digital relay — a fixed `PUMP_RUN_DURATION_MS` pulse plus a `PUMP_MAX_RUNTIME_MS` backstop; the timer starts only on the OFF→ON transition so repeated `pump_on` commands can't extend it), circulation fan and exhaust fan (`setFan`/`setExhaustFan`, both digital on/off), and grow-light/soil-heater/air-heater (`setLight`/`setSoilHeater`/`setAirHeater`, `ledcWrite` PWM, one LEDC timer each so adjusting one frequency can't disturb another). Circulation fan, exhaust fan, and both heaters share a different failsafe shape than the pump: their timer refreshes on *every* on/power>0 call, so they run continuously as long as the backend keeps reconfirming its decision (~every 10 min) and self-shut-off after 15 min (`*_MAX_RUNTIME_MS`) of silence. The circulation fan and air heater are one physical unit (a combined fan+PTC-radiator module) — the fan is the heater's only airflow, so `setAirHeater()` keeps `FAN_PIN` asserted whenever heater power is nonzero regardless of the last explicit `setFan()` call; `isFanOn()` reflects the actual pin state, not just the last request (see `ActuatorService::applyFanOutput()`). The exhaust fan (`EXHAUST_FAN_PIN`, vents air outward) is a separate, independent actuator with no coupling to anything else — it exists to take over active cooling (previously the circulation fan's job) so the circulation fan can stay purely heating-coupled.
- **NetworkService** — Wi-Fi station connect with non-blocking reconnect-on-drop.
- **MqttService** — wraps PubSubClient; non-blocking reconnect (gated on Wi-Fi being up first) and JSON telemetry publish (ArduinoJson, one consolidated payload every `SENSOR_READ_INTERVAL_MS`) to `MQTT_TELEMETRY_TOPIC`. On connect it publishes retained "online" to `MQTT_STATUS_TOPIC` and registers a Last-Will (retained "offline" on that same topic) so the backend sees the device go offline even on an ungraceful disconnect (power loss, crash), not just a stale retained "online". Also subscribes to `MQTT_COMMANDS_TOPIC` and dispatches parsed `pump_on`/`fan_on`/`exhaust_fan_on`/`light_brightness`/`soil_heater_power`/`air_heater_power` JSON to a handler registered via `onCommand()` — this is how the sibling `Backend` project's AI Agronomist decisions reach `ActuatorService` (see `main.cpp`'s `mqtt.onCommand(...)` lambda). Each field has a matching `has*` flag on `CommandData` (set iff that key was present in the JSON) so a partial command only touches the actuators it mentions instead of zeroing out the rest. `PubSubClient::setCallback()` only accepts a plain function pointer, so `MqttService::handleMessage` is static and there's a static `_commandHandler` — safe only because the project has exactly one `MqttService` instance.

An `ESPAsyncWebServer` (port 80) is also started directly in `main.cpp` (not its own service class), exposing `GET /capture` which returns the current camera JPEG — this is the endpoint the Backend's AI Agronomist cycle polls to get a photo alongside sensor trends.

**Non-blocking loop pattern**: every periodic action (sensor read, MQTT publish, Wi-Fi/MQTT reconnect) is gated by a `NonBlockingTimer` ([include/NonBlockingTimer.h](include/NonBlockingTimer.h)), a tiny `millis()`-based interval helper with an `expire()` to force the next `.elapsed()` to fire immediately (used to get the first sensor read/telemetry publish right after boot/MQTT-connect instead of waiting a full interval). `loop()` never calls `delay()`; new periodic behavior should follow the same pattern — construct a `NonBlockingTimer` with the desired interval and check `.elapsed()` each pass rather than blocking. Camera capture is on-demand only (via the `/capture` HTTP handler below), not on a periodic timer.

Service classes generally follow the same shape: a `begin()` for one-time init. Network, Mqtt, Sensors, and Actuators each also have an `update()` called every `loop()` (Wi-Fi/MQTT reconnect logic; Sensors' non-blocking ADC sampling and sensor re-probing; Actuators' failsafe timers, respectively); CameraService has no periodic `update()` — `retryIfDown()` is only invoked on-demand from the `/capture` handler. The pump has an independent failsafe: `ActuatorService::update()` force-shuts it off after `PUMP_MAX_RUNTIME_MS`, regardless of whether it was turned on locally or via an MQTT command.
