"""Database entities for satellite intelligence backend."""
from __future__ import annotations

import uuid
from datetime import datetime, date, timezone
from sqlalchemy import (
    String,
    DateTime,
    Date,
    Float,
    ForeignKey,
    JSON,
    Text,
)
from sqlalchemy.orm import Mapped, mapped_column, relationship
from app.db.session import Base


def uid() -> str:
    """Generate a UUID4 string."""
    return str(uuid.uuid4())


def now_utc() -> datetime:
    """Return current UTC datetime."""
    return datetime.now(timezone.utc)


class Location(Base):
    __tablename__ = "locations"

    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=uid)
    name: Mapped[str] = mapped_column(String(255), default="Unnamed site", index=True)
    geometry_wkt: Mapped[str] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=now_utc)

    observations: Mapped[list[Observation]] = relationship(
        "Observation", back_populates="location", cascade="all, delete-orphan"
    )
    change_events: Mapped[list[ChangeEvent]] = relationship(
        "ChangeEvent", back_populates="location", cascade="all, delete-orphan"
    )


class SatelliteSource(Base):
    __tablename__ = "satellite_sources"

    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=uid)
    name: Mapped[str] = mapped_column(String(128), unique=True, index=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=now_utc)

    observations: Mapped[list[Observation]] = relationship(
        "Observation", back_populates="source"
    )


class Observation(Base):
    __tablename__ = "observations"

    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=uid)
    location_id: Mapped[str] = mapped_column(
        ForeignKey("locations.id", ondelete="CASCADE"), index=True
    )
    source_id: Mapped[str | None] = mapped_column(
        ForeignKey("satellite_sources.id", ondelete="SET NULL"), nullable=True, index=True
    )
    acquisition_date: Mapped[date] = mapped_column(Date, index=True)
    sensor: Mapped[str] = mapped_column(String(64), index=True)
    raster_path: Mapped[str] = mapped_column(Text)
    footprint_wkt: Mapped[str] = mapped_column(Text)
    metadata_json: Mapped[dict] = mapped_column(JSON, default=dict)
    quality_score: Mapped[float] = mapped_column(Float, default=1.0)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=now_utc)

    location: Mapped[Location] = relationship("Location", back_populates="observations")
    source: Mapped[SatelliteSource | None] = relationship("SatelliteSource", back_populates="observations")
    embedding: Mapped[Embedding | None] = relationship(
        "Embedding", back_populates="observation", uselist=False, cascade="all, delete-orphan"
    )


class ProcessingRun(Base):
    __tablename__ = "processing_runs"

    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=uid)
    operation: Mapped[str] = mapped_column(String(64), index=True)
    status: Mapped[str] = mapped_column(String(32), default="queued", index=True)
    provenance: Mapped[dict] = mapped_column(JSON, default=dict)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=now_utc)
    completed_at: Mapped[datetime | None] = mapped_column(DateTime, nullable=True)


class Embedding(Base):
    __tablename__ = "embeddings"

    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=uid)
    observation_id: Mapped[str] = mapped_column(
        ForeignKey("observations.id", ondelete="CASCADE"), unique=True, index=True
    )
    vector: Mapped[list] = mapped_column(JSON)
    model_name: Mapped[str] = mapped_column(String(128))
    model_version: Mapped[str] = mapped_column(String(128))
    run_id: Mapped[str | None] = mapped_column(
        ForeignKey("processing_runs.id", ondelete="SET NULL"), nullable=True, index=True
    )
    created_at: Mapped[datetime] = mapped_column(DateTime, default=now_utc)

    observation: Mapped[Observation] = relationship("Observation", back_populates="embedding")


class ChangeEvent(Base):
    __tablename__ = "change_events"

    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=uid)
    location_id: Mapped[str] = mapped_column(
        ForeignKey("locations.id", ondelete="CASCADE"), index=True
    )
    before_observation_id: Mapped[str] = mapped_column(
        ForeignKey("observations.id", ondelete="CASCADE"), index=True
    )
    after_observation_id: Mapped[str] = mapped_column(
        ForeignKey("observations.id", ondelete="CASCADE"), index=True
    )
    change_class: Mapped[str] = mapped_column(String(64), index=True)
    confidence: Mapped[float] = mapped_column(Float)
    evidence: Mapped[dict] = mapped_column(JSON, default=dict)
    run_id: Mapped[str] = mapped_column(
        ForeignKey("processing_runs.id", ondelete="CASCADE"), index=True
    )
    created_at: Mapped[datetime] = mapped_column(DateTime, default=now_utc)

    location: Mapped[Location] = relationship("Location", back_populates="change_events")
    reviews: Mapped[list[AnalystReview]] = relationship(
        "AnalystReview", back_populates="change_event", cascade="all, delete-orphan"
    )


class AnalystReview(Base):
    __tablename__ = "analyst_reviews"

    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=uid)
    change_event_id: Mapped[str] = mapped_column(
        ForeignKey("change_events.id", ondelete="CASCADE"), index=True
    )
    analyst: Mapped[str] = mapped_column(String(128), index=True)
    decision: Mapped[str] = mapped_column(String(32), index=True)
    note: Mapped[str | None] = mapped_column(Text, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime, default=now_utc)

    change_event: Mapped[ChangeEvent] = relationship("ChangeEvent", back_populates="reviews")
