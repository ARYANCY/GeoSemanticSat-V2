"""Unit and integration tests for Pretrained Geospatial Foundation Models:
- TerraMind-1.0-base (IBM-ESA)
- SatMAE++ Transformers (BiliSakura)
- GFM Composition Pretraining (SAR + Optical)
- Prithvi-EO-2.0-600M-TL (IBM-NASA)
"""
from __future__ import annotations

import numpy as np
import pytest
from fastapi.testclient import TestClient

from app.core.config import settings
from app.main import app
from app.services.embeddings.models.terramind import TerraMindEmbedder
from app.services.embeddings.models.satmae_pp import SatMaePPEmbedder
from app.services.embeddings.models.gfm_composition import GFMCompositionEmbedder
from app.services.embeddings.models.prithvi_temporal import PrithviTemporalEmbedder
from app.services.embeddings.service import (
    create_embedder,
    get_available_models_status,
    get_embedder,
    LocalEmbedder,
    norm,
)


@pytest.fixture
def synthetic_multispectral_patch() -> np.ndarray:
    """Generate synthetic 12-band Sentinel-2 patch of shape (12, 64, 64)."""
    np.random.seed(42)
    return np.random.uniform(0.0, 1.0, size=(12, 64, 64)).astype(np.float32)


@pytest.fixture
def synthetic_sar_patch() -> np.ndarray:
    """Generate synthetic 2-band Sentinel-1 SAR (VV, VH) patch of shape (2, 64, 64)."""
    np.random.seed(42)
    return np.random.uniform(0.0, 1.0, size=(2, 64, 64)).astype(np.float32)


# ==============================================================================
# 1. TerraMind-1.0-base Tests
# ==============================================================================

def test_terramind_embedder_image_and_text(synthetic_multispectral_patch):
    """Verify TerraMind-1.0-base generates normalized 128-dim vectors for both imagery and text."""
    embedder = TerraMindEmbedder()
    assert embedder.DIMENSION == 128
    assert embedder.MODEL_NAME == "ibm-esa-geospatial/TerraMind-1.0-base"

    # Image embedding
    img_vec = embedder.image(synthetic_multispectral_patch)
    assert isinstance(img_vec, np.ndarray)
    assert img_vec.shape == (128,)
    assert img_vec.dtype == np.float32
    assert np.isclose(np.linalg.norm(img_vec), 1.0, atol=1e-4)

    # Text query embedding
    text_vec = embedder.text("new military structures and runways near river")
    assert isinstance(text_vec, np.ndarray)
    assert text_vec.shape == (128,)
    assert np.isclose(np.linalg.norm(text_vec), 1.0, atol=1e-4)

    # Semantic cross-modal similarity (Text to Image)
    sim = float(np.dot(img_vec, text_vec))
    assert -1.0 <= sim <= 1.0


# ==============================================================================
# 2. SatMAE++ Transformers Tests
# ==============================================================================

def test_satmae_pp_embedder(synthetic_multispectral_patch):
    """Verify SatMAE++ produces grouped multi-spectral patch representations."""
    embedder = SatMaePPEmbedder()
    assert embedder.DIMENSION == 128
    assert embedder.MODEL_NAME == "BiliSakura/SATMAE-PP-transformers"

    vec = embedder.image(synthetic_multispectral_patch)
    assert vec.shape == (128,)
    assert np.isclose(np.linalg.norm(vec), 1.0, atol=1e-4)

    # Band grouping check
    group_feats = embedder.encode_band_groups(synthetic_multispectral_patch)
    assert len(group_feats) == embedder.NUM_BAND_GROUPS * 16


# ==============================================================================
# 3. GFM Composition Pretraining Tests
# ==============================================================================

def test_gfm_composition_embedder(synthetic_multispectral_patch, synthetic_sar_patch):
    """Verify GFM compositional SAR+Optical fusion."""
    embedder = GFMCompositionEmbedder()
    assert embedder.DIMENSION == 128

    # Optical only
    opt_vec = embedder.image(synthetic_multispectral_patch)
    assert opt_vec.shape == (128,)
    assert np.isclose(np.linalg.norm(opt_vec), 1.0, atol=1e-4)

    # Multi-sensor SAR + Optical Composition
    composed_vec = embedder.compose_sar_optical(
        optical=synthetic_multispectral_patch, sar=synthetic_sar_patch
    )
    assert composed_vec.shape == (128,)
    assert np.isclose(np.linalg.norm(composed_vec), 1.0, atol=1e-4)


# ==============================================================================
# 4. Prithvi-EO-2.0-600M-TL Spatio-Temporal Tests
# ==============================================================================

def test_prithvi_temporal_embedder(synthetic_multispectral_patch):
    """Verify Prithvi-EO-2.0 temporal sequence modeling and change metrics."""
    embedder = PrithviTemporalEmbedder()
    assert embedder.DIMENSION == 128
    assert embedder.MODEL_NAME == "ibm-nasa-geospatial/Prithvi-EO-2.0-600M-TL"

    # Single epoch
    vec1 = embedder.image(synthetic_multispectral_patch)
    assert vec1.shape == (128,)

    # Multi-temporal series [T1, T2, T3]
    t2 = synthetic_multispectral_patch * 1.2
    t3 = synthetic_multispectral_patch * 0.8
    seq_vec = embedder.encode_temporal_sequence([synthetic_multispectral_patch, t2, t3])
    assert seq_vec.shape == (128,)
    assert np.isclose(np.linalg.norm(seq_vec), 1.0, atol=1e-4)

    # Temporal change analysis
    change_metrics = embedder.analyze_temporal_change(synthetic_multispectral_patch, t2)
    assert "temporal_distance" in change_metrics
    assert "trajectory_magnitude" in change_metrics
    assert "delta_ndvi" in change_metrics


# ==============================================================================
# 5. Registry & Model Status Tests
# ==============================================================================

def test_model_registry_and_status():
    """Verify factory methods and model status reporting."""
    status = get_available_models_status()
    assert "active_model" in status
    assert "terramind-1.0-base" in status["models"]
    assert "satmae-pp" in status["models"]
    assert "gfm-composition" in status["models"]
    assert "prithvi-eo-2.0-600m-tl" in status["models"]

    tm = create_embedder("terramind")
    assert isinstance(tm, TerraMindEmbedder)

    sm = create_embedder("satmae_pp")
    assert isinstance(sm, SatMaePPEmbedder)

    gfm = create_embedder("gfm_composition")
    assert isinstance(gfm, GFMCompositionEmbedder)

    pr = create_embedder("prithvi")
    assert isinstance(pr, PrithviTemporalEmbedder)


# ==============================================================================
# 6. Text Retrieval with TerraMind Active Embedder
# ==============================================================================

def test_text_retrieval_with_terramind(monkeypatch):
    """Verify text search endpoint executes successfully when TerraMind backbone is selected."""
    monkeypatch.setattr(settings, "eo_model_name", "terramind")
    get_embedder.cache_clear()

    with TestClient(app) as client:
        resp = client.post(
            "/api/v1/search/text",
            json={"query": "structures near river", "top_k": 5},
        )
        assert resp.status_code == 200
        data = resp.json()
        assert "results" in data

    # Reset cache
    get_embedder.cache_clear()
