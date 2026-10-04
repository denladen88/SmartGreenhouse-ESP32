# Repository Guidelines

## Project Structure & Module Organization

This directory contains the ESP32-S3 firmware within the SmartGreenhouse monorepo. Production code is split into service pairs: public interfaces in `include/` and implementations in `src/` (for example, `SensorService.h` and `SensorService.cpp`). `src/main.cpp` wires sensors, networking, MQTT, camera, and actuators together. Hardware constants and MQTT topics belong in `include/Config.h`; local credentials belong in the ignored `include/Secrets.h`, based on `Secrets.example.h`. Keep wiring references in `docs/` synchronized with pin changes. `test/` is reserved for PlatformIO tests.

## Build, Test, and Development Commands

Run commands from `ESP-32/`:

- `pio run` — compile the pinned `esp32-s3-devkitc-1` environment.
- `pio run -t upload` — build and flash the connected board.
- `pio run -t monitor` — open the 115200-baud serial monitor with timestamps and exception decoding.
- `pio run -t upload -t monitor` — flash, then monitor.
- `pio test` — run PlatformIO tests when test cases exist.
- `pio check` — run available static analysis.

Do not casually upgrade `espressif32@7.0.1`; the firmware relies on the Arduino ESP32 2.x LEDC API and N16R8 memory configuration.

## Coding Style & Naming Conventions

Use C++17-compatible Arduino code with two-space indentation and opening braces on the same line. Name classes and structs in `PascalCase`, methods and local variables in `camelCase`, private fields with a leading underscore, and configuration constants in `UPPER_SNAKE_CASE`. Keep hardware ownership inside its service class. The main loop must remain non-blocking: use `NonBlockingTimer` instead of `delay()` for recurring work. Preserve Ukrainian diagnostics and comments where they explain hardware behavior.

## Testing Guidelines

There is currently no substantive automated test suite. Every change must at least pass `pio run`; run `pio check` for logic-heavy changes. Add PlatformIO tests under `test/<feature>/test_main.cpp`. For sensor or actuator changes, document the board, observed serial output, and physical verification performed.

## Commit & Pull Request Guidelines

History generally uses Conventional Commit prefixes such as `feat:`, `fix:`, and `refactor:`. Write imperative, focused subjects, for example: `fix: prevent repeated pump timer extension`. Pull requests should describe behavior, affected pins/topics, safety implications, and verification commands. Include serial logs or wiring screenshots when hardware behavior changes, and mention any required matching Backend contract update.

## Security & Hardware Safety

Never commit Wi-Fi, MQTT, or API credentials. Preserve actuator runtime failsafes, PWM ceilings, and fan/heater coupling. Treat pin reassignment, relay polarity, heater power, and MQTT command changes as safety-sensitive.
