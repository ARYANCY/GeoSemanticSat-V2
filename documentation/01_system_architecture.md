# System architecture

## Backend

`app/main.py` owns FastAPI startup, middleware, and routes. `app/db/session.py` supplies SQLAlchemy sessions. The backend persists locations, sources, observations, processing runs, embeddings, changes, reviews, missions, and alerts.

```mermaid
sequenceDiagram
  participant C as API client
  participant A as FastAPI
  participant R as Rasterio
  participant E as Embedder
  participant D as Database
  C->>A: POST /api/v1/ingest
  A->>R: validate and read local GeoTIFF
  A->>E: image embedding
  A->>D: run, metadata, embedding
  A-->>C: ingest response
```

Compose defaults to SQLite in the named `api_db` volume. The optional `db` service runs only with the `postgres` profile.

## Desktop

The UI invokes `GeoSemanticSat.Core` and `GeoSemanticSat.Engine` directly. Core contains GeoTIFF handling, change detection, quality masks, clustering, vector indexing, reviews, and provenance. Engine contains embedding, retrieval, benchmarking, and local intelligence services. The map tile service can use network providers except in `OfflineStrict` mode.