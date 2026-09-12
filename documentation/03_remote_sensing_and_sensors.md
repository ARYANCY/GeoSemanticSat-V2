# Rasters and sensor labels

The backend explicitly maps 1-based bands for `Sentinel-2`, `Landsat-8`, `Landsat-9`, and `PlanetScope` in `app/main.py`. The mapping is not a catalog of all supported products.

Desktop `SensorPlatform` and `SpectralBand` types represent optical and SAR-oriented concepts. `GeoTiffReader` is the desktop reader; backend ingestion uses Rasterio.

Backend ingestion requires an existing local `.tif` or `.tiff` under `DATA_ROOT`, a CRS, at least one band, and no more than `MAX_INGEST_RASTER_PIXELS` pixels. It transforms bounds to EPSG:4326 when transformation succeeds; it otherwise records a source-bounds fallback polygon. It samples at most 12 bands to an array no larger than 256 by 256 for embedding.

`scripts/create_sample_data.py` makes two small 3-band and one 1-band EPSG:4326 GeoTIFFs. They are smoke-test fixtures, not satellite acquisitions.