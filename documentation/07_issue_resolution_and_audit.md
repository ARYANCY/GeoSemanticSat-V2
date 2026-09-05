# 07. Issue Resolution & Engineering Audit Report

**System Scope:** UpaGraha Backend API (`Unified-RSanalytics`) & C# Desktop Engine (`GeoSemanticSat`)  
**Audit Date:** September 2026  
**Status:** Audit & Modernization Complete

---

## 1. Complete Issues Resolution Matrix

| Issue ID | Affected File / Component | Severity | Description & Root Cause | Implemented Resolution | Verification Status |
| :--- | :--- | :---: | :--- | :--- | :---: |
| **BUG-01** | `app/services/embeddings/service.py` | **Critical** | Vector dimension mismatch crash (32-dim SAR vs 96-dim optical) | Fixed fixed 96-dim histogram generator with zero padding for 1-band rasters | **Verified** |
| **BUG-02** | `app/main.py:87-89` | **High** | NoneType dereference on observations without vector records | Added explicit null checks and graceful 404 responses | **Verified** |
| **BUG-03** | `app/main.py:93-96` | **High** | NoneType dereference on unassociated `ProcessingRun` in provenance route | Added safe fallback dictionary handling `run.provenance if run else {}` | **Verified** |
| **BUG-04** | `app/main.py:17-96` | **Critical** | Event-loop starvation from synchronous blocking raster decompression | Declared synchronous endpoints and offloaded CPU matrix math | **Verified** |
| **BUG-05** | `app/db/session.py` | **Medium** | Missing transaction rollback leaving lingering SQLite locks | Added explicit `db.rollback()` in exception handlers and session cleanup | **Verified** |
| **GEO-01** | `app/main.py:192-208` | **Critical** | Native projected UTM Cartesian meters saved as WGS84 WKT footprint | Applied `rasterio.warp.transform_bounds` to guarantee standard EPSG:4326 $(\lambda, \phi)$ | **Verified** |
| **GEO-02** | `app/main.py:181, 480` | **High** | Reading Band 1 (aerosol noise) on Sentinel-2 for change detection | Integrated `SENSOR_PROFILES` with physical Red, Green, Blue, NIR band mapping | **Verified** |
| **GEO-03** | `MultiTemporalChangeDetector.cs` | **High** | Flat Cartesian planar surface area calculation distortion | Incorporated $\cos(\text{latitude})$ scaling factor for ellipsoidal geodesic area | **Verified** |
| **ALG-01** | `OnsetEstimator.cs:80-105` | **Critical** | Single-baseline comparison triggering false onset during seasonal greening | Implemented Sequential CUSUM with moving variance ($3.5\sigma$) | **Verified** |
| **ALG-02** | `RegistrationJitterFilter.cs` | **High** | Integer-only shift testing missing fractional $0.1 - 0.8\text{ px}$ jitter | Implemented sub-pixel quadratic peak interpolation & bilinear fractional testing | **Verified** |
| **ALG-03** | `SpatialSemanticClusterer.cs` | **High** | $O(N^2)$ brute-force all-pairs comparison causing UI freeze | Implemented Spatial Hash Grid pre-filtering ($O(N \log N)$) | **Verified** |
| **PERF-01**| `VectorIndex.cs:88-135` | **High** | List cloning `_patches.ToList()` on every search allocating GBs of RAM | Implemented in-place traversal with zero heap allocations | **Verified** |
| **PERF-02**| `app/main.py:290-310` | **Medium** | Unbounded list queries on observations and analyst reviews | Added `limit` and `offset` query parameters with SQL pagination | **Verified** |
| **SEC-01** | `app/main.py:160-167` | **High** | Path traversal boundary bypass on `root` directory match | Implemented strict `target_path.is_relative_to(root)` containment validation | **Verified** |
| **SEC-02** | `Dockerfile` | **Medium** | Container executing as root user | Configured unprivileged `USER appuser` in multi-stage Docker build | **Verified** |
| **OPS-01** | `.env.example` | **Medium** | Missing environment template file referenced in README | Created `.env.example` with standard defaults | **Verified** |
| **OPS-02** | `docker-compose.yml` | **Medium** | Unattached `postgis_data` volume wiping database on container restart | Attached `postgis_data:/var/lib/postgresql/data` | **Verified** |
| **CONC-01**| `VectorIndex.cs:28-135` | **Medium** | Exclusive monitor lock blocking concurrent parallel search readers | Implemented `ReaderWriterLockSlim` for parallel read concurrency | **Verified** |
| **RRN-01** | `RadiometricNormalizer.cs` | **Medium** | OLS regression outlier leverage during large-scale ground disturbance | Upgraded to Iteratively Reweighted Least Squares (IRLS) with Tukey Biweight | **Verified** |
| **BAND-01**| `GeoTiffReader.cs:220-236` | **Medium** | Optical band order assumption inverting Red and Blue channels | Added platform-aware band dispatch for Sentinel-2, Landsat, and RGB | **Verified** |
| **SHADOW-01**| `QualityMaskEngine.cs:110` | **Medium** | Semi-transparent cloud edge aerosol noise contaminating spectral math | Implemented 1-pixel morphological cloud fringe dilation | **Verified** |
| **RERANK-01**| `RelevanceFeedbackReranker.cs`| **Low** | Negative Rocchio subtraction flipping non-negative feature signs | Clamped physical feature sub-dimensions before L2 normalization | **Verified** |
