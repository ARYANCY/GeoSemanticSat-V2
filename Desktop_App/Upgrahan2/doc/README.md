# GeoSemanticSat Documentation Repository

This directory contains the complete technical documentation, user guides, GIS primer, architectural specifications, and implementation reports generated for the **GeoSemanticSat** platform (Semantic Retrieval and Multi-Temporal Change Analysis of Satellite Imagery).

---

## Document Index & Reading Guide

| Document | Purpose & Audience | Key Topics Covered |
|:---|:---|:---|
| **[analyst_user_guide.md](analyst_user_guide.md)** | **End-User / Imagery Analyst** operational handbook | • How to launch and operate the Desktop UI<br>• Natural language search & prompt phrasing<br>• Spatiotemporal coordinate queries<br>• Multi-temporal change detection & false-alarm suppression<br>• How to plug in external GeoTIFF datasets |
| **[gis_and_remote_sensing_primer.md](gis_and_remote_sensing_primer.md)** | **GIS & Remote Sensing Primer** for engineers and analysts | • Coordinate Reference Systems (WGS 84, UTM, EPSG codes)<br>• Multi-spectral electromagnetic bands (RGB, NIR, SWIR, TIR)<br>• Mathematical indices ($\text{NDVI}, \text{NDWI}, \text{NDBI}, \text{BSI}$)<br>• SAR radar polarimetry & C-band backscatter<br>• Sensor platform characteristics (Sentinel-2, Sentinel-1, Landsat 8/9, ISRO Bhuvan) |
| **[technical_architecture_and_correctness_report.md](technical_architecture_and_correctness_report.md)** | **Deep Architectural & Verification Report** | • Mathematical proof and justification for false-alarm suppression (RRN, Registration Jitter filter, Quality cloud masking)<br>• Vector indexing and cosine similarity bounds<br>• DBSCAN unsupervised facility discovery<br>• W3C PROV-O geospatial audit trail schema<br>• Reproducibility and air-gapped on-premises design |
| **[walkthrough.md](walkthrough.md)** | **Feature Walkthrough & Changelog** | • 8-Mode multi-spectral and electromagnetic visualization suite<br>• Interactive focused site inspector with reticle & telemetry shifts<br>• End-to-end guided analyst workflow ribbon<br>• Matte black military-grade user experience |
| **[ARCHITECTURE.md](ARCHITECTURE.md)** | **Software Architecture Reference** | • Solution structure (`GeoSemanticSat.Core`, `.Engine`, `.UI`, `.Cli`, `.Tests`)<br>• Core domain models, clean architecture boundaries, and execution pipeline |
| **[implementation_plan.md](implementation_plan.md)** | **Engineering Implementation Record** | • Initial technical requirements mapping<br>• Problem statement ID 26227 specification traceability |

---

## Quick Reference: Running the Solution

### 1. Build and Test
```bash
# Build the complete solution
dotnet build GeoSemanticSat.slnx

# Run all 17 automated regression & correctness tests
dotnet test src/GeoSemanticSat.Tests/GeoSemanticSat.Tests.csproj
```

### 2. Run the Cross-Platform Desktop UI
```bash
dotnet run --project src/GeoSemanticSat.UI
```

### 3. Run Automated CLI Benchmarks
```bash
dotnet run --project src/GeoSemanticSat.Cli -- benchmark ./benchmark_output
```
