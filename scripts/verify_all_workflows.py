"""Comprehensive End-to-End Workflow Verification Suite.

Tests every workflow across the UpaGraha / Unified-RSanalytics platform:
1. Model Inventory & Staging Verification (All 5 Foundation Models)
2. Embeddings & Feature Extraction (TerraMind, SatMAE++, GFM Composition, Prithvi-EO-2.0)
3. GEOINT Grounding Context Assembly
4. Qwen3-8B Conversational Reasoning & SITREP Generation
5. Automated Insight Synthesis
6. Full FastAPI Endpoints (Search, Chat, Insights, Models, Provenance)
"""
from __future__ import annotations

import sys
from datetime import date
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import numpy as np
from fastapi.testclient import TestClient
from sqlalchemy.orm import Session

from app.core.config import settings
from app.db.session import engine, Base
from app.main import app
from app.models.entities import Location, SatelliteSource, Observation, ChangeEvent, ProcessingRun
from app.services.embeddings.models.terramind import TerraMindEmbedder
from app.services.embeddings.models.satmae_pp import SatMaePPEmbedder
from app.services.embeddings.models.gfm_composition import GFMCompositionEmbedder
from app.services.embeddings.models.prithvi_temporal import PrithviTemporalEmbedder
from app.services.embeddings.service import get_available_models_status, create_embedder
from app.services.llm.geoint_grounding import GeointGroundingEngine
from app.services.llm.qwen_service import get_qwen_service
from app.services.llm.insight_generator import InsightGenerator


def verify_all():
    print("=" * 80)
    print("UPAGRAHA-V2 — COMPREHENSIVE END-TO-END WORKFLOW VERIFICATION")
    print("=" * 80)

    # --------------------------------------------------------------------------
    # Workflow 1: Foundation Models Staging Inventory
    # --------------------------------------------------------------------------
    print("\n[Workflow 1/6] Verifying Staged Foundation Models...")
    status = get_available_models_status()
    models = status["models"]
    
    assert "qwen3-8b" in models, "Qwen3-8B missing from inventory"
    assert "terramind-1.0-base" in models, "TerraMind missing from inventory"
    assert "satmae-pp" in models, "SatMAE++ missing from inventory"
    assert "gfm-composition" in models, "GFM Composition missing from inventory"
    assert "prithvi-eo-2.0-300m" in models, "Prithvi 300M missing from inventory"

    print("  [OK] Qwen3-8B:                 Staged =", models["qwen3-8b"]["staged"])
    print("  [OK] TerraMind-1.0-base:       Staged =", models["terramind-1.0-base"]["staged"])
    print("  [OK] SatMAE++:                 Staged =", models["satmae-pp"]["staged"])
    print("  [OK] GFM Composition:          Staged =", models["gfm-composition"]["staged"])
    print("  [OK] Prithvi-EO-2.0-300M:      Staged =", models["prithvi-eo-2.0-300m"]["staged"])

    # --------------------------------------------------------------------------
    # Workflow 2: Vision & Multimodal Feature Extraction
    # --------------------------------------------------------------------------
    print("\n[Workflow 2/6] Verifying Foundation Feature Extraction Pipelines...")
    np.random.seed(42)
    s2_patch = np.random.uniform(0.0, 1.0, size=(12, 64, 64)).astype(np.float32)
    s1_patch = np.random.uniform(0.0, 1.0, size=(2, 64, 64)).astype(np.float32)

    # TerraMind
    tm = TerraMindEmbedder()
    tm_img = tm.image(s2_patch)
    tm_txt = tm.text("new military structures near river")
    assert tm_img.shape == (128,) and tm_txt.shape == (128,)
    print(f"  [OK] TerraMind Cross-Modal Vector generated (cosine dot = {float(np.dot(tm_img, tm_txt)):.4f})")

    # SatMAE++
    smae = SatMaePPEmbedder()
    smae_vec = smae.image(s2_patch)
    assert smae_vec.shape == (128,)
    print(f"  [OK] SatMAE++ Grouped Spectral Vector generated (norm = {float(np.linalg.norm(smae_vec)):.4f})")

    # GFM Composition
    gfm = GFMCompositionEmbedder()
    gfm_vec = gfm.compose_sar_optical(s2_patch, s1_patch)
    assert gfm_vec.shape == (128,)
    print(f"  [OK] GFM Compositional SAR+Optical Vector generated (norm = {float(np.linalg.norm(gfm_vec)):.4f})")

    # Prithvi-EO-2.0
    prithvi = PrithviTemporalEmbedder()
    t_seq = prithvi.encode_temporal_sequence([s2_patch, s2_patch * 1.1, s2_patch * 0.9])
    t_metrics = prithvi.analyze_temporal_change(s2_patch, s2_patch * 1.2)
    assert t_seq.shape == (128,)
    print(f"  [OK] Prithvi Spatio-Temporal Trajectory generated (distance = {t_metrics['temporal_distance']:.4f})")

    # --------------------------------------------------------------------------
    # Workflow 3: Database & Sample Record Seeding
    # --------------------------------------------------------------------------
    print("\n[Workflow 3/6] Setting up Grounded Database Records...")
    Base.metadata.create_all(bind=engine)
    with Session(engine) as db:
        loc = db.query(Location).filter_by(name="Workflow Verification Sector").first()
        if not loc:
            loc = Location(name="Workflow Verification Sector", geometry_wkt="POLYGON ((77.20 28.60, 77.22 28.60, 77.22 28.62, 77.20 28.62, 77.20 28.60))")
            db.add(loc)
            db.flush()

        src = db.query(SatelliteSource).filter_by(name="Sentinel-2 L2A Calibrated").first()
        if not src:
            src = SatelliteSource(name="Sentinel-2 L2A Calibrated")
            db.add(src)
            db.flush()

        obs1 = db.query(Observation).filter_by(location_id=loc.id, acquisition_date=date(2024, 1, 15)).first()
        if not obs1:
            obs1 = Observation(
                location_id=loc.id,
                source_id=src.id,
                acquisition_date=date(2024, 1, 15),
                sensor="Sentinel-2",
                raster_path="sample_before.tif",
                footprint_wkt=loc.geometry_wkt,
                quality_score=0.96,
            )
            db.add(obs1)
            db.flush()

        obs2 = db.query(Observation).filter_by(location_id=loc.id, acquisition_date=date(2024, 3, 20)).first()
        if not obs2:
            obs2 = Observation(
                location_id=loc.id,
                source_id=src.id,
                acquisition_date=date(2024, 3, 20),
                sensor="Sentinel-2",
                raster_path="sample_after.tif",
                footprint_wkt=loc.geometry_wkt,
                quality_score=0.94,
            )
            db.add(obs2)
            db.flush()

        event = db.query(ChangeEvent).filter_by(location_id=loc.id).first()
        if not event:
            run = ProcessingRun(
                operation="change_analysis",
                status="completed",
                provenance={"algorithm": "multi-band-cva-v2", "bands": 6},
            )
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
                    "delta_ndvi": -0.38,
                    "delta_ndbi": 0.31,
                    "quality_factor": 0.95,
                    "false_alarm_risk": 0.05,
                    "prithvi_temporal_metrics": {
                        "temporal_distance": 0.52,
                        "trajectory_magnitude": 0.81,
                        "delta_ndbi": 0.31,
                        "delta_ndvi": -0.38,
                    },
                },
                run_id=run.id,
            )
            db.add(event)
            db.commit()

        change_id = event.id
        obs_id = obs1.id
        print(f"  [OK] Observation and ChangeEvent seeded: Change ID = {change_id}")

    # --------------------------------------------------------------------------
    # Workflow 4: Grounding & Conversational Qwen Reasoning
    # --------------------------------------------------------------------------
    print("\n[Workflow 4/6] Verifying Grounded GEOINT Dossier & Qwen Reasoning...")
    with Session(engine) as db:
        ctx = GeointGroundingEngine.build_change_event_context(db, change_id)
        dossier = GeointGroundingEngine.format_grounded_evidence_prompt(ctx)
        assert "GROUNDED GEOINT EVIDENCE DOSSIER" in dossier
        assert "CONSTRUCTION" in dossier
        print("  [OK] Grounded Evidence Dossier compiled successfully.")

        qwen = get_qwen_service()

        # SITREP Report
        sitrep = qwen.generate([{"role": "user", "content": "Generate a military SITREP brief."}], grounded_context=dossier)
        assert "SITREP" in sitrep or "BRIEF" in sitrep
        print("  [OK] Military-standard SITREP brief generated.")

        # Etiology query
        why_resp = qwen.generate([{"role": "user", "content": "Why was this classified as construction?"}], grounded_context=dossier)
        assert len(why_resp) > 50
        print("  [OK] Change etiology reasoning generated.")

        # False alarm query
        fa_resp = qwen.generate([{"role": "user", "content": "Evaluate false alarm risk."}], grounded_context=dossier)
        assert "Jitter" in fa_resp or "false" in fa_resp.lower()
        print("  [OK] False alarm suppression evaluation generated.")

    # --------------------------------------------------------------------------
    # Workflow 5: Automated Insight Synthesizer
    # --------------------------------------------------------------------------
    print("\n[Workflow 5/6] Verifying Automated Insight Synthesizer...")
    with Session(engine) as db:
        insight = InsightGenerator.generate_change_insight(db, change_id)
        assert insight["severity"] == "CRITICAL"
        assert insight["change_class"] == "CONSTRUCTION"
        assert len(insight["physical_evidence"]) >= 2
        assert len(insight["recommendations"]) >= 2
        print(f"  [OK] Insight generated: Severity = {insight['severity']} | Summary = {insight['summary'][:60]}...")

    # --------------------------------------------------------------------------
    # Workflow 6: REST API Gateway Verification
    # --------------------------------------------------------------------------
    print("\n[Workflow 6/6] Verifying REST API Endpoints...")
    with TestClient(app) as client:
        # Health
        r_health = client.get("/health")
        assert r_health.status_code == 200 and r_health.json()["status"] == "ok"
        print("  [OK] GET /health: 200 OK")

        # Models status
        r_models = client.get("/api/v1/models/status")
        assert r_models.status_code == 200 and "qwen3-8b" in r_models.json()["models"]
        print("  [OK] GET /api/v1/models/status: 200 OK")

        # Chat
        r_chat = client.post("/api/v1/chat", json={
            "messages": [{"role": "user", "content": "What is the physical evidence for this change?"}],
            "change_id": change_id,
        })
        assert r_chat.status_code == 200 and "message" in r_chat.json()
        print("  [OK] POST /api/v1/chat: 200 OK")

        # Insights
        r_insight = client.post("/api/v1/insights/generate", json={"change_id": change_id})
        assert r_insight.status_code == 200 and r_insight.json()["severity"] == "CRITICAL"
        print("  [OK] POST /api/v1/insights/generate: 200 OK")

        # Model reload
        r_load = client.post("/api/v1/models/load", json={"model_name": "qwen3-8b"})
        assert r_load.status_code == 200
        print("  [OK] POST /api/v1/models/load: 200 OK")

    print("\n" + "=" * 80)
    print("ALL WORKFLOWS VERIFIED SUCCESSFULLY — ZERO DEFECTS FOUND")
    print("=" * 80)


if __name__ == "__main__":
    verify_all()
