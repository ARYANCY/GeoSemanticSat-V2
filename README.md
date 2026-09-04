# SIH 2026 Satellite Intelligence Backend

Offline-first FastAPI backend for local GeoTIFF ingestion, location timelines, visual similarity, change evidence, analyst review and provenance. It makes no runtime HTTP, cloud, map-tile, or model-download requests.

## Quick start

```powershell
Copy-Item .env.example .env
pip install -r requirements.txt
python scripts/init_db.py
python scripts/create_sample_data.py
uvicorn app.main:app --reload
```

Open `http://127.0.0.1:8000/docs` or call `GET /health`.

## Offline smoke test

Ingest `data/sample_before.tif` and `data/sample_after.tif` through `POST /api/v1/ingest`, then call image search or change analysis with their returned observation IDs. Run `python scripts/build_index.py` after batch ingestion to write a local FAISS index.

## Model status

The included histogram embedding is visual-only, intended to keep the image-to-image workflow testable without falsifying semantic AI. Text search intentionally returns `503` until a licensed RemoteCLIP adapter and weights are staged locally at the configured path. The included pixel-difference change detector is baseline evidence only; it is not a trained change classifier. See `manual.txt` for the model, PostGIS, licensing, and production-extension guidance.

## Repository hygiene

Do not commit `.env`, satellite archives, generated indexes, databases, or model weights. `.gitignore` retains only empty local-data/model/index directory markers. Use Git LFS or an approved internal artifact store for large permitted assets.
