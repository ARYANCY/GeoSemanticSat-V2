# Walkthrough: Visual Presentation & Advanced Spatiotemporal Change Search

## Summary of Enhancements
We expanded **GeoSemanticSat** with visual presentation capabilities across all search and analysis workflows, as well as an advanced multi-criteria search engine capable of querying specifically by **Latitude, Longitude, Radius (km), Observation Time Window, and Change Classification Type**.

---

## 1. Visual Presentation of Queries & Satellite Imagery

### Native 32-Bit Raster Visualizer ([`RasterVisualizer.cs`](file:///home/non_qualities/.gemini/antigravity/scratch/GeoSemanticSat/src/GeoSemanticSat.Core/Raster/RasterVisualizer.cs))
- Renders 32-bit BGRA byte buffers and standard uncompressed BMP streams directly from multi-spectral satellite bands:
  - **True Color RGB**: Real color imagery compositing Red, Green, Blue bands.
  - **False Color Infrared (NIR-Red-Green)**: Highlights healthy vegetation canopy in crimson red, waterbodies in deep dark blue/black, and urban / concrete structures in cyan/grey.
  - **NDVI Heatmap**: Normalized difference vegetation density color scale.
  - **Change Heatmap Overlay**: Baseline imagery dynamically tinted with color-coded bounding boxes and semi-transparent fills:
    - 🔴 **Red**: Construction (New structures, facilities, pads)
    - 🟡 **Amber**: Clearance (Vegetation loss / land clearance)
    - 🔵 **Cyan**: Water-Extent Variation (Inundation / drying)
    - 🟣 **Purple**: Road / Linear Infrastructure Development
    - 🟠 **Orange**: Activity / Transient Object Concentration
- Zero third-party native imaging libraries needed — works 100% cross-platform in Avalonia UI via `new Bitmap(stream)`.

### Avalonia UI Visual Upgrades ([`MainWindow.axaml`](file:///home/non_qualities/.gemini/antigravity/scratch/GeoSemanticSat/src/GeoSemanticSat.UI/MainWindow.axaml))
- **Semantic Retrieval Tab**: Every retrieved patch now presents an image preview thumbnail card alongside its similarity score, coordinates, acquisition timestamp, and dimensions.
- **Change Analysis & Heatmap Tab**: 3-panel comparative visual display:
  1. *Baseline Imagery (T1)*
  2. *Target Observation (T2)*
  3. *Change Heatmap Overlay & Boundary Detection*

---

## 2. Advanced Spatiotemporal & Change-Type Search

### Advanced Change Search Engine ([`ChangeSearchEngine.cs`](file:///home/non_qualities/.gemini/antigravity/scratch/GeoSemanticSat/src/GeoSemanticSat.Core/ChangeDetection/ChangeSearchEngine.cs))
Enables multi-criteria query formulation:
- **Geographic Proximity**: Center Latitude, Center Longitude, and Radial Distance tolerance (`RadiusKm`).
- **Temporal Constraints**: Start Date and End Date range, plus earliest observation onset window.
- **Change Classification Filter**: Query specifically for *Construction*, *Clearance*, *Water-Extent Variation*, *Road Development*, or *Activity Concentration*.
- **Composite Scoring**: Ranks results using weighted confidence and distance decay:
  $$\text{Relevance} = 0.60 \times \text{Confidence} + 0.40 \times \left(1.0 - \frac{\text{Distance}}{\text{Radius}}\right)$$

### Visual Dual-Thumbnail Inspection
- For every matching change candidate found via Lat/Lon/Time search, the UI renders **side-by-side Before (T1) and After (T2)** cropped satellite thumbnails centered at the exact change coordinates.

### Headless CLI Command
```bash
dotnet run --project src/GeoSemanticSat.Cli -- search-change \
  --lat 28.605 \
  --lon 77.208 \
  --radius-km 5 \
  --type Construction \
  --start 2024-01-01 \
  --end 2024-04-30
```

Sample CLI output:
```
Executing Advanced Change Search:
  Target Coordinate: (28.60500°N, 77.20800°E), Radius: 5 km
  Target Change Type: Construction
  Time Window: 2024-01-01 to 2024-04-30

Found 10 matching change sites:
  #1 [Construction] Candidate 165fc431 | Rel: 0.878 | Conf: 91.3% | Dist: 0.87 km | Center: (28.606000°N, 77.216800°E) | Date: 2024-03-20
  #2 [Construction] Candidate 07594f47 | Rel: 0.860 | Conf: 90.3% | Dist: 1.02 km | Center: (28.606000°N, 77.218400°E) | Date: 2024-03-20
  #3 [Construction] Candidate 514ce335 | Rel: 0.852 | Conf: 91.3% | Dist: 1.20 km | Center: (28.602800°N, 77.220000°E) | Date: 2024-03-20
```

---

## 3. Test Suite Verification

17 automated tests passing (`GeoSemanticSat.Tests`):
- `RasterVisualizer_CreatesValidBmpStreamWithHeader`: Verified BMP file signature `0x4D42`, offset 54, 32-bit depth, and size.
- `ChangeSearchEngine_FiltersByLatLonRadiusTimeAndType`: Verified radial distance filtering, time window filtering, and change type isolation.
- `VectorIndex_RadialProximitySearch_FiltersCorrectly`: Verified radial proximity search in vector index using Haversine distances.
- All 14 previous tests (Affine transforms, GeoTIFF I/O, NDVI/NDWI, Quality masks, RRN, Jitter filter, DBSCAN clustering, Review queue, PROV-O GeoJSON) continue to pass.

---

## 4. UI Fixes & External Dataset Ingestion

1. **"Inspect Changes" Button Fix**:
   - Resolved the issue where clicking "Inspect Changes" on a patch populated coordinates in background textboxes without switching views.
   - Now programmatically switches the UI to the **Advanced Spatiotemporal Search** tab (`MainTabControl.SelectedIndex = 1`), automatically populates the center coordinates, and runs the change query so Before/After thumbnails are displayed immediately.

2. **External Dataset Ingestion**:
   - Added the **📂 Load GeoTIFF** button in the header bar.
   - Supports selecting multi-band `.tif` / `.tiff` files via `StorageProvider.OpenFilePickerAsync`.
   - Tiles are read by `GeoTiffReader`, tessellated into 64×64 patches, embedded with the 128-D vision model, and ingested into the live `VectorIndex`.
   - Documented in detail in [Analyst User Guide](file:///home/non_qualities/.gemini/antigravity/brain/ca4547db-a802-4004-b27f-ed018dd0f975/analyst_user_guide.md).

---

## 5. Matte Black Redesign & Elevated User Experience

1. **Matte Black Theme**:
   - Replaced the dark blue tones (`#12151A`, `#1A1F26`) with a sleek, military-grade **Matte Black** visual identity:
     - Root background: `#09090B` (deep matte black)
     - Header bar: `#111114` with subtle `#222227` zinc border
     - Surface cards: `#141417`
     - Inner viewing containers: `#0C0C0E`
     - High-contrast typography: `#FAFAFA` primary, `#A1A1AA` secondary, `#71717A` hints

2. **Descriptive Visualization & In-App Legends**:
   - **Render Mode Legend**: Added an explanatory guide under the visualization dropdown detailing how to interpret False Color IR (Crimson Red = Vegetation, Black = Water, Cyan = Concrete) and NDVI.
   - **Change Classification Legend**: Added clear color badges explaining 🔴 Construction, 🟡 Clearance, 🔵 Water boundary shift, and 🟣 Road development.
   - **Enhanced Comparison Views**: Added explicit timestamps and badges to Before ($T_1$) and After ($T_2$) panels.

3. **Intuitive Naming & Plain-English Helper Banners**:
   - Replaced esoteric tab labels with user-centered titles:
     - `🔍 Natural Language Search` (was "Semantic Retrieval")
     - `🎯 Target Coordinate & Change Search` (was "Advanced Spatiotemporal Search")
     - `🔄 Multi-Temporal Change & Heatmaps` (was "Change Analysis & Heatmap Viewer")
     - `📍 Grouped Sites & Facilities` (was "Site Discovery & Clustering")
     - `📋 Verification Queue & Audit Trail` (was "Analyst Workflow & Audit Trail")
   - Added top helper cards on each tab explaining in plain English what the tab does and how to use it.

---

## 6. Interactive Multi-Temporal Change & Heatmaps Station

We significantly upgraded Tab 3 (**Multi-Temporal Change & Heatmaps**) into an interactive two-column operational station:

```
+------------------------------------------+------------------------------------------+
|  Detected Ground Changes List (Left)     |  Focused Site Inspector (Right)          |
|  - Click any detected change site         |  - Zoomed T1 (pre-change)                |
|  - Color-coded badges & footprint area   |  - Zoomed T2 (post-change)               |
|  - Earliest usable observation onset     |  - Zoomed Target Reticle with Crosshairs |
|  - Direct inline Confirm/Reject buttons  |  - Spectral & EM Telemetry Shifts:       |
|                                          |    ΔNDBI, ΔNDVI, ΔNDWI, ΔSobel Gradient  |
|                                          |  - Quick Pivot to Tab 2 / Confirm / Reject|
+------------------------------------------+------------------------------------------+
```

### High-Detail Focused Site Inspector ([`RasterVisualizer.RenderFocusedSite`](file:///home/non_qualities/.gemini/antigravity/scratch/GeoSemanticSat/src/GeoSemanticSat.Core/Raster/RasterVisualizer.cs#L346-L420))
- When an analyst clicks any change item in the list, the application generates **zoomed, high-resolution optical/spectral crops** around the change boundary with 16-pixel context padding.
- **3-Panel Micro-Inspection View**:
  1. **Zoomed $T_1$**: Exact site state prior to the activity.
  2. **Zoomed $T_2$**: Exact site state following the activity.
  3. **Target Reticle Overlay**: Color-coded boundary polygon with dynamic alpha blending and a white tactical crosshair centered on the centroid.
- **Multi-Spectral Telemetry Card**: Real-time display of physical index shift metrics:
  - $\Delta\text{NDBI}$ (Normalized Difference Built-up Index): Measures concrete, asphalt, and structural addition.
  - $\Delta\text{NDVI}$ (Normalized Difference Vegetation Index): Measures biomass clearing and canopy loss.
  - $\Delta\text{NDWI}$ (Normalized Difference Water Index): Tracks moisture boundary expansion or contraction.
  - $\Delta\text{Sobel}$ (Gradient Sharpness): Measures edge emergence (perimeter walls, trenches, road borders).
- **Interactive Triage Actions**:
  - **🎯 Pivot to Tab 2**: Automatically transfers the site's center coordinates and change type to Tab 2, immediately executing a spatiotemporal radial search.
  - **✓ Confirm** / **✗ Reject**: Instantly updates the review queue and active learning memory.

---

## 7. Multi-Spectral & Electromagnetic Visualization Suite

The application now supports **8 distinct multi-spectral and electromagnetic visualization modes** across both the Natural Language Search tab and the Change Analysis Station:

| Render Mode | Spectral Bands / Formulation | Target Phenomenon & Visual Signature |
|:---|:---|:---|
| **True Color (RGB)** | Red (B4), Green (B3), Blue (B2) | Natural human eye appearance. Standard baseline verification. |
| **False Color Infrared** | NIR (B8), Red (B4), Green (B3) | **Crimson Red** = Dense healthy vegetation/chlorophyll, **Black** = Waterbodies, **Cyan/Grey** = Concrete, buildings, bare ground. |
| **SWIR Geological & Moisture** | SWIR1 (B11), NIR (B8), Red (B4) | Penetrates atmospheric haze. **Gold/Orange** = Disturbed soil, earthworks, excavation; **Emerald Green** = Canopy; **Navy** = Moisture. |
| **NDBI Built-Up & Concrete** | $\frac{\text{SWIR} - \text{NIR}}{\text{SWIR} + \text{NIR}}$ | **Fiery Orange-Red** highlights new buildings, concrete foundations, and tarmac; dark slate background suppresses vegetation. |
| **NDVI Vegetation Loss Map** | $\frac{\text{NIR} - \text{Red}}{\text{NIR} + \text{Red}}$ | Continuous colormap from brown/yellow (bare soil/stress) to deep green (dense forest). Instantly pinpoints clearing boundaries. |
| **NDWI Water Boundary** | $\frac{\text{Green} - \text{NIR}}{\text{Green} + \text{NIR}}$ | **Electric Cyan** highlight on all open water surfaces, reservoirs, and drainage canals. High contrast against terrain. |
| **SAR Radar Backscatter (C-Band)** | Simulated HH/HV Microwave Return ($\lambda \approx 5.6\text{ cm}$) | Penetrates cloud cover. **Glow White/Cyan** = Double-bounce corner reflectors (metallic structures, buildings); **Black** = Specular calm water; **Phosphor Green** = Diffuse terrain backscatter. |
| **Thermal IR Heat Radiance** | Simulated Longwave TIR ($8 - 14\ \mu\text{m}$) | Surface thermal emission: **Glowing White/Red** = High heat signatures (active industrial sites, hot metal, equipment); **Deep Blue** = Cold moisture/canopy. |

Switching the mode in the dropdown updates both the full-scene overview and the focused site micro-inspector instantaneously without reloading or re-downloading imagery.

