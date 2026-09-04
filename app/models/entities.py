import uuid
from datetime import datetime, date
from sqlalchemy import String, DateTime, Date, Float, ForeignKey, JSON, Text
from sqlalchemy.orm import Mapped, mapped_column
from app.db.session import Base
def uid(): return str(uuid.uuid4())
class Location(Base):
    __tablename__="locations"
    id: Mapped[str]=mapped_column(String(36),primary_key=True,default=uid)
    name: Mapped[str]=mapped_column(String(255),default="Unnamed site")
    geometry_wkt: Mapped[str]=mapped_column(Text)
class SatelliteSource(Base):
    __tablename__="satellite_sources"
    id: Mapped[str]=mapped_column(String(36),primary_key=True,default=uid)
    name: Mapped[str]=mapped_column(String(128),unique=True)
class Observation(Base):
    __tablename__="observations"
    id: Mapped[str]=mapped_column(String(36),primary_key=True,default=uid)
    location_id: Mapped[str]=mapped_column(ForeignKey("locations.id"),index=True)
    source_id: Mapped[str|None]=mapped_column(ForeignKey("satellite_sources.id"),nullable=True)
    acquisition_date: Mapped[date]=mapped_column(Date,index=True)
    sensor: Mapped[str]=mapped_column(String(64),index=True)
    raster_path: Mapped[str]=mapped_column(Text)
    footprint_wkt: Mapped[str]=mapped_column(Text)
    metadata_json: Mapped[dict]=mapped_column(JSON,default=dict)
    quality_score: Mapped[float]=mapped_column(Float,default=0.0)
class ProcessingRun(Base):
    __tablename__="processing_runs"
    id: Mapped[str]=mapped_column(String(36),primary_key=True,default=uid)
    operation: Mapped[str]=mapped_column(String(64)); status: Mapped[str]=mapped_column(String(32),default="queued")
    provenance: Mapped[dict]=mapped_column(JSON,default=dict)
class Embedding(Base):
    __tablename__="embeddings"
    id: Mapped[str]=mapped_column(String(36),primary_key=True,default=uid)
    observation_id: Mapped[str]=mapped_column(ForeignKey("observations.id"),unique=True,index=True)
    vector: Mapped[list]=mapped_column(JSON); model_name: Mapped[str]=mapped_column(String(128)); model_version: Mapped[str]=mapped_column(String(128)); run_id: Mapped[str|None]=mapped_column(ForeignKey("processing_runs.id"),nullable=True)
class ChangeEvent(Base):
    __tablename__="change_events"
    id: Mapped[str]=mapped_column(String(36),primary_key=True,default=uid); location_id: Mapped[str]=mapped_column(ForeignKey("locations.id")); before_observation_id: Mapped[str]=mapped_column(ForeignKey("observations.id")); after_observation_id: Mapped[str]=mapped_column(ForeignKey("observations.id")); change_class: Mapped[str]=mapped_column(String(64)); confidence: Mapped[float]=mapped_column(Float); evidence: Mapped[dict]=mapped_column(JSON); run_id: Mapped[str]=mapped_column(ForeignKey("processing_runs.id"))
class AnalystReview(Base):
    __tablename__="analyst_reviews"
    id: Mapped[str]=mapped_column(String(36),primary_key=True,default=uid); change_event_id: Mapped[str]=mapped_column(ForeignKey("change_events.id")); analyst: Mapped[str]=mapped_column(String(128)); decision: Mapped[str]=mapped_column(String(32)); note: Mapped[str|None]=mapped_column(Text,nullable=True)
