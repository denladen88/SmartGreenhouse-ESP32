# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

.NET 8 worker service backend for the SmartPlant/SmartGreenhouse system. It subscribes to MQTT telemetry published by the ESP-32 firmware (sibling project, see `../ESP-32/CLAUDE.md`), persists it to SQLite, and periodically runs an "AI Agronomist" cycle: it pulls a recent sensor trend plus a live photo from the ESP32-CAM, sends both to Google Gemini for analysis, and publishes the resulting pump/fan decision back over MQTT.

## Build / run commands

Standard .NET CLI, run from `Backend/`:

```
dotnet build                       # build
dotnet run                         # run the worker service
dotnet build SmartGreenhouse.Backend.sln
```

No test project exists yet. There is no separate lint config — rely on the C# compiler/analyzers via `dotnet build`.

## Required local config (not in git)

Configuration is layered `appsettings.json` → `appsettings.Development.json` → user secrets (`UserSecretsId` is set in the `.csproj`), bound to strongly-typed `Options` classes in `Models/` via `builder.Services.Configure<T>(...)` in `Program.cs`:

- `Mqtt` → `MqttOptions` — broker address/port/credentials and topics (`Topic` for inbound telemetry, `CommandsTopic` for outbound AI decisions). Must match the ESP-32 firmware's `MQTT_TELEMETRY_TOPIC`/`MQTT_COMMANDS_TOPIC` in `Config.h` — both currently use `smartplant/telemetry` and `smartplant/commands`.
- `Gemini` → `GeminiOptions` — Gemini API key + model name.
- `Esp32` → `Esp32Options` — HTTP URL of the ESP32-CAM still-capture endpoint.
- `AiAgronomist` → `AiAgronomistOptions` — poll interval and trend window/bucket sizes (minutes).
- `Plant` → `PlantOptions` — free-text plant name/care notes injected verbatim into the Gemini prompt so the model judges sensor trends against species-specific norms rather than generic thresholds.

**`appsettings.json` currently contains a live Gemini API key in plaintext and is not gitignored** (only `.env` and `*.db*` are ignored). The `UserSecretsId` is already configured, so secrets should be moved into `dotnet user-secrets` (or an environment-specific file that *is* gitignored) rather than committed `appsettings.json`.

## Architecture

`Program.cs` wires up a generic `Host` (`Host.CreateApplicationBuilder`) with two long-running `BackgroundService`s and a `DbContext`; there is no web/API surface — this is a pure worker process.

- **AppDbContext** (`Data/AppDbContext.cs`) — single `DbSet<TelemetryRecord>`, SQLite file `greenhouse.db` in the working directory (`Data Source=greenhouse.db`, hardcoded fallback in `OnConfiguring`). Schema is created via `db.Database.EnsureCreated()` at startup in `Program.cs` — no EF migrations are used.

- **MqttBackgroundService** (`Services/MqttBackgroundService.cs`) — owns the MQTT connection (MQTTnet `IManagedMqttClient`, auto-reconnect). Subscribes to `MqttOptions.Topic`, deserializes each message as `TelemetryMessage` (snake_case JSON matching the ESP-32 firmware's payload shape), logs it, and persists it as a `TelemetryRecord`. It's registered as both a singleton and the `IMqttPublisher` implementation (`AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttBackgroundService>())`), so other services can publish outbound MQTT messages (e.g. AI commands) through the same managed client without re-implementing connection handling.

- **AiAgronomistService** (`Services/AiAgronomistService.cs`) — a `BackgroundService` on a `PeriodicTimer` (`AiAgronomistOptions.PollIntervalMinutes`). Each cycle: reads `TelemetryRecord`s from the last `TrendWindowMinutes` out of `AppDbContext`, downsamples them into `TrendBucketMinutes`-sized averaged buckets, fetches a JPEG from the ESP32-CAM's HTTP capture endpoint, and sends both (image as inline base64 + trend as text) to the Gemini `generateContent` API with a prompt that embeds `PlantOptions.CareNotes`. The prompt explicitly instructs the model to judge soil-moisture *trend direction* rather than a fixed threshold (the capacitive-style soil sensor drifts/corrodes over time) and to reason about light/temperature using the plant-specific profile rather than generic assumptions. The parsed `AiDecision` (pump/fan booleans + reasoning text) is converted to an `AiCommand` and published via `IMqttPublisher` to `MqttOptions.CommandsTopic`.

- **Models/** — plain POJOs/records split between MQTT wire types (`TelemetryMessage`, `AiCommand` — `JsonPropertyName`-annotated snake_case to match the ESP-32/MQTT side), the EF entity (`TelemetryRecord`), the Gemini response DTO (nested private records inside `AiAgronomistService`), and the `*Options` config-binding classes described above.

**Cross-project note**: the ESP-32 firmware (`MqttService`, wired up in its `main.cpp`) subscribes to `MQTT_COMMANDS_TOPIC` and applies `pump_on`/`fan_on` directly to `ActuatorService`, so `AiAgronomistService`'s published commands on `smartplant/commands` do reach the device. The pump still has an independent 5s failsafe timeout on the firmware side regardless of command source.
