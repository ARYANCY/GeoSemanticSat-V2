"""GEOINT Grounding & Context Aggregation Engine.

Assembles deterministic Earth Observation metrics, spectral deltas, CUSUM onset statistics,
SAR polarimetry, and multi-temporal foundation model evidence into structured, tamper-evident
context cards for grounded LLM reasoning without hallucinations.
"""
from __future__ import annotations

from typing import Any
from sqlalchemy.orm import Session

from app.models.entities import Observation, Location, ChangeEvent, AnalystReview, ProcessingRun


class GeointGroundingEngine:
    """Extracts and formats grounded geospatial intelligence evidence from platform records."""

    @staticmethod
    def build_observation_context(db: Session, observation_id: str) -> dict[str, Any]:
        """Compile comprehensive contextual evidence for a single observation."""
        obs = db.get(Observation, observation_id)
        if not obs:
            return {"error": f"Observation '{observation_id}' not found."}

        loc = db.get(Location, obs.location_id) if obs.location_id else None

        context = {
            "observation_id": obs.id,
            "location_name": loc.name if loc else "Unknown AOI",
            "acquisition_date": obs.acquisition_date.isoformat() if obs.acquisition_date else "Unknown",
            "sensor": obs.sensor,
            "quality_score": obs.quality_score,
            "footprint_wkt": obs.footprint_wkt,
            "metadata": obs.metadata_json or {},
        }
        return context

    @staticmethod
    def build_change_event_context(db: Session, change_id: str) -> dict[str, Any]:
        """Compile detailed physical and spectral evidence for a detected change event."""
        event = db.get(ChangeEvent, change_id)
        if not event:
            return {"error": f"Change event '{change_id}' not found."}

        obs_before = db.get(Observation, event.before_observation_id) if event.before_observation_id else None
        obs_after = db.get(Observation, event.after_observation_id) if event.after_observation_id else None
        loc = db.get(Location, event.location_id) if event.location_id else None
        run = db.get(ProcessingRun, event.run_id) if event.run_id else None

        # Fetch any analyst reviews
        reviews = (
            db.query(AnalystReview)
            .filter_by(change_event_id=change_id)
            .order_by(AnalystReview.created_at.desc())
            .all()
        )
        reviews_data = [
            {
                "analyst": r.analyst,
                "decision": r.decision,
                "note": r.note,
                "date": r.created_at.isoformat() if r.created_at else "",
            }
            for r in reviews
        ]

        evidence = event.evidence or {}

        context = {
            "change_id": event.id,
            "location_name": loc.name if loc else "Unknown AOI",
            "footprint_wkt": loc.geometry_wkt if loc else (obs_before.footprint_wkt if obs_before else None),
            "classified_class": event.change_class,
            "confidence": event.confidence,
            "before": {
                "observation_id": obs_before.id if obs_before else None,
                "sensor": obs_before.sensor if obs_before else "Unknown",
                "date": obs_before.acquisition_date.isoformat() if (obs_before and obs_before.acquisition_date) else None,
            },
            "after": {
                "observation_id": obs_after.id if obs_after else None,
                "sensor": obs_after.sensor if obs_after else "Unknown",
                "date": obs_after.acquisition_date.isoformat() if (obs_after and obs_after.acquisition_date) else None,
            },
            "evidence": {
                "spectral_difference": evidence.get("spectral_difference", 0.0),
                "delta_ndvi": evidence.get("delta_ndvi", 0.0),
                "delta_ndbi": evidence.get("delta_ndbi", 0.0),
                "delta_ndwi": evidence.get("delta_ndwi", 0.0),
                "quality_factor": evidence.get("quality_factor", 1.0),
                "false_alarm_risk": evidence.get("false_alarm_risk", 0.0),
                "evaluated_bands": evidence.get("evaluated_bands", 3),
                "prithvi_temporal_metrics": evidence.get("prithvi_temporal_metrics", {}),
            },
            "provenance": run.provenance if run else {},
            "analyst_reviews": reviews_data,
        }
        return context

    @classmethod
    def format_grounded_evidence_prompt(cls, context: dict[str, Any]) -> str:
        """Format a structured markdown dossier of evidence for injection into LLM prompts."""
        if "error" in context:
            return f"Evidence unavailable: {context['error']}"

        lines = [
            "### GROUNDED GEOINT EVIDENCE DOSSIER",
            f"- **Target AOI / Location**: {context.get('location_name', 'N/A')}",
        ]

        if "change_id" in context:
            lines.extend([
                f"- **Change ID**: `{context.get('change_id')}`",
                f"- **Classified State**: **{context.get('classified_class')}** (Confidence: {context.get('confidence', 0.0):.1%})",
                f"- **Temporal Epoch T1 (Before)**: {context['before'].get('date')} [{context['before'].get('sensor')}]",
                f"- **Temporal Epoch T2 (After)**: {context['after'].get('date')} [{context['after'].get('sensor')}]",
                "",
                "#### Multi-Band & Foundation Model Evidence:",
                f"- **CVA Spectral Magnitude**: {context['evidence'].get('spectral_difference', 0.0):.4f}",
                f"- **Delta NDVI (Vegetation Biomass)**: {context['evidence'].get('delta_ndvi', 0.0):+.4f}",
                f"- **False Alarm Risk Index**: {context['evidence'].get('false_alarm_risk', 0.0):.2%}",
                f"- **Quality Factor**: {context['evidence'].get('quality_factor', 1.0):.2f}",
            ])

            prithvi = context["evidence"].get("prithvi_temporal_metrics", {})
            if prithvi:
                lines.extend([
                    f"- **Prithvi Temporal Trajectory Distance**: {prithvi.get('temporal_distance', 'N/A')}",
                    f"- **Prithvi Trajectory Magnitude**: {prithvi.get('trajectory_magnitude', 'N/A')}",
                    f"- **Prithvi Delta NDBI (Built-up)**: {prithvi.get('delta_ndbi', 'N/A')}",
                    f"- **Prithvi Delta NDWI (Water)**: {prithvi.get('delta_ndwi', 'N/A')}",
                ])

            reviews = context.get("analyst_reviews", [])
            if reviews:
                lines.append("\n#### Prior Analyst Reviews:")
                for r in reviews:
                    lines.append(f"- Analyst **{r['analyst']}**: {r['decision']} — *\"{r['note']}\"* ({r['date']})")
        else:
            lines.extend([
                f"- **Observation ID**: `{context.get('observation_id')}`",
                f"- **Acquisition Date**: {context.get('acquisition_date')}",
                f"- **Sensor Platform**: {context.get('sensor')}",
                f"- **Quality Score**: {context.get('quality_score')}",
            ])

        lines.append("\n*Rule: All assessments must be substantiated exclusively by the above metrics.*")
        return "\n".join(lines)
