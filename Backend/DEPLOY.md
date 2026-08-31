# Deploying Backend

**Deployment is now automated via CI/CD.** The full guide moved to the repo root:
[`../DEPLOY.md`](../DEPLOY.md).

Short version: `git push` to `main` → GitHub Actions builds the image and pushes it
to GHCR → a self-hosted runner on the home server (`lenovo-srv`) pulls it and runs
`docker compose up -d`. Data lives in the Docker named volume
`smartgreenhouse_greenhouse_data` (no longer in `Backend/appdata/`).

For local dev, one-time setup, rollback, and troubleshooting see
[`../DEPLOY.md`](../DEPLOY.md).
