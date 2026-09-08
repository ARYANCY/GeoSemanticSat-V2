"""Embedding service for offline satellite intelligence visual and text retrieval.

Supports native deterministic baseline and pretrained Earth Observation foundation models:
- TerraMind-1.0-base (IBM-ESA Any-to-Any Multimodal)
- SatMAE++ Transformers (Grouped Multi-Spectral MAE)
- GFM Composition Pretraining (SAR + Optical Multi-Sensor)
- Prithvi-EO-2.0-600M-TL (IBM-NASA Spatio-Temporal ViT)
"""
from __future__ import annotations

import functools
from typing import Any, Protocol, runtime_checkable
import numpy as np

from app.core.config import settings
from app.services.embeddings.models.terramind import TerraMindEmbedder
from app.services.embeddings.models.satmae_pp import SatMaePPEmbedder
from app.services.embeddings.models.gfm_composition import GFMCompositionEmbedder
from app.services.embeddings.models.prithvi_temporal import PrithviTemporalEmbedder


@runtime_checkable
class EmbedderProtocol(Protocol):
    def image(self, x: np.ndarray) -> np.ndarray: ...
    def text(self, query: str) -> np.ndarray: ...


class LocalEmbedder:
    """Deterministic visual baseline embedding producing fixed 96-dimensional vectors.
    
    Maintains a fixed vector length across 1-band, 2-band, and 3-band rasters
    to ensure consistent index dimension and dot-product safety.
    """

    HIST_BINS: int = 32
    TARGET_BANDS: int = 3
    TOTAL_DIM: int = HIST_BINS * TARGET_BANDS  # 96 dimensions
    MODEL_NAME: str = "histogram-baseline"

    @property
    def is_loaded(self) -> bool:
        return True

    def image(self, x: np.ndarray) -> np.ndarray:
        """Compute a fixed 96-dimensional histogram feature vector from an input raster.
        
        Args:
            x: NumPy array of shape (bands, height, width) or (height, width).
            
        Returns:
            L2-normalized float32 NumPy array of shape (96,).
        """
        if x.ndim == 2:
            x = x[np.newaxis, ...]

        num_bands = x.shape[0]
        histograms: list[np.ndarray] = []

        for i in range(self.TARGET_BANDS):
            band_idx = min(i, num_bands - 1)
            band_data = x[band_idx].ravel()
            hist, _ = np.histogram(band_data, bins=self.HIST_BINS, range=(0.0, 1.0))
            histograms.append(hist)

        vector = np.concatenate(histograms).astype(np.float32)
        norm_val = np.linalg.norm(vector)
        return vector / (norm_val + 1e-12)

    def text(self, query: str) -> np.ndarray:
        """Compute text embedding vector. Requires staged TerraMind or RemoteCLIP weights."""
        raise RuntimeError(
            "Text search requires staged TerraMind-1.0-base or RemoteCLIP weights; "
            "runtime downloads are disabled in offline mode."
        )


def create_embedder(model_name: str | None = None) -> Any:
    """Factory function instantiating the requested embedding backbone."""
    name = (model_name or settings.eo_model_name).lower().strip()

    if name in {"terramind", "terramind-1.0-base", "ibm-esa-geospatial/terramind-1.0-base"}:
        return TerraMindEmbedder(weights_path=settings.terramind_model_path)
    elif name in {"satmae_pp", "satmae-pp", "satmae", "bilisakura/satmae-pp-transformers"}:
        return SatMaePPEmbedder(weights_path=settings.satmae_pp_model_path)
    elif name in {"gfm_composition", "gfm", "gfm-composition"}:
        return GFMCompositionEmbedder(weights_path=settings.gfm_model_path)
    elif name in {"prithvi", "prithvi_temporal", "prithvi-eo-2.0", "ibm-nasa-geospatial/prithvi-eo-2.0-600m-tl"}:
        return PrithviTemporalEmbedder(weights_path=settings.prithvi_model_path)
    else:
        return LocalEmbedder()


@functools.lru_cache(maxsize=4)
def get_embedder(model_name: str | None = None) -> Any:
    """Return a cached instance of the specified embedder."""
    return create_embedder(model_name)


def embedder() -> Any:
    """Return the active singleton embedder configured in settings."""
    return get_embedder(settings.eo_model_name)


def get_available_models_status() -> dict[str, Any]:
    """Inspect and return operational status for all foundation models."""
    return {
        "active_model": settings.eo_model_name,
        "models": {
            "baseline": {
                "name": "histogram-baseline",
                "dimension": 96,
                "modalities": ["optical", "sar"],
                "status": "ready (native)",
            },
            "terramind-1.0-base": {
                "name": "ibm-esa-geospatial/TerraMind-1.0-base",
                "dimension": 128,
                "modalities": ["text", "optical", "sar", "topography"],
                "staged": settings.terramind_model_path.is_file(),
                "path": str(settings.terramind_model_path),
                "status": "staged" if settings.terramind_model_path.is_file() else "available (offline fallback adapter)",
            },
            "satmae-pp": {
                "name": "BiliSakura/SATMAE-PP-transformers",
                "dimension": 128,
                "modalities": ["grouped multi-spectral optical"],
                "staged": settings.satmae_pp_model_path.is_file(),
                "path": str(settings.satmae_pp_model_path),
                "status": "staged" if settings.satmae_pp_model_path.is_file() else "available (offline fallback adapter)",
            },
            "gfm-composition": {
                "name": "GFM_Composition_Pretraining",
                "dimension": 128,
                "modalities": ["sentinel-1 sar", "sentinel-2 optical"],
                "staged": settings.gfm_model_path.is_file(),
                "path": str(settings.gfm_model_path),
                "status": "staged" if settings.gfm_model_path.is_file() else "available (offline fallback adapter)",
            },
            "prithvi-eo-2.0-600m-tl": {
                "name": "ibm-nasa-geospatial/Prithvi-EO-2.0-600M-TL",
                "dimension": 128,
                "modalities": ["multi-temporal sentinel-2"],
                "staged": settings.prithvi_model_path.is_file(),
                "path": str(settings.prithvi_model_path),
                "status": "staged" if settings.prithvi_model_path.is_file() else "available (offline fallback adapter)",
            },
        },
    }


def norm(v: np.ndarray | list[float]) -> np.ndarray:
    """L2-normalize a vector safely."""
    arr = np.asarray(v, dtype=np.float32)
    norm_val = np.linalg.norm(arr)
    return arr / (norm_val + 1e-12)
