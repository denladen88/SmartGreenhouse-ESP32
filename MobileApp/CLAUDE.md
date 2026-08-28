@AGENTS.md

# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Project overview

Expo (React Native, TypeScript) mobile app for monitoring and controlling the SmartGreenhouse system. It's a thin client over the sibling `../Backend` project's HTTP + SignalR API — all sensor data, AI decisions, and actuator commands flow through Backend, never directly to the ESP-32 (`../ESP-32`).

LAN-only by design: no JWT/OAuth, no public HTTPS. Auth is a single shared `X-Api-Key` header (see `src/config/ConfigContext.tsx`), acceptable only because the app and Backend are expected to stay on the same home Wi-Fi network.

## Build / run commands

```
npm install
npx expo start             # Metro dev server; scan the QR with Expo Go on a phone on the same Wi-Fi
npx tsc --noEmit            # type-check (no separate test suite/lint config yet)
```

Requires Node **>=20.19.4** (Metro/`react-native` engine requirement) — an older 20.18.x prints a warning but still runs.

## Required local config (not in code)

Unlike Backend/ESP-32, there's no config file to edit — on first launch the app shows a Settings-only screen asking for:
- **Backend URL** — e.g. `http://192.168.1.50:5080` (the LAN address Backend's `dotnet run`/Docker container binds to; see `../Backend/CLAUDE.md`).
- **API key** — must match `Api:Key` in `../Backend/appsettings.json`.

Both are stored via `expo-secure-store` (`src/config/ConfigContext.tsx`), not hardcoded — the Backend's LAN IP varies per network/user.

## Architecture

- **`src/config/ConfigContext.tsx`** — React context wrapping `expo-secure-store` for `backendUrl`/`apiKey`. `RootNavigator` gates the whole app on `isConfigured`.
- **`src/api/client.ts`** (`ApiClient`) — thin `fetch` wrapper adding `X-Api-Key` (and `Content-Type` only on POST/PUT). `404` throws by default; pass `get(path, { notFoundAsNull: true })` for the three endpoints where "no data yet" is legitimate (`/api/planting/current`, `/api/telemetry/latest`, `/api/plant-profile`) so a mistyped/moved route still surfaces as an error instead of silently reading as empty.
- **`src/api/hooks.ts`** (`useApiClient`) — memoized `ApiClient` built from `ConfigContext`, used by every screen via TanStack Query (`useQuery`/`useMutation`). Global defaults (`retry: 1`, `staleTime: 30s`) are set on the `QueryClient` in `App.tsx`.
- **`src/api/signalr.ts`** (`useLiveUpdates`) — one `@microsoft/signalr` connection to Backend's `/hubs/live`, mounted once in `RootNavigator`'s `ConfiguredApp`; **returns a `LiveStatus`** (`connecting`/`connected`/`reconnecting`/`disconnected`) which `ConfiguredApp` renders as an "offline" banner. Patches the TanStack Query cache directly on `"TelemetryReceived"`/`"DecisionReceived"`/`"PlantProfileReceived"` server pushes instead of polling — see `../Backend/Hubs/TelemetryHub.cs` and the broadcast call sites in `MqttBackgroundService`/`AiAgronomistService`. The `['decisions','history']` slice cap and `HistoryScreen`'s fetch `count` share one constant (`src/api/constants.ts`). The API key rides in the `?access_token=` query param (not a header) because the SignalR WebSocket handshake can't set custom headers — `ApiKeyMiddleware` on the Backend side accepts either.
- **`src/types.ts`** — hand-written mirrors of the Backend DTOs. Most are camelCase (System.Text.Json's ASP.NET Core default); `AiCommand` is the one exception, snake_case (`pump_on`/`fan_on`/...) to match `[JsonPropertyName]` on the C# record, which itself matches the MQTT/ESP-32 wire format.
- **`src/navigation/RootNavigator.tsx`** — gates in order: (1) `ConfigContext.loading` → spinner, (2) `!isConfigured` → `SettingsScreen` alone (first-run setup), (3) configured but `GET /api/planting/current` is `null` → `OnboardingScreen`, (4) otherwise the main `Tab.Navigator` (`Dashboard`/`Camera`/`Controls`/`History`/`SettingsTab`). `ProfileEdit` is a stack screen (not a tab), opened from a button in Settings.
- **`src/screens/`** — one screen per tab, each independent (own `useQuery`/`useMutation` calls against `useApiClient()`), plus `OnboardingScreen` (posts a new `Planting`, see `../Backend/Controllers/PlantingController.cs`; guards against replacing an existing planting without confirmation) and `ProfileEditScreen` (`PUT /api/plant-profile`, range fields only), both reachable via buttons in Settings (`showOnboardingShortcut` prop). `CameraScreen` fetches `GET /api/camera/snapshot` as a binary blob (200 JPEG / 204 night / 502 ESP down) — outside react-query.
- **`src/components/Sparkline.tsx`** — deliberately dependency-free bar-style trend indicator (plain `View`s) instead of a charting library. Downsamples to ~60 buckets and keeps `null` gaps rather than collapsing the time axis.

**Manual control semantics**: `ControlsScreen`'s `POST /api/commands` is a one-shot override, not a persistent mode — Backend's local AI controller re-publishes its own decision every `LocalControlIntervalMinutes` (~10 min) regardless, so a manual override is naturally superseded rather than "sticking" forever.
