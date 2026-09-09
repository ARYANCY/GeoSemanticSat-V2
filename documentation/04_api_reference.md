# 04. FastAPI Gateway & Endpoint Reference

> The source-aligned API reference is maintained in
> [00_developer_guide.md](00_developer_guide.md) and
> [07_implementation_reference.md](07_implementation_reference.md). Verify
> request and response details against `app/schemas/api.py` before integrating.

**Service:** Unified-RSanalytics REST API  
**Default Host & Port:** `http://127.0.0.1:8000` | OpenAPI Docs: `http://127.0.0.1:8000/docs`  
**API Version:** `v1` (FastAPI 0.110+)

---

## 1. Endpoint Summary Matrix

| Method | Endpoint Path | Tag | Description | Response Model | HTTP Status |
| :--- | :--- | :--- | :--- | :--- | :---: |
| `GET` | `/health` | System | Health verification and offline status | `HealthResponse` | 200 |
| `GET` | `/system/status` | System | Record counts and model status | `SystemStatusResponse` | 200 |
| `POST` | `/api/v1/ingest` | Ingest | Ingest GeoTIFF raster & generate embeddings | `IngestResponse` | 201 |
| `GET` | `/api/v1/ingest/{job_id}` | Ingest | Query status & provenance of ingestion run | `ProcessingJobResponse` | 200 |
| `GET` | `/api/v1/observations` | Observations | List observations with sensor filter & pagination | `list[ObservationItem]` | 200 |
| `POST` | `/api/v1/search/image` | Search | Visual similarity search by observation ID | `SearchResponse` | 200 |
| `POST` | `/api/v1/search/text` | Search | Semantic text search (requires RemoteCLIP) | `SearchResponse` | 503 / 200 |
| `GET` | `/api/v1/locations/{id}` | Locations | Retrieve location details and count | `LocationDetailResponse` | 200 |
| `GET` | `/api/v1/locations/{id}/timeline` | Locations | Time-series observations ordered by date | `list[LocationTimelineItem]` | 200 |
| `POST` | `/api/v1/similar-locations` | Locations | Retrieve similar locations by latest embedding | `SearchResponse` | 200 |
| `POST` | `/api/v1/change/analyze` | Change | Multi-band CVA bi-temporal change detection | `ChangeAnalysisResponse` | 201 |
| `GET` | `/api/v1/change/{id}` | Change | Retrieve change event details & evidence | `ChangeEventResponse` | 200 |
| `GET` | `/api/v1/change/{id}/provenance` | Change | Processing trail and algorithm provenance | `ChangeProvenanceResponse` | 200 |
| `POST` | `/api/v1/change/{id}/review` | Review | Submit analyst decision (`confirmed`, etc.) | `ReviewResponse` | 201 |
| `GET` | `/api/v1/reviews` | Review | List analyst reviews with pagination | `list[ReviewItem]` | 200 |

---

## 2. Request & Response Payload Examples

### 2.1. Ingestion (`POST /api/v1/ingest`)

#### Request:
```json
{
  "path": "cogs/sentinel2_kibithu_20250101.tif",
  "sensor": "Sentinel-2",
  "acquisition_date": "2025-01-01",
  "location_name": "Kibithu Border Sector",
  "source": "ESA Copernicus"
}
```

#### Response (`HTTP 201 Created`):
```json
{
  "job_id": "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d",
  "status": "completed",
  "observation_id": "a4f82631-419b-4b47-b892-0b192837461a",
  "location_id": "c1928471-5582-4f91-8812-78192837411b"
}
```

---

### 2.2. Change Analysis (`POST /api/v1/change/analyze`)

#### Request:
```json
{
  "before_observation_id": "a4f82631-419b-4b47-b892-0b192837461a",
  "after_observation_id": "f8910293-1123-4123-9912-881920394812"
}
```

#### Response (`HTTP 201 Created`):
```json
{
  "change_id": "e4920192-3381-4912-9901-771829304192",
  "class": "CONSTRUCTION",
  "confidence": 0.9421,
  "evidence": {
    "spectral_difference": 0.718291,
    "delta_ndvi": -0.2415,
    "quality_factor": 1.0,
    "false_alarm_risk": 0.0,
    "evaluated_bands": 4,
    "mask_available": false
  }
}
```

---

### 2.3. Analyst Review Submission (`POST /api/v1/change/{change_id}/review`)

#### Request:
```json
{
  "analyst": "Officer_Kapoor_GEOINT",
  "decision": "confirmed",
  "note": "Verified linear road clearing and structural excavation in Sector 4."
}
```

#### Response (`HTTP 201 Created`):
```json
{
  "review_id": "d1920394-8812-4912-7711-229102938471"
}
```
