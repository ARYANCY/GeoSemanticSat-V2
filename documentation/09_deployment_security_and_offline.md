# Deployment, storage, and offline controls

`docker compose up -d` builds and starts the API. The container runs as `appuser` (UID 1000), exposes port 8000, and mounts source, scripts, tests, data, models, and indexes. SQLite is held in the `api_db` named volume at `/app/dbdata`, not in the bind-mounted `data` directory.

The Docker image installs `libexpat1` for Rasterio's bundled GDAL dependency. It installs a CPU-only Torch wheel before requirements because `torch==2.5.1` is pinned, although no current Python source import uses Torch. Do not infer GPU support from that dependency.

Backend configuration sets Hugging Face and Transformers offline variables and enables loopback middleware by default. These mechanisms do not prevent desktop map networking unless the UI uses `OfflineStrict`.

`.env` is optional for Compose. Start from `.env.example` only when values need changing, and do not commit `.env`.