"""Create two tiny local GeoTIFFs for offline API smoke tests."""
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import numpy as np
import rasterio
from rasterio.transform import from_origin
from app.core.config import settings

settings.data_root.mkdir(parents=True, exist_ok=True)
transform = from_origin(92.0, 27.0, 0.0001, 0.0001)
base = np.zeros((3, 128, 128), dtype=np.uint16)
base[:, 20:80, 20:80] = 500
changed = base.copy()
changed[:, 70:110, 70:110] = 1800
for name, image in (("sample_before.tif", base), ("sample_after.tif", changed)):
    with rasterio.open(settings.data_root / name, "w", driver="GTiff", height=128, width=128, count=3, dtype=image.dtype, crs="EPSG:4326", transform=transform) as dst:
        dst.write(image)
print("Created data/sample_before.tif and data/sample_after.tif")
