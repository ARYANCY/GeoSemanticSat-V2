"""Pretrained Geospatial Foundation Models package."""
from app.services.embeddings.models.terramind import TerraMindEmbedder
from app.services.embeddings.models.satmae_pp import SatMaePPEmbedder
from app.services.embeddings.models.gfm_composition import GFMCompositionEmbedder
from app.services.embeddings.models.prithvi_temporal import PrithviTemporalEmbedder

__all__ = [
    "TerraMindEmbedder",
    "SatMaePPEmbedder",
    "GFMCompositionEmbedder",
    "PrithviTemporalEmbedder",
]
