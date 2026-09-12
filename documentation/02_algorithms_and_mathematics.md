# Implemented algorithms

This page describes code behavior, not independently validated accuracy or performance.

- `SpectralIndices` supplies the desktop indices used by change logic, including NDVI, NDWI, NDBI, MNDWI, and BSI where bands or fallbacks are available.
- `MultiTemporalChangeDetector` excludes unusable pixels, calculates scene-drift-adjusted deltas, and assigns the strongest matching rule: water extent variation, clearance, construction, road development, activity concentration, or no change. Water requires both NDWI movement and an MNDWI-consistent endpoint.
- `OnsetEstimator` calculates an AOI metric over usable pixels. With at least three usable observations, it uses a baseline, a standard-deviation floor of `0.035`, slack `0.5 sigma`, and threshold `max(0.12, 3.5 sigma)` for CUSUM onset.
- `QualityMaskEngine` marks optical cloud, shadow, snow, water, haze, saturation, and no-data. Cloud, shadow, snow, saturation, and no-data make a pixel unusable; water and haze do not.
- `SemanticEmbeddingLayout` defines a 128-dimensional vector. Image comparison uses full-vector cosine; text-to-image comparison uses renormalized shared semantic axes.

The Python baseline embedder is 128-dimensional and defines the same semantic axes. Python search accommodates stored 96- and 128-element vectors; that is compatibility code, not a format promise.