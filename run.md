# UpaGraha / GeoSemanticSat — Operational Setup & Run Guide

Comprehensive guide to set up, configure, test, and run the **UpaGraha / GeoSemanticSat** air-gapped satellite analytics platform, covering the **FastAPI Analytics Backend**, **Pretrained Geospatial Foundation Models**, and the **Avalonia Desktop Intelligence Studio**.

---

## Table of Contents
1. [Prerequisites & System Requirements](#1-prerequisites--system-requirements)
2. [Quickstart (3 Steps)](#2-quickstart-3-steps)
3. [Python Analytics Backend Setup](#3-python-analytics-backend-setup)
   - [Option A: Conda Environment (Recommended)](#option-a-conda-environment-recommended)
   - [Option B: Virtual Environment (`venv`)](#option-b-virtual-environment-venv)
   - [Option C: Docker Container Deployment](#option-c-docker-container-deployment)
4. [Pretrained Geospatial Foundation Models Setup](#4-pretrained-geospatial-foundation-models-setup)
5. [Database Initialization & Synthetic Data Generation](#5-database-initialization--synthetic-data-generation)
6. [Running the Backend Service](#6-running-the-backend-service)
7. [API Verification & Interactive Documentation](#7-api-verification--interactive-documentation)
8. [Desktop Application Setup & Execution](#8-desktop-application-setup--execution)
   - [Option A: Running Pre-Compiled Standalone Release (No SDK Required)](#option-a-running-pre-compiled-standalone-release-no-sdk-required)
   - [Option B: Running from Source (.NET 10 SDK)](#option-b-running-from-source-net-10-sdk)
9. [Running Test Suites](#9-running-test-suites)
10. [Offline Map Basemap & Tile Caching](#10-offline-map-basemap--tile-caching)
11. [Troubleshooting & FAQ](#11-troubleshooting--faq)

---

## 1. Prerequisites & System Requirements

- **Operating System:** Windows 10/11 x64, Linux (Ubuntu 22.04+), or macOS
- **Python:** Python 3.11.x (recommended) or 3.12.x
- **.NET SDK:** .NET 10 SDK (for building the desktop app from source) or Windows x64 runtime (for standalone binary)
- **Docker Desktop (Optional):** Required only if running backend in containerized mode

---

## 2. Quickstart (3 Steps)

From the project root:

```powershell
# 1. Activate your Conda / Python environment
conda activate ps227_sih2026

# 2. Initialize database and stage foundation models
python scripts/init_db.py
python scripts/create_sample_data.py
python scripts/stage_foundation_models.py
python scripts/export_models_to_onnx.py
python scripts/build_index.py

# 3. Start the FastAPI analytics backend
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload
```

Then launch the Desktop Studio:
```powershell
dotnet run --project "Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\GeoSemanticSat.UI.csproj"
```

---

## 3. Python Analytics Backend Setup

### Option A: Conda Environment (Recommended)

```powershell
# 1. Create a dedicated Conda environment with Python 3.11
conda create -n ps227_sih2026 python=3.11 -y

# 2. Activate the environment
conda activate ps227_sih2026

# 3. Install dependencies
pip install -r requirements.txt
```

---

### Option B: Virtual Environment (`venv`)

```powershell
# 1. Create a virtual environment
python -m venv .venv

# 2. Activate the virtual environment
# Windows PowerShell:
.\.venv\Scripts\Activate.ps1
# Linux / macOS:
# source .venv/bin/activate

# 3. Install dependencies
python -m pip install --upgrade pip
pip install -r requirements.txt
```

---

### Option C: Docker Container Deployment

```powershell
# 1. Build and start services in detached mode
docker compose up -d --build

# 2. Initialize database and sample rasters inside container
docker compose exec api python scripts/init_db.py
docker compose exec api python scripts/create_sample_data.py
docker compose exec api python scripts/stage_foundation_models.py

# 3. View container logs
docker compose logs -f
```

---

## 4. Pretrained Geospatial Foundation Models Setup

The platform supports 4 state-of-the-art Earth Observation foundation models:

1. **[TerraMind-1.0-base](https://huggingface.co/ibm-esa-geospatial/TerraMind-1.0-base)** (IBM / ESA Any-to-Any Multimodal foundation model)
2. **[SatMAE++ Transformers](https://huggingface.co/BiliSakura/SATMAE-PP-transformers)** (Grouped multi-spectral Vision Transformer Masked Autoencoder)
3. **[GFM Composition Pretraining](https://github.com/05kashyap/GFM_Composition_Pretraining)** (Multi-sensor Sentinel-1 SAR + Sentinel-2 Optical composition)
4. **[Prithvi-EO-2.0-600M-TL](https://huggingface.co/ibm-nasa-geospatial/Prithvi-EO-2.0-600M-TL)** (IBM / NASA Geospatial 600M parameter spatio-temporal sequence model)

### Stage and Export Models

```powershell
# 1. Generate local TorchScript / ONNX weights for offline execution
python scripts/export_models_to_onnx.py

# 2. Verify model staging and inspect manifest
python scripts/stage_foundation_models.py
```

### Switching Active Foundation Model

In your `.env` file or environment variables:
```env
# Available: terramind | satmae_pp | gfm_composition | prithvi | baseline
EO_MODEL_NAME=terramind
```

---

## 5. Database Initialization & Synthetic Data Generation

Initialize the SQLite database schema and generate local test GeoTIFF rasters (Sentinel-2 multi-band and Sentinel-1 SAR):

```powershell
# Initialize SQLite database schema
python scripts/init_db.py

# Generate sample synthetic satellite rasters under data/
python scripts/create_sample_data.py

# Build local FAISS cosine similarity index from stored embeddings
python scripts/build_index.py
```

---

## 6. Running the Backend Service

Start the FastAPI application with auto-reload:

```powershell
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload
```

---

## 7. API Verification & Interactive Documentation

Once running, verify service availability at:

| Endpoint | Method | URL | Description |
| :--- | :---: | :--- | :--- |
| **Root Navigation** | `GET` | [http://127.0.0.1:8000/](http://127.0.0.1:8000/) | Service metadata and navigation |
| **Interactive Docs (Swagger)** | `GET` | [http://127.0.0.1:8000/docs](http://127.0.0.1:8000/docs) | Interactive API exploration and test execution |
| **ReDoc Specification** | `GET` | [http://127.0.0.1:8000/redoc](http://127.0.0.1:8000/redoc) | Clean API specification |
| **Health Check** | `GET` | [http://127.0.0.1:8000/health](http://127.0.0.1:8000/health) | System health & offline mode verification |
| **System Status** | `GET` | [http://127.0.0.1:8000/system/status](http://127.0.0.1:8000/system/status) | DB state, observation count, and active foundation models |
| **Semantic Text Search** | `POST` | `http://127.0.0.1:8000/api/v1/search/text` | Natural language text-to-satellite query (TerraMind) |
| **Visual Search** | `POST` | `http://127.0.0.1:8000/api/v1/search/image` | Search visually similar satellite observations |
| **Change Analysis** | `POST` | `http://127.0.0.1:8000/api/v1/change/analyze` | Bi-temporal change analysis + Prithvi temporal sequence |

### Quick Smoke Test (PowerShell)

```powershell
# Health check test
Invoke-RestMethod -Uri "http://127.0.0.1:8000/health" -Method Get

# System status test
Invoke-RestMethod -Uri "http://127.0.0.1:8000/system/status" -Method Get
```

---

## 8. Desktop Application Setup & Execution

> **Note on Architecture:** The desktop UI performs change detection, CUSUM onset, and SIMD vector search **in-process** via `GeoSemanticSat.Core` and does not require the FastAPI backend to be running.

### Option A: Running Pre-Compiled Standalone Release (No SDK Required)

Launch the pre-compiled binary:

```powershell
Start-Process `
  -FilePath "$PWD\Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\bin\Release\net10.0\win-x64\GeoSemanticSat.UI.exe" `
  -WorkingDirectory "$PWD\Desktop_App\Upgrahan2\src\GeoSemanticSat.UI\bin\Release\net10.0\win-x64"
```

---

### Option B: Running from Source (.NET 10 SDK)

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

## 9. Running Test Suites

### Backend Python & Foundation Model Test Suite

```powershell
# Run all automated tests (API integration, raster math, and 4 foundation models)
conda run -n ps227_sih2026 pytest -v
```

*Expected output: `13 passed, 2 warnings`*

### Desktop C# Engine Test Suite

```powershell
# Run change detection, vector indexing, and algorithm correctness tests
dotnet test "Desktop_App\Upgrahan2\src\GeoSemanticSat.Tests\GeoSemanticSat.Tests.csproj"
```

---

## 10. Offline Map Basemap & Tile Caching

The Desktop Map Engine uses a four-tier hybrid tile caching architecture (`L1 Memory` -> `L2 Disk` -> `L3 Online Fetch` -> `L4 Procedural Graticule`):

- **Default Cache Directory:** `%LocalAppData%\GeoSemanticSat\MapTileCache\`
- **Operational Modes:**
  - `Auto`: Checks local disk cache first; fetches missing tiles from open-source basemaps if online and caches them locally.
  - `OfflineStrict`: 100% Air-Gapped mode. Never attempts network connections; renders strictly from disk or tactical procedural grid.
  - `OnlinePreferred`: Checks for updated basemap tiles before falling back to disk cache.
- **Supported Basemaps:** CartoDB Dark Matter, OpenStreetMap, ESRI Satellite, Sentinel-2 Cloudless (EOX 10m), USGS National Map.

---

## 11. Troubleshooting & FAQ

### Q: How do I verify offline mode is active?
**A:** Query `GET /health`. The response `{"status":"ok","offline_mode":true}` confirms 100% sovereign air-gapped readiness.

### Q: What if `torch` or `onnx` is not installed?
**A:** All foundation model adapters (`TerraMindEmbedder`, `SatMaePPEmbedder`, `GFMCompositionEmbedder`, `PrithviTemporalEmbedder`) include automatic offline fallback to deterministic multi-spectral semantic projection engines with zero crash risk.

### Q: Why do we never commit `bin/` or `obj/`?
**A:** `bin/` and `obj/` directories are local build outputs. Always check `git status` before committing to keep the repository clean.
