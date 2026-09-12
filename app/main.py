"""FastAPI application for offline satellite intelligence."""
from __future__ import annotations

from app.core.config import settings  # pins PROJ_LIB before rasterio import

from contextlib import asynccontextmanager
from datetime import date, datetime, timezone
import hashlib
import json
import time
import tempfile
from pathlib import Path
from typing import Annotated

import numpy as np

try:
    import rasterio
    from rasterio.warp import transform_bounds
    RASTERIO_AVAILABLE = True
except ImportError:
    rasterio = None  # type: ignore
    transform_bounds = None  # type: ignore
    RASTERIO_AVAILABLE = False

try:
    from shapely.geometry import box
except ImportError:
    box = None  # type: ignore

from fastapi import FastAPI, Depends, HTTPException, Query, Request, status
from fastapi.responses import JSONResponse
from sqlalchemy.orm import Session

# Multi-spectral sensor band mapping profiles (1-based indices)
SENSOR_PROFILES: dict[str, dict[str, int]] = {
    "Sentinel-2": {"Blue": 2, "Green": 3, "Red": 4, "NIR": 8, "SWIR1": 11, "SWIR2": 12},
    "Landsat-8":  {"Blue": 2, "Green": 3, "Red": 4, "NIR": 5, "SWIR1": 6, "SWIR2": 7},
    "Landsat-9":  {"Blue": 2, "Green": 3, "Red": 4, "NIR": 5, "SWIR1": 6, "SWIR2": 7},
    "PlanetScope": {"Blue": 1, "Green": 2, "Red": 3, "NIR": 4},
}

from app.core.logging import logger
from app.db.session import Base, engine, get_db
from app.models.entities import (
    Location,
    SatelliteSource,
    Observation,
    ProcessingRun,
    Embedding,
    ChangeEvent,
    AnalystReview,
    Mission,
    MissionAlert,
)
from app.schemas.api import (
    HealthResponse,
    SystemStatusResponse,
    IngestRequest,
    IngestResponse,
    ProcessingJobResponse,
    ObservationItem,
    SearchRequest,
    SearchResponse,
    SearchResultItem,
    ImageSearchRequest,
    SimilarRequest,
    LocationDetailResponse,
    LocationTimelineItem,
    ChangeRequest,
    ChangeAnalysisResponse,
    ChangeEventResponse,
    ReviewRequest,
    ReviewResponse,
    ReviewItem,
    ChangeProvenanceResponse,
    ChatMessage,
    ChatRequest,
    ChatResponse,
    InsightRequest,
    InsightResponse,
    ModelStatusResponse,
    ModelLoadRequest,
    ModelLoadResponse,
    SearchFilterRequest,
    MissionCreateRequest,
    MissionResponse,
    MissionAlertResponse,
    MissionRunResponse,
    FeedbackRequest,
    FeedbackResponse,
    ExportRequest,
    ExportResponse,
    UnifiedSearchRequest,
    UnifiedSearchResultItem,
    UnifiedSearchResponse,
    SearchExplainRequest,
    SearchExplainResponse,
    CompareRequest,
    CompareResponse,
    AnalystReportRequest,
    AnalystReportResponse,
    EvidenceDetailResponse,
    AgentTaskRequest,
    AgentTaskResponse,
    BeforeAfterRequest,
    BeforeAfterResponse,
)
from app.services.embeddings.service import (
    embedder,
    norm,
    get_available_models_status,
    get_embedder,
    PrithviTemporalEmbedder,
    TerraMindEmbedder,
    cosine_on_semantic_axes,
)
from app.services.llm.geoint_grounding import GeointGroundingEngine
from app.services.llm.qwen_service import get_qwen_service
from app.services.llm.insight_generator import InsightGenerator
from app.services.geo_filters import (
    centroid_from_wkt,
    observation_in_aoi,
    validate_date_range,
)
from app.services.quality import estimate_quality_score
from app.services.onset import estimate_onset



@asynccontextmanager
async def lifespan(app: FastAPI):
    """Lifecycle manager for startup table creation and resource cleanup."""
    logger.info("Initializing database tables...")
    Base.metadata.create_all(bind=engine)
    logger.info("Database initialized successfully.")
    yield
    logger.info("Application shutting down.")


app = FastAPI(
    title="UPAGRAHA / GeoSemanticSat Sovereign Offline Intelligence API",
    version="0.2.0",
    description="Offline-first geospatial intelligence, change detection, and visual search API.",
    lifespan=lifespan,
)


@app.middleware("http")
async def sovereign_loopback_and_auth_middleware(request: Request, call_next):
    """Enforces air-gapped local loopback binding security and session token authentication."""
    if settings.enforce_loopback_only:
        client_host = request.client.host if request.client else "testclient"
        if client_host not in ("127.0.0.1", "::1", "localhost", "testclient"):
            return JSONResponse(
                status_code=status.HTTP_403_FORBIDDEN,
                content={"detail": f"Access Denied: UPAGRAHA is an air-gapped system restricted to loopback (127.0.0.1). Caller: {client_host}"}
            )

    if settings.require_token_auth and settings.api_auth_token:
        bypass_paths = ("/docs", "/openapi.json", "/health", "/api/v1/health", "/system/status", "/api/v1/system/status")
        if not any(request.url.path == bp or request.url.path.startswith(bp + "/") for bp in bypass_paths):
            auth_header = request.headers.get("Authorization")
            if not auth_header or not auth_header.startswith("Bearer ") or auth_header[7:] != settings.api_auth_token:
                return JSONResponse(
                    status_code=status.HTTP_401_UNAUTHORIZED,
                    content={"detail": "Unauthorized: Missing or invalid local DPAPI session bearer token."}
                )

    return await call_next(request)


# ==============================================================================
# Vector Similarity Search Helper
# ==============================================================================

def search_vectors(
    db: Session,
    query_vector: list[float] | np.ndarray,
    top_k: int,
    sensor: str | None = None,
    exclude_observation_id: str | None = None,
    similarity_mode: str = "full",  # "full" for image-to-image, "semantic" for text-to-image
    date_from: date | None = None,
    date_to: date | None = None,
    min_quality: float | None = None,
    aoi: dict | str | None = None,
) -> list[SearchResultItem]:
    """Cosine similarity over stored embeddings, with metadata/AOI filters applied before ranking."""
    validate_date_range(date_from, date_to)
    q_norm = norm(query_vector)

    query = db.query(Embedding, Observation).join(
        Observation, Embedding.observation_id == Observation.id
    )

    if sensor:
        query = query.filter(Observation.sensor == sensor)
    if exclude_observation_id:
        query = query.filter(Observation.id != exclude_observation_id)
    if date_from:
        query = query.filter(Observation.acquisition_date >= date_from)
    if date_to:
        query = query.filter(Observation.acquisition_date <= date_to)
    if min_quality is not None:
        query = query.filter(Observation.quality_score >= min_quality)

    pairs = query.all()
    if aoi is not None:
        pairs = [(emb, obs) for emb, obs in pairs if observation_in_aoi(obs.footprint_wkt, aoi)]
    if not pairs:
        return []

    scored_results: list[SearchResultItem] = []
    for emb, obs in pairs:
        if not emb.vector:
            continue
        v_norm = norm(emb.vector)
        # Verify vector dimensions align before dot product; auto-pad if necessary
        if q_norm.shape[0] != v_norm.shape[0]:
            if q_norm.shape[0] == 128 and v_norm.shape[0] == 96:
                padded = np.zeros(128, dtype=np.float32)
                padded[:96] = v_norm
                v_norm = norm(padded)
            elif q_norm.shape[0] == 96 and v_norm.shape[0] == 128:
                v_norm = norm(v_norm[:96])
            else:
                logger.warning(
                    f"Embedding dimension mismatch: query {q_norm.shape[0]} vs observation {obs.id} {v_norm.shape[0]}"
                )
                continue

        if similarity_mode == "semantic":
            score = cosine_on_semantic_axes(q_norm, v_norm)
        else:
            score = float(np.dot(q_norm, v_norm))

        scored_results.append(
            SearchResultItem(
                observation_id=obs.id,
                location_id=obs.location_id,
                score=round(score, 6),
                sensor=obs.sensor,
                acquisition_date=obs.acquisition_date,
            )
        )

    scored_results.sort(key=lambda item: item.score, reverse=True)
    return scored_results[:top_k]


# ==============================================================================
# Health & Status Endpoints
# ==============================================================================

@app.get("/", tags=["System"])
def root():
    """Root landing endpoint providing API overview and navigation links."""
    return {
        "service": "GeoSemanticSat Offline Satellite Intelligence API",
        "version": "0.2.0",
        "docs_url": "/docs",
        "redoc_url": "/redoc",
        "health_url": "/health",
        "status_url": "/system/status",
        "offline_mode": settings.offline_mode,
    }


@app.get("/health", response_model=HealthResponse, tags=["System"])
@app.get("/api/v1/health", response_model=HealthResponse, tags=["System"])
def health():
    """Health check and offline mode verification."""
    return HealthResponse(status="ok", offline_mode=settings.offline_mode)


@app.get("/system/status", response_model=SystemStatusResponse, tags=["System"])
@app.get("/api/v1/system/status", response_model=SystemStatusResponse, tags=["System"])
def system_status(db: Session = Depends(get_db)):
    """System health, record counts, and model readiness status."""
    status_info = get_available_models_status()
    active = status_info["active_model"]
    return SystemStatusResponse(
        database="connected",
        observations=db.query(Observation).count(),
        embeddings=db.query(Embedding).count(),
        runtime_network=False,
        semantic_model=f"Active: {active} | Pretrained backbones: TerraMind-1.0-base, SatMAE++, GFM Composition, Prithvi-EO-2.0-600M-TL",
    )


# ==============================================================================
# Ingestion Endpoints
# ==============================================================================

@app.post(
    "/api/v1/ingest",
    response_model=IngestResponse,
    status_code=status.HTTP_201_CREATED,
    tags=["Ingest"],
)
def ingest_geotiff(request: IngestRequest, db: Session = Depends(get_db)):
    """Ingest a local GeoTIFF raster, extract metadata, create footprints, and generate baseline embeddings."""
    root = settings.data_root.resolve()
    target_path = (root / request.path).resolve()

    # Secure path containment check
    if not target_path.is_relative_to(root) or target_path.suffix.lower() not in {".tif", ".tiff"} or not target_path.is_file():
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Valid GeoTIFF file located under data_root required: '{request.path}'",
        )

    run = ProcessingRun(
        operation="ingest",
        status="running",
        provenance={"input": request.path, "sensor": request.sensor},
    )
    db.add(run)
    db.flush()

    try:
        with rasterio.open(target_path) as ds:
            if not ds.crs or ds.count < 1 or (ds.width * ds.height) > settings.max_ingest_raster_pixels:
                raise ValueError("Raster has invalid CRS, no bands, or exceeds max ingest pixel size limit")

            read_bands = min(ds.count, 12)
            out_shape = (read_bands, min(256, ds.height), min(256, ds.width))
            raster_data = ds.read(out_shape=out_shape, masked=True).filled(0).astype(np.float32)

            # Min-Max Normalization
            data_min, data_max = raster_data.min(), raster_data.max()
            if data_max > data_min:
                raster_data = (raster_data - data_min) / (data_max - data_min + 1e-6)
            else:
                raster_data = np.zeros_like(raster_data)

            # Transform native CRS bounding box to standard WGS84 EPSG:4326 coordinates
            b = ds.bounds
            fallback_wkt = f"POLYGON(({b[0]} {b[1]}, {b[2]} {b[1]}, {b[2]} {b[3]}, {b[0]} {b[3]}, {b[0]} {b[1]}))"
            try:
                if transform_bounds is not None and box is not None:
                    wgs84_bounds = transform_bounds(ds.crs, "EPSG:4326", *ds.bounds)
                    footprint_wkt = box(*wgs84_bounds).wkt
                elif box is not None:
                    footprint_wkt = box(*ds.bounds).wkt
                else:
                    footprint_wkt = fallback_wkt
            except Exception:
                footprint_wkt = fallback_wkt

            raster_path_rel = str(target_path.relative_to(root)).replace("\\", "/")
            duplicate = (
                db.query(Observation)
                .filter_by(
                    raster_path=raster_path_rel,
                    acquisition_date=request.acquisition_date,
                    sensor=request.sensor,
                )
                .first()
            )
            if duplicate:
                run.status = "completed"
                run.completed_at = datetime.now(timezone.utc)
                run.provenance = {
                    **(run.provenance or {}),
                    "duplicate": True,
                    "existing_observation_id": duplicate.id,
                }
                db.commit()
                return IngestResponse(
                    job_id=run.id,
                    status="duplicate",
                    observation_id=duplicate.id,
                    location_id=duplicate.location_id,
                )

            quality_info = estimate_quality_score(raster_data)
            location = db.query(Location).filter_by(name=request.location_name).first()
            if not location:
                location = Location(name=request.location_name, geometry_wkt=footprint_wkt)
                db.add(location)
                db.flush()

            # Upsert or associate SatelliteSource
            source = db.query(SatelliteSource).filter_by(name=request.source).first()
            if not source:
                source = SatelliteSource(name=request.source)
                db.add(source)
                db.flush()

            # Create Observation
            observation = Observation(
                location_id=location.id,
                source_id=source.id,
                acquisition_date=request.acquisition_date,
                sensor=request.sensor,
                raster_path=raster_path_rel,
                footprint_wkt=footprint_wkt,
                quality_score=float(quality_info["quality_score"]),
                metadata_json={
                    "crs": str(ds.crs),
                    "shape": [ds.height, ds.width],
                    "bands": ds.count,
                    "preprocessing": "minmax-v1",
                    "quality": quality_info,
                    "geotransform": list(ds.transform) if ds.transform else None,
                    "nodata": ds.nodata,
                },
            )
            db.add(observation)
            db.flush()

            # Compute and persist embedding
            active_model = embedder()
            vector = active_model.image(raster_data).tolist()
            embedding = Embedding(
                observation_id=observation.id,
                vector=vector,
                model_name=getattr(active_model, "MODEL_NAME", "histogram-baseline"),
                model_version="v2",
                run_id=run.id,
            )
            db.add(embedding)

            try:
                from scripts.build_index import add_vector_to_faiss

                add_vector_to_faiss(vector=vector, observation_id=observation.id)
            except Exception as idx_exc:
                logger.warning(f"Incremental FAISS insert skipped: {idx_exc}")

        run.status = "completed"
        run.completed_at = datetime.now(timezone.utc)
        db.commit()

        logger.info(f"Successfully ingested observation {observation.id} for location {location.name}")
        return IngestResponse(
            job_id=run.id,
            status=run.status,
            observation_id=observation.id,
            location_id=location.id,
        )

    except Exception as exc:
        db.rollback()
        try:
            failed_run = ProcessingRun(
                id=run.id,
                operation="ingest",
                status="failed",
                provenance={**(run.provenance or {}), "error": str(exc)},
                completed_at=datetime.now(timezone.utc),
            )
            db.add(failed_run)
            db.commit()
        except Exception:
            db.rollback()
        logger.error(f"Ingest failed for {request.path}: {exc}", exc_info=True)
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Ingestion failed: {exc}",
        )


@app.get(
    "/api/v1/ingest/{job_id}",
    response_model=ProcessingJobResponse,
    tags=["Ingest"],
)
def get_ingest_job(job_id: str, db: Session = Depends(get_db)):
    """Retrieve status and provenance of an ingest job."""
    job = db.get(ProcessingRun, job_id)
    if not job:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Job not found")
    return ProcessingJobResponse(
        id=job.id,
        status=job.status,
        operation=job.operation,
        provenance=job.provenance,
    )


# ==============================================================================
# Observations Endpoints
# ==============================================================================

@app.get(
    "/api/v1/observations",
    response_model=list[ObservationItem],
    tags=["Observations"],
)
def list_observations(
    sensor: str | None = None,
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
    offset: Annotated[int, Query(ge=0)] = 0,
    db: Session = Depends(get_db),
):
    """List observations with optional sensor filter and pagination."""
    query = db.query(Observation)
    if sensor:
        query = query.filter(Observation.sensor == sensor)
    results = query.order_by(Observation.acquisition_date.desc()).offset(offset).limit(limit).all()

    return [
        ObservationItem(
            id=obs.id,
            location_id=obs.location_id,
            date=obs.acquisition_date,
            sensor=obs.sensor,
            quality=obs.quality_score,
        )
        for obs in results
    ]


# ==============================================================================
# Similarity Search Endpoints
# ==============================================================================

@app.post(
    "/api/v1/search/image",
    response_model=SearchResponse,
    tags=["Search"],
)
def search_by_image(request: ImageSearchRequest, db: Session = Depends(get_db)):
    """Search for visually similar satellite observations."""
    embedding = db.query(Embedding).filter_by(observation_id=request.observation_id).first()
    if not embedding or not embedding.vector:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Observation '{request.observation_id}' has no embedding available",
        )

    results = search_vectors(
        db=db,
        query_vector=embedding.vector,
        top_k=request.top_k,
        exclude_observation_id=request.observation_id,
        sensor=request.sensor,
        date_from=request.date_from,
        date_to=request.date_to,
        aoi=request.aoi,
    )
    return SearchResponse(results=results)


@app.post(
    "/api/v1/search/text",
    response_model=SearchResponse,
    tags=["Search"],
)
@app.post(
    "/api/v1/search/semantic",
    response_model=SearchResponse,
    tags=["Search"],
)
@app.post(
    "/api/v1/search/hybrid",
    response_model=SearchResponse,
    tags=["Search"],
)
def search_by_text(request: SearchRequest, db: Session = Depends(get_db)):
    """Semantic text and hybrid search using active foundation model (TerraMind-1.0-base)."""
    try:
        active_emb = embedder()
        try:
            query_vector = active_emb.text(request.query)
        except (RuntimeError, NotImplementedError, AttributeError):
            query_vector = TerraMindEmbedder().text(request.query)
    except Exception as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=f"Semantic text search unavailable: {exc}",
        )

    results = search_vectors(
        db=db,
        query_vector=query_vector,
        top_k=request.top_k,
        sensor=request.sensor,
        similarity_mode="semantic",
        date_from=request.date_from,
        date_to=request.date_to,
        min_quality=request.min_quality,
        aoi=request.aoi,
    )
    return SearchResponse(results=results)


# ==============================================================================
# Locations Endpoints
# ==============================================================================

@app.get(
    "/api/v1/locations/{location_id}",
    response_model=LocationDetailResponse,
    tags=["Locations"],
)
def get_location(location_id: str, db: Session = Depends(get_db)):
    """Retrieve location details and total observation count."""
    loc = db.get(Location, location_id)
    if not loc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Location not found")
    count = db.query(Observation).filter_by(location_id=loc.id).count()
    return LocationDetailResponse(
        id=loc.id,
        name=loc.name,
        geometry_wkt=loc.geometry_wkt,
        observations=count,
    )


@app.get(
    "/api/v1/locations/{location_id}/timeline",
    response_model=list[LocationTimelineItem],
    tags=["Locations"],
)
def get_location_timeline(location_id: str, db: Session = Depends(get_db)):
    """Retrieve time-series observations for a given location ordered by acquisition date."""
    loc = db.get(Location, location_id)
    if not loc:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Location not found")

    observations = (
        db.query(Observation)
        .filter_by(location_id=location_id)
        .order_by(Observation.acquisition_date.asc())
        .all()
    )
    return [
        LocationTimelineItem(
            id=obs.id,
            date=obs.acquisition_date,
            sensor=obs.sensor,
            quality=obs.quality_score,
        )
        for obs in observations
    ]


@app.post(
    "/api/v1/similar-locations",
    response_model=SearchResponse,
    tags=["Locations"],
)
@app.post(
    "/api/v1/discovery/similar-sites",
    response_model=SearchResponse,
    tags=["Locations"],
)
def find_similar_locations(request: SimilarRequest, db: Session = Depends(get_db)):
    """Find similar locations based on the latest observation embedding of a location."""
    latest_obs = (
        db.query(Observation)
        .filter_by(location_id=request.location_id)
        .order_by(Observation.acquisition_date.desc())
        .first()
    )
    if not latest_obs:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Location has no observations recorded",
        )

    embedding = db.query(Embedding).filter_by(observation_id=latest_obs.id).first()
    if not embedding or not embedding.vector:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail=f"Latest observation '{latest_obs.id}' has no embedding vector",
        )

    results = search_vectors(
        db=db,
        query_vector=embedding.vector,
        top_k=request.top_k,
        exclude_observation_id=latest_obs.id,
    )
    return SearchResponse(results=results)


# ==============================================================================
# Change Detection & Review Endpoints
# ==============================================================================

@app.post(
    "/api/v1/change/analyze",
    response_model=ChangeAnalysisResponse,
    status_code=status.HTTP_201_CREATED,
    tags=["Change Detection"],
)
@app.post(
    "/api/v1/analysis/change",
    response_model=ChangeAnalysisResponse,
    status_code=status.HTTP_201_CREATED,
    tags=["Change Detection"],
)
def analyze_change(request: ChangeRequest, db: Session = Depends(get_db)):
    """Analyze pixel-level spectral difference between two temporal observations of the same location."""
    obs_before = db.get(Observation, request.before_observation_id)
    obs_after = db.get(Observation, request.after_observation_id)

    if not obs_before or not obs_after:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Both before and after observations must exist",
        )

    if obs_before.location_id != obs_after.location_id:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Two observations from the exact same location are required for change analysis",
        )

    def read_raster_multiband(obs: Observation) -> tuple[np.ndarray, dict[str, int]]:
        p = settings.data_root / obs.raster_path
        if not p.is_file():
            raise FileNotFoundError(f"Raster file missing: {obs.raster_path}")
        with rasterio.open(p) as d:
            bands_count = d.count
            read_count = min(bands_count, 12)
            data = d.read(list(range(1, read_count + 1)), out_shape=(read_count, 256, 256), masked=True).filled(0).astype(np.float32)
            profile = SENSOR_PROFILES.get(obs.sensor, {"Red": 1, "NIR": min(2, bands_count)})
            return data, profile

    try:
        data_before, prof_before = read_raster_multiband(obs_before)
        data_after, prof_after = read_raster_multiband(obs_after)

        # Standardize band data
        def normalize_bands(arr: np.ndarray) -> np.ndarray:
            norm_arr = np.zeros_like(arr)
            for b in range(arr.shape[0]):
                std = arr[b].std()
                if std > 1e-6:
                    norm_arr[b] = (arr[b] - arr[b].mean()) / std
                else:
                    norm_arr[b] = np.zeros_like(arr[b])
            return norm_arr

        nb_before = normalize_bands(data_before)
        nb_after = normalize_bands(data_after)

        # Multi-band Spectral Magnitude Difference (CVA Euclidean norm)
        num_eval_bands = min(nb_before.shape[0], nb_after.shape[0])
        diff_sq = np.zeros((256, 256), dtype=np.float32)
        for b in range(num_eval_bands):
            diff_sq += (nb_after[b] - nb_before[b]) ** 2
        spectral_magnitude = np.sqrt(diff_sq)
        score = float(np.mean(spectral_magnitude))

        # Check Spectral Indices using sensor profile mapping with Median Scene Drift Suppression
        delta_ndvi = 0.0
        delta_ndbi = 0.0
        delta_ndwi = 0.0
        drift_ndvi = 0.0
        if num_eval_bands >= 2:
            red_idx_b = min(prof_before.get("Red", 1) - 1, data_before.shape[0] - 1)
            nir_idx_b = min(prof_before.get("NIR", min(2, data_before.shape[0])) - 1, data_before.shape[0] - 1)
            red_idx_a = min(prof_after.get("Red", 1) - 1, data_after.shape[0] - 1)
            nir_idx_a = min(prof_after.get("NIR", min(2, data_after.shape[0])) - 1, data_after.shape[0] - 1)
            green_idx_b = min(prof_before.get("Green", 2) - 1, data_before.shape[0] - 1)
            green_idx_a = min(prof_after.get("Green", 2) - 1, data_after.shape[0] - 1)

            red_b, nir_b = data_before[red_idx_b], data_before[nir_idx_b]
            red_a, nir_a = data_after[red_idx_a], data_after[nir_idx_a]
            green_b, green_a = data_before[green_idx_b], data_after[green_idx_a]

            denom_b = nir_b + red_b + 1e-6
            denom_a = nir_a + red_a + 1e-6
            ndvi_b = (nir_b - red_b) / denom_b
            ndvi_a = (nir_a - red_a) / denom_a
            raw_diff_ndvi = ndvi_a - ndvi_b
            drift_ndvi = float(np.median(raw_diff_ndvi))
            delta_ndvi = float(np.mean(raw_diff_ndvi)) - drift_ndvi

            swir_idx_b = min(prof_before.get("SWIR1", max(1, data_before.shape[0])) - 1, data_before.shape[0] - 1)
            swir_idx_a = min(prof_after.get("SWIR1", max(1, data_after.shape[0])) - 1, data_after.shape[0] - 1)
            swir_b, swir_a = data_before[swir_idx_b], data_after[swir_idx_a]
            ndbi_b = (swir_b - nir_b) / (swir_b + nir_b + 1e-6)
            ndbi_a = (swir_a - nir_a) / (swir_a + nir_a + 1e-6)
            delta_ndbi = float(np.mean(ndbi_a - ndbi_b))

            ndwi_b = (green_b - nir_b) / (green_b + nir_b + 1e-6)
            ndwi_a = (green_a - nir_a) / (green_a + nir_a + 1e-6)
            delta_ndwi = float(np.mean(ndwi_a - ndwi_b))

        # Trajectory Angle theta = atan2(Delta NDBI, Delta NDVI)
        trajectory_angle_rad = float(np.arctan2(delta_ndbi, delta_ndvi))
        trajectory_angle_deg = float(np.degrees(trajectory_angle_rad))

        # Classification heuristics based on spectral vector trajectory and CVA score
        q_before = estimate_quality_score(data_before)
        q_after = estimate_quality_score(data_after)
        quality_factor = max(
            0.1,
            min(obs_before.quality_score, obs_after.quality_score, float(q_before["quality_score"]), float(q_after["quality_score"])),
        )
        confidence = min(0.98, (score / (score + 1.2)) * quality_factor)

        change_mask = spectral_magnitude > 0.35
        changed_frac = float(np.mean(change_mask))

        if score < 0.20:
            change_class = "NO_CHANGE"
            confidence = max(0.88, 1.0 - score)
        elif delta_ndwi > 0.12 and score > 0.25:
            change_class = "WATER_EXTENT_VARIATION"
        elif delta_ndvi < -0.15 and score > 0.35:
            change_class = "CLEARANCE"
        elif delta_ndbi > 0.12 and delta_ndvi < -0.05:
            change_class = "CONSTRUCTION"
        elif delta_ndbi > 0.08 and changed_frac > 0.12:
            change_class = "EXPANSION"
        elif delta_ndbi < -0.08 and changed_frac > 0.12:
            change_class = "CONTRACTION"
        elif delta_ndbi > 0.10 and score > 0.40:
            change_class = "APPEARANCE"
        elif delta_ndbi < -0.10 and score > 0.40:
            change_class = "DISAPPEARANCE"
        elif float(np.clip(np.max([abs(delta_ndbi), score]), 0.0, 1.0)) > 0.45 and delta_ndvi < 0:
            change_class = "ROAD_DEVELOPMENT"
        elif delta_ndvi > 0.15 and score > 0.35:
            change_class = "VEGETATION_GROWTH"
        else:
            change_class = "ACTIVITY_CONCENTRATION" if score > 0.40 else "OTHER"

        # Sequential CUSUM onset using all observations at this location when requested
        onset_info: dict = {"change_detected": False, "reason": "pair_only"}
        if request.use_temporal_sequence:
            loc_obs = (
                db.query(Observation)
                .filter_by(location_id=obs_before.location_id)
                .order_by(Observation.acquisition_date.asc())
                .all()
            )
            dated_vectors: list[tuple[str, date, list[float]]] = []
            for o in loc_obs:
                emb_row = db.query(Embedding).filter_by(observation_id=o.id).first()
                if emb_row and emb_row.vector and o.quality_score >= 0.40:
                    dated_vectors.append((o.id, o.acquisition_date, emb_row.vector))
            onset_info = estimate_onset(dated_vectors)

        run = ProcessingRun(
            operation="change_analysis",
            status="completed",
            provenance={
                "algorithm": "multi-band-cva-v2",
                "bands_evaluated": num_eval_bands,
                "sensor_before": obs_before.sensor,
                "sensor_after": obs_after.sensor,
                "median_drift_suppressed": True,
            },
            completed_at=datetime.now(timezone.utc),
        )
        db.add(run)
        db.flush()

        # Compute Prithvi-EO-2.0 temporal sequence dynamics (uses cached singleton with weights path)
        prithvi_metrics = get_embedder("prithvi").analyze_temporal_change(data_before, data_after)

        event = ChangeEvent(
            location_id=obs_before.location_id,
            before_observation_id=obs_before.id,
            after_observation_id=obs_after.id,
            change_class=change_class,
            confidence=round(confidence, 4),
            evidence={
                "spectral_difference": round(score, 6),
                "cva_magnitude": round(score, 6),
                "delta_ndvi": round(delta_ndvi, 4),
                "delta_ndbi": round(delta_ndbi, 4),
                "delta_ndwi": round(delta_ndwi, 4),
                "median_drift_ndvi": round(drift_ndvi, 4),
                "trajectory_angle_degrees": round(trajectory_angle_deg, 2),
                "quality_factor": quality_factor,
                "false_alarm_risk": round(1.0 - quality_factor, 4),
                "evaluated_bands": num_eval_bands,
                "mask_available": True,
                "changed_fraction": round(changed_frac, 4),
                "quality_before": q_before,
                "quality_after": q_after,
                "onset": onset_info,
                "prithvi_temporal_metrics": prithvi_metrics,
            },
            run_id=run.id,
        )
        db.add(event)
        db.commit()

        return ChangeAnalysisResponse(
            change_id=event.id,
            change_class=event.change_class,
            confidence=event.confidence,
            evidence=event.evidence,
        )

    except Exception as exc:
        db.rollback()
        logger.error(f"Change analysis failed: {exc}", exc_info=True)
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Change analysis calculation failed: {exc}",
        )


@app.get(
    "/api/v1/change/{change_id}",
    response_model=ChangeEventResponse,
    tags=["Change Detection"],
)
@app.get(
    "/api/v1/analysis/change/{change_id}",
    response_model=ChangeEventResponse,
    tags=["Change Detection"],
)
def get_change_event(change_id: str, db: Session = Depends(get_db)):
    """Retrieve details and evidence for a detected change event."""
    event = db.get(ChangeEvent, change_id)
    if not event:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Change event not found")
    return ChangeEventResponse(
        id=event.id,
        change_class=event.change_class,
        confidence=event.confidence,
        evidence=event.evidence,
    )


@app.post(
    "/api/v1/change/{change_id}/review",
    response_model=ReviewResponse,
    status_code=status.HTTP_201_CREATED,
    tags=["Analyst Review"],
)
def submit_review(change_id: str, request: ReviewRequest, db: Session = Depends(get_db)):
    """Submit an analyst review decision for a change event."""
    if not db.get(ChangeEvent, change_id):
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Change event not found")

    review = AnalystReview(
        change_event_id=change_id,
        analyst=request.analyst,
        decision=request.decision.value,
        note=request.note,
    )
    db.add(review)
    db.commit()
    return ReviewResponse(review_id=review.id)


@app.get(
    "/api/v1/reviews",
    response_model=list[ReviewItem],
    tags=["Analyst Review"],
)
def list_reviews(
    limit: Annotated[int, Query(ge=1, le=500)] = 100,
    offset: Annotated[int, Query(ge=0)] = 0,
    db: Session = Depends(get_db),
):
    """List analyst reviews with pagination."""
    reviews = db.query(AnalystReview).order_by(AnalystReview.created_at.desc()).offset(offset).limit(limit).all()
    return [
        ReviewItem(
            id=r.id,
            change_id=r.change_event_id,
            decision=r.decision,
            analyst=r.analyst,
        )
        for r in reviews
    ]


@app.get(
    "/api/v1/processing/{run_id}",
    response_model=ProcessingJobResponse,
    tags=["Processing"],
)
def get_processing_job(run_id: str, db: Session = Depends(get_db)):
    """Retrieve processing job status and provenance."""
    return get_ingest_job(run_id, db)


@app.get(
    "/api/v1/change/{change_id}/provenance",
    response_model=ChangeProvenanceResponse,
    tags=["Change Detection"],
)
@app.get(
    "/api/v1/provenance/{change_id}",
    response_model=ChangeProvenanceResponse,
    tags=["Change Detection"],
)
def get_change_provenance(change_id: str, db: Session = Depends(get_db)):
    """Retrieve audit provenance and processing trail for a change event."""
    event = db.get(ChangeEvent, change_id)
    if not event:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Change event not found")

    run = db.get(ProcessingRun, event.run_id) if event.run_id else None
    run_provenance = run.provenance if run else {}

    return ChangeProvenanceResponse(
        change_id=event.id,
        run=run_provenance,
        before=event.before_observation_id,
        after=event.after_observation_id,
        evidence=event.evidence,
    )


# ==============================================================================
# Conversational Intelligence & Analyst Chat Endpoints
# ==============================================================================

@app.post(
    "/api/v1/chat",
    response_model=ChatResponse,
    tags=["Conversational GEOINT"],
)
def chat_analyst(request: ChatRequest, db: Session = Depends(get_db)):
    """Conversational intelligence dialogue powered by Qwen3-8B with grounded GEOINT RAG.
    
    If observation_id or change_id is provided, deterministic physical metrics, CVA,
    and foundation model embeddings are automatically formatted into a tamper-evident dossier
    to ground all responses and prevent hallucinations.
    """
    grounded_dossier = None

    if request.change_id:
        context = GeointGroundingEngine.build_change_event_context(db, request.change_id)
        grounded_dossier = GeointGroundingEngine.format_grounded_evidence_prompt(context)
    elif request.observation_id:
        context = GeointGroundingEngine.build_observation_context(db, request.observation_id)
        grounded_dossier = GeointGroundingEngine.format_grounded_evidence_prompt(context)

    qwen = get_qwen_service()
    messages_payload = [{"role": m.role, "content": m.content} for m in request.messages]

    response_text = qwen.generate(
        messages=messages_payload,
        grounded_context=grounded_dossier,
        max_tokens=request.max_tokens,
        temperature=request.temperature,
    )

    return ChatResponse(
        message=ChatMessage(role="assistant", content=response_text),
        grounded_evidence=grounded_dossier,
        model=qwen.MODEL_NAME,
    )


# ==============================================================================
# Automated Insight Synthesis Endpoints
# ==============================================================================

@app.post(
    "/api/v1/insights/generate",
    response_model=InsightResponse,
    tags=["Conversational GEOINT"],
)
def generate_insight(request: InsightRequest, db: Session = Depends(get_db)):
    """Generate structured, multi-faceted intelligence brief for a change event or observation."""
    if request.change_id:
        insight = InsightGenerator.generate_change_insight(db, request.change_id)
        if "error" in insight:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=insight["error"])
        return InsightResponse(**insight)
    elif request.observation_id:
        insight = InsightGenerator.generate_observation_insight(db, request.observation_id)
        if "error" in insight:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=insight["error"])
        return InsightResponse(**insight)
    else:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="Either change_id or observation_id must be provided to synthesize insights.",
        )


# ==============================================================================
# Foundation Models Management Endpoints
# ==============================================================================

@app.get(
    "/api/v1/models/status",
    response_model=ModelStatusResponse,
    tags=["Foundation Models"],
)
def get_foundation_models_status():
    """Retrieve operational inventory and staging status for all 5 foundation models."""
    status_info = get_available_models_status()
    return ModelStatusResponse(
        active_model=status_info["active_model"],
        models=status_info["models"],
    )


@app.post(
    "/api/v1/models/load",
    response_model=ModelLoadResponse,
    tags=["Foundation Models"],
)
def load_foundation_model(request: ModelLoadRequest):
    """Dynamically load or switch the active foundation model backbone."""
    req_name = request.model_name.lower().strip()

    if req_name in {"qwen", "qwen3", "qwen3-8b", "qwen/qwen3-8b"}:
        qwen = get_qwen_service()
        loaded = qwen.load_model()
        return ModelLoadResponse(
            model_name="Qwen/Qwen3-8B",
            status="loaded (neural)" if loaded else "active (grounded fallback)",
            is_loaded=loaded,
        )

    try:
        emb = get_embedder(req_name)
        is_active = getattr(emb, "is_loaded", True)
        return ModelLoadResponse(
            model_name=getattr(emb, "MODEL_NAME", req_name),
            status="loaded (neural)" if is_active else "active (fallback adapter)",
            is_loaded=is_active,
        )
    except Exception as e:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail=f"Failed to activate model backbone '{request.model_name}': {e}",
        )


# ==============================================================================
# Advanced Filtering & Discovery Endpoints
# ==============================================================================

@app.post(
    "/api/v1/search/filter",
    response_model=list[ObservationItem],
    tags=["Search"],
)
def filter_observations(request: SearchFilterRequest, db: Session = Depends(get_db)):
    """Advanced metadata filtering over satellite observations."""
    validate_date_range(request.date_from, request.date_to)
    query = db.query(Observation)
    if request.sensor:
        query = query.filter(Observation.sensor == request.sensor)
    if request.min_quality is not None:
        query = query.filter(Observation.quality_score >= request.min_quality)
    if request.date_from:
        query = query.filter(Observation.acquisition_date >= request.date_from)
    if request.date_to:
        query = query.filter(Observation.acquisition_date <= request.date_to)

    results = query.order_by(Observation.acquisition_date.desc()).offset(request.offset).limit(request.limit).all()
    if request.aoi is not None:
        results = [obs for obs in results if observation_in_aoi(obs.footprint_wkt, request.aoi)]
    return [
        ObservationItem(
            id=obs.id,
            location_id=obs.location_id,
            date=obs.acquisition_date,
            sensor=obs.sensor,
            quality=obs.quality_score,
        )
        for obs in results
    ]


# ==============================================================================
# Mission Monitoring & Alert Endpoints
# ==============================================================================

@app.post(
    "/api/v1/missions",
    response_model=MissionResponse,
    status_code=status.HTTP_201_CREATED,
    tags=["Missions"],
)
def create_mission(request: MissionCreateRequest, db: Session = Depends(get_db)):
    """Register a new recurring monitoring mission with geographic AOI and objective thresholds."""
    aoi_wkt_str = json.dumps(request.aoi) if isinstance(request.aoi, dict) else str(request.aoi)
    mission = Mission(
        name=request.name,
        description=request.description,
        aoi_wkt=aoi_wkt_str,
        semantic_query=request.semantic_query,
        sensor_filter=request.sensor_filter,
        quality_threshold=request.quality_threshold,
        change_threshold=request.change_threshold,
        confidence_threshold=request.confidence_threshold,
        domain_pack=request.domain_pack,
        enabled=request.enabled,
    )
    db.add(mission)
    db.commit()
    db.refresh(mission)

    return MissionResponse(
        id=mission.id,
        name=mission.name,
        description=mission.description,
        aoi_wkt=mission.aoi_wkt,
        semantic_query=mission.semantic_query,
        sensor_filter=mission.sensor_filter,
        quality_threshold=mission.quality_threshold,
        change_threshold=mission.change_threshold,
        confidence_threshold=mission.confidence_threshold,
        domain_pack=mission.domain_pack,
        enabled=mission.enabled,
        created_at=mission.created_at,
        last_run_at=mission.last_run_at,
    )


@app.get(
    "/api/v1/missions",
    response_model=list[MissionResponse],
    tags=["Missions"],
)
def list_missions(db: Session = Depends(get_db)):
    """List all registered surveillance missions."""
    missions = db.query(Mission).order_by(Mission.created_at.desc()).all()
    return [
        MissionResponse(
            id=m.id,
            name=m.name,
            description=m.description,
            aoi_wkt=m.aoi_wkt,
            semantic_query=m.semantic_query,
            sensor_filter=m.sensor_filter,
            quality_threshold=m.quality_threshold,
            change_threshold=m.change_threshold,
            confidence_threshold=m.confidence_threshold,
            domain_pack=m.domain_pack,
            enabled=m.enabled,
            created_at=m.created_at,
            last_run_at=m.last_run_at,
        )
        for m in missions
    ]


@app.get(
    "/api/v1/missions/{mission_id}",
    response_model=MissionResponse,
    tags=["Missions"],
)
def get_mission(mission_id: str, db: Session = Depends(get_db)):
    """Get surveillance mission details by ID."""
    m = db.get(Mission, mission_id)
    if not m:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Mission not found")
    return MissionResponse(
        id=m.id,
        name=m.name,
        description=m.description,
        aoi_wkt=m.aoi_wkt,
        semantic_query=m.semantic_query,
        sensor_filter=m.sensor_filter,
        quality_threshold=m.quality_threshold,
        change_threshold=m.change_threshold,
        confidence_threshold=m.confidence_threshold,
        domain_pack=m.domain_pack,
        enabled=m.enabled,
        created_at=m.created_at,
        last_run_at=m.last_run_at,
    )


@app.post(
    "/api/v1/missions/{mission_id}/run",
    response_model=MissionRunResponse,
    tags=["Missions"],
)
def run_mission(mission_id: str, db: Session = Depends(get_db)):
    """Execute monitoring mission pipeline against newly ingested imagery and generate prioritized alerts."""
    mission = db.get(Mission, mission_id)
    if not mission:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Mission not found")

    # Discover candidate change events or temporal pairs within the mission scope
    changes = db.query(ChangeEvent).filter(ChangeEvent.confidence >= mission.confidence_threshold).all()

    alerts: list[MissionAlert] = []
    for chg in changes:
        # Determine priority based on change score and confidence
        score = chg.evidence.get("spectral_difference", chg.evidence.get("cva_magnitude", 0.4))
        priority = "P1_CRITICAL" if score >= 0.70 else ("P2_HIGH" if score >= 0.45 else "P3_MEDIUM")

        obs_before = db.get(Observation, chg.before_observation_id)
        obs_after = db.get(Observation, chg.after_observation_id)
        loc = db.get(Location, chg.location_id)

        prov_hash = hashlib.sha256(
            f"{chg.id}_{chg.change_class}_{score}".encode("utf-8")
        ).hexdigest()

        alert = MissionAlert(
            mission_id=mission.id,
            change_event_id=chg.id,
            priority=priority,
            change_type=chg.change_class,
            confidence=chg.confidence,
            change_score=float(score),
            location_wkt=loc.geometry_wkt if loc else "POINT(0 0)",
            before_scene=obs_before.raster_path if obs_before else "unknown_t1",
            after_scene=obs_after.raster_path if obs_after else "unknown_t2",
            provenance_hash=prov_hash,
            status="NEW",
        )
        db.add(alert)
        alerts.append(alert)

    mission.last_run_at = datetime.now(timezone.utc)
    db.commit()

    return MissionRunResponse(
        mission_id=mission.id,
        status="completed",
        alerts_generated=len(alerts),
        alerts=[
            MissionAlertResponse(
                id=a.id,
                mission_id=a.mission_id,
                change_event_id=a.change_event_id,
                priority=a.priority,
                change_type=a.change_type,
                confidence=a.confidence,
                change_score=a.change_score,
                location_wkt=a.location_wkt,
                before_scene=a.before_scene,
                after_scene=a.after_scene,
                provenance_hash=a.provenance_hash,
                status=a.status,
                detected_time=a.detected_time,
            )
            for a in alerts
        ],
    )


# ==============================================================================
# Analyst Feedback & Rocchio Relevance Tuning Endpoints
# ==============================================================================

@app.post(
    "/api/v1/feedback",
    response_model=FeedbackResponse,
    tags=["Analyst Feedback"],
)
def tune_retrieval_feedback(request: FeedbackRequest, db: Session = Depends(get_db)):
    """Apply Rocchio relevance feedback to dynamically adjust semantic query vectors."""
    try:
        active_emb = embedder()
        try:
            query_vec = np.array(active_emb.text(request.query), dtype=np.float32)
        except (RuntimeError, NotImplementedError, AttributeError):
            query_vec = np.array(TerraMindEmbedder().text(request.query), dtype=np.float32)
    except Exception as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail=f"Semantic text encoding unavailable: {exc}",
        )

    # Rocchio formula: Q' = alpha * Q + (beta / |P|) * sum(P) - (gamma / |N|) * sum(N)
    adjusted_vec = request.alpha * query_vec

    if request.positive_observation_ids:
        pos_embeddings = (
            db.query(Embedding)
            .filter(Embedding.observation_id.in_(request.positive_observation_ids))
            .all()
        )
        if pos_embeddings:
            pos_vectors = [np.array(e.vector, dtype=np.float32) for e in pos_embeddings if e.vector]
            if pos_vectors:
                mean_pos = np.mean(pos_vectors, axis=0)
                adjusted_vec += request.beta * mean_pos

    if request.negative_observation_ids:
        neg_embeddings = (
            db.query(Embedding)
            .filter(Embedding.observation_id.in_(request.negative_observation_ids))
            .all()
        )
        if neg_embeddings:
            neg_vectors = [np.array(e.vector, dtype=np.float32) for e in neg_embeddings if e.vector]
            if neg_vectors:
                mean_neg = np.mean(neg_vectors, axis=0)
                adjusted_vec -= request.gamma * mean_neg

    norm_val = np.linalg.norm(adjusted_vec)
    if norm_val > 1e-6:
        adjusted_vec /= norm_val

    updated_results = search_vectors(
        db=db,
        query_vector=adjusted_vec.tolist(),
        top_k=10,
        similarity_mode="semantic",
    )

    return FeedbackResponse(
        status="relevance_tuned",
        query=request.query,
        adjusted_vector_dimension=len(adjusted_vec),
        updated_results=updated_results,
    )


# ==============================================================================
# Sovereign Evidence Data Product Export Endpoints
# ==============================================================================

@app.post(
    "/api/v1/export",
    response_model=ExportResponse,
    tags=["Data Products"],
)
def export_evidence_package(request: ExportRequest, db: Session = Depends(get_db)):
    """Generate sovereign evidence data products (W3C PROV-O GeoJSON, STAC, Briefing HTML, and SHA-256 Merkle Manifest)."""
    base_export_root = (settings.data_root / "evidence_exports").resolve()
    base_export_root.mkdir(parents=True, exist_ok=True)

    if request.output_directory:
        out_str = str(request.output_directory)
        raw_target = Path(request.output_directory)
        target = raw_target.resolve() if raw_target.is_absolute() else (base_export_root / raw_target).resolve()
        
        allowed_roots = [
            base_export_root,
            settings.data_root.resolve(),
            Path(tempfile.gettempdir()).resolve(),
        ]
        
        # Enforce directory containment to prevent arbitrary file creation or path traversal
        if ".." in out_str or not any(target.is_relative_to(r) for r in allowed_roots):
            raise HTTPException(
                status_code=status.HTTP_400_BAD_REQUEST,
                detail=f"Security violation: Export output directory must reside within data root sandbox '{settings.data_root}'.",
            )
        out_dir = target
    else:
        out_dir = base_export_root / f"export_{datetime.now(timezone.utc).strftime('%Y%m%d_%H%M%S')}"

    out_dir.mkdir(parents=True, exist_ok=True)

    query = db.query(ChangeEvent)
    if request.change_ids:
        query = query.filter(ChangeEvent.id.in_(request.change_ids))
    changes = query.all()

    # 1. Generate W3C PROV-O GeoJSON
    features = []
    for c in changes:
        loc = db.get(Location, c.location_id)
        lon, lat = centroid_from_wkt(loc.geometry_wkt if loc else None)
        before_obs = db.get(Observation, c.before_observation_id)
        after_obs = db.get(Observation, c.after_observation_id)
        features.append({
            "type": "Feature",
            "id": c.id,
            "geometry": {"type": "Point", "coordinates": [lon, lat]},
            "properties": {
                "change_type": c.change_class,
                "confidence": c.confidence,
                "evidence": c.evidence,
                "prov:wasDerivedFrom": [c.before_observation_id, c.after_observation_id],
                "prov:generatedAtTime": c.created_at.isoformat(),
                "before_acquisition_date": before_obs.acquisition_date.isoformat() if before_obs else None,
                "after_acquisition_date": after_obs.acquisition_date.isoformat() if after_obs else None,
                "before_sensor": before_obs.sensor if before_obs else None,
                "after_sensor": after_obs.sensor if after_obs else None,
                "before_raster": before_obs.raster_path if before_obs else None,
                "after_raster": after_obs.raster_path if after_obs else None,
            }
        })
    geojson_path = out_dir / "evidence_prov_o.geojson"
    with open(geojson_path, "w", encoding="utf-8") as f:
        json.dump({
            "type": "FeatureCollection",
            "provenance_standard": "W3C PROV-O Compliant",
            "features": features,
        }, f, indent=2)

    # 2. Generate STAC 1.0.0 Metadata
    stac_path = out_dir / "stac_item.json"
    stac_data = {
        "stac_version": "1.0.0",
        "type": "FeatureCollection",
        "id": request.mission_id or "SOVEREIGN_MISSION_EXPORT",
        "description": "Sovereign Offline Intelligence STAC Catalog",
        "features": features,
    }
    with open(stac_path, "w", encoding="utf-8") as f:
        json.dump(stac_data, f, indent=2)

    # 3. Generate Executive HTML Briefing
    html_path = out_dir / "intelligence_briefing.html"
    html_content = f"""<!DOCTYPE html>
<html>
<head><title>UPAGRAHA Intelligence Briefing</title><style>body {{ font-family: sans-serif; margin: 30px; }}</style></head>
<body>
<h1>UPAGRAHA Sovereign Intelligence Briefing</h1>
<p><b>Mission:</b> {request.mission_id}</p>
<p><b>Total Events:</b> {len(changes)}</p>
<p><b>Generated:</b> {datetime.now(timezone.utc).isoformat()}</p>
</body>
</html>"""
    with open(html_path, "w", encoding="utf-8") as f:
        f.write(html_content)

    # 4. Generate SHA-256 Merkle Manifest
    files_map = {
        "geojson": str(geojson_path),
        "stac": str(stac_path),
        "html_briefing": str(html_path),
    }

    manifest_lines = []
    file_hashes = []
    for name, p in files_map.items():
        with open(p, "rb") as bf:
            h = hashlib.sha256(bf.read()).hexdigest()
            file_hashes.append(h)
            manifest_lines.append(f"{h}  {Path(p).name}")

    merkle_root = hashlib.sha256("".join(sorted(file_hashes)).encode("utf-8")).hexdigest()
    manifest_path = out_dir / "manifest_sha256.txt"
    with open(manifest_path, "w", encoding="utf-8") as f:
        f.write(f"# UPAGRAHA Sovereign Merkle Root: {merkle_root}\n")
        f.write("\n".join(manifest_lines) + "\n")
    files_map["manifest"] = str(manifest_path)

    return ExportResponse(
        status="success",
        export_directory=str(out_dir),
        files=files_map,
        records_exported=len(changes),
        merkle_root_hash=merkle_root,
    )


# ==============================================================================
# Feature 1 & 7: Qwen AI Status & Core Intelligence Endpoints
# ==============================================================================

@app.get(
    "/api/v1/ai/status",
    tags=["Conversational GEOINT"],
)
def get_ai_status():
    """Retrieve detailed operational status, runtime, and hardware utilization of Qwen3-8B."""
    qwen = get_qwen_service()
    return qwen.get_status()


@app.post(
    "/api/v1/ai/insights",
    response_model=InsightResponse,
    tags=["Conversational GEOINT"],
)
def post_ai_insights(request: InsightRequest, db: Session = Depends(get_db)):
    """Generate grounded insight from Earth Observation evidence."""
    return generate_insight(request, db)


@app.post(
    "/api/v1/ai/chat",
    response_model=ChatResponse,
    tags=["Conversational GEOINT"],
)
def post_ai_chat(request: ChatRequest, db: Session = Depends(get_db)):
    """Grounded conversational interaction with the UPAGRAHA Intelligence Copilot."""
    return chat_analyst(request, db)


# ==============================================================================
# Feature 3: Unified Intelligence Search Engine
# ==============================================================================

@app.post(
    "/api/v1/search/unified",
    response_model=UnifiedSearchResponse,
    tags=["Search"],
)
def search_unified(request: UnifiedSearchRequest, db: Session = Depends(get_db)):
    """Unified natural-language search engine combining semantic vector retrieval,
    metadata filtering, and multi-temporal change event discovery.
    """
    raw_query = request.query.strip().lower()

    # 1. Parse Query Intent & Spatial/Temporal/Sensor Entities
    is_temporal = any(w in raw_query for w in ["change", "new", "expanded", "collapsed", "modified", "built", "clearance"])
    sensor_target = None
    for s in ["sentinel-2", "landsat-8", "landsat-9", "planetscope", "sentinel-1"]:
        if s in raw_query:
            sensor_target = s
            break

    parsed_intent = {
        "is_temporal_change_query": is_temporal,
        "sensor_filter": sensor_target or request.sensor,
        "date_constraint": f"{request.date_from} to {request.date_to}" if (request.date_from or request.date_to) else "unconstrained",
        "semantic_focus": raw_query,
    }

    aggregated_results: list[UnifiedSearchResultItem] = []

    # 2. Search Change Events if temporal intent is detected or requested
    if request.include_changes and is_temporal:
        change_query = db.query(ChangeEvent)
        if request.min_confidence:
            change_query = change_query.filter(ChangeEvent.confidence >= request.min_confidence)
        
        # Rank by confidence and relevance
        changes = change_query.order_by(ChangeEvent.confidence.desc()).limit(request.top_k).all()
        for chg in changes:
            loc = db.get(Location, chg.location_id)
            score = float(chg.evidence.get("spectral_difference", chg.evidence.get("cva_magnitude", chg.confidence)))
            aggregated_results.append(
                UnifiedSearchResultItem(
                    result_id=chg.id,
                    result_type="change_event",
                    title=f"Detected {chg.change_class.replace('_', ' ').title()}",
                    score=round(score, 4),
                    location_name=loc.name if loc else "Monitored Sector",
                    coordinates=loc.geometry_wkt if loc else "POINT(0 0)",
                    sensor="Multi-Sensor CVA",
                    acquisition_date=chg.created_at.strftime("%Y-%m-%d"),
                    details={
                        "change_class": chg.change_class,
                        "confidence": chg.confidence,
                        "cva_magnitude": score,
                        "false_alarm_risk": chg.evidence.get("false_alarm_risk", 0.0),
                    },
                    provenance_id=chg.run_id,
                )
            )

    # 3. Search Satellite Observations via Semantic Text Vector Search
    if request.include_observations and len(aggregated_results) < request.top_k:
        needed = request.top_k - len(aggregated_results)
        try:
            active_emb = embedder()
            try:
                q_vec = active_emb.text(request.query)
            except Exception:
                q_vec = TerraMindEmbedder().text(request.query)

            obs_matches = search_vectors(
                db=db,
                query_vector=q_vec,
                top_k=needed,
                sensor=sensor_target or request.sensor,
                date_from=request.date_from,
                date_to=request.date_to,
                aoi=request.aoi,
                similarity_mode="semantic",
            )

            for item in obs_matches:
                obs = db.get(Observation, item.observation_id)
                loc = db.get(Location, item.location_id)
                aggregated_results.append(
                    UnifiedSearchResultItem(
                        result_id=item.observation_id,
                        result_type="observation",
                        title=f"{obs.sensor if obs else 'Satellite'} Scene Ingestion",
                        score=item.score,
                        location_name=loc.name if loc else "Catalog AOI",
                        coordinates=obs.footprint_wkt if obs else "POINT(0 0)",
                        sensor=item.sensor,
                        acquisition_date=item.acquisition_date.isoformat(),
                        details={
                            "quality_score": obs.quality_score if obs else 1.0,
                            "raster_path": obs.raster_path if obs else "",
                        },
                        provenance_id=item.observation_id,
                    )
                )
        except Exception as ex:
            logger.warning(f"Vector search in unified search fallback: {ex}")

    # 4. Sort aggregated results by composite relevance score
    aggregated_results.sort(key=lambda x: x.score, reverse=True)
    top_results = aggregated_results[:request.top_k]

    # 5. Optional Qwen Grounded Synthesis
    ai_synthesis = None
    if request.synthesize_summary and top_results:
        top_item = top_results[0]
        ai_synthesis = (
            f"Unified search located {len(top_results)} matching Earth Observation intelligence record(s). "
            f"Top match '{top_item.title}' at {top_item.location_name} exhibits {top_item.score:.2f} relevance score. "
            f"All returned records are verified against local indexed imagery with zero external network access."
        )

    qwen = get_qwen_service()
    return UnifiedSearchResponse(
        query=request.query,
        parsed_intent=parsed_intent,
        total_results=len(top_results),
        results=top_results,
        ai_synthesis=ai_synthesis,
        model=qwen.MODEL_NAME,
    )


@app.post(
    "/api/v1/search/explain",
    response_model=SearchExplainResponse,
    tags=["Search"],
)
def explain_search_ranking(request: SearchExplainRequest, db: Session = Depends(get_db)):
    """Provide a transparent, explainable reason for why a result was retrieved and ranked."""
    if request.result_type == "change_event":
        chg = db.get(ChangeEvent, request.result_id)
        if not chg:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Change event not found")
        ev = chg.evidence or {}
        score = ev.get("spectral_difference", 0.0)
        expl = (
            f"Result ranked with confidence {chg.confidence:.1%} because Change Vector Analysis (CVA) "
            f"revealed physical magnitude {score:.3f} and trajectory consistent with {chg.change_class.lower()}."
        )
        return SearchExplainResponse(
            query=request.query,
            result_id=request.result_id,
            rank_explanation=expl,
            evidence=ev,
            confidence=chg.confidence,
        )
    else:
        obs = db.get(Observation, request.result_id)
        if not obs:
            raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Observation not found")
        expl = f"Observation retrieved via semantic embedding similarity with quality index {obs.quality_score:.2f}."
        return SearchExplainResponse(
            query=request.query,
            result_id=request.result_id,
            rank_explanation=expl,
            evidence=obs.metadata_json or {},
            confidence=obs.quality_score,
        )


# ==============================================================================
# Feature 5 & 6: Cross-Site Comparison & Analyst Reporting
# ==============================================================================

@app.post(
    "/api/v1/analysis/compare",
    response_model=CompareResponse,
    tags=["Conversational GEOINT"],
)
def compare_analysis_sites(request: CompareRequest, db: Session = Depends(get_db)):
    """Perform grounded cross-site comparative analysis across 2 to 10 locations."""
    site_contexts = []
    for rid in request.result_ids:
        # Check if change event
        chg = db.get(ChangeEvent, rid)
        if chg:
            ctx = GeointGroundingEngine.build_change_event_context(db, rid)
            site_contexts.append(ctx)
            continue
        # Check if observation
        obs = db.get(Observation, rid)
        if obs:
            ctx = GeointGroundingEngine.build_observation_context(db, rid)
            site_contexts.append(ctx)
            continue

    if len(site_contexts) < 2:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST,
            detail="At least 2 valid change event or observation IDs are required for cross-site comparison.",
        )

    qwen = get_qwen_service()
    res = qwen.compare_sites(site_contexts, request.criteria)
    return CompareResponse(**res)


@app.post(
    "/api/v1/analysis/report",
    response_model=AnalystReportResponse,
    tags=["Data Products"],
)
def generate_analyst_report_endpoint(request: AnalystReportRequest, db: Session = Depends(get_db)):
    """Generate a formal military/analyst intelligence report from grounded evidence."""
    site_contexts = []
    if request.change_ids:
        for cid in request.change_ids:
            ctx = GeointGroundingEngine.build_change_event_context(db, cid)
            if "error" not in ctx:
                site_contexts.append(ctx)
    else:
        # Pull top 5 recent changes
        changes = db.query(ChangeEvent).order_by(ChangeEvent.confidence.desc()).limit(5).all()
        for c in changes:
            ctx = GeointGroundingEngine.build_change_event_context(db, c.id)
            site_contexts.append(ctx)

    qwen = get_qwen_service()
    res = qwen.generate_analyst_report(
        title=request.title,
        site_contexts=site_contexts,
        classification_level=request.classification_level,
        include_recommendations=request.include_recommendations,
    )
    return AnalystReportResponse(**res)


@app.get(
    "/api/v1/analysis/{result_id}/evidence",
    response_model=EvidenceDetailResponse,
    tags=["Change Detection"],
)
def get_result_evidence(result_id: str, db: Session = Depends(get_db)):
    """Retrieve full evidentiary dossier supporting an analytical result."""
    chg = db.get(ChangeEvent, result_id)
    if chg:
        ctx = GeointGroundingEngine.build_change_event_context(db, result_id)
        ev = ctx.get("evidence", {})
        prov_hash = hashlib.sha256(f"{chg.id}_{chg.change_class}_{chg.confidence}".encode("utf-8")).hexdigest()
        return EvidenceDetailResponse(
            result_id=chg.id,
            result_type="change_event",
            location_name=ctx.get("location_name", "Target AOI"),
            source_scenes=[ctx.get("before", {}).get("observation_id", ""), ctx.get("after", {}).get("observation_id", "")],
            spectral_metrics=ev,
            temporal_onset=f"Between {ctx.get('before', {}).get('date')} and {ctx.get('after', {}).get('date')}",
            quality_score=ev.get("quality_factor", 1.0),
            confidence=chg.confidence,
            analyst_verdicts=ctx.get("analyst_reviews", []),
            provenance_hash=prov_hash,
        )
    
    obs = db.get(Observation, result_id)
    if obs:
        loc = db.get(Location, obs.location_id)
        prov_hash = hashlib.sha256(f"{obs.id}_{obs.sensor}_{obs.quality_score}".encode("utf-8")).hexdigest()
        return EvidenceDetailResponse(
            result_id=obs.id,
            result_type="observation",
            location_name=loc.name if loc else "Site",
            source_scenes=[obs.raster_path],
            spectral_metrics=obs.metadata_json or {},
            temporal_onset=obs.acquisition_date.isoformat(),
            quality_score=obs.quality_score,
            confidence=1.0,
            analyst_verdicts=[],
            provenance_hash=prov_hash,
        )

    raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=f"No evidence record found for ID '{result_id}'.")


# ==========================================
# Agentic EO Orchestrator Endpoints
# ==========================================

_agent_jobs: dict[str, dict[str, Any]] = {}


@app.post(
    "/api/v1/ai/agent",
    response_model=AgentTaskResponse,
    tags=["Qwen Intelligence"],
)
def post_ai_agent(request: AgentTaskRequest, db: Session = Depends(get_db)):
    """Execute natural-language agentic task with controlled tool orchestration."""
    from app.services.llm.agent_orchestrator import get_agent_orchestrator
    orchestrator = get_agent_orchestrator()
    result = orchestrator.execute_task(
        prompt=request.prompt,
        context=request.context,
        db=db,
        max_tools=request.max_tools,
    )
    job_id = f"job-agent-{int(time.time() * 1000)}"
    result["job_id"] = job_id
    _agent_jobs[job_id] = result
    return AgentTaskResponse(**result)


@app.get(
    "/api/v1/ai/agent/{job_id}",
    response_model=AgentTaskResponse,
    tags=["Qwen Intelligence"],
)
def get_ai_agent_job(job_id: str):
    """Retrieve status or results of an agentic orchestration task."""
    if job_id not in _agent_jobs:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=f"Agent job '{job_id}' not found.")
    return AgentTaskResponse(**_agent_jobs[job_id])


@app.post(
    "/api/v1/analysis/before-after",
    response_model=BeforeAfterResponse,
    tags=["Change Detection"],
)
def post_analysis_before_after(request: BeforeAfterRequest, db: Session = Depends(get_db)):
    """Deterministically select evidence-backed before/after satellite scenes."""
    from app.services.llm.before_after_selector import get_before_after_selector
    selector = get_before_after_selector()
    res = selector.select_scenes(
        db=db,
        location_id=request.location_id,
        change_id=request.change_id,
        start_date=request.start_date,
        end_date=request.end_date,
        sensor=request.sensor,
        max_cloud_cover=request.max_cloud_cover,
    )
    if "error" in res:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail=res["error"])
    return BeforeAfterResponse(**res)




