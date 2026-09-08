"""Offline staging and verification utility for Pretrained Geospatial Foundation Models.

Models managed:
1. TerraMind-1.0-base (IBM-ESA Any-to-Any Multimodal) - https://huggingface.co/ibm-esa-geospatial/TerraMind-1.0-base
2. SatMAE++ Transformers (Grouped Multi-Spectral MAE) - https://huggingface.co/BiliSakura/SATMAE-PP-transformers
3. GFM Composition Pretraining (SAR + Optical) - https://github.com/05kashyap/GFM_Composition_Pretraining
4. Prithvi-EO-2.0-600M-TL (IBM-NASA Spatio-Temporal ViT) - https://huggingface.co/ibm-nasa-geospatial/Prithvi-EO-2.0-600M-TL
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

# Ensure project root in sys.path
PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from app.core.config import settings
from app.core.logging import logger

FOUNDATION_MODELS = {
    "terramind": {
        "name": "TerraMind-1.0-base",
        "repo": "https://huggingface.co/ibm-esa-geospatial/TerraMind-1.0-base",
        "organization": "IBM / European Space Agency (ESA)",
        "modalities": ["Text", "Multi-Spectral Optical (B01-B12)", "SAR (VV/VH)", "Topography"],
        "architecture": "Any-to-Any Multimodal Transformer",
        "weights_file": settings.terramind_model_path,
        "role": "Natural language text-to-satellite semantic retrieval and cross-modal search.",
    },
    "satmae_pp": {
        "name": "SATMAE-PP-transformers",
        "repo": "https://huggingface.co/BiliSakura/SATMAE-PP-transformers",
        "organization": "BiliSakura / Academic",
        "modalities": ["Grouped Multi-Spectral (RGB, RedEdge, NIR, SWIR)"],
        "architecture": "Group-Aware Masked Autoencoder (ViT)",
        "weights_file": settings.satmae_pp_model_path,
        "role": "Deep multi-spectral optical patch feature encoding and representation.",
    },
    "gfm_composition": {
        "name": "GFM_Composition_Pretraining",
        "repo": "https://github.com/05kashyap/GFM_Composition_Pretraining",
        "organization": "05kashyap / Open Source",
        "modalities": ["Sentinel-1 SAR (VV/VH)", "Sentinel-2 Optical (MSI)"],
        "architecture": "Compositional Cross-Sensor Pretraining",
        "weights_file": settings.gfm_model_path,
        "role": "Multi-sensor SAR+Optical fusion for all-weather cloud-resilient change detection.",
    },
    "prithvi": {
        "name": "Prithvi-EO-2.0-600M-TL",
        "repo": "https://huggingface.co/ibm-nasa-geospatial/Prithvi-EO-2.0-600M-TL",
        "organization": "IBM / NASA Geospatial",
        "modalities": ["Multi-Temporal Sentinel-2 Time-Series (B02, B03, B04, B8A, B11, B12)"],
        "architecture": "3D Spatio-Temporal Masked Autoencoder (600M parameters)",
        "weights_file": settings.prithvi_model_path,
        "role": "Multi-temporal sequence modeling, temporal change analysis, and CUSUM onset.",
    },
}


def stage_models_directory() -> None:
    """Prepare and verify offline directory structure and manifest for foundation models."""
    settings.ensure_directories()

    manifest_path = settings.model_root / "foundation_models_manifest.json"
    manifest: dict[str, dict] = {}

    print("=" * 80)
    print("UPAGRAHA / GEOSEMANTICSAT — PRETRAINED FOUNDATION MODELS STAGING")
    print("=" * 80)

    for key, spec in FOUNDATION_MODELS.items():
        weights_path: Path = spec["weights_file"]
        is_staged = weights_path.is_file()
        file_size_mb = round(weights_path.stat().st_size / (1024 * 1024), 2) if is_staged else 0.0

        print(f"\n[Model: {spec['name']}]")
        print(f"  • Source:        {spec['repo']}")
        print(f"  • Provider:      {spec['organization']}")
        print(f"  • Modalities:    {', '.join(spec['modalities'])}")
        print(f"  • Primary Role:  {spec['role']}")
        print(f"  • Staging Path:  {weights_path}")
        print(f"  • Offline State: {'[STAGED - ' + str(file_size_mb) + ' MB]' if is_staged else '[NOT STAGED - Active Native Fallback Ready]'}")

        manifest[key] = {
            "name": spec["name"],
            "repo": spec["repo"],
            "organization": spec["organization"],
            "modalities": spec["modalities"],
            "role": spec["role"],
            "path": str(weights_path),
            "staged": is_staged,
            "size_mb": file_size_mb,
            "fallback_available": True,
        }

    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(f"\nManifest successfully written to: {manifest_path}")
    print("=" * 80)


if __name__ == "__main__":
    stage_models_directory()
