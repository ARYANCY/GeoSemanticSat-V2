"""Pydantic API request and response schemas."""
from __future__ import annotations

from datetime import date, datetime
from enum import StrEnum
from typing import Any
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


# ==========================================
# Chat & Conversational Intelligence Schemas
# ==========================================

class ChatMessage(BaseModel):
    role: str = Field(..., description="Role of the speaker: user, assistant, or system")
    content: str = Field(..., description="Message text content")


class ChatRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    messages: list[ChatMessage] = Field(..., min_length=1, description="Conversation history")
    observation_id: str | None = Field(default=None, description="Optional target observation ID for grounding")
    change_id: str | None = Field(default=None, description="Optional target change event ID for grounding")
    max_tokens: int = Field(default=1024, ge=64, le=4096)
    temperature: float = Field(default=0.2, ge=0.0, le=1.0)


class ChatResponse(BaseModel):
    message: ChatMessage
    grounded_evidence: str | None = None
    model: str = "Qwen/Qwen3-8B"


# ==========================================
# Automated Insight Schemas
# ==========================================

class InsightRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    change_id: str | None = Field(default=None, description="Target change event ID to synthesize")
    observation_id: str | None = Field(default=None, description="Target observation ID to synthesize")


class InsightResponse(BaseModel):
    change_id: str | None = None
    observation_id: str | None = None
    location_name: str | None = None
    severity: str = "NOMINAL"
    change_class: str | None = None
    confidence: float = 1.0
    summary: str
    physical_evidence: list[str] = Field(default_factory=list)
    timeline: str | None = None
    false_alarm_risk: float | None = None
    recommendations: list[str] = Field(default_factory=list)
    grounded_dossier: str | None = None


# ==========================================
# Foundation Model Management Schemas
# ==========================================

class ModelStatusResponse(BaseModel):
    active_model: str
    models: dict[str, dict]


class ModelLoadRequest(BaseModel):
    model_name: str = Field(..., description="Name of the model to activate or reload")


class ModelLoadResponse(BaseModel):
    model_name: str
    status: str
    is_loaded: bool


# ==========================================
# Mission Monitoring, Feedback & Export Schemas
# ==========================================

class SearchFilterRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    sensor: str | None = Field(default=None, max_length=64)
    min_quality: float | None = Field(default=None, ge=0.0, le=1.0)
    date_from: date | None = None
    date_to: date | None = None
    limit: int = Field(default=50, ge=1, le=500)
    offset: int = Field(default=0, ge=0)


class MissionCreateRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    name: str = Field(..., min_length=1, max_length=255)
    description: str = Field(default="", max_length=1000)
    aoi: dict | str = Field(..., description="GeoJSON geometry or WKT polygon")
    semantic_query: str = Field(..., min_length=1, max_length=512)
    sensor_filter: str | None = Field(default=None, max_length=64)
    quality_threshold: float = Field(default=0.60, ge=0.0, le=1.0)
    change_threshold: float = Field(default=0.35, ge=0.0, le=2.0)
    confidence_threshold: float = Field(default=0.65, ge=0.0, le=1.0)
    domain_pack: str = Field(default="Defence", max_length=64)
    enabled: bool = True


class MissionResponse(BaseModel):
    id: str
    name: str
    description: str
    aoi_wkt: str
    semantic_query: str
    sensor_filter: str | None
    quality_threshold: float
    change_threshold: float
    confidence_threshold: float
    domain_pack: str
    enabled: bool
    created_at: datetime
    last_run_at: datetime | None


class MissionAlertResponse(BaseModel):
    id: str
    mission_id: str
    change_event_id: str | None
    priority: str
    change_type: str
    confidence: float
    change_score: float
    location_wkt: str
    before_scene: str
    after_scene: str
    provenance_hash: str
    status: str
    detected_time: datetime


class MissionRunResponse(BaseModel):
    mission_id: str
    status: str
    alerts_generated: int
    alerts: list[MissionAlertResponse]


class FeedbackRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    query: str = Field(..., min_length=1, max_length=512)
    positive_observation_ids: list[str] = Field(default_factory=list)
    negative_observation_ids: list[str] = Field(default_factory=list)
    alpha: float = Field(default=1.0, ge=0.0, le=2.0)
    beta: float = Field(default=0.75, ge=0.0, le=2.0)
    gamma: float = Field(default=0.25, ge=0.0, le=2.0)


class FeedbackResponse(BaseModel):
    status: str
    query: str
    adjusted_vector_dimension: int
    updated_results: list[SearchResultItem]


class ExportRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    change_ids: list[str] = Field(default_factory=list, description="Specific change IDs to export, or all if empty")
    format: str = Field(default="all", description="json, geojson, stac, briefing, or all")
    mission_id: str | None = Field(default="SOVEREIGN_MISSION_EXPORT")
    output_directory: str | None = Field(default=None)


class ExportResponse(BaseModel):
    status: str
    export_directory: str
    files: dict[str, str]
    records_exported: int
    merkle_root_hash: str


# ==========================================
# Unified Search & AI Intelligence Schemas
# ==========================================

class UnifiedSearchRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    query: str = Field(..., min_length=1, max_length=1000, description="Natural language search query")
    top_k: int = Field(default=10, ge=1, le=100)
    aoi: dict | str | None = Field(default=None, description="Optional bounding box or GeoJSON AOI")
    date_from: date | None = None
    date_to: date | None = None
    sensor: str | None = None
    min_confidence: float | None = None
    include_changes: bool = True
    include_observations: bool = True
    synthesize_summary: bool = True


class UnifiedSearchResultItem(BaseModel):
    result_id: str
    result_type: str  # "observation", "change_event", "mission_alert"
    title: str
    score: float
    location_name: str
    coordinates: str
    sensor: str
    acquisition_date: str
    details: dict = Field(default_factory=dict)
    provenance_id: str | None = None


class UnifiedSearchResponse(BaseModel):
    query: str
    parsed_intent: dict
    total_results: int
    results: list[UnifiedSearchResultItem]
    ai_synthesis: str | None = None
    model: str = "Qwen/Qwen3-8B"


class SearchExplainRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    query: str = Field(..., min_length=1)
    result_id: str = Field(..., min_length=1)
    result_type: str = Field(default="change_event")


class SearchExplainResponse(BaseModel):
    query: str
    result_id: str
    rank_explanation: str
    evidence: dict
    confidence: float


class CompareRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    result_ids: list[str] = Field(..., min_length=2, max_length=10, description="List of 2 to 10 result/change IDs to compare")
    criteria: list[str] = Field(default_factory=list, description="Optional comparison criteria (e.g. ['biomass', 'structures', 'confidence'])")


class CompareResponse(BaseModel):
    sites_compared: int
    comparison_matrix: list[dict]
    cross_site_analysis: str
    recommendation: str
    model: str = "Qwen/Qwen3-8B"


class AnalystReportRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    title: str = Field(default="Sovereign Earth Observation Intelligence Report")
    change_ids: list[str] = Field(default_factory=list)
    mission_id: str | None = None
    classification_level: str = Field(default="RESTRICTED // GEOINT")
    include_recommendations: bool = True


class AnalystReportResponse(BaseModel):
    title: str
    classification_level: str
    generated_at: datetime
    executive_summary: str
    key_findings: list[str]
    site_assessments: list[dict]
    strategic_recommendations: list[str]
    provenance_merkle_root: str
    model: str = "Qwen/Qwen3-8B"


class EvidenceDetailResponse(BaseModel):
    result_id: str
    result_type: str
    location_name: str
    source_scenes: list[str]
    spectral_metrics: dict
    temporal_onset: str | None
    quality_score: float
    confidence: float
    analyst_verdicts: list[dict]
    provenance_hash: str


# ==========================================
# Agentic EO Orchestrator & Before/After Schemas
# ==========================================

class ToolCallRecord(BaseModel):
    tool: str
    arguments: dict[str, Any] = Field(default_factory=dict)
    status: str = "completed"
    output: Any = None
    execution_time_ms: float = 0.0


class MapActionRecord(BaseModel):
    action: str = "focus"  # focus, highlight, bounds, none
    geometry: dict[str, Any] | None = None
    center: list[float] | None = None  # [lat, lon]
    zoom: float | None = None


class BeforeAfterSceneInfo(BaseModel):
    scene_id: str
    acquisition_time: str
    sensor: str
    image_url_or_local_asset: str
    quality: float


class BeforeAfterPairInfo(BaseModel):
    enabled: bool = False
    before: BeforeAfterSceneInfo | None = None
    after: BeforeAfterSceneInfo | None = None


class BeforeAfterRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    location_id: str | None = Field(default=None, description="Location ID to query scenes for")
    change_id: str | None = Field(default=None, description="Change event ID if candidate already identified")
    start_date: str | None = Field(default=None, description="Start date constraint (YYYY-MM-DD)")
    end_date: str | None = Field(default=None, description="End date constraint (YYYY-MM-DD)")
    sensor: str | None = Field(default=None, description="Preferred sensor (e.g. Sentinel-2, Landsat-8)")
    max_cloud_cover: float = Field(default=0.15, ge=0.0, le=1.0)


class BeforeAfterResponse(BaseModel):
    before: BeforeAfterSceneInfo
    after: BeforeAfterSceneInfo
    change: dict[str, Any] = Field(default_factory=dict)
    earliest_supported_observation: str | None = None
    provenance_id: str


class AgentTaskRequest(BaseModel):
    model_config = ConfigDict(extra="ignore")

    prompt: str = Field(..., min_length=1, description="Analyst query or directive")
    context: dict[str, Any] = Field(default_factory=dict, description="Active context e.g. current bbox, selected candidate, mission ID")
    max_tools: int = Field(default=6, ge=1, le=15, description="Maximum tools to plan/execute")
    session_id: str | None = Field(default=None, description="Conversational session ID")


class QwenAgentOutputContract(BaseModel):
    intent: str
    plan: list[str] = Field(default_factory=list)
    tool_calls: list[ToolCallRecord] = Field(default_factory=list)
    status: str = "completed"  # planning, executing, completed, partial, insufficient_evidence, failed
    answer: str
    results: list[dict[str, Any]] = Field(default_factory=list)
    selected_result: dict[str, Any] | None = None
    before_after: BeforeAfterPairInfo = Field(default_factory=BeforeAfterPairInfo)
    change: dict[str, Any] = Field(default_factory=dict)
    map_action: MapActionRecord | None = None
    evidence: list[str] = Field(default_factory=list)
    provenance_id: str | None = None
    uncertainties: list[str] = Field(default_factory=list)
    suggested_followups: list[str] = Field(default_factory=list)


class AgentTaskResponse(QwenAgentOutputContract):
    job_id: str | None = None




