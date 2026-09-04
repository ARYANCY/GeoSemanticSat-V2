# Implementation Plan: Visual Presentation & Advanced Spatiotemporal Change Search

## Overview
The user requested:
1. **Visual Presentation**:
   - Visual thumbnails and raster previews for query results and tile patches in Avalonia UI.
   - Interactive Before (T1), After (T2), and Difference/Change Heatmap visualizer for multi-temporal change analysis.
   - Visual spatial distribution map of clusters and detected change sites across geographic coordinates.
2. **Advanced Spatiotemporal Change Search**:
   - Querying specifically for changes using geographic coordinates (Latitude, Longitude, Radius in km or Bounding Box).
   - Time range filtering (Start Date, End Date, Earliest Observation onset).
   - Filter by specific change type (*Construction*, *Clearance*, *Water-Extent Variation*, *Road Development*, *Activity*).
   - Multi-criteria hybrid search: Semantic natural language text + Spatial proximity + Time window + Change classification.

---

## User Review Required
> [!IMPORTANT]
> - Imagery rendering will be executed natively in pure C# using Avalonia's `WriteableBitmap` (generating RGB / False Color composites from multi-spectral bands: NIR-Red-Green false-color infrared for vegetation/water analysis and True-Color RGB) without external cloud or native library dependencies, keeping the application 100% lightweight, cross-platform, and offline.
> - A new **"Advanced Change Search"** workflow will allow analysts to pin a coordinate `(Lat, Lon)`, specify a search radius (km), choose a time interval, select specific change types (e.g. *Construction* or *Clearance*), and view ranked changes with visual Before/After/Heatmap image previews.

---

## Proposed Changes

### 1. `GeoSemanticSat.Core`
#### Rendering Engine
- **[NEW] `GeoSemanticSat.Core/Raster/RasterVisualizer.cs`**:
  - `RenderRgbBitmap(SatelliteTile tile, int startX, int startY, int w, int h)`: Extracts Red, Green, Blue bands and creates 32-bit BGRA pixel byte arrays for visual rendering.
  - `RenderFalseColorIrBitmap(SatelliteTile tile, ...)`: Creates Standard False Color Infrared (NIR -> Red, Red -> Green, Green -> Blue) highlighting healthy vegetation in red, water in dark blue/black, and urban structures in cyan/grey.
  - `RenderChangeHeatmapBitmap(SatelliteTile t1, SatelliteTile t2, List<ChangeRecord> changes, ...)`: Generates color-coded change overlay (Red = Construction, Amber = Clearance, Blue = Water, Purple = Road, Orange = Activity) blended over baseline imagery.

#### Spatial & Temporal Search Enhancements
- **[MODIFY] `GeoSemanticSat.Core/VectorIndex/VectorIndex.cs`**:
  - Add `SearchFilter.CenterCoordinate` and `SearchFilter.RadiusKm` for circular radius proximity searches around specified `(Lat, Lon)`.
- **[NEW] `GeoSemanticSat.Core/ChangeDetection/ChangeSearchEngine.cs`**:
  - Multi-criteria change search engine supporting query by:
    - Geographic coordinates `(Latitude, Longitude)` with radial tolerance `RadiusKm` or Bounding Box.
    - Observation time window `[StartTime, EndTime]`.
    - Earliest Observation onset window.
    - Target Change Type (*Construction*, *Clearance*, *Water*, *Road*, *Activity*).
    - Minimum Confidence threshold.

---

### 2. `GeoSemanticSat.Engine`
- **[MODIFY] `GeoSemanticSat.Engine/Retrieval/SemanticSearchEngine.cs`**:
  - Integrate spatial radius filtering `DistanceToKm(coord) <= radiusKm` in addition to Bounding Box filtering.
  - Support combined semantic text + spatial proximity + temporal window ranking.

---

### 3. `GeoSemanticSat.UI`
- **[MODIFY] `GeoSemanticSat.UI/MainWindow.axaml` and `MainWindow.axaml.cs`**:
  - **Visual Presentation in Semantic Retrieval Tab**:
    - Add thumbnail preview card `<Image Source="{Binding Thumbnail}" Width="120" Height="120" />` for each retrieved result patch.
    - False-color vs True-color toggle.
  - **Visual Presentation in Change Analysis Tab**:
    - Interactive **3-panel visual viewer**:
      1. Baseline Imagery (T1)
      2. Target Observation (T2)
      3. Difference / Change Heatmap Overlay with bounding boxes demarcating detected changes.
    - Change inspection panel showing change metrics, confidence, and earliest observation date.
  - **New Tab / Sub-panel: "Advanced Spatiotemporal Change Search"**:
    - Input fields for **Center Latitude**, **Center Longitude**, and **Radius (km)** (with quick presets for target zones).
    - **Time Window**: Start Date and End Date pickers.
    - **Change Type Selector**: Multi-select or dropdown (*Construction*, *Clearance*, *Water Variation*, *Road*, *Activity*, or *Any*).
    - **Search Button**: Executes spatial-temporal-change query and displays matching change sites with side-by-side Before/After previews and metadata.
  - **Visual Presentation in Discovery & Clustering Tab**:
    - Render a 2D spatial distribution canvas/grid plotting cluster centroids, bounding boxes, and site counts across latitude and longitude.

---

### 4. `GeoSemanticSat.Cli`
- **[MODIFY] `GeoSemanticSat.Cli/Program.cs`**:
  - Add CLI command `search-change --lat 28.605 --lon 77.208 --radius-km 5 --type Construction --start 2024-01-01 --end 2024-04-30`.

---

### 5. `GeoSemanticSat.Tests`
- **[NEW] `GeoSemanticSat.Tests/AdvancedSearchAndVisualizationTests.cs`**:
  - Tests radial spatial search around (Lat, Lon) with distance thresholds.
  - Tests multi-criteria change search by time, location, and change type.
  - Tests raster visualizer pixel buffer generation and dimension integrity.

---

## Verification Plan

### Automated Tests
```bash
export DOTNET_CLI_HOME=/home/non_qualities/.gemini/antigravity/scratch/.dotnet
export NUGET_PACKAGES=/home/non_qualities/.gemini/antigravity/scratch/.nuget/packages
cd /home/non_qualities/.gemini/antigravity/scratch/GeoSemanticSat
dotnet test src/GeoSemanticSat.Tests/GeoSemanticSat.Tests.csproj --verbosity normal
```

### CLI Verification
```bash
dotnet run --project src/GeoSemanticSat.Cli -- search-change --lat 28.605 --lon 77.208 --radius 5 --type Construction
```

### UI Verification
Build and run the Avalonia UI to verify the visual presentations and search controls:
```bash
dotnet build src/GeoSemanticSat.UI/GeoSemanticSat.UI.csproj
```
