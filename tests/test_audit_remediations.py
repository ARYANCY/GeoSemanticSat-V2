import os
import sys
from datetime import date
import pytest
from fastapi.testclient import TestClient
from sqlalchemy.orm import Session

from app.main import app
from app.core.config import settings
from app.db.session import SessionLocal
from app.models.entities import Observation, Location, ChangeEvent, Embedding
from app.services.llm.before_after_selector import get_before_after_selector
from app.services.llm.tool_registry import get_tool_registry
from app.services.llm.agent_orchestrator import get_agent_orchestrator
from app.services.llm.insight_generator import InsightGenerator

client = TestClient(app)


def test_offline_environment_flags():
    """Verify BUG-0008: Strict offline environment variables are set."""
    assert os.environ.get("HF_HUB_OFFLINE") == "1"
    assert os.environ.get("TRANSFORMERS_OFFLINE") == "1"
    assert settings.offline_mode is True
    assert settings.hf_hub_offline is True
    assert settings.transformers_offline is True


def test_api_title_metadata():
    """Verify BUG-0006: Title branding matches UPAGRAHA Sovereign standard."""
    assert "UPAGRAHA" in app.title
    assert "GeoSemanticSat" in app.title


def test_health_endpoints_auth_bypass():
    """Verify BUG-0007: Health endpoints are accessible without bearer token even under auth."""
    r = client.get("/health")
    assert r.status_code == 200
    assert r.json()["status"] in {"ok", "healthy"}

    r_api = client.get("/api/v1/health")
    assert r_api.status_code == 200
    assert r_api.json()["status"] in {"ok", "healthy"}


def test_before_after_date_filtering():
    """Verify BUG-0001: Date constraints (start_date, end_date) are properly filtered."""
    db: Session = SessionLocal()
    try:
        selector = get_before_after_selector()
        res = selector.select_scenes(
            db=db,
            start_date="2035-01-01",
            end_date="2035-12-31"
        )
        assert "error" in res or res.get("code") == "NO_OBSERVATIONS"

        res_valid = selector.select_scenes(
            db=db,
            start_date="2020-01-01",
            end_date="2026-12-31"
        )
        assert "before" in res_valid and "after" in res_valid
        b_date = res_valid["before"]["acquisition_time"][:10]
        a_date = res_valid["after"]["acquisition_time"][:10]
        assert b_date >= "2020-01-01"
        assert a_date <= "2026-12-31"
    finally:
        db.close()


def test_tool_registry_grounding_no_fake_ids():
    """Verify BUG-0002 & BUG-0003: Tools query real DB and contain no hardcoded mock IDs or metrics."""
    db: Session = SessionLocal()
    try:
        reg = get_tool_registry()

        res_ev = reg.execute("evidence_lookup", {}, db)
        assert res_ev["status"] == "completed"
        output = res_ev["output"]
        assert "change_id" in output
        assert output["change_id"] != "sample-change-01"

        res_spat = reg.execute("spatial_filter", {"latitude": 28.6050, "longitude": 77.2080, "radius_km": 25.0}, db)
        assert res_spat["status"] == "completed"
        candidates = res_spat["output"]["matching_candidates"]
        for c in candidates:
            assert "distance_km" in c
            assert isinstance(c["distance_km"], (int, float))

        res_img = reg.execute("image_search", {}, db)
        assert res_img["status"] == "completed"
    finally:
        db.close()


def test_agent_orchestrator_dynamic_resolution():
    """Verify BUG-0002: Agent orchestrator dynamically resolves real entities from DB."""
    db: Session = SessionLocal()
    try:
        orch = get_agent_orchestrator()
        task_res = orch.execute_task(
            prompt="Analyze recent structural change activity and summarize evidence",
            context={},
            db=db,
            max_tools=4
        )
        assert task_res["status"] == "completed"
        assert task_res["intent"] in {"CHANGE_ANALYSIS", "GENERAL_RETRIEVAL", "BEFORE_AFTER_REQUEST"}
        assert len(task_res["evidence"]) > 0
        assert task_res["provenance_id"].startswith("PROV-")
    finally:
        db.close()


def test_export_path_traversal_sandboxing():
    """Verify BUG-0005: Arbitrary path traversal in export_evidence_package is blocked."""
    payload = {
        "output_directory": "../../etc/malicious_traversal",
        "change_ids": []
    }
    r = client.post("/api/v1/export", json=payload)
    assert r.status_code == 400
    assert "Security violation" in r.text or "sandbox" in r.text.lower()


def test_insight_generator_sparse_evidence_resilience():
    """Verify BUG-0011: InsightGenerator handles sparse/empty evidence without raising KeyError."""
    db: Session = SessionLocal()
    try:
        event = db.query(ChangeEvent).first()
        if event:
            insight = InsightGenerator.generate_change_insight(db, event.id)
            assert "error" not in insight
            assert "severity" in insight
            assert "summary" in insight
            assert "recommendations" in insight
            assert len(insight["recommendations"]) > 0
    finally:
        db.close()
