"""Unit and integration tests for Qwen Agentic EO Orchestrator:
- Intent and entity parsing
- Tool registry schema validation and path-traversal guard
- Deterministic before/after scene selection
- Grounded reasoning & structured agent contract
- REST endpoints: /api/v1/ai/agent, /api/v1/ai/agent/{job_id}, /api/v1/analysis/before-after
"""
from __future__ import annotations

import pytest
from datetime import date
from fastapi.testclient import TestClient
from sqlalchemy.orm import Session

from app.main import app
from app.db.session import get_db, engine, Base
from app.models.entities import Location, SatelliteSource, Observation, ChangeEvent, ProcessingRun
from app.services.llm.tool_registry import get_tool_registry
from app.services.llm.before_after_selector import get_before_after_selector
from app.services.llm.agent_orchestrator import get_agent_orchestrator


@pytest.fixture(autouse=True)
def setup_database():
    """Ensure database schema is ready."""
    Base.metadata.create_all(bind=engine)
    yield


@pytest.fixture
def agent_test_data():
    """Seed test observations and change event."""
    with Session(engine) as db:
        loc = Location(name="Agent AOI Strategic Zone 4", geometry_wkt="POLYGON ((77.20 28.60, 77.25 28.60, 77.25 28.65, 77.20 28.65, 77.20 28.60))")
        db.add(loc)
        db.flush()

        src = db.query(SatelliteSource).filter_by(name="Sentinel-2 L2A").first()
        if not src:
            src = SatelliteSource(name="Sentinel-2 L2A")
            db.add(src)
            db.flush()

        obs1 = Observation(
            location_id=loc.id,
            source_id=src.id,
            acquisition_date=date(2024, 1, 10),
            sensor="Sentinel-2",
            raster_path="rasters/s2_before_sample.tif",
            footprint_wkt=loc.geometry_wkt,
            quality_score=0.96,
        )
        obs2 = Observation(
            location_id=loc.id,
            source_id=src.id,
            acquisition_date=date(2024, 4, 12),
            sensor="Sentinel-2",
            raster_path="rasters/s2_after_sample.tif",
            footprint_wkt=loc.geometry_wkt,
            quality_score=0.93,
        )
        db.add_all([obs1, obs2])
        db.flush()

        run = ProcessingRun(operation="cva_analysis", status="completed", provenance={"algo": "cva-multi-band"})
        db.add(run)
        db.flush()

        event = ChangeEvent(
            location_id=loc.id,
            before_observation_id=obs1.id,
            after_observation_id=obs2.id,
            change_class="CONSTRUCTION",
            confidence=0.91,
            evidence={
                "spectral_difference": 0.74,
                "delta_ndvi": -0.36,
                "delta_ndbi": 0.31,
                "quality_factor": 0.94,
                "false_alarm_risk": 0.06,
                "earliest_onset": "2024-03-12",
            },
            run_id=run.id,
        )
        db.add(event)
        db.commit()

        return {
            "location_id": loc.id,
            "change_id": event.id,
            "obs1_id": obs1.id,
            "obs2_id": obs2.id,
        }


def test_tool_registry_registration_and_listing():
    """Verify tool registry contains all 15 controlled tools."""
    registry = get_tool_registry()
    tools = registry.list_tools()
    tool_names = {t["name"] for t in tools}

    expected_tools = {
        "semantic_search", "metadata_search", "spatial_filter", "temporal_filter",
        "image_search", "change_detection", "quality_assessment", "similar_site_search",
        "mission_search", "alert_search", "provenance_lookup", "before_after_selector",
        "scene_metadata", "evidence_lookup", "export_result",
    }
    assert expected_tools.issubset(tool_names)
    assert len(tools) >= 15


def test_tool_registry_security_path_traversal_blocked():
    """Verify security guard blocks directory traversal in tool arguments."""
    registry = get_tool_registry()
    with Session(engine) as db:
        res = registry.execute("metadata_search", {"sensor": "../../etc/passwd"}, db)
        assert res["status"] == "error"
        assert res["code"] == "PATH_TRAVERSAL_BLOCKED"


def test_tool_registry_unregistered_tool_rejection():
    """Verify registry rejects unauthorized tools."""
    registry = get_tool_registry()
    with Session(engine) as db:
        res = registry.execute("arbitrary_bash_exec", {"cmd": "ls"}, db)
        assert res["status"] == "error"
        assert res["code"] == "UNREGISTERED_TOOL"


def test_deterministic_before_after_selection(agent_test_data):
    """Verify selector pairs earliest baseline and cleanest post-onset scene."""
    selector = get_before_after_selector()
    with Session(engine) as db:
        res = selector.select_scenes(
            db=db,
            location_id=agent_test_data["location_id"],
            change_id=agent_test_data["change_id"],
        )
        assert "error" not in res
        assert "before" in res
        assert "after" in res
        assert res["before"]["scene_id"] == agent_test_data["obs1_id"]
        assert res["after"]["scene_id"] == agent_test_data["obs2_id"]
        assert res["change"]["type"] == "CONSTRUCTION"
        assert res["change"]["confidence"] == 0.91
        assert "PROV-" in res["provenance_id"]


def test_agent_orchestrator_execution(agent_test_data):
    """Verify end-to-end agent orchestration for change query."""
    orchestrator = get_agent_orchestrator()
    with Session(engine) as db:
        out = orchestrator.execute_task(
            prompt="Show newly constructed roads in this sector and give me the before and after image",
            context={"location_id": agent_test_data["location_id"], "active_change_id": agent_test_data["change_id"]},
            db=db,
            max_tools=5,
        )

        assert out["status"] == "completed"
        assert out["intent"] in {"CHANGE_ANALYSIS", "BEFORE_AFTER_REQUEST"}
        assert len(out["plan"]) > 0
        assert len(out["tool_calls"]) > 0
        assert out["before_after"]["enabled"] is True
        assert out["before_after"]["before"]["scene_id"] == agent_test_data["obs1_id"]
        assert len(out["evidence"]) > 0
        assert "map_action" in out
        assert out["map_action"]["action"] == "focus"
        assert len(out["suggested_followups"]) > 0


def test_api_agent_endpoint(agent_test_data):
    """Verify POST /api/v1/ai/agent REST endpoint."""
    with TestClient(app) as client:
        resp = client.post(
            "/api/v1/ai/agent",
            json={
                "prompt": "Identify any construction changes in the area and display before after imagery",
                "context": {"location_id": agent_test_data["location_id"], "active_change_id": agent_test_data["change_id"]},
                "max_tools": 5,
            },
        )
        assert resp.status_code == 200
        data = resp.json()
        assert data["status"] == "completed"
        assert data["job_id"] is not None
        assert data["before_after"]["enabled"] is True
        assert "before" in data["before_after"]
        assert len(data["evidence"]) > 0

        # Poll status via GET /api/v1/ai/agent/{job_id}
        job_id = data["job_id"]
        poll_resp = client.get(f"/api/v1/ai/agent/{job_id}")
        assert poll_resp.status_code == 200
        poll_data = poll_resp.json()
        assert poll_data["job_id"] == job_id
        assert poll_data["status"] == "completed"


def test_api_before_after_endpoint(agent_test_data):
    """Verify POST /api/v1/analysis/before-after REST endpoint."""
    with TestClient(app) as client:
        resp = client.post(
            "/api/v1/analysis/before-after",
            json={
                "location_id": agent_test_data["location_id"],
                "change_id": agent_test_data["change_id"],
                "max_cloud_cover": 0.15,
            },
        )
        assert resp.status_code == 200
        data = resp.json()
        assert data["before"]["scene_id"] == agent_test_data["obs1_id"]
        assert data["after"]["scene_id"] == agent_test_data["obs2_id"]
        assert data["change"]["type"] == "CONSTRUCTION"
        assert len(data["provenance_id"]) > 5
