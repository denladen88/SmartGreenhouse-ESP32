# Deploying Backend to a home server

Run these steps on the server itself (Linux), not on the dev machine.

1. Install Docker and the Docker Compose plugin.
2. Clone the repository:
   ```
   git clone <repo-url>
   cd SmartGreenhouse
   ```
3. Create `Backend/.env` manually (it's gitignored — never comes from git):
   ```
   Gemini__ApiKey=your-real-key-here
   ```
4. Start it:
   ```
   docker compose up -d --build
   ```
5. Check logs:
   ```
   docker compose logs -f backend
   ```

## Updating later

```
git pull
docker compose up -d --build
```

`greenhouse.db` and `Photos/` live in `Backend/appdata/` on the host (mounted into the container), so they survive rebuilds and restarts.

## If the container can't reach the MQTT broker or ESP32-CAM

Edit `docker-compose.yml` and uncomment `network_mode: host`, then `docker compose up -d --build` again.
