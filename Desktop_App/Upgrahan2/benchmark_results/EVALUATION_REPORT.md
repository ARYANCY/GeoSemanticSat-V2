# Reproducible Evaluation Report - Problem Statement 26227
**Client**: Ministry of Defence (MoD) / Indian Army (DGIS)  
**System**: GeoSemanticSat (.NET 10.0 Native Architecture)  
**Evaluation Date**: 2026-09-06 08:24:34 UTC  
**Environment**: Linux x86_64, .NET 10.0, 100% On-Premises Offline Operation  

## 1. Metric Summary
| Metric | Value | Reference Target |
|---|---|---|
| **Indexed Scenes / Patches** | 128 patches (128 total) | Scale test |
| **Initial Index Build Time** | 4 ms | < 500 ms |
| **Incremental Ingestion Time** | 3 ms (0.05 ms/patch) | Sub-second per tile |
| **Storage Footprint** | 77.48 KB (619.9 bytes/patch) | Ultra-lightweight binary |
| **Query Latency (P50)** | 33.6 µs (0.0034 ms) | < 10 ms |
| **Query Latency (P95)** | 49.8 µs (0.0050 ms) | < 25 ms |
| **Change Detection Precision** | 52.4% | Precision-first (> 85%) |
| **Change Detection Recall** | 68.8% | High analytical discovery |
| **Change Detection F1-Score** | 0.59 | Balanced precision-recall |
| **Earliest Observation Onset** | Exact match (2024-03-20) | Accurate CUSUM detection |
| **False-Alarm Suppression** | 100% cloud/shadow, seasonal & jitter rejection | High precision |

## 2. Supported Sensor Sources
- **Copernicus Sentinel-2 Optical** (RGB, NIR, SWIR1, SWIR2, 10m GSD)
- **Copernicus Sentinel-1 SAR** (VV/VH Polarization ratio, all-weather)
- **USGS Landsat Collection 2** (Multispectral 30m GSD)
- **NRSC/ISRO Bhuvan** (LISS-III / AWiFS open Earth-observation data)

## 3. Compliance with Sovereign Operational Constraints
- **Zero Cloud / External Network Dependence**: Operates 100% offline with staged weights and packages.
- **Georeferencing & Spatial Provenance**: Fully preserves EPSG:4326 / UTM coordinates and exports W3C PROV-O GeoJSON.
- **Active Learning**: Analyst confirmations and rejections dynamically rerank the discovery queue using Rocchio feedback.
