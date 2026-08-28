# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Project overview

React + Vite + TypeScript web control panel for the SmartGreenhouse system — a browser-based sibling of `../MobileApp` (Expo/React Native). Both are thin clients over `../Backend`'s HTTP + SignalR API; neither talks to the ESP-32 (`../ESP-32`) directly.

LAN-only by design, same as MobileApp: no real login, just a shared `X-Api-Key` header stored in `localStorage`.

## Build / run commands

```
npm install
npm run dev              # Vite dev server (default http://localhost:5173), proxies nothing — talks to Backend via CORS
npx tsc -b               # type-check
npm run build             # production build -> dist/
```

**Deployment is unusual**: there's no separate hosting for this app. `npm run build` output (`dist/`) is meant to be copied into `../Backend/wwwroot/`, which `../Backend/Program.cs` serves directly (`UseStaticFiles()` + `MapFallbackToFile("index.html")`) — so in normal use there's one process (Backend) and one URL (`http://<backend-host>:5080/`), not two. The dev server (`npm run dev`) is only for iterating on this app's code; it talks to a separately-running Backend over CORS (`AddCors`/`UseCors` in `Program.cs`, `AllowAnyOrigin` since auth is a header, not a cookie).

`../Backend/SmartGreenhouse.Backend.csproj` has an explicit `<Content Update="wwwroot/**/*" CopyToOutputDirectory="PreserveNewest" />` — without it, `Microsoft.NET.Sdk.Web`'s default only copies `wwwroot/` on `dotnet publish`, not on `dotnet build`/`dotnet run`, and Backend's `ContentRootPath` is pinned to the build output dir (`AppContext.BaseDirectory`), not the source tree.

## Required local config (not in code)

Same pattern as MobileApp: no config file, no env vars — first launch shows a settings-only view asking for the Backend URL and `X-Api-Key`. When served from Backend's `wwwroot` (the normal case), the URL defaults to `window.location.origin` (`src/config.ts`) since the app and API share an origin; when running the Vite dev server against a Backend on another port, it must be entered manually.

## Architecture

Deliberately mirrors `../MobileApp/src` file-for-file where the platform allows it — if you're changing behavior here, check whether the same fix is needed there (and vice versa):

- **`src/config.ts` / `src/ConfigContext.tsx`** — `localStorage`-backed config (web analog of `MobileApp/src/config/ConfigContext.tsx`'s `expo-secure-store`). No async loading state needed (`localStorage` is synchronous), unlike the mobile version.
- **`src/api/client.ts`** (`ApiClient`) — identical contract to `MobileApp/src/api/client.ts`: adds `X-Api-Key` (`Content-Type` only on POST/PUT); `404` throws unless the caller passes `get(path, { notFoundAsNull: true })` (only `/api/planting/current`, `/api/telemetry/latest`, `/api/plant-profile`).
- **`src/api/hooks.ts`** (`useApiClient`) — same memoization pattern as the mobile version. `QueryClient` defaults (`retry: 1`, `staleTime: 30s`) live in `src/main.tsx`.
- **`src/api/signalr.ts`** (`useLiveUpdates`) — same `@microsoft/signalr` connection to `/hubs/live`; **returns a `LiveStatus`** rendered as an "offline" pill in `App.tsx`'s `Layout` via `LiveStatusContext`. Patches TanStack Query cache on `"TelemetryReceived"`/`"DecisionReceived"`/`"PlantProfileReceived"`. Includes an id-based dedup on the `['decisions','history']` array; its slice cap and `HistoryPage`'s fetch `count` share `src/api/constants.ts`.
- **`src/types.ts`** — hand-written mirror of the Backend DTOs, kept in sync with `MobileApp/src/types.ts` (same camelCase-except-`AiCommand` contract).
- **`src/App.tsx`** — gating, analogous to `MobileApp/src/navigation/RootNavigator.tsx`: not configured → `SettingsPage` alone; configured but `GET /api/planting/current` is `null` → redirect to `/onboarding`; otherwise the main `Layout` (top nav + `<Outlet/>`) with routes for Dashboard/Camera/Controls/History/Profile/Settings. Uses `react-router-dom` (`MobileApp` uses React Navigation instead — same shape, different library per platform).
- **`src/pages/`** — one page per route, same query/mutation logic as the matching `MobileApp/src/screens/*Screen.tsx`, rendered as plain HTML. `CameraPage` (`GET /api/camera/snapshot` as a blob → object URL; 200/204/502 states) and `ProfileEditPage` (`PUT /api/plant-profile`, range fields only) have no react-query cache entry of their own.
- **`src/components/Sparkline.tsx`** — same dependency-free bar-style trend indicator as the mobile version (`div`s instead of `View`s). Downsamples to ~60 buckets, keeps `null` gaps.
- **`src/index.css`** — plain CSS, no UI framework (matches the "home tool, not a product" scope decision — see `../Backend/CLAUDE.md` context on why multi-tenant/SaaS concerns were deliberately deferred).
