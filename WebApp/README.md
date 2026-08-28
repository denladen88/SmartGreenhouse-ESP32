# SmartGreenhouse WebApp

Browser control panel for the SmartGreenhouse system — a React + Vite + TypeScript
sibling of `../MobileApp`. Both are thin clients over `../Backend`'s HTTP + SignalR
API; neither talks to the ESP-32 directly. LAN-only: auth is a single shared
`X-Api-Key`, entered once on the Settings screen and kept in `localStorage`.

## Commands

```
npm install
npm run dev       # Vite dev server (http://localhost:5173), talks to a separately-running Backend over CORS
npx tsc -b        # type-check
npm run lint      # oxlint
npm run build     # production build -> dist/
```

## Deployment

There is no separate hosting. `npm run build` output (`dist/`) is copied into
`../Backend/wwwroot/`, which `../Backend/Program.cs` serves via `UseStaticFiles()`
+ `MapFallbackToFile("index.html")` — so in normal use there is one process and one
URL (`http://<backend-host>:5080/`). When served from `wwwroot`, the Backend URL
defaults to `window.location.origin`; the dev server needs it entered manually in
Settings.

See [CLAUDE.md](./CLAUDE.md) for architecture notes and the file-for-file mapping
to `../MobileApp/src`.
