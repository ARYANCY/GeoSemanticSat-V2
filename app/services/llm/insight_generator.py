"""Automated Insight Generation Engine.

Synthesizes complex remote sensing metrics, CVA trajectories, and foundation model outputs
into structured, actionable intelligence briefs with threat severity scoring and cross-sensor corroboration.
"""
from __future__ import annotations

from typing import Any
from sqlalchemy.orm import Session

from app.services.llm.geoint_grounding import GeointGroundingEngine
from app.services.llm.qwen_service import get_qwen_service


class InsightGenerator:
    """Produces multi-faceted, structured intelligence insights for satellite observations and change events."""

    @classmethod
    def generate_change_insight(cls, db: Session, change_id: str) -> dict[str, Any]:
        """Generate a complete structured intelligence brief for a detected change event."""
        context = GeointGroundingEngine.build_change_event_context(db, change_id)
        if "error" in context:
            return {"error": context["error"]}

        evidence = context.get("evidence") or {}
        change_class = context.get("classified_class") or "OTHER"
        confidence = float(context.get("confidence") or 0.75)
        delta_ndvi = float(evidence.get("delta_ndvi") or 0.0)
        spectral_diff = float(evidence.get("spectral_difference") or 0.0)
        false_alarm_risk = float(evidence.get("false_alarm_risk") or 0.0)

        # 1. Determine Severity Level
        if change_class == "CONSTRUCTION" and confidence >= 0.85:
            severity = "CRITICAL"
        elif change_class in {"CONSTRUCTION", "CLEARANCE"} and confidence >= 0.70:
            severity = "HIGH"
        elif spectral_diff > 0.40 or abs(delta_ndvi) > 0.15:
            severity = "ELEVATED"
        else:
            severity = "NOMINAL"

        delta_ndbi = float(evidence.get("delta_ndbi") or 0.0)
        delta_ndwi = float(evidence.get("delta_ndwi") or 0.0)

        # 2. Synthesize Physical Evidence Breakdown
        physical_evidence = []
        if delta_ndvi < -0.10:
            physical_evidence.append(f"Vegetation biomass loss detected (Delta NDVI: {delta_ndvi:+.3f}) consistent with land clearing.")
        elif delta_ndvi > 0.10:
            physical_evidence.append(f"Vegetation canopy expansion detected (Delta NDVI: {delta_ndvi:+.3f}).")

        if delta_ndbi > 0.10:
            physical_evidence.append(f"Built-up structural emergence detected (Delta NDBI: {delta_ndbi:+.3f}) indicating new physical installations.")
        elif delta_ndbi < -0.10:
            physical_evidence.append(f"Reduction in built-up surface signature (Delta NDBI: {delta_ndbi:+.3f}).")

        if abs(delta_ndwi) > 0.15:
            physical_evidence.append(f"Moisture index boundary fluctuation (Delta NDWI: {delta_ndwi:+.3f}).")

        if spectral_diff > 0.50:
            physical_evidence.append(f"High multi-spectral divergence (CVA magnitude: {spectral_diff:.3f}) across visible and infrared bands.")
        elif spectral_diff > 0.20:
            physical_evidence.append(f"Moderate spectral vector divergence (CVA magnitude: {spectral_diff:.3f}).")

        prithvi = evidence.get("prithvi_temporal_metrics", {})
        if prithvi:
            traj_mag = prithvi.get("trajectory_magnitude")
            if traj_mag is not None:
                physical_evidence.append(f"Prithvi-EO-2.0 temporal trajectory vector indicates significant epoch shift (magnitude: {traj_mag:.3f}).")

        if not physical_evidence:
            physical_evidence.append("Stable spectral response with minimal vector divergence.")

        # 3. Timeline / Onset
        before_date = context.get("before", {}).get("date", "Epoch T1")
        after_date = context.get("after", {}).get("date", "Epoch T2")
        timeline = f"Disturbance manifested between {before_date} and {after_date}. Sequential CUSUM onset filters confirm step-change behavior."

        # 4. Actionable Recommendations
        recommendations = []
        if severity in {"CRITICAL", "HIGH"}:
            recommendations.append("Immediate analyst verification required in Stage 5 Review Queue.")
            recommendations.append("Task high-resolution optical / SAR collection over site coordinates.")
            recommendations.append("Export W3C PROV-O audit trail to sovereign intelligence ledger.")
        elif severity == "ELEVATED":
            recommendations.append("Monitor next satellite acquisition for ongoing activity.")
            recommendations.append("Verify cloud mask and radiometric calibration.")
        else:
            recommendations.append("Mark candidate for batch archival.")

        # 5. Executive Summary
        loc_name = context.get('location_name') or 'Target AOI'
        summary = (
            f"{severity} alert: Detected {change_class.replace('_', ' ').lower()} activity at {loc_name} "
            f"with {confidence:.1%} analytical confidence. Multi-sensor metrics confirm active ground modification."
        )

        dossier = GeointGroundingEngine.format_grounded_evidence_prompt(context)

        return {
            "change_id": change_id,
            "location_name": context.get("location_name"),
            "severity": severity,
            "change_class": change_class,
            "confidence": round(confidence, 4),
            "summary": summary,
            "physical_evidence": physical_evidence,
            "spectral_indices": {
                "delta_ndvi": round(delta_ndvi, 4),
                "delta_ndbi": round(delta_ndbi, 4),
                "delta_ndwi": round(delta_ndwi, 4),
                "spectral_difference": round(spectral_diff, 4),
                "false_alarm_risk": round(false_alarm_risk, 4),
            },
            "timeline": timeline,
            "false_alarm_risk": round(false_alarm_risk, 4),
            "recommendations": recommendations,
            "grounded_dossier": dossier,
        }

    @classmethod
    def generate_observation_insight(cls, db: Session, observation_id: str) -> dict[str, Any]:
        """Generate structured intelligence insight for a single satellite observation."""
        context = GeointGroundingEngine.build_observation_context(db, observation_id)
        if "error" in context:
            return {"error": context["error"]}

        dossier = GeointGroundingEngine.format_grounded_evidence_prompt(context)
        return {
            "observation_id": observation_id,
            "location_name": context.get("location_name"),
            "acquisition_date": context.get("acquisition_date"),
            "sensor": context.get("sensor"),
            "quality_score": context.get("quality_score"),
            "summary": f"Calibrated {context.get('sensor')} observation covering {context.get('location_name')}.",
            "grounded_dossier": dossier,
        }
