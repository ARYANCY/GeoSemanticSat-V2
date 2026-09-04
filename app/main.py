"""FastAPI application for offline satellite intelligence."""
from __future__ import annotations

from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Annotated

import numpy as np
import rasterio
from shapely.geometry import box
from fastapi import FastAPI, Depends, HTTPException, Query, status
from sqlalchemy.orm import Session

from app.core.config import settings
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
)
from app.services.embeddings.service import embedder, norm


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Lifecycle manager for startup table creation and resource cleanup."""
    logger.info("Initializing database tables...")
    Base.metadata.create_all(bind=engine)
    logger.info("Database initialized successfully.")
    yield
    logger.info("Application shutting down.")


app = FastAPI(
    title="SIH 26227 Offline Satellite Intelligence API",
    version="0.2.0",
    description="Offline-first geospatial intelligence, change detection, and visual search API.",
    lifespan=lifespan,
)


# ==============================================================================
# Vector Similarity Search Helper
# ==============================================================================

def search_vectors(
    db: Session,
    query_vector: list[float] | np.ndarray,
    top_k: int,
    sensor: str | None = None,
    exclude_observation_id: str | None = None,
) -> list[SearchResultItem]:
    """Execute efficient cosine similarity search over stored observation embeddings."""
    q_norm = norm(query_vector)

    query = db.query(Embedding, Observation).join(
        Observation, Embedding.observation_id == Observation.id
    )

    if sensor:
        query = query.filter(Observation.sensor == sensor)
    if exclude_observation_id:
        query = query.filter(Observation.id != exclude_observation_id)

    pairs = query.all()
    if not pairs:
        return []

    scored_results: list[SearchResultItem] = []
    for emb, obs in pairs:
        if not emb.vector:
            continue
        v_norm = norm(emb.vector)
        # Verify vector dimensions align before dot product
        if q_norm.shape[0] != v_norm.shape[0]:
            logger.warning(
                f"Embedding dimension mismatch: query {q_norm.shape[0]} vs observation {obs.id} {v_norm.shape[0]}"
            )
            continue

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

@app.get("/health", response_model=HealthResponse, tags=["System"])
def health():
    """Health check and offline mode verification."""
    return HealthResponse(status="ok", offline_mode=settings.offline_mode)


@app.get("/system/status", response_model=SystemStatusResponse, tags=["System"])
def system_status(db: Session = Depends(get_db)):
    """System health, record counts, and model readiness status."""
    return SystemStatusResponse(
        database="connected",
        observations=db.query(Observation).count(),
        embeddings=db.query(Embedding).count(),
        runtime_network=False,
        semantic_model="RemoteCLIP local adapter required for text search",
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

            read_bands = min(ds.count, 3)
            out_shape = (read_bands, min(256, ds.height), min(256, ds.width))
            raster_data = ds.read(out_shape=out_shape, masked=True).filled(0).astype(np.float32)

            # Min-Max Normalization
            data_min, data_max = raster_data.min(), raster_data.max()
            if data_max > data_min:
                raster_data = (raster_data - data_min) / (data_max - data_min + 1e-6)
            else:
                raster_data = np.zeros_like(raster_data)

            footprint_wkt = box(*ds.bounds).wkt

            # Upsert or associate Location
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
                raster_path=str(target_path.relative_to(root)).replace("\\", "/"),
                footprint_wkt=footprint_wkt,
                quality_score=1.0,
                metadata_json={
                    "crs": str(ds.crs),
                    "shape": [ds.height, ds.width],
                    "bands": ds.count,
                    "preprocessing": "minmax-v1",
                },
            )
            db.add(observation)
            db.flush()

            # Compute and persist embedding
            vector = embedder().image(raster_data).tolist()
            embedding = Embedding(
                observation_id=observation.id,
                vector=vector,
                model_name="histogram-baseline",
                model_version="v2",
                run_id=run.id,
            )
            db.add(embedding)

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
        # Mark run as failed
        run.status = "failed"
        run.provenance = {**run.provenance, "error": str(exc)}
        run.completed_at = datetime.now(timezone.utc)
        db.commit()
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
    )
    return SearchResponse(results=results)


@app.post("/api/v1/search/text", tags=["Search"])
@app.post("/api/v1/search/hybrid", tags=["Search"])
def search_by_text(request: SearchRequest):
    """Semantic text and hybrid search (Requires staged RemoteCLIP model)."""
    raise HTTPException(
        status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
        detail="RemoteCLIP local weights and adapter are required for text retrieval; runtime downloads are disabled.",
    )


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

    def read_raster_band(obs: Observation) -> np.ndarray:
        p = settings.data_root / obs.raster_path
        if not p.is_file():
            raise FileNotFoundError(f"Raster file missing: {obs.raster_path}")
        with rasterio.open(p) as d:
            return d.read(1, out_shape=(256, 256), masked=True).filled(0).astype(np.float32)

    try:
        arr_before = read_raster_band(obs_before)
        arr_after = read_raster_band(obs_after)

        std_b, std_a = arr_before.std(), arr_after.std()
        norm_b = (arr_before - arr_before.mean()) / (std_b + 1e-6) if std_b > 0 else np.zeros_like(arr_before)
        norm_a = (arr_after - arr_after.mean()) / (std_a + 1e-6) if std_a > 0 else np.zeros_like(arr_after)

        score = float(np.mean(np.abs(norm_b - norm_a)))
        quality_factor = max(0.1, min(obs_before.quality_score, obs_after.quality_score))
        confidence = min(0.95, (score / (score + 1.0)) * quality_factor)
        change_class = "NO_CHANGE" if score < 0.25 else "OTHER"

        run = ProcessingRun(
            operation="change_analysis",
            status="completed",
            provenance={
                "algorithm": "normalized-absolute-difference-v2",
                "warning": "baseline score only; semantic class requires trained change model",
            },
            completed_at=datetime.now(timezone.utc),
        )
        db.add(run)
        db.flush()

        event = ChangeEvent(
            location_id=obs_before.location_id,
            before_observation_id=obs_before.id,
            after_observation_id=obs_after.id,
            change_class=change_class,
            confidence=round(confidence, 4),
            evidence={
                "spectral_difference": round(score, 6),
                "quality_factor": quality_factor,
                "false_alarm_risk": round(1.0 - quality_factor, 4),
                "mask_available": False,
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
