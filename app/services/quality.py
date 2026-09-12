"""Optical quality scoring from raster arrays (cloud/haze/saturation proxies)."""
from __future__ import annotations

import numpy as np


def estimate_quality_score(raster: np.ndarray) -> dict[str, float | bool]:
    """Score scene usability from min-max normalized reflectance in [0, 1].

    This is a local heuristic, not a SCL product. Bright-saturated fraction
    approximates cloud/snow; dark fraction approximates shadow/nodata.
    """
    arr = np.asarray(raster, dtype=np.float32)
    if arr.size == 0:
        return {"quality_score": 0.0, "cloud_or_snow_frac": 1.0, "shadow_frac": 0.0, "usable": False}

    vis = arr[: min(3, arr.shape[0])]
    mean_vis = vis.mean(axis=0)
    cloud_frac = float(np.mean(mean_vis > 0.85))
    shadow_frac = float(np.mean(mean_vis < 0.04))
    haze_proxy = float(np.clip(np.std(mean_vis) * 0.5, 0.0, 1.0))
    quality = float(np.clip(1.0 - 0.75 * cloud_frac - 0.35 * shadow_frac - 0.15 * (1.0 - haze_proxy), 0.0, 1.0))
    return {
        "quality_score": round(quality, 4),
        "cloud_or_snow_frac": round(cloud_frac, 4),
        "shadow_frac": round(shadow_frac, 4),
        "haze_proxy": round(haze_proxy, 4),
        "usable": quality >= 0.40,
    }
