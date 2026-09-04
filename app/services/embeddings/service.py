"""Embedding service for offline satellite intelligence visual and text retrieval."""
from __future__ import annotations

import functools
import numpy as np


class LocalEmbedder:
    """Deterministic visual baseline embedding producing fixed 96-dimensional vectors.
    
    Not semantic RemoteCLIP; maintains a fixed vector length across 1-band, 2-band,
    and 3-band rasters to ensure consistent index dimension and dot-product safety.
    """

    HIST_BINS: int = 32
    TARGET_BANDS: int = 3
    TOTAL_DIM: int = HIST_BINS * TARGET_BANDS  # 96 dimensions

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
        """Compute text embedding vector. Requires staged RemoteCLIP weights."""
        raise RuntimeError(
            "Text search requires staged RemoteCLIP weights and a licensed local adapter; "
            "runtime downloads are disabled in offline mode."
        )


@functools.lru_cache(maxsize=1)
def embedder() -> LocalEmbedder:
    """Return a cached singleton instance of LocalEmbedder."""
    return LocalEmbedder()


def norm(v: np.ndarray | list[float]) -> np.ndarray:
    """L2-normalize a vector safely."""
    arr = np.asarray(v, dtype=np.float32)
    norm_val = np.linalg.norm(arr)
    return arr / (norm_val + 1e-12)
