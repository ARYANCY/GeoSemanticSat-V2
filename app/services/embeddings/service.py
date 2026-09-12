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


# Single source of truth semantic axes matching SemanticEmbeddingLayout.cs
SEMANTIC_AXES: tuple[int, ...] = (16, 17, 18, 19, 20, 21, 22, 23, 37, 64, 65)


def cosine_on_semantic_axes(a: np.ndarray, b: np.ndarray) -> float:
    """Compute cosine similarity restricted to shared semantic axes.
    
    Renormalizes each side over the semantic subspace so the result is a true
    cosine in [-1, 1] not diluted by appearance block dimensions.
    """
    sub_a = np.asarray([a[i] if i < len(a) else 0.0 for i in SEMANTIC_AXES], dtype=np.float32)
    sub_b = np.asarray([b[i] if i < len(b) else 0.0 for i in SEMANTIC_AXES], dtype=np.float32)
    norm_a = np.linalg.norm(sub_a)
    norm_b = np.linalg.norm(sub_b)
    if norm_a < 1e-12 or norm_b < 1e-12:
        return 0.0
    return float(np.clip(np.dot(sub_a, sub_b) / (norm_a * norm_b), -1.0, 1.0))


class LocalEmbedder:
    """High-Fidelity 128-dimensional baseline embedding matching SemanticEmbeddingLayout.
    
    Structure:
      - 0..15: Spectral Band Statistics (Means & Standard Deviations)
      - 16..23: Shared Bounded Semantic Indices (NDVI, NDWI, NDBI, MNDWI, BSI, etc.)
      - 32..37: Spatial Gradient & Structural Morphology
      - 64..79: Texture Energy & High-Frequency Activity Peaks (GLCM proxy)
      - 80..127: Multi-Quadrant Spatial Distribution
    """

    DIMENSION: int = 128
    MODEL_NAME: str = "native-semantic-128d"

    @property
    def is_loaded(self) -> bool:
        return True

    def image(self, x: np.ndarray) -> np.ndarray:
        """Encode raster into normalized 128-dimensional feature vector."""
        if x.ndim == 2:
            x = x[np.newaxis, ...]

        num_bands, h, w = x.shape
        embedding = np.zeros(self.DIMENSION, dtype=np.float32)

        # 1. Band Statistics -> [0..15]
        for b in range(min(num_bands, 8)):
            band = x[b]
            embedding[b] = float(np.mean(band))
            embedding[8 + b] = float(np.std(band))

        # 2. Shared Semantic Axes -> [16..23]
        red = x[min(2, num_bands - 1)]
        green = x[min(1, num_bands - 1)]
        blue = x[0]
        nir = x[min(3, num_bands - 1)] if num_bands >= 4 else red
        swir = x[min(4, num_bands - 1)] if num_bands >= 5 else nir

        m_r, m_g, m_b, m_n, m_s = float(np.mean(red)), float(np.mean(green)), float(np.mean(blue)), float(np.mean(nir)), float(np.mean(swir))
        embedding[16] = float(np.clip((m_n - m_r) / (m_n + m_r + 1e-6), -1.0, 1.0))  # Vegetation (NDVI)
        embedding[17] = float(np.clip((m_g - m_n) / (m_g + m_n + 1e-6), -1.0, 1.0))  # Water (NDWI)
        embedding[18] = float(np.clip((m_s - m_n) / (m_s + m_n + 1e-6), -1.0, 1.0))  # BuiltUp (NDBI)
        embedding[19] = float(np.clip((m_g - m_s) / (m_g + m_s + 1e-6), -1.0, 1.0))  # OpenWater (MNDWI)
        embedding[20] = float(np.clip(((m_s + m_r) - (m_n + m_b)) / ((m_s + m_r) + (m_n + m_b) + 1e-6), -1.0, 1.0))  # BareSoil (BSI)
        embedding[21] = float(np.clip(embedding[18] * (embedding[17] + 1.0) * 0.5, -1.0, 1.0))  # StructureNearWater
        embedding[22] = float(np.clip((1.0 - embedding[16]) * embedding[20] * 0.5, -1.0, 1.0))  # ClearedGround
        embedding[23] = float(np.clip(float(np.std(red) + np.std(green)) * (embedding[18] + 1.0), -1.0, 1.0))  # ManMadeContrast

        # 3. Spatial Gradients -> [32..37]
        if h > 2 and w > 2:
            gy, gx = np.gradient(red)
            grad_mag = np.sqrt(gx * gx + gy * gy)
            embedding[32] = float(np.mean(grad_mag))
            embedding[33] = float(np.mean(np.abs(gx)))
            embedding[34] = float(np.mean(np.abs(gy)))
            embedding[37] = float(np.clip(np.max([embedding[33], embedding[34]]), 0.0, 1.0))  # LinearContinuity

        # 4. Texture Energy & Peaks -> [64..65]
        diff = np.abs(red - m_r)
        embedding[64] = float(np.clip(np.mean(diff ** 2), 0.0, 1.0))  # TextureEnergy
        embedding[65] = float(np.mean(diff > 0.30))  # ActivityPeaks

        # 5. Multi-quadrant Layout -> [80..95]
        mid_y, mid_x = h // 2, w // 2
        for q_idx, (y1, y2, x1, x2) in enumerate([
            (0, mid_y, 0, mid_x), (0, mid_y, mid_x, w),
            (mid_y, h, 0, mid_x), (mid_y, h, mid_x, w)
        ]):
            if y2 > y1 and x2 > x1:
                q_patch = red[y1:y2, x1:x2]
                embedding[80 + q_idx * 4] = float(np.mean(q_patch))
                embedding[81 + q_idx * 4] = float(np.std(q_patch))

        norm_val = np.linalg.norm(embedding)
        return (embedding / (norm_val + 1e-12)).astype(np.float32)

    def text(self, query: str) -> np.ndarray:
        """Encode a natural language semantic query into the shared 128-dimensional semantic subspace."""
        # Reuse the module-level cached TerraMind singleton to avoid repeated allocation.
        return get_embedder("terramind").text(query)


def create_embedder(model_name: str | None = None) -> Any:
    """Factory function instantiating the requested embedding backbone."""
    name = (model_name or settings.eo_model_name).lower().strip()

    if name in {"terramind", "terramind-1.0-base", "ibm-esa-geospatial/terramind-1.0-base"}:
        lora = settings.terramind_lora_path if settings.use_fine_tuned_weights and settings.terramind_lora_path.is_file() else None
        return TerraMindEmbedder(weights_path=settings.terramind_model_path, lora_path=lora)
    elif name in {"satmae_pp", "satmae-pp", "satmae", "bilisakura/satmae-pp-transformers"}:
        lora = settings.satmae_lora_path if settings.use_fine_tuned_weights and settings.satmae_lora_path.is_file() else None
        return SatMaePPEmbedder(weights_path=settings.satmae_pp_model_path, lora_path=lora)
    elif name in {"gfm_composition", "gfm", "gfm-composition"}:
        slots = settings.gfm_slots_path if settings.use_fine_tuned_weights and settings.gfm_slots_path.is_file() else None
        return GFMCompositionEmbedder(weights_path=settings.gfm_model_path, slots_path=slots)
    elif name in {"prithvi", "prithvi_temporal", "prithvi-eo-2.0", "ibm-nasa-geospatial/prithvi-eo-2.0-600m-tl", "ibm-nasa-geospatial/prithvi-eo-2.0-300m", "prithvi-300m"}:
        path = settings.prithvi_300m_model_path if settings.prithvi_300m_model_path.is_file() else settings.prithvi_model_path
        lora = settings.prithvi_lora_path if settings.use_fine_tuned_weights and settings.prithvi_lora_path.is_file() else None
        return PrithviTemporalEmbedder(weights_path=path, lora_path=lora)
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
    qwen_staged = settings.qwen_model_path.is_dir() and any(settings.qwen_model_path.iterdir())
    prithvi_staged = settings.prithvi_300m_model_path.is_file() or settings.prithvi_model_path.is_file() or (settings.model_root / "prithvi").is_dir() and any((settings.model_root / "prithvi").iterdir())
    terramind_staged = settings.terramind_model_path.is_file() or (settings.model_root / "terramind").is_dir() and any((settings.model_root / "terramind").iterdir())
    satmae_staged = settings.satmae_pp_model_path.is_file() or (settings.model_root / "satmae_pp").is_dir() and any((settings.model_root / "satmae_pp").iterdir())
    gfm_staged = settings.gfm_model_path.is_file() or (settings.model_root / "gfm_composition").is_dir() and any((settings.model_root / "gfm_composition").iterdir())

    terramind_ft = settings.terramind_lora_path.is_file()
    prithvi_ft = settings.prithvi_lora_path.is_file()
    satmae_ft = settings.satmae_lora_path.is_file()
    gfm_ft = settings.gfm_slots_path.is_file()

    return {
        "active_model": settings.eo_model_name,
        "models": {
            "baseline": {
                "name": "native-semantic-128d",
                "dimension": 128,
                "modalities": ["optical", "sar"],
                "status": "ready (native)",
                "fine_tuned": False,
            },
            "qwen3-8b": {
                "name": "Qwen/Qwen3-8B",
                "role": "Conversational Intelligence & Reasoning Agent",
                "type": "llm",
                "staged": qwen_staged,
                "path": str(settings.qwen_model_path),
                "status": "staged" if qwen_staged else "ready (grounded fallback agent)",
                "fine_tuned": False,
            },
            "terramind-1.0-base": {
                "name": "ibm-esa-geospatial/TerraMind-1.0-base",
                "dimension": 128,
                "modalities": ["text", "optical", "sar", "topography"],
                "staged": terramind_staged,
                "fine_tuned": terramind_ft,
                "path": str(settings.terramind_model_path),
                "lora_path": str(settings.terramind_lora_path) if terramind_ft else None,
                "status": "fine-tuned (LoRA active)" if terramind_ft else ("staged" if terramind_staged else "available (offline fallback adapter)"),
            },
            "satmae-pp": {
                "name": "BiliSakura/SATMAE-PP-transformers",
                "dimension": 128,
                "modalities": ["grouped multi-spectral optical"],
                "staged": satmae_staged,
                "fine_tuned": satmae_ft,
                "path": str(settings.satmae_pp_model_path),
                "lora_path": str(settings.satmae_lora_path) if satmae_ft else None,
                "status": "fine-tuned (LoRA active)" if satmae_ft else ("staged" if satmae_staged else "available (offline fallback adapter)"),
            },
            "gfm-composition": {
                "name": "GFM_Composition_Pretraining",
                "dimension": 128,
                "modalities": ["sentinel-1 sar", "sentinel-2 optical"],
                "staged": gfm_staged,
                "fine_tuned": gfm_ft,
                "path": str(settings.gfm_model_path),
                "slots_path": str(settings.gfm_slots_path) if gfm_ft else None,
                "status": "fine-tuned (slots active)" if gfm_ft else ("staged" if gfm_staged else "available (offline fallback adapter)"),
            },
            "prithvi-eo-2.0-300m": {
                "name": "ibm-nasa-geospatial/Prithvi-EO-2.0-300M",
                "dimension": 128,
                "modalities": ["multi-temporal sentinel-2"],
                "staged": prithvi_staged,
                "fine_tuned": prithvi_ft,
                "path": str(settings.prithvi_300m_model_path),
                "lora_path": str(settings.prithvi_lora_path) if prithvi_ft else None,
                "status": "fine-tuned (LoRA active)" if prithvi_ft else ("staged" if prithvi_staged else "available (offline fallback adapter)"),
            },
            "prithvi-eo-2.0-600m-tl": {
                "name": "ibm-nasa-geospatial/Prithvi-EO-2.0-600M-TL",
                "dimension": 128,
                "modalities": ["multi-temporal sentinel-2"],
                "staged": prithvi_staged,
                "fine_tuned": False,
                "path": str(settings.prithvi_model_path),
                "status": "staged" if prithvi_staged else "available (offline fallback adapter)",
            },
        },
    }


def norm(v: np.ndarray | list[float]) -> np.ndarray:
    """L2-normalize a vector safely."""
    arr = np.asarray(v, dtype=np.float32)
    norm_val = np.linalg.norm(arr)
    return arr / (norm_val + 1e-12)
