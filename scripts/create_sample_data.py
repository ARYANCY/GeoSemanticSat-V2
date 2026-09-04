"""Create tiny local sample GeoTIFFs (both 3-band RGB and 1-band SAR/NDVI) for smoke tests."""
from __future__ import annotations

import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

import numpy as np
import rasterio
from rasterio.transform import from_origin
from app.core.config import settings


def generate_sample_rasters() -> None:
    """Generate sample test GeoTIFFs under settings.data_root."""
    settings.data_root.mkdir(parents=True, exist_ok=True)
    transform = from_origin(92.0, 27.0, 0.0001, 0.0001)

    # 3-band Before / After
    base_3band = np.zeros((3, 128, 128), dtype=np.uint16)
    base_3band[:, 20:80, 20:80] = 500

    changed_3band = base_3band.copy()
    changed_3band[:, 70:110, 70:110] = 1800

    # 1-band Panchromatic / SAR sample
    base_1band = np.zeros((1, 128, 128), dtype=np.uint16)
    base_1band[0, 30:90, 30:90] = 750

    samples = [
        ("sample_before.tif", base_3band),
        ("sample_after.tif", changed_3band),
        ("sample_singleband.tif", base_1band),
    ]

    for filename, image in samples:
        target_path = settings.data_root / filename
        with rasterio.open(
            target_path,
            "w",
            driver="GTiff",
            height=image.shape[1],
            width=image.shape[2],
            count=image.shape[0],
            dtype=image.dtype,
            crs="EPSG:4326",
            transform=transform,
        ) as dst:
            dst.write(image)
        print(f"Created sample GeoTIFF: {target_path} (bands={image.shape[0]})")


if __name__ == "__main__":
    generate_sample_rasters()
