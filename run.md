# GeoSemanticSat — Comprehensive Operational Run & Setup Guide

This guide provides end-to-end instructions to set up, configure, test, and run the **GeoSemanticSat** satellite intelligence platform, including the **FastAPI Offline Analytics Backend** and the **Desktop Analyst Workflow Studio**.

---

## Table of Contents
1. [Prerequisites & System Requirements](#1-prerequisites--system-requirements)
2. [Python Analytics Backend Setup](#2-python-analytics-backend-setup)
   - [Option A: Conda Environment (Recommended)](#option-a-conda-environment-recommended)
   - [Option B: Python Virtual Environment (`venv`)](#option-b-python-virtual-environment-venv)
   - [Option C: Docker Container Deployment](#option-c-docker-container-deployment)
3. [Sample Data Generation & Database Initialization](#3-sample-data-generation--database-initialization)
4. [Running the Backend Service](#4-running-the-backend-service)
5. [API Endpoints & Verification](#5-api-endpoints--verification)
6. [Desktop Application Setup & Execution](#6-desktop-application-setup--execution)
   - [Option A: Running Pre-Compiled Standalone Release (No SDK Required)](#option-a-running-pre-compiled-standalone-release-no-sdk-required)
   - [Option B: Building and Running from Source (.NET 10 SDK)](#option-b-building-and-running-from-source-net-10-sdk)
7. [Running Test Suites](#7-running-test-suites)
8. [Offline Map Basemap & Tile Caching](#8-offline-map-basemap--tile-caching)
9. [Troubleshooting & Common Questions](#9-troubleshooting--common-questions)

---

## 1. Prerequisites & System Requirements

- **Operating System:** Windows 10/11 x64, Linux (Ubuntu 22.04+), or macOS
- **Python:** Python 3.11.x (recommended) or 3.12.x
- **Desktop UI Runtime:** Windows 10/11 x64 (DirectX/Avalonia GPU acceleration supported)
- **C++ Build Tools / GDAL & PROJ:** Pre-bundled via standard wheels (`rasterio`, `shapely`)

---

## 2. Python Analytics Backend Setup

### Option A: Conda Environment (Recommended)

From the project root directory:

```powershell
# 1. Create dedicated Conda environment with Python 3.11
conda create -n geosemanticsat python=3.11 -y

# 2. Activate the environment
conda activate geosemanticsat

# 3. Install all locked dependencies
pip install -r requirements.txt
```

---

### Option B: Python Virtual Environment (`venv`)

```powershell
# 1. Create a virtual environment
python -m venv .venv

# 2. Activate the virtual environment
# On Windows PowerShell:
.\.venv\Scripts\Activate.ps1
# On Linux/macOS:
# source .venv/bin/activate

# 3. Upgrade pip and install dependencies
python -m pip install --upgrade pip
pip install -r requirements.txt
```

---

### Option C: Docker Container Deployment

```powershell
# Build and launch all backend services in detached mode
docker compose up -d --build

# View container logs
docker compose logs -f
```

---

## 3. Sample Data Generation & Database Initialization

Initialize the SQLite database schema and generate local test GeoTIFF rasters (3-band RGB and 1-band SAR/NDVI):

```powershell
# Initialize database tables
python scripts/init_db.py

# Generate sample synthetic satellite rasters under data/
python scripts/create_sample_data.py
```

---

## 4. Running the Backend Service

Execute the FastAPI uvicorn application:

```powershell
# Windows PowerShell (with Rasterio PROJ/GDAL bindings configured)
$env:PROJ_DATA = "$PWD\.venv\Lib\site-packages\rasterio\proj_data"
$env:GDAL_DATA = "$PWD\.venv\Lib\site-packages\rasterio\gdal_data"

python -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload
```

---

## 5. API Endpoints & Verification

Once the backend is running, verify service availability:

| Endpoint | Method | URL | Description |
| :--- | :---: | :--- | :--- |
| **API Root** | `GET` | [http://127.0.0.1:8000/](http://127.0.0.1:8000/) | Service metadata and navigation overview |
| **Swagger UI** | `GET` | [http://127.0.0.1:8000/docs](http://127.0.0.1:8000/docs) | Interactive API documentation & test runner |
| **ReDoc UI** | `GET` | [http://127.0.0.1:8000/redoc](http://127.0.0.1:8000/redoc) | Clean API reference specification |
| **Health Check** | `GET` | [http://127.0.0.1:8000/health](http://127.0.0.1:8000/health) | System health and offline status flag |
| **System Status**| `GET` | [http://127.0.0.1:8000/system/status](http://127.0.0.1:8000/system/status) | DB connection state, observation and vector counts |
| **Ingest GeoTIFF**| `POST` | `http://127.0.0.1:8000/api/v1/ingest` | Ingest new multi-band raster and index embeddings |
| **Search Vectors**| `POST` | `http://127.0.0.1:8000/api/v1/search` | Execute cosine similarity search across vectors |
| **Change Analysis**| `POST` | `http://127.0.0.1:8000/api/v1/change` | Compute bi-temporal change detection & spectral physics |

### Quick Smoke Test via PowerShell

```powershell
# Health check test
Invoke-RestMethod -Uri "http://127.0.0.1:8000/health" -Method Get

# System status test
Invoke-RestMethod -Uri "http://127.0.0.1:8000/system/status" -Method Get
```

---

## 6. Desktop Application Setup & Execution

### Option A: Running Pre-Compiled Standalone Release (No SDK Required)

The repository includes a pre-compiled, self-contained Windows x64 binary. Launch it directly in your active desktop session:

```powershell
Start-Process `
  -FilePath "$PWD\Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\bin\Release\net10.0\win-x64\GeoSemanticSat.UI.exe" `
  -WorkingDirectory "$PWD\Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\bin\Release\net10.0\win-x64"
```

---

### Option B: Building and Running from Source (.NET 10 SDK)

When the [.NET 10 SDK](https://dotnet.microsoft.com/download) is installed:

```powershell
# Run the Desktop Application directly
dotnet run --project "Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\GeoSemanticSat.UI.csproj"

# Build a self-contained release binary
dotnet publish "Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\GeoSemanticSat.UI.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true
```

---

## 7. Running Test Suites

### Backend Python Test Suite

```powershell
# Run all automated API and raster processing unit tests
pytest -v
```

*Expected output: `7 passed, 2 warnings`*

### Desktop C# / .NET Test Suite

```powershell
# Run hybrid tile math, caching, and slippy map projection tests
dotnet test "Desktop_App\Upgrahan2\src\GeoSemanticSat.Tests\GeoSemanticSat.Tests.csproj"
```

---

## 8. Offline Map Basemap & Tile Caching

The Desktop Map Engine uses a multi-tier caching architecture (`L1 Memory` -> `L2 Disk` -> `L3 Online Fetch` -> `L4 Procedural Graticule`):

- **Default Cache Directory:** `%LocalAppData%\GeoSemanticSat\MapTileCache\`
- **Operational Modes:**
  - `Auto`: Checks local disk cache first; fetches missing tiles from open-source basemaps if online and caches them locally.
  - `OfflineStrict`: 100% Air-Gapped mode. Never attempts network connections; strictly renders from disk or tactical fallback grid.
  - `OnlinePreferred`: Checks for updated basemap tiles before falling back to disk cache.
- **Supported Basemaps:**
  - CartoDB Dark Matter (Tactical)
  - OpenStreetMap Standard
  - ESRI World Imagery (Satellite)
  - Sentinel-2 Cloudless (EOX 10m)
  - USGS The National Map

---

## 9. Troubleshooting & Common Questions

### Q: Why did visiting `http://127.0.0.1:8000/` return `{"detail":"Not Found"}`?
**A:** FastAPI serves API documentation at `/docs` or `/redoc`. A root route (`GET /`) is now added to provide an overview and immediate links to documentation and health endpoints.

### Q: How do I verify the backend is running while in air-gapped mode?
**A:** Query `GET /health`. The response `{"status":"ok","offline_mode":true}` confirms sovereign air-gapped readiness.

### Q: How do I pre-cache an Area of Interest (AOI) for field missions?
**A:** Use the desktop studio's Area of Interest pre-cache function or invoke `HybridTileService.PrecacheRegionAsync(minLat, minLon, maxLat, maxLon, minZoom, maxZoom)` before deploying to an isolated network.
