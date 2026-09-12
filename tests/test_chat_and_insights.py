"""Unit and integration tests for Qwen3-8B Conversational GEOINT and Insight Synthesis:
- GEOINT Grounding Context Engine
- QwenService reasoning & offline fallback
- Automated Insight Generator
- REST endpoints: /api/v1/chat, /api/v1/insights/generate, /api/v1/models/status, /api/v1/models/load
"""
from __future__ import annotations

import pytest
from datetime import date
from fastapi.testclient import TestClient
from sqlalchemy.orm import Session

from app.main import app
from app.db.session import get_db, engine, Base
from app.models.entities import Location, SatelliteSource, Observation, ChangeEvent, ProcessingRun
from app.services.llm.geoint_grounding import GeointGroundingEngine
from app.services.llm.qwen_service import get_qwen_service
from app.services.llm.insight_generator import InsightGenerator


@pytest.fixture(autouse=True)
def setup_database():
    """Ensure tables exist for testing."""
    Base.metadata.create_all(bind=engine)
    yield


@pytest.fixture
def sample_change_event():
    """Create sample observation and change event in the database."""
    with Session(engine) as db:
        loc = Location(name="Test AOI Sector 7", geometry_wkt="POLYGON ((77.1 28.5, 77.2 28.5, 77.2 28.6, 77.1 28.6, 77.1 28.5))")
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
            acquisition_date=date(2024, 1, 15),
            sensor="Sentinel-2",
            raster_path="sample_before.tif",
            footprint_wkt=loc.geometry_wkt,
            quality_score=0.95,
        )
        obs2 = Observation(
            location_id=loc.id,
            source_id=src.id,
            acquisition_date=date(2024, 3, 20),
            sensor="Sentinel-2",
            raster_path="sample_after.tif",
            footprint_wkt=loc.geometry_wkt,
            quality_score=0.92,
        )
        db.add_all([obs1, obs2])
        db.flush()

        run = ProcessingRun(
            operation="change_analysis",
            status="completed",
            provenance={"algorithm": "multi-band-cva-v2"},
        )
        db.add(run)
        db.flush()

        event = ChangeEvent(
            location_id=loc.id,
            before_observation_id=obs1.id,
            after_observation_id=obs2.id,
            change_class="CONSTRUCTION",
            confidence=0.89,
            evidence={
                "spectral_difference": 0.68,
                "delta_ndvi": -0.32,
                "delta_ndbi": 0.28,
                "quality_factor": 0.93,
                "false_alarm_risk": 0.07,
                "prithvi_temporal_metrics": {
                    "temporal_distance": 0.45,
                    "trajectory_magnitude": 0.72,
                    "delta_ndbi": 0.28,
                    "delta_ndvi": -0.32,
                },
            },
            run_id=run.id,
        )
        db.add(event)
        db.commit()

        return {
            "change_id": event.id,
            "observation_id": obs1.id,
            "location_name": loc.name,
        }


def test_geoint_grounding_change_context(sample_change_event):
    """Verify that grounding engine extracts structured physical and spectral evidence."""
    with Session(engine) as db:
        ctx = GeointGroundingEngine.build_change_event_context(db, sample_change_event["change_id"])
        assert "error" not in ctx
        assert ctx["change_id"] == sample_change_event["change_id"]
        assert ctx["classified_class"] == "CONSTRUCTION"
        assert ctx["confidence"] == 0.89
        assert ctx["evidence"]["delta_ndvi"] == -0.32

        prompt = GeointGroundingEngine.format_grounded_evidence_prompt(ctx)
        assert "GROUNDED GEOINT EVIDENCE DOSSIER" in prompt
        assert "CONSTRUCTION" in prompt
        assert "-0.32" in prompt


def test_qwen_service_reasoning(sample_change_event):
    """Verify that QwenService generates grounded reasoning and SITREP reports."""
    qwen = get_qwen_service()
    with Session(engine) as db:
        ctx = GeointGroundingEngine.build_change_event_context(db, sample_change_event["change_id"])
        dossier = GeointGroundingEngine.format_grounded_evidence_prompt(ctx)

    # Test report generation
    report_response = qwen.generate(
        messages=[{"role": "user", "content": "Generate an intelligence report for this site"}],
        grounded_context=dossier,
    )
    assert "INTELLIGENCE BRIEF" in report_response or "GEOINT" in report_response
    assert "RECOMMENDATIONS" in report_response

    # Test why/cause query
    why_response = qwen.generate(
        messages=[{"role": "user", "content": "Why was this classified as construction?"}],
        grounded_context=dossier,
    )
    assert "ETIOLOGY" in why_response or "NDBI" in why_response or "NDVI" in why_response


def test_automated_insight_generator(sample_change_event):
    """Verify automated insight synthesis for change events."""
    with Session(engine) as db:
        insight = InsightGenerator.generate_change_insight(db, sample_change_event["change_id"])
        assert insight["change_id"] == sample_change_event["change_id"]
        assert insight["severity"] in {"CRITICAL", "HIGH"}
        assert insight["change_class"] == "CONSTRUCTION"
        assert len(insight["physical_evidence"]) > 0
        assert len(insight["recommendations"]) > 0
        assert "summary" in insight


def test_api_chat_endpoint(sample_change_event):
    """Verify /api/v1/chat REST endpoint."""
    with TestClient(app) as client:
        resp = client.post(
            "/api/v1/chat",
            json={
                "messages": [{"role": "user", "content": "What is the false alarm probability for this detection?"}],
                "change_id": sample_change_event["change_id"],
            },
        )
        assert resp.status_code == 200
        data = resp.json()
        assert "message" in data
        assert data["message"]["role"] == "assistant"
        assert "grounded_evidence" in data
        assert len(data["message"]["content"]) > 20


def test_api_insights_endpoint(sample_change_event):
    """Verify /api/v1/insights/generate REST endpoint."""
    with TestClient(app) as client:
        resp = client.post(
            "/api/v1/insights/generate",
            json={"change_id": sample_change_event["change_id"]},
        )
        assert resp.status_code == 200
        data = resp.json()
        assert data["severity"] in {"CRITICAL", "HIGH", "ELEVATED", "NOMINAL"}
        assert "physical_evidence" in data
        assert "recommendations" in data


def test_api_models_status_and_load():
    """Verify /api/v1/models/status and /api/v1/models/load endpoints."""
    with TestClient(app) as client:
        # Status
        resp = client.get("/api/v1/models/status")
        assert resp.status_code == 200
        data = resp.json()
        assert "qwen3-8b" in data["models"]
        assert "terramind-1.0-base" in data["models"]
        assert "prithvi-eo-2.0-300m" in data["models"]

        # Load Qwen
        resp = client.post("/api/v1/models/load", json={"model_name": "qwen3-8b"})
        assert resp.status_code == 200
        assert resp.json()["model_name"] == "Qwen/Qwen3-8B"


def test_api_ai_status():
    """Verify /api/v1/ai/status returns complete hardware and model metadata."""
    with TestClient(app) as client:
        resp = client.get("/api/v1/ai/status")
        assert resp.status_code == 200
        data = resp.json()
        assert data["model_name"] == "Qwen/Qwen3-8B"
        assert "runtime" in data
        assert "device" in data
        assert "host_free_ram_gb" in data
        assert data["air_gapped"] is True


def test_api_unified_search_and_explain(sample_change_event):
    """Verify /api/v1/search/unified and /api/v1/search/explain endpoints."""
    with TestClient(app) as client:
        # 1. Search for new infrastructure changes
        search_resp = client.post(
            "/api/v1/search/unified",
            json={
                "query": "Find new construction and structure changes in the monitored area",
                "top_k": 5,
                "include_changes": True,
            },
        )
        assert search_resp.status_code == 200
        data = search_resp.json()
        assert "parsed_intent" in data
        assert data["parsed_intent"]["is_temporal_change_query"] is True
        assert "results" in data
        assert "ai_synthesis" in data

        # 2. Explain ranking of the result
        explain_resp = client.post(
            "/api/v1/search/explain",
            json={
                "query": "new construction",
                "result_id": sample_change_event["change_id"],
                "result_type": "change_event",
            },
        )
        assert explain_resp.status_code == 200
        exp_data = explain_resp.json()
        assert "rank_explanation" in exp_data
        assert "confidence" in exp_data


def test_api_comparison_and_analyst_report(sample_change_event):
    """Verify /api/v1/analysis/compare, /api/v1/analysis/report, and evidence endpoints."""
    with TestClient(app) as client:
        # 1. Compare endpoints
        comp_resp = client.post(
            "/api/v1/analysis/compare",
            json={
                "result_ids": [sample_change_event["change_id"], sample_change_event["observation_id"]],
                "criteria": ["biomass", "confidence"],
            },
        )
        assert comp_resp.status_code == 200
        comp_data = comp_resp.json()
        assert comp_data["sites_compared"] == 2
        assert "comparison_matrix" in comp_data
        assert "cross_site_analysis" in comp_data
        assert "recommendation" in comp_data

        # 2. Analyst report endpoint
        rep_resp = client.post(
            "/api/v1/analysis/report",
            json={
                "title": "Sector 7 Sovereign Verification Report",
                "change_ids": [sample_change_event["change_id"]],
                "classification_level": "SECRET // NOFORN",
            },
        )
        assert rep_resp.status_code == 200
        rep_data = rep_resp.json()
        assert rep_data["title"] == "Sector 7 Sovereign Verification Report"
        assert "executive_summary" in rep_data
        assert len(rep_data["key_findings"]) > 0
        assert "provenance_merkle_root" in rep_data
        assert len(rep_data["provenance_merkle_root"]) == 64

        # 3. Evidence retrieval endpoint
        ev_resp = client.get(f"/api/v1/analysis/{sample_change_event['change_id']}/evidence")
        assert ev_resp.status_code == 200
        ev_data = ev_resp.json()
        assert ev_data["result_id"] == sample_change_event["change_id"]
        assert "spectral_metrics" in ev_data
        assert "provenance_hash" in ev_data

