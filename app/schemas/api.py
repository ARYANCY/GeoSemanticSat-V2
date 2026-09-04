"""Pydantic API request and response schemas."""
from __future__ import annotations

from datetime import date, datetime
from enum import StrEnum
from pydantic import BaseModel, Field, ConfigDict


class ReviewDecision(StrEnum):
    CONFIRMED = "confirmed"
    REJECTED = "rejected"
    NEEDS_REVIEW = "needs_review"


# ==========================================
# Request Schemas
# ==========================================

class IngestRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    path: str = Field(..., min_length=1, description="Relative GeoTIFF path under DATA_ROOT")
    sensor: str = Field(..., min_length=1, max_length=64, description="Sensor name (e.g. Sentinel-2)")
    acquisition_date: date = Field(..., description="Acquisition date (YYYY-MM-DD)")
    location_name: str = Field(default="Unnamed site", max_length=255)
    source: str = Field(default="local", max_length=128)


class SearchRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    query: str = Field(..., min_length=1, max_length=512, description="Text search query")
    top_k: int = Field(default=10, ge=1, le=100)
    sensor: str | None = Field(default=None, max_length=64)


class ImageSearchRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    observation_id: str = Field(..., min_length=1)
    top_k: int = Field(default=10, ge=1, le=100)


class ChangeRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    before_observation_id: str = Field(..., min_length=1)
    after_observation_id: str = Field(..., min_length=1)


class ReviewRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    analyst: str = Field(..., min_length=1, max_length=128)
    decision: ReviewDecision
    note: str | None = Field(default=None, max_length=4000)


class SimilarRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    location_id: str = Field(..., min_length=1)
    top_k: int = Field(default=10, ge=1, le=100)


# ==========================================
# Response Schemas
# ==========================================

class HealthResponse(BaseModel):
    status: str
    offline_mode: bool


class SystemStatusResponse(BaseModel):
    database: str
    observations: int
    embeddings: int
    runtime_network: bool
    semantic_model: str


class IngestResponse(BaseModel):
    job_id: str
    status: str
    observation_id: str
    location_id: str


class ProcessingJobResponse(BaseModel):
    id: str
    status: str
    operation: str
    provenance: dict


class ObservationItem(BaseModel):
    id: str
    location_id: str
    date: date
    sensor: str
    quality: float


class SearchResultItem(BaseModel):
    observation_id: str
    location_id: str
    score: float
    sensor: str
    acquisition_date: date


class SearchResponse(BaseModel):
    results: list[SearchResultItem]


class LocationDetailResponse(BaseModel):
    id: str
    name: str
    geometry_wkt: str
    observations: int


class LocationTimelineItem(BaseModel):
    id: str
    date: date
    sensor: str
    quality: float


class ChangeAnalysisResponse(BaseModel):
    change_id: str
    change_class: str = Field(..., alias="class")
    confidence: float
    evidence: dict

    model_config = ConfigDict(populate_by_name=True)


class ChangeEventResponse(BaseModel):
    id: str
    change_class: str = Field(..., alias="class")
    confidence: float
    evidence: dict

    model_config = ConfigDict(populate_by_name=True)


class ReviewResponse(BaseModel):
    review_id: str


class ReviewItem(BaseModel):
    id: str
    change_id: str
    decision: str
    analyst: str


class ChangeProvenanceResponse(BaseModel):
    change_id: str
    run: dict
    before: str
    after: str
    evidence: dict
