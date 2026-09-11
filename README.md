# UpaGraha: Offline-First Geospatial Intelligence & Satellite Analytics Platform

> **Developer documentation source of truth:** start with
> [documentation/00_developer_guide.md](documentation/00_developer_guide.md) and
> [documentation/07_implementation_reference.md](documentation/07_implementation_reference.md).
> Those pages are aligned with the current source tree. Some older sections in
> this README and the topic pages describe planned or historical capabilities;
> they must not be used as operational instructions when they conflict with the
> implementation-aligned guide.

[![Platform](https://img.shields.io/badge/Platform-Air--Gapped%20%7C%20Zero--Internet-blue.svg)](#)
[![Backend](https://img.shields.io/badge/Backend-FastAPI%20%7C%20Python%203.11-green.svg)](#)
[![Engine](https://img.shields.io/badge/Core%20Engine-.NET%2010%20%7C%20C%23-purple.svg)](#)
[![Spatial DB](https://img.shields.io/badge/Spatial%20DB-PostgreSQL%2016%20%2B%20PostGIS%203.4-blue.svg)](#)
[![Vector Acceleration](https://img.shields.io/badge/SIMD-AVX2%20Vector256-orange.svg)](#)
[![Audit Standard](https://img.shields.io/badge/Provenance-W3C%20PROV--O%20GeoJSON-teal.svg)](#)

**UpaGraha** is an enterprise-grade Earth Observation (EO) and Geospatial Intelligence (GEOINT) platform architected for **100% air-gapped, zero-internet, on-premises operation**. It empowers defense and intelligence analysts, disaster management teams, and environmental monitoring organizations to ingest high-resolution satellite imagery, execute sub-millisecond visual similarity searches, detect physical land-use changes, track multi-temporal onset timelines, and conduct analyst verification workflows without external network dependencies or third-party cloud mapping services.

---

## Key Capabilities & Highlights

- **Air-Gapped & Sovereign Deployment**: Zero cloud or telemetry dependencies; fully self-contained on local workstations, edge appliances, or sovereign data centers.
- **Sub-Millisecond Vector Search**: AVX2 SIMD-accelerated cosine similarity search over dense multi-spectral embedding spaces with custom binary GSSV and FAISS indices.
- **Multi-Band Change Vector Analysis (CVA)**: True spectral magnitude and directional angle classification across multispectral bands (`Construction`, `Clearance`, `WaterExtentVariation`, `RoadDevelopment`, `ActivityConcentration`).
- **Sequential CUSUM Change Onset Detection**: Rolling-variance statistical process control ($3.5\sigma$) that pinpoints the exact acquisition date of physical change while rejecting seasonal vegetation phenology.
- **Sub-Pixel Orthorectification Jitter Suppression**: Quadratic parabolic peak interpolation and bilinear shifting to eliminate false alarms from $0.1 - 0.8\text{ px}$ registration jitter.
- **Radiometric Normalization**: Pseudo-Invariant Feature (PIF) normalization using Iteratively Reweighted Least Squares (IRLS) with Tukey biweight loss to correct atmospheric and illumination variations.
- **Spatial-Semantic Unsupervised Site Discovery**: $O(N \log N)$ Spatial Hash Grid DBSCAN clustering combining embedding cosine distance and geospatial Euclidean distance.
- **WGS84 Ellipsoidal Surface Area Calculations**: True ground surface area computations with latitude-dependent ellipsoidal scaling ($\cos\phi$ correction).
- **Analyst Review & Active Learning**: Interactive confirmation/rejection triage queues, Rocchio relevance feedback reranking, and tamper-evident W3C PROV-O GeoJSON audit exports.

---

## Documentation Reference Suite

| Document | Focus Area | Description |
| :--- | :--- | :--- |
| **00. Developer Guide** | [documentation/00_developer_guide.md](documentation/00_developer_guide.md) | Source-aligned architecture, stack, workflows, and capability boundaries. |
| **01. System Architecture** | [documentation/01_system_architecture.md](documentation/01_system_architecture.md) | Architecture reference; historical material is marked in the page. |
| **02. Algorithms & Mathematics** | [documentation/02_algorithms_and_mathematics.md](documentation/02_algorithms_and_mathematics.md) | Algorithm reference; current formulas and thresholds are in the implementation reference. |
| **03. Remote Sensing & Sensors** | [documentation/03_remote_sensing_and_sensors.md](documentation/03_remote_sensing_and_sensors.md) | Sensor and spectral-index reference. |
| **04. API Gateway Reference** | [documentation/04_api_reference.md](documentation/04_api_reference.md) | Endpoint reference; verify contracts against the schemas module. |
| **05. Desktop Intelligence Engine** | [documentation/05_desktop_engine_guide.md](documentation/05_desktop_engine_guide.md) | Desktop engine and UI reference. |
| **06. Deployment & Security** | [documentation/06_deployment_and_security.md](documentation/06_deployment_and_security.md) | Deployment reference; actual defaults are in the implementation guide. |
| **07. Implementation Reference** | [documentation/07_implementation_reference.md](documentation/07_implementation_reference.md) | Verified formulas, algorithms, persistence model, and limitations. |
| **08. AI Agent & UI Studio** | [documentation/08_ai_agent_and_ui_studio.md](documentation/08_ai_agent_and_ui_studio.md) | Qwen agent orchestration, automated insight synthesis, and modern dual-theme UI studio. |

---

## System Architecture Blueprint

```mermaid
graph TD
    subgraph Presentation and Client Layer
        UI["Avalonia Desktop Studio / Web Intelligence Portal"]
        MapCanvas["MapLibre GL Offline WebGL Canvas"]
    end

    subgraph Service Tier - Python Backend
        API["FastAPI REST Gateway (:8000)"]
        IngestService["GeoTIFF Ingestion & WGS84 Reprojection"]
        CVAService["Multi-Band Change Vector Analysis (CVA)"]
        Embedder["96-dim Visual Feature Extractor"]
    end

    subgraph High-Throughput Core - .NET C# Engine
        Engine["GeoSemanticSat Core Subsystem"]
        SIMD["AVX2 SIMD Vector Acceleration"]
        CUSUM["Sequential CUSUM Onset Estimator"]
        DBSCAN["Spatial Hash Grid DBSCAN (O(N log N))"]
        JitterFilter["Sub-Pixel Peak Interpolation Filter"]
        TukeyNorm["Tukey Biweight PIF Normalizer"]
    end

    subgraph Persistent Storage and Indexing
        PostGIS[("PostgreSQL 16 + PostGIS 3.4")]
        BinaryIndex[("Binary GSSV / FAISS Vector Store")]
        RasterStore[("Local COG Pyramid Storage")]
    end

    UI --> API
    UI --> Engine
    MapCanvas --> IngestService
    API --> IngestService
    API --> CVAService
    API --> Embedder
    API --> PostGIS
    Engine --> SIMD
    Engine --> CUSUM
    Engine --> DBSCAN
    Engine --> JitterFilter
    Engine --> TukeyNorm
    Engine --> BinaryIndex
    IngestService --> RasterStore
    Engine --> RasterStore
```

---

## Repository Structure

```
UpaGraha/
|-- documentation/                  # Comprehensive system documentation (01-07)
|-- Unified-RSanalytics/            # Unified application workspace
    |-- app/                        # Python FastAPI backend service
    |   |-- api/                    # REST route controllers (search, change, ingestion, clusters)
    |   |-- core/                   # Application config, database engine, logging
    |   |-- models/                 # SQLAlchemy ORM models and Pydantic schemas
    |   |-- services/               # CVA, CUSUM, feature extraction, raster processing
    |   +-- utils/                  # Coordinate transforms, PROV-O generators
    |-- Desktop_App/                # Native .NET 10 desktop application (GeoSemanticSat)
    |   +-- Upgrahan2/
    |       |-- src/
    |       |   |-- GeoSemanticSat.Core/     # Raster I/O, SIMD index, CVA, CUSUM, DBSCAN
    |       |   |-- GeoSemanticSat.Engine/   # Semantic search dual-encoders, ONNX runner
    |       |   |-- GeoSemanticSat.UI/       # Avalonia XAML desktop user interface
    |       |   |-- GeoSemanticSat.Cli/      # Headless command-line CLI tool
    |       |   +-- GeoSemanticSat.Tests/    # xUnit automated verification test suite
    |       +-- doc/                         # Analyst guides and architectural reports
    |-- data/                       # Local raster storage and raw satellite acquisitions
    |-- indexes/                    # FAISS vector indexes and binary GSSV index files
    |-- scripts/                    # Database migrations, test data generators, benchmark scripts
    |-- tests/                      # Python pytest automated test suite
    |-- Dockerfile                  # Hardened non-root air-gapped container image
    |-- docker-compose.yml          # PostGIS and Backend service orchestration
    |-- requirements.txt            # Locked Python production dependencies
    +-- README.md                   # Workspace overview
```

---

## System Capabilities & Performance Matrix

| Capability / Subsystem | Traditional System Baseline | UpaGraha Production Standard | Operational Benefit |
| :--- | :--- | :--- | :--- |
| **Spatial Reference System** | Native UTM meters without reprojection | Automatic WGS84 (EPSG:4326) transformation | Consistent alignment across global GIS viewers and vector layers |
| **Change Detection** | Single-band scalar difference $|I_2 - I_1|$ | Multi-Band Change Vector Analysis (CVA) | Accurate classification: `CLEARANCE`, `CONSTRUCTION`, `WATER` |
| **Onset Date Estimation** | Single-baseline comparison ($T_i - T_0$) | Sequential CUSUM with Rolling Variance ($3.5\sigma$) | Immune to seasonal vegetation greening false alarms |
| **Jitter Suppression** | Integer shift testing ($\pm 1\text{ px}$) | Sub-Pixel Quadratic Peak & Bilinear Interpolation | Eliminates false alarms from $0.1 - 0.8\text{ px}$ registration jitter |
| **Radiometric Calibration** | Ordinary Least Squares (OLS) | Iteratively Reweighted Least Squares (IRLS) with Tukey | Outlier-resistant gain/bias fitting under large ground disturbances |
| **Spatial Clustering** | Brute-force $O(N^2)$ all-pairs DBSCAN | Spatial Hash Grid pre-filtering ($O(N \log N)$) | Sub-second unsupervised cluster discovery across 100k patches |
| **Vector Similarity** | Heap-allocated list cloning on query | Zero-allocation in-place SIMD search (`ReaderWriterLockSlim`) | $< 1.0\text{ms}$ search latency with parallel reader threads |
| **Surface Area Math** | Flat planar Cartesian calculation | WGS84 Ellipsoidal Geodesic Area ($\cos\phi$ corrected) | True physical area in $\text{m}^2$ / hectares across all latitudes |

---

## Core Mathematical & Algorithmic Formulations

```
+-------------------------------------------------------------------------------------------------------------+
|                                     UPAGRAHA MATHEMATICAL FORMULATION SUITE                                 |
+-------------------------------------------------------------------------------------------------------------+
| 1. MULTI-BAND SPECTRAL MAGNITUDE                                                                            |
|    M(x, y) = sqrt( sum_{b=1}^B ( rho_b^(T2)(x, y) - rho_b^(T1)(x, y) )^2 )                                 |
|                                                                                                             |
| 2. SEQUENTIAL CUSUM ONSET DETECTION                                                                         |
|    S_k^+ = max(0, S_{k-1}^+ + (Y_k - mu_0) - 0.5*sigma_0),   Alarm when S_k^+ > 3.5*sigma_0                 |
|                                                                                                             |
| 3. SUB-PIXEL JITTER PARABOLIC PEAK FITTING                                                                  |
|    Delta x_sub = x_max + [ C(y_max, x_max-1) - C(y_max, x_max+1) ] / [ 2*(2*C_center - C_left - C_right) ] |
|                                                                                                             |
| 4. ELLIPSOIDAL GEODESIC SURFACE AREA                                                                        |
|    Area(meters^2) = Width * Height * GSD^2 * cos(Latitude)                                                  |
|                                                                                                             |
| 5. ROCCHIO RELEVANCE FEEDBACK                                                                               |
|    Q_new = alpha * Q_orig + (beta / |D_R|) * sum(d in D_R) - (gamma / |D_NR|) * sum(d in D_NR)              |
+-------------------------------------------------------------------------------------------------------------+
```

---

## Sensor & Spectral Band Directory

| Sensor Platform | Native Bands Configured | Spatial GSD | Primary Strategic Indices |
| :--- | :--- | :---: | :--- |
| **Sentinel-2 MSI** | B2 (Blue), B3 (Green), B4 (Red), B8 (NIR), B11 (SWIR1), B12 (SWIR2) | 10m / 20m | $\text{NDVI}, \text{NDWI}, \text{NDBI}, \text{MNDWI}, \text{BSI}$ |
| **Landsat-8/9 OLI**| B2 (Blue), B3 (Green), B4 (Red), B5 (NIR), B6 (SWIR1), B7 (SWIR2) | 30m | $\text{NDVI}, \text{NDWI}, \text{NDBI}, \text{BSI}, \text{NDSI}$ |
| **PlanetScope** | B1 (Blue), B2 (Green), B3 (Red), B4 (NIR) | 3.0m | High-resolution tactical change detection & visual search |
| **Sentinel-1 SAR** | C-Band (VV, VH Polarization) | 10m | All-weather structural coherence & flood boundary mapping |
| **ISRO Bhuvan / Cartosat** | VNIR (Red, Green, Blue, NIR) | 2.5m - 23.5m | $\text{NDVI}, \text{NDWI}$, Sobel structural edge contrast |

---

## REST API Gateway Reference

| Method | Endpoint Path | Function & Purpose | Key Parameters / Request Body |
| :--- | :--- | :--- | :--- |
| `GET` | `/health` | Service health status and database connectivity | None |
| `POST` | `/api/v1/search/text` | Natural language semantic search across indexed imagery | `{"query": "structures near river", "top_k": 10}` |
| `POST` | `/api/v1/search/image` | Image-to-image similarity search using patch embeddings | `{"patch_id": "...", "top_k": 10, "threshold": 0.75}` |
| `POST` | `/api/v1/search/spatiotemporal` | Multi-criteria spatial bounding box, time window, and sensor query | `{"bbox": [min_lon, min_lat, max_lon, max_lat], "time_range": [...]}` |
| `POST` | `/api/v1/change/detect` | Multi-temporal Change Vector Analysis (CVA) between T1 and T2 | `{"scene_t1_id": "...", "scene_t2_id": "...", "threshold": 0.15}` |
| `POST` | `/api/v1/change/timeline` | Sequential CUSUM onset date estimation for an ROI | `{"roi_geometry": {...}, "time_series": [...]}` |
| `POST` | `/api/v1/clusters/discover` | Unsupervised spatial-semantic clustering (DBSCAN) | `{"eps_spatial_km": 2.5, "min_samples": 3}` |
| `POST` | `/api/v1/ingest/geotiff` | Ingest and tile external GeoTIFF files into COG and index | `{"file_path": "/data/acquisitions/scene.tif", "platform": "Sentinel2"}` |
| `GET` | `/api/v1/audit/prov-o` | Export W3C PROV-O GeoJSON audit trail of analyst decisions | `{"session_id": "...", "format": "geojson"}` |

---

## Desktop Intelligence Studio (GeoSemanticSat)

The native .NET 10 desktop application provides 5 operational stations designed for tactical and intelligence analysts:

1. **Natural Language Search**: Free-text semantic discovery across imagery archives without prior coordinates.
2. **Target Coordinate & Change Search**: Radial proximity and bounding box queries with side-by-side Before ($T_1$) and After ($T_2$) visual comparison.
3. **Multi-Temporal Change & Heatmaps**: Full-tile pixel-level change detection with automated false-alarm suppression and spectral telemetry cards ($\Delta\text{NDBI}, \Delta\text{NDVI}, \Delta\text{NDWI}, \Delta\text{Sobel}$).
4. **Grouped Sites & Facilities**: Unsupervised spatial-semantic clustering grouping related construction and clearance patches into facility complexes.
5. **Verification Queue & Audit Trail**: Active learning relevance feedback and tamper-evident W3C PROV-O GeoJSON audit reporting.

---

## Quickstart & Air-Gapped Setup

### 1. Environment Configuration
```powershell
# Copy environment configuration template
Copy-Item Unified-RSanalytics/.env.example Unified-RSanalytics/.env
```

### 2. Database & Vector Index Initialization
```powershell
# Initialize database schema and PostGIS spatial extensions
python -m scripts.init_db

# Generate synthetic multi-spectral test rasters
python -m scripts.create_sample_data

# Build local FAISS cosine similarity index
python -m scripts.build_index
```

### 3. Launching Python Analytics Service via Docker Compose
```powershell
docker-compose up -d
```
Verify health status at `http://127.0.0.1:8000/health`.

### 4. Running the Desktop Application (.NET 10)
```powershell
cd Unified-RSanalytics/Desktop_App/Upgrahan2
dotnet run --project src/GeoSemanticSat.UI
```

### 5. Running Automated Verification Tests
```powershell
# Run Python backend tests
pytest -v

# Run .NET engine tests
dotnet test Unified-RSanalytics/Desktop_App/Upgrahan2/src/GeoSemanticSat.Tests
```

---

## Security, Governance & Air-Gapped Hardening

- **Unprivileged Execution:** Backend container executes under dedicated system user `appuser (UID 1000)`.
- **Path Traversal Protection:** All GeoTIFF path inputs are validated via strict `Path.is_relative_to(DATA_ROOT)` containment checks.
- **Persistent Storage:** PostGIS database volume (`postgis_data`) is attached directly to container storage.
- **Zero Telemetry:** All third-party telemetry, automatic updates, and remote CDN fetches are disabled.
- **Cryptographic Provenance:** Every analysis step records SHA-256 raster checksums and analyst IDs conforming to the W3C PROV-O standard.

---

*UpaGraha Enterprise Geospatial Intelligence Suite. Designed for mission-critical offline operations.*
#   G e o S e m a n t i c S a t - V 2  
 #   G e o S e m a n t i c S a t - V 2  
 #   U n i f i e d - R S a n a l y t i c s - V 2  
 