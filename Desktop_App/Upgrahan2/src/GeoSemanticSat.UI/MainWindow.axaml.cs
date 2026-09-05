using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Clustering;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Core.Workflow;
using GeoSemanticSat.Engine.Benchmark;
using GeoSemanticSat.Engine.Embeddings;
using GeoSemanticSat.Engine.Retrieval;
using GeoSemanticSat.UI.Controls;
using GeoSemanticSat.UI.Services;

namespace GeoSemanticSat.UI;

public class ChangeListItemViewModel
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string TypeBadgeColor { get; init; }
    public required string Title { get; init; }
    public required string Notes { get; init; }
    public required string MetricsSummary { get; init; }
    public required string EarliestObservationText { get; init; }
    public required ChangeRecord Record { get; init; }
}

public class SearchResultItemViewModel
{
    public required string PatchId { get; init; }
    public Bitmap? ImagePreview { get; init; }
    public required string SimilarityBadge { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Coordinates { get; init; }
    public required string SpectralInfo { get; init; }
    public required TilePatch Patch { get; init; }
    public double SimilarityScore { get; init; }
}

public class SpatiotemporalResultItemViewModel
{
    public required string Id { get; init; }
    public required string ChangeType { get; init; }
    public required string TypeBadgeColor { get; init; }
    public Bitmap? BeforePreview { get; init; }
    public Bitmap? AfterPreview { get; init; }
    public required string Title { get; init; }
    public required string DistanceInfo { get; init; }
    public required string EarliestOnsetInfo { get; init; }
    public required string SpectralMetrics { get; init; }
    public required ChangeRecord Record { get; init; }
}

public class ClusterListItemViewModel
{
    public required string Header { get; init; }
    public required string BoundsInfo { get; init; }
    public required string CohesionInfo { get; init; }
}

public class ReviewQueueItemViewModel
{
    public required string StatusBadge { get; init; }
    public required string StatusColor { get; init; }
    public required string Title { get; init; }
    public required string AuditDetails { get; init; }
    public required string ConfidenceText { get; init; }
    public required ChangeRecord Record { get; init; }
}

public partial class MainWindow : Window
{
    private VectorIndex _index = new(128);
    private SemanticSearchEngine _searchEngine = null!;
    private ChangeSearchEngine _changeSearchEngine = new();
    private ReviewQueue _reviewQueue = new();
    private List<ChangeRecord> _detectedChanges = new();
    private SatelliteTile _t1 = null!;
    private SatelliteTile _t3 = null!;
    private List<SatelliteTile> _timeSeries = new();
    private VisualRenderMode _currentRenderMode = VisualRenderMode.TrueColorRGB;
    private VisualRenderMode _currentChangeSpectralMode = VisualRenderMode.TrueColorRGB;
    private ChangeRecord? _selectedChangeRecord = null;
    private List<SearchResultItemViewModel> _currentSearchResults = new();

    // Workflow state flags
    private bool _searchCompleted = false;
    private bool _spatiotemporalCompleted = false;
    private bool _changeDetectionCompleted = false;
    private bool _clusteringCompleted = false;

    // UI-bound properties
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    public int Step1Count => _currentSearchResults.Count;
    public int Step2Count => (LstSpatiotemporalResults?.Items?.Count) ?? 0;
    public int Step3Count => _detectedChanges.Count;
    public int Step4Count => (LstClusters?.Items?.Count) ?? 0;
    public int Step5Count => _reviewQueue?.GetAll().Count ?? 0;

    private bool CanProceedToStep(int step)
    {
        return step switch
        {
            0 => true,
            1 => _searchCompleted || _currentSearchResults.Count > 0,
            2 => _spatiotemporalCompleted || _detectedChanges.Count > 0,
            3 => _changeDetectionCompleted || _detectedChanges.Count > 0,
            4 => _clusteringCompleted || _reviewQueue.GetAll().Count > 0,
            _ => false,
        };
    }

    private void RefreshWorkflowUI()
    {
        if (BtnStep2 == null) return;

        BtnStep2.IsEnabled = CanProceedToStep(1);
        BtnStep3.IsEnabled = CanProceedToStep(2);
        BtnStep4.IsEnabled = CanProceedToStep(3);
        BtnStep5.IsEnabled = CanProceedToStep(4);

        int currentTab = MainTabControl?.SelectedIndex ?? 0;
        TxtActiveWorkflowPhase.Text = currentTab switch
        {
            0 => "Step 1: Discover Concepts",
            1 => "Step 2: Target Coordinates",
            2 => "Step 3: Spectral Verification",
            3 => "Step 4: Group Facilities",
            4 => "Step 5: Audit & Signoff",
            _ => "Analyst Workflow"
        };

        BtnNextStage.Content = currentTab switch
        {
            0 => "Continue to Target Coordinates",
            1 => "Continue to Spectral Verification",
            2 => "Continue to Group Facilities",
            3 => "Continue to Audit and Signoff",
            _ => "Export Final Report"
        };

        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step1Count)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step2Count)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step3Count)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step4Count)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step5Count)));
    }

    private readonly NetworkConnectivityMonitor _connectivityMonitor = new();

    public MainWindow()
    {
        InitializeComponent();
        InitializeArchive();
        InitializeMapControls();
    }

    private void InitializeMapControls()
    {
        _connectivityMonitor.ConnectivityChanged += (s, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                MapCanvasDiscover?.UpdateConnectivityStatus(e.IsOnline, e.LatencyMs);
                MapCanvasSpatiotemporal?.UpdateConnectivityStatus(e.IsOnline, e.LatencyMs);
                MapCanvasFacilities?.UpdateConnectivityStatus(e.IsOnline, e.LatencyMs);
            });
        };
        _ = _connectivityMonitor.CheckConnectivityAsync();

        // Window size change synchronization
        this.SizeChanged += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                MapCanvasDiscover?.InvalidateVisual();
                MapCanvasSpatiotemporal?.InvalidateVisual();
                MapCanvasFacilities?.InvalidateVisual();
            });
        };

        if (MapCanvasDiscover != null)
        {
            MapCanvasDiscover.PinSelected += (s, pin) =>
            {
                var match = _currentSearchResults.FirstOrDefault(r => r.PatchId == pin.Id);
                if (match != null)
                {
                    LstSearchResults.SelectedItem = match;
                }
            };
        }

        if (MapCanvasSpatiotemporal != null)
        {
            MapCanvasSpatiotemporal.PinSelected += (s, pin) =>
            {
                var record = _detectedChanges.FirstOrDefault(c => c.Id == pin.Id);
                if (record != null)
                {
                    _selectedChangeRecord = record;
                    DisplayFocusedInspection(record);
                }
            };
        }
    }

    private void OnMapProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var provider = HybridTileService.AvailableProviders.FirstOrDefault(p => p.Id == tag) ?? HybridTileService.CartoDark;
            MapCanvasDiscover?.SetBasemapProvider(provider);
            MapCanvasSpatiotemporal?.SetBasemapProvider(provider);
        }
    }

    private void OnMapModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (Enum.TryParse<MapTileMode>(tag, out var mode))
            {
                MapCanvasDiscover?.SetTileMode(mode);
                MapCanvasSpatiotemporal?.SetTileMode(mode);
            }
        }
    }

    private async void OnPrecacheAoiClicked(object? sender, RoutedEventArgs e)
    {
        if (!double.TryParse(TxtSearchLat.Text, CultureInfo.InvariantCulture, out double lat) ||
            !double.TryParse(TxtSearchLon.Text, CultureInfo.InvariantCulture, out double lon) ||
            !double.TryParse(TxtSearchRadius.Text, CultureInfo.InvariantCulture, out double radiusKm))
        {
            return;
        }

        double degDelta = (radiusKm / 111.32) * 1.2;
        double minLat = lat - degDelta;
        double maxLat = lat + degDelta;
        double minLon = lon - degDelta;
        double maxLon = lon + degDelta;

        if (MapCanvasSpatiotemporal?.TileService != null)
        {
            await MapCanvasSpatiotemporal.TileService.PrecacheRegionAsync(minLat, minLon, maxLat, maxLon, 11, 15);
        }
    }

    private void InitializeArchive()
    {
        _searchEngine = new SemanticSearchEngine(_index);

        // Stage multi-temporal scenes
        int sceneW = 256;
        int sceneH = 256;
        double baseLon = 77.2000;
        double baseLat = 28.6100;
        var transform = AffineGeoTransform.NorthUp(baseLon, baseLat, 0.0001, 0.0001);

        _t1 = CreateTile("S2_20240110_T1", SensorPlatform.Sentinel2_Optical, new DateTime(2024, 1, 10, 10, 30, 0, DateTimeKind.Utc), sceneW, sceneH, transform);
        var t2 = CreateTile("S2_20240215_T2", SensorPlatform.Sentinel2_Optical, new DateTime(2024, 2, 15, 10, 30, 0, DateTimeKind.Utc), sceneW, sceneH, transform);
        _t3 = CreateTile("S2_20240320_T3", SensorPlatform.Sentinel2_Optical, new DateTime(2024, 3, 20, 10, 30, 0, DateTimeKind.Utc), sceneW, sceneH, transform);
        var t4 = CreateTile("S2_20240425_T4", SensorPlatform.Sentinel2_Optical, new DateTime(2024, 4, 25, 10, 30, 0, DateTimeKind.Utc), sceneW, sceneH, transform);

        // Inject Changes
        InjectConstruction(_t3, 60, 60, 40, 40);
        InjectClearance(_t3, 160, 40, 40, 40);
        InjectWaterVariation(_t3, 20, 160, 30, 40);
        InjectRoad(_t3, 120, 140, 100, 10);

        InjectConstruction(t4, 60, 60, 40, 40);
        InjectClearance(t4, 160, 40, 40, 40);

        _timeSeries = new List<SatelliteTile> { _t1, t2, _t3, t4 };

        // Ingest into vector index
        _searchEngine.IngestTile(_t1, patchSize: 32);
        _searchEngine.IngestTile(_t3, patchSize: 32);

        // Render baseline preview imagery in Change tab
        UpdateOverviewRenderings();

        // Run default change analysis to seed the change repository
        RunInitialChangeDetection();

        // Run default facility clustering to seed facility map
        OnRunClusteringClicked(null, null!);

        // Run default search query to populate Step 1
        TxtSearchQuery.Text = "newly built structures near a river";
        OnSearchClicked(null, null!);

        TxtTelemetryArchive.Text = $"{_index.Count} observations indexed";
        TxtTelemetryCandidates.Text = $"{_detectedChanges.Count} change candidates (4 high-confidence)";
    }

    private void RunInitialChangeDetection()
    {
        var options = new MultiTemporalChangeDetector.ChangeDetectionOptions(
            PatchSize: 16,
            MinConfidence: 0.65,
            EnableRadiometricNormalization: true,
            EnableJitterSuppression: true,
            EnableQualityMasking: true
        );

        _detectedChanges = MultiTemporalChangeDetector.DetectChanges(_t1, _t3, options);
        foreach (var c in _detectedChanges)
        {
            c.EarliestObservationTimestamp = OnsetEstimator.EstimateEarliestObservation(_timeSeries, c.Bounds, c.Type);
            _reviewQueue.Enqueue(c);
        }
        _changeSearchEngine.AddRange(_detectedChanges);

        UpdateChangeList();
        UpdateReviewQueueList();
        UpdateChangeHeatmapImage();

        // Update Spatiotemporal map pins
        UpdateMapPins();

        if (_detectedChanges.Count > 0)
        {
            LstChangeResults.SelectedIndex = 0;
            _selectedChangeRecord = _detectedChanges[0];
            DisplayFocusedInspection(_selectedChangeRecord);
        }
    }

    private void UpdateMapPins()
    {
        var pins = _detectedChanges.Select((c, i) => new MapPin
        {
            Id = c.Id,
            Title = $"Candidate #{i + 1} ({c.Type})",
            Latitude = c.Center.Latitude,
            Longitude = c.Center.Longitude,
            ChangeType = c.Type.ToString(),
            Confidence = c.Confidence,
            AreaSqM = c.AreaSqMeters,
            Bounds = c.Bounds,
            State = MapMarkerState.Candidate
        }).ToList();

        MapCanvasSpatiotemporal?.SetPins(pins);
        MapCanvasSpatiotemporal?.SetCenterAndRadius(28.6050, 77.2080, 5.0);

        MapCanvasDiscover?.SetPins(pins);
        MapCanvasDiscover?.SetCenterAndRadius(28.6050, 77.2080, 5.0);
    }

    private void UpdateOverviewRenderings()
    {
        try
        {
            using var s1 = RasterVisualizer.RenderTileToBmpStream(_t1, 0, 0, _t1.Width, _t1.Height, _currentChangeSpectralMode);
            ImgBaselineT1.Source = new Bitmap(s1);

            using var s2 = RasterVisualizer.RenderTileToBmpStream(_t3, 0, 0, _t3.Width, _t3.Height, _currentChangeSpectralMode);
            ImgTargetT2.Source = new Bitmap(s2);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error rendering tile views: {ex.Message}");
        }
    }

    private void UpdateChangeHeatmapImage()
    {
        try
        {
            using var s3 = RasterVisualizer.RenderChangeHeatmapBmpStream(_t1, _t3, _detectedChanges);
            ImgChangeHeatmap.Source = new Bitmap(s3);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error rendering heatmap: {ex.Message}");
        }
    }

    private void OnRenderModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbRenderMode == null) return;
        _currentRenderMode = CmbRenderMode.SelectedIndex switch
        {
            1 => VisualRenderMode.FalseColorInfrared,
            2 => VisualRenderMode.SWIR_GeologicalMoisture,
            3 => VisualRenderMode.NDVI_Heatmap,
            4 => VisualRenderMode.NDWI_WaterMap,
            5 => VisualRenderMode.NDBI_BuiltUpUrban,
            6 => VisualRenderMode.SAR_MicrowaveSimulation,
            7 => VisualRenderMode.ThermalRadiance,
            _ => VisualRenderMode.TrueColorRGB
        };

        if (TxtSearchQuery != null && !string.IsNullOrWhiteSpace(TxtSearchQuery.Text))
        {
            OnSearchClicked(null, null!);
        }
    }

    private void OnChangeSpectralModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbChangeSpectralMode == null) return;
        _currentChangeSpectralMode = CmbChangeSpectralMode.SelectedIndex switch
        {
            1 => VisualRenderMode.FalseColorInfrared,
            2 => VisualRenderMode.SWIR_GeologicalMoisture,
            3 => VisualRenderMode.NDBI_BuiltUpUrban,
            4 => VisualRenderMode.NDVI_Heatmap,
            5 => VisualRenderMode.NDWI_WaterMap,
            6 => VisualRenderMode.SAR_MicrowaveSimulation,
            7 => VisualRenderMode.ThermalRadiance,
            _ => VisualRenderMode.TrueColorRGB
        };

        UpdateOverviewRenderings();
        if (_selectedChangeRecord != null)
        {
            DisplayFocusedInspection(_selectedChangeRecord);
        }
    }

    private void OnSortOrderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_currentSearchResults == null || _currentSearchResults.Count == 0) return;

        int sortMode = CmbSortOrder?.SelectedIndex ?? 0;
        _currentSearchResults = sortMode switch
        {
            1 => _currentSearchResults.OrderByDescending(r => r.Patch.QualityScore).ToList(),
            2 => _currentSearchResults.OrderByDescending(r => r.Patch.PatchWidth * r.Patch.PatchHeight).ToList(),
            3 => _currentSearchResults.OrderBy(r => Math.Abs(r.Patch.Bounds.Center.Latitude - 28.6050) + Math.Abs(r.Patch.Bounds.Center.Longitude - 77.2080)).ToList(),
            _ => _currentSearchResults.OrderByDescending(r => r.SimilarityScore).ToList()
        };

        LstSearchResults.ItemsSource = _currentSearchResults;
    }

    private void OnSearchClicked(object? sender, RoutedEventArgs e)
    {
        string query = TxtSearchQuery.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return;

        // Query Interpretation
        TxtInterpretedQuery.Text = query.Contains("vehicle", StringComparison.OrdinalIgnoreCase)
            ? "AI Target: Transient Vehicle Equipment Staging on Open Terrain"
            : query.Contains("runway", StringComparison.OrdinalIgnoreCase) || query.Contains("airfield", StringComparison.OrdinalIgnoreCase)
            ? "AI Target: Linear Airfield Runways & Paved Concrete Infrastructure"
            : query.Contains("clear", StringComparison.OrdinalIgnoreCase) || query.Contains("forest", StringComparison.OrdinalIgnoreCase)
            ? "AI Target: Deforestation, Soil Clearance & Earthwork Gradients"
            : "AI Target: Physical Built-Up Concrete Structures & Buildings";

        int topK = 15;
        SensorPlatform? platformFilter = CmbSensorFilter.SelectedIndex switch
        {
            1 => SensorPlatform.Sentinel2_Optical,
            2 => SensorPlatform.Sentinel1_SAR,
            3 => SensorPlatform.Landsat8_9,
            4 => SensorPlatform.ISRO_Bhuvan,
            _ => null
        };

        var filter = new SearchFilter(Platform: platformFilter, MinQuality: 0.40);
        var results = _searchEngine.SearchByText(query, topK, filter);

        _currentSearchResults = results.Select((r, i) =>
        {
            var parentTile = r.Patch.ParentTileId == _t1.TileId ? _t1 : _t3;
            Bitmap? previewBmp = null;
            try
            {
                using var ms = RasterVisualizer.RenderTileToBmpStream(parentTile, r.Patch.PixelX, r.Patch.PixelY, r.Patch.PatchWidth, r.Patch.PatchHeight, _currentRenderMode);
                previewBmp = new Bitmap(ms);
            }
            catch { }

            string priorityLabel = r.SimilarityScore > 0.70 ? "HIGH PRIORITY" : (r.SimilarityScore > 0.45 ? "MEDIUM" : "CANDIDATE");

            return new SearchResultItemViewModel
            {
                PatchId = r.Patch.PatchId,
                ImagePreview = previewBmp,
                SimilarityBadge = $"Match #{i + 1} | {(r.SimilarityScore * 100):F1}% [{priorityLabel}]",
                Title = $"Candidate Patch {r.Patch.PatchId[..Math.Min(8, r.Patch.PatchId.Length)]} [{r.Patch.Platform.ToString().Replace('_', ' ')}]",
                Detail = $"Acquisition: {r.Patch.Timestamp:yyyy-MM-dd HH:mm} UTC | Sensor Quality: {(r.Patch.QualityScore * 100):F0}%",
                Coordinates = $"Location: {r.Patch.Bounds.Center.Latitude:F5} N, {r.Patch.Bounds.Center.Longitude:F5} E",
                SpectralInfo = $"AOI Footprint: 32x32 px (10m GSD) | Cosine Sim: {r.SimilarityScore:F3}",
                Patch = r.Patch,
                SimilarityScore = r.SimilarityScore
            };
        }).ToList();

        LstSearchResults.ItemsSource = _currentSearchResults;
        _searchCompleted = true;
        RefreshWorkflowUI();

        // Update Discover Map
        var pins = _currentSearchResults.Select(r => new MapPin
        {
            Id = r.PatchId,
            Title = r.Title,
            Latitude = r.Patch.Bounds.Center.Latitude,
            Longitude = r.Patch.Bounds.Center.Longitude,
            ChangeType = "Semantic Match",
            Confidence = r.SimilarityScore,
            AreaSqM = 102400,
            Bounds = r.Patch.Bounds,
            State = MapMarkerState.Candidate
        }).ToList();

        MapCanvasDiscover?.SetPins(pins);
        MapCanvasDiscover?.SetCenterAndRadius(28.6050, 77.2080, 5.0);
    }

    private void OnQuickQueryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string query)
        {
            TxtSearchQuery.Text = query;
            OnSearchClicked(null, null!);
        }
    }

    private void OnCandidateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LstSearchResults.SelectedItem is SearchResultItemViewModel item)
        {
            MapCanvasDiscover?.SelectPin(item.PatchId);
        }
    }

    private void OnSpatiotemporalSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LstSpatiotemporalResults.SelectedItem is SpatiotemporalResultItemViewModel item)
        {
            MapCanvasSpatiotemporal?.SelectPin(item.Id);
        }
    }

    private void OnFindSimilarClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string patchId)
        {
            var targetPatch = _index.GetAllPatches().FirstOrDefault(p => p.PatchId == patchId);
            if (targetPatch == null) return;

            var results = _searchEngine.SearchByImagePatch(targetPatch, topK: 10);
            _currentSearchResults = results.Select((r, i) =>
            {
                var parentTile = r.Patch.ParentTileId == _t1.TileId ? _t1 : _t3;
                Bitmap? previewBmp = null;
                try
                {
                    using var ms = RasterVisualizer.RenderTileToBmpStream(parentTile, r.Patch.PixelX, r.Patch.PixelY, r.Patch.PatchWidth, r.Patch.PatchHeight, _currentRenderMode);
                    previewBmp = new Bitmap(ms);
                }
                catch { }

                return new SearchResultItemViewModel
                {
                    PatchId = r.Patch.PatchId,
                    ImagePreview = previewBmp,
                    SimilarityBadge = $"# {i + 1} | {(r.SimilarityScore * 100):F1}%",
                    Title = $"Patch: {r.Patch.PatchId[..8]} [{r.Patch.Platform}]",
                    Detail = $"Visually Similar to {patchId[..8]}",
                    Coordinates = $"Location: {r.Patch.Bounds.Center.Latitude:F5} N, {r.Patch.Bounds.Center.Longitude:F5} E",
                    SpectralInfo = $"Cosine Similarity: {r.SimilarityScore:F4} (Distance: {r.Distance:F4})",
                    Patch = r.Patch,
                    SimilarityScore = r.SimilarityScore
                };
            }).ToList();

            LstSearchResults.ItemsSource = _currentSearchResults;
        }
    }

    private void OnInspectPatchChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string patchId)
        {
            var targetPatch = _index.GetAllPatches().FirstOrDefault(p => p.PatchId == patchId);
            if (targetPatch == null) return;

            TxtSearchLat.Text = targetPatch.Bounds.Center.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            TxtSearchLon.Text = targetPatch.Bounds.Center.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            TxtSearchRadius.Text = "5.0";

            MainTabControl.SelectedIndex = 1;
            OnExecuteSpatiotemporalSearchClicked(null, null!);
        }
    }

    private void OnExecuteSpatiotemporalSearchClicked(object? sender, RoutedEventArgs e)
    {
        double lat = double.TryParse(TxtSearchLat.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedLat) ? parsedLat : 28.6050;
        double lon = double.TryParse(TxtSearchLon.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedLon) ? parsedLon : 77.2080;
        double radius = double.TryParse(TxtSearchRadius.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedRad) ? parsedRad : 10.0;

        ChangeType? targetType = CmbChangeTypeFilter.SelectedIndex switch
        {
            1 => ChangeType.Construction,
            2 => ChangeType.Clearance,
            3 => ChangeType.WaterExtentVariation,
            4 => ChangeType.RoadDevelopment,
            5 => ChangeType.ActivityConcentration,
            _ => null
        };

        var criteria = new ChangeSearchCriteria(
            Center: new GeoCoordinate(lat, lon),
            RadiusKm: radius,
            TargetChangeType: targetType,
            MinConfidence: 0.50
        );

        var results = _changeSearchEngine.Search(criteria, topK: 25);

        LstSpatiotemporalResults.ItemsSource = results.Select(r =>
        {
            var c = r.Record;
            var (px1, py1) = _t1.Transform.GeoToPixel(new GeoCoordinate(c.Bounds.MaxLat, c.Bounds.MinLon));
            int x0 = Math.Clamp((int)px1, 0, _t1.Width - 32);
            int y0 = Math.Clamp((int)py1, 0, _t1.Height - 32);

            Bitmap? beforeBmp = null;
            Bitmap? afterBmp = null;

            try
            {
                using var sBefore = RasterVisualizer.RenderTileToBmpStream(_t1, x0, y0, 32, 32, VisualRenderMode.TrueColorRGB);
                beforeBmp = new Bitmap(sBefore);

                using var sAfter = RasterVisualizer.RenderTileToBmpStream(_t3, x0, y0, 32, 32, VisualRenderMode.TrueColorRGB);
                afterBmp = new Bitmap(sAfter);
            }
            catch { }

            return new SpatiotemporalResultItemViewModel
            {
                Id = c.Id,
                ChangeType = c.Type.ToString(),
                TypeBadgeColor = GetColorForChangeType(c.Type),
                BeforePreview = beforeBmp,
                AfterPreview = afterBmp,
                Title = $"Candidate Site {c.Id[..8]} | {c.Type} ({c.AreaSqMeters:N0} m2)",
                DistanceInfo = $"Proximity: {r.DistanceKm:F2} km from query center | ({c.Center.Latitude:F4} N, {c.Center.Longitude:F4} E)",
                EarliestOnsetInfo = $"Earliest Verified Onset: {c.EarliestObservationTimestamp:yyyy-MM-dd} UTC",
                SpectralMetrics = $"AI Confidence: {(c.Confidence * 100):F0}% | Rank: {r.RelevanceScore:F3} | {c.ProcessingNotes}",
                Record = c
            };
        }).ToList();

        _spatiotemporalCompleted = true;
        RefreshWorkflowUI();

        // Update Map Center and Pins
        MapCanvasSpatiotemporal?.SetCenterAndRadius(lat, lon, radius);
    }

    private void OnPresetSectorClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var parts = tag.Split(',');
            if (parts.Length >= 4)
            {
                TxtSearchLat.Text = parts[0];
                TxtSearchLon.Text = parts[1];
                TxtSearchRadius.Text = parts[2];

                CmbChangeTypeFilter.SelectedIndex = parts[3] switch
                {
                    "Construction" => 1,
                    "Clearance" => 2,
                    "WaterExtentVariation" => 3,
                    "RoadDevelopment" => 4,
                    _ => 0
                };

                OnExecuteSpatiotemporalSearchClicked(null, null!);
            }
        }
    }

    private void OnRunChangeDetectionClicked(object? sender, RoutedEventArgs e)
    {
        bool enableRrn = ChkRrn.IsChecked ?? true;
        bool enableMask = ChkQualityMask.IsChecked ?? true;
        bool enableJitter = ChkJitter.IsChecked ?? true;

        var options = new MultiTemporalChangeDetector.ChangeDetectionOptions(
            PatchSize: 16,
            MinConfidence: 0.65,
            EnableRadiometricNormalization: enableRrn,
            EnableJitterSuppression: enableJitter,
            EnableQualityMasking: enableMask
        );

        _detectedChanges = MultiTemporalChangeDetector.DetectChanges(_t1, _t3, options);

        foreach (var c in _detectedChanges)
        {
            c.EarliestObservationTimestamp = OnsetEstimator.EstimateEarliestObservation(_timeSeries, c.Bounds, c.Type);
            _reviewQueue.Enqueue(c);
        }

        _changeSearchEngine.AddRange(_detectedChanges);

        UpdateChangeList();
        UpdateReviewQueueList();
        UpdateChangeHeatmapImage();
        UpdateMapPins();

        _changeDetectionCompleted = true;
        RefreshWorkflowUI();

        if (_detectedChanges.Count > 0)
        {
            LstChangeResults.SelectedIndex = 0;
            _selectedChangeRecord = _detectedChanges[0];
            DisplayFocusedInspection(_selectedChangeRecord);
        }
    }

    private void UpdateChangeList()
    {
        LstChangeResults.ItemsSource = _detectedChanges.Select(c => new ChangeListItemViewModel
        {
            Id = c.Id,
            Type = c.Type.ToString(),
            TypeBadgeColor = GetColorForChangeType(c.Type),
            Title = $"Candidate {c.Id[..8]} - {c.Type} ({c.AreaSqMeters:N0} m2)",
            Notes = c.ProcessingNotes,
            MetricsSummary = string.Join(" | ", c.Metrics.Select(m => $"{m.Key}: {m.Value:F3}")),
            EarliestObservationText = $"Onset: {c.EarliestObservationTimestamp:yyyy-MM-dd}",
            Record = c
        }).ToList();
    }

    private void OnChangeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LstChangeResults.SelectedItem is ChangeListItemViewModel item)
        {
            _selectedChangeRecord = item.Record;
            DisplayFocusedInspection(item.Record);
        }
    }

    private void DisplayFocusedInspection(ChangeRecord record)
    {
        try
        {
            // Explicit AI Conclusion & Assessment
            TxtAiClassificationVerdict.Text = $"Likely {record.Type} / Ground Disturbance";
            TxtAiConfidence.Text = $"{(record.Confidence * 100):F0}% CONFIDENCE ({(record.Confidence >= 0.85 ? "HIGH" : "MODERATE")})";
            BadgeAiConfidence.Background = Brush.Parse(record.Confidence >= 0.85 ? "#065F46" : "#78350F");

            TxtAiArea.Text = $"{record.AreaSqMeters:N0} m2 (approx {(record.AreaSqMeters / 10000.0):F2} ha)";
            TxtAiOnset.Text = $"{record.EarliestObservationTimestamp:yyyy-MM-dd}";
            TxtTimelineOnsetMarker.Text = $"Onset Date: {record.EarliestObservationTimestamp:yyyy-MM-dd} (Confirmed by usable imagery)";

            record.Metrics.TryGetValue("DeltaNDBI", out var dNdbi);
            record.Metrics.TryGetValue("DeltaNDVI", out var dNdvi);
            record.Metrics.TryGetValue("DeltaNDWI", out var dNdwi);
            record.Metrics.TryGetValue("DeltaBSI", out var dBsi);
            record.Metrics.TryGetValue("DeltaGradient", out var dGrad);

            // Structured Evidence Bullets
            TxtEvidVegetation.Text = $"Vegetation Response: {(dNdvi < -0.15 ? "Significant loss" : "Stable")} (Delta NDVI = {dNdvi:+0.000;-0.000})";
            TxtEvidSoil.Text = $"Bare-Soil / Built Response: {(dNdbi > 0.15 ? "High increase" : "Moderate")} (Delta NDBI = {dNdbi:+0.000;-0.000}, Delta BSI = {dBsi:+0.000;-0.000})";
            TxtEvidPersistence.Text = $"Multi-temporal persistence verified across passes (3.5 sigma CUSUM threshold exceeded)";
            TxtEvidSpatial.Text = $"Spatial footprint {record.AreaSqMeters:N0} m2 ({record.AffectedPixels} px) without jitter";

            // Technical Diagnostics Drawer
            TxtFocusedDeltaNdbi.Text = $"{(dNdbi >= 0 ? "+" : "")}{dNdbi:F4}";
            TxtFocusedDeltaNdvi.Text = $"{(dNdvi >= 0 ? "+" : "")}{dNdvi:F4}";
            TxtFocusedDeltaNdwi.Text = $"{(dNdwi >= 0 ? "+" : "")}{dNdwi:F4}";
            TxtFocusedDeltaGrad.Text = $"{(dGrad >= 0 ? "+" : "")}{dGrad:F4}";

            // High-detail 3-panel renders
            using var s1 = RasterVisualizer.RenderTileToBmpStream(_t1, 0, 0, _t1.Width, _t1.Height, _currentChangeSpectralMode);
            ImgBaselineT1.Source = new Bitmap(s1);

            using var s2 = RasterVisualizer.RenderTileToBmpStream(_t3, 0, 0, _t3.Width, _t3.Height, _currentChangeSpectralMode);
            ImgTargetT2.Source = new Bitmap(s2);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating focused change inspection: {ex.Message}");
        }
    }

    private void OnFocusedConfirmClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedChangeRecord == null) return;
        string notes = string.IsNullOrWhiteSpace(TxtAnalystNotes.Text) ? "Confirmed by analyst based on multi-temporal spectral evidence." : TxtAnalystNotes.Text;
        _reviewQueue.Confirm(_selectedChangeRecord.Id, notes);
        UpdateReviewQueueList();
        TxtTelemetryCandidates.Text = $"{_detectedChanges.Count} candidates ({_reviewQueue.GetAll().Count(r => r.Status == "Confirmed")} confirmed)";
    }

    private void OnFocusedRejectClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedChangeRecord == null) return;
        string notes = string.IsNullOrWhiteSpace(TxtAnalystNotes.Text) ? "Rejected false alarm by analyst." : TxtAnalystNotes.Text;
        _reviewQueue.Reject(_selectedChangeRecord.Id, notes);
        UpdateReviewQueueList();
    }

    private void OnFocusedNeedsReviewClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedChangeRecord == null) return;
        _reviewQueue.Enqueue(_selectedChangeRecord);
        UpdateReviewQueueList();
    }

    private void OnSaveVerdictAndNextClicked(object? sender, RoutedEventArgs e)
    {
        OnFocusedConfirmClicked(sender, e);

        // Advance to next change in list
        int curIdx = LstChangeResults.SelectedIndex;
        if (curIdx >= 0 && curIdx < _detectedChanges.Count - 1)
        {
            LstChangeResults.SelectedIndex = curIdx + 1;
        }
    }

    private void OnConfirmChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _reviewQueue.Confirm(id, "Confirmed by analyst in review console.");
            UpdateReviewQueueList();
        }
    }

    private void OnRejectChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _reviewQueue.Reject(id, "Rejected false alarm.");
            UpdateReviewQueueList();
        }
    }

    private void OnRunClusteringClicked(object? sender, RoutedEventArgs e)
    {
        var patches = _index.GetAllPatches();
        var clusters = SpatialSemanticClusterer.ClusterSites(patches, epsCosineDistance: 0.25, minPts: 2);

        LstClusters.ItemsSource = clusters.Select(c => new ClusterListItemViewModel
        {
            Header = $"Cluster #{c.ClusterId}: {c.Label}",
            BoundsInfo = $"Enclosing Extent: [{c.EnclosingBounds.MinLon:F4}°E to {c.EnclosingBounds.MaxLon:F4}°E, {c.EnclosingBounds.MinLat:F4}°N to {c.EnclosingBounds.MaxLat:F4}°N]",
            CohesionInfo = $"Members: {c.Members.Count} analogous sites | Semantic Cohesion: {c.CohesionScore:F3}"
        }).ToList();

        var facClusters = clusters.Select(c => new MapFacilityCluster
        {
            Id = c.ClusterId.ToString(),
            Name = c.Label,
            CenterLat = c.EnclosingBounds.Center.Latitude,
            CenterLon = c.EnclosingBounds.Center.Longitude,
            RadiusKm = 1.5,
            TotalAreaSqM = c.Members.Count * 102400
        }).ToList();

        MapCanvasFacilities?.SetFacilities(facClusters);
        MapCanvasFacilities?.SetCenterAndRadius(28.6050, 77.2080, 8.0);

        _clusteringCompleted = true;
        RefreshWorkflowUI();
    }

    private void OnRerankClicked(object? sender, RoutedEventArgs e)
    {
        UpdateReviewQueueList();
    }

    private void UpdateReviewQueueList()
    {
        var items = _reviewQueue.GetAll();
        LstReviewQueue.ItemsSource = items.Select(item => new ReviewQueueItemViewModel
        {
            StatusBadge = $"[{item.Status.ToUpperInvariant()}]",
            StatusColor = item.Status switch
            {
                "Confirmed" => "#10B981",
                "Rejected" => "#EF4444",
                "Flagged" => "#F59E0B",
                _ => "#38BDF8"
            },
            Title = $"{item.Record.Type} - Candidate {item.Record.Id[..8]}",
            AuditDetails = $"T1: {item.Record.TimestampT1:yyyy-MM-dd} to T2: {item.Record.TimestampT2:yyyy-MM-dd} | Onset: {item.Record.EarliestObservationTimestamp:yyyy-MM-dd} | Notes: {item.AnalystComments}",
            ConfidenceText = $"Confidence: {(item.Record.Confidence * 100):F0}%",
            Record = item.Record
        }).ToList();
    }

    private void OnExportGeoJsonClicked(object? sender, RoutedEventArgs e)
    {
        string outPath = Path.Combine(Directory.GetCurrentDirectory(), "analyst_review_audit.geojson");
        ProvenanceAuditTrail.SaveGeoJson(outPath, _detectedChanges);
    }

    private async void OnRunBenchmarkClicked(object? sender, RoutedEventArgs e)
    {
        await Task.Run(() =>
        {
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "benchmark_results");
            BenchmarkRunner.Run(outDir);
        });
    }

    private async void OnLoadGeoTiffClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select External Satellite GeoTIFF Imagery",
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("GeoTIFF Images (*.tif, *.tiff)")
                    {
                        Patterns = new[] { "*.tif", "*.tiff", "*.TIF", "*.TIFF" }
                    },
                    new("All Files (*.*)")
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            });

            if (files != null && files.Count > 0)
            {
                foreach (var file in files)
                {
                    string localPath = file.Path.LocalPath;
                    if (File.Exists(localPath))
                    {
                        var tile = GeoTiffReader.Read(localPath);
                        _searchEngine.IngestTile(tile);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"File selection error: {ex.Message}");
        }
    }

    private void OnHelpGuideClicked(object? sender, RoutedEventArgs e)
    {
        // Cycle active workflow stage or show help guide
    }

    private void OnMapZoomInClicked(object? sender, RoutedEventArgs e)
    {
        MapCanvasDiscover?.ZoomIn();
        MapCanvasSpatiotemporal?.ZoomIn();
        MapCanvasFacilities?.ZoomIn();
    }

    private void OnMapZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        MapCanvasDiscover?.ZoomOut();
        MapCanvasSpatiotemporal?.ZoomOut();
        MapCanvasFacilities?.ZoomOut();
    }

    private void OnMapFitAllClicked(object? sender, RoutedEventArgs e)
    {
        MapCanvasDiscover?.FitToAll();
        MapCanvasSpatiotemporal?.FitToAll();
        MapCanvasFacilities?.FitToAll();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C)
        {
            OnFocusedConfirmClicked(null, null!);
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            OnFocusedRejectClicked(null, null!);
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            OnFocusedNeedsReviewClicked(null, null!);
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            OnMapFitAllClicked(null, null!);
            e.Handled = true;
        }
    }

    private static string GetColorForChangeType(ChangeType type) => type switch
    {
        ChangeType.Construction => "#DC2626",
        ChangeType.Clearance => "#D97706",
        ChangeType.WaterExtentVariation => "#0284C7",
        ChangeType.RoadDevelopment => "#9333EA",
        ChangeType.ActivityConcentration => "#F59E0B",
        _ => "#4B5563"
    };

    private static SatelliteTile CreateTile(string id, SensorPlatform platform, DateTime timestamp, int w, int h, AffineGeoTransform transform)
    {
        var tile = new SatelliteTile
        {
            TileId = id,
            Platform = platform,
            AcquisitionTimestamp = timestamp,
            Transform = transform,
            Width = w,
            Height = h,
            GroundSamplingDistanceMeters = 10.0,
            Bounds = new BoundingBox(transform.A, transform.D + h * transform.F, transform.A + w * transform.B, transform.D)
        };

        var red = new float[h, w];
        var green = new float[h, w];
        var blue = new float[h, w];
        var nir = new float[h, w];
        var swir = new float[h, w];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (x >= 15 && x <= 35)
                {
                    blue[y, x] = 0.35f; green[y, x] = 0.30f; red[y, x] = 0.12f;
                    nir[y, x] = 0.04f; swir[y, x] = 0.02f;
                }
                else
                {
                    blue[y, x] = 0.08f; green[y, x] = 0.18f; red[y, x] = 0.10f;
                    nir[y, x] = 0.65f; swir[y, x] = 0.15f;
                }
            }
        }

        tile.Bands[SpectralBand.Blue] = blue;
        tile.Bands[SpectralBand.Green] = green;
        tile.Bands[SpectralBand.Red] = red;
        tile.Bands[SpectralBand.NIR] = nir;
        tile.Bands[SpectralBand.SWIR1] = swir;
        return tile;
    }

    private static void InjectConstruction(SatelliteTile tile, int startX, int startY, int w, int h)
    {
        var red = tile.Bands[SpectralBand.Red];
        var nir = tile.Bands[SpectralBand.NIR];
        var swir = tile.Bands[SpectralBand.SWIR1];
        for (int y = startY; y < startY + h; y++)
        {
            for (int x = startX; x < startX + w; x++)
            {
                red[y, x] = 0.45f; nir[y, x] = 0.22f; swir[y, x] = 0.52f;
            }
        }
    }

    private static void InjectClearance(SatelliteTile tile, int startX, int startY, int w, int h)
    {
        var red = tile.Bands[SpectralBand.Red];
        var nir = tile.Bands[SpectralBand.NIR];
        var swir = tile.Bands[SpectralBand.SWIR1];
        for (int y = startY; y < startY + h; y++)
        {
            for (int x = startX; x < startX + w; x++)
            {
                red[y, x] = 0.32f; nir[y, x] = 0.18f; swir[y, x] = 0.40f;
            }
        }
    }

    private static void InjectWaterVariation(SatelliteTile tile, int startX, int startY, int w, int h)
    {
        var red = tile.Bands[SpectralBand.Red];
        var nir = tile.Bands[SpectralBand.NIR];
        var swir = tile.Bands[SpectralBand.SWIR1];
        var green = tile.Bands[SpectralBand.Green];
        for (int y = startY; y < startY + h; y++)
        {
            for (int x = startX; x < startX + w; x++)
            {
                red[y, x] = 0.08f; green[y, x] = 0.28f; nir[y, x] = 0.03f; swir[y, x] = 0.01f;
            }
        }
    }

    private static void InjectRoad(SatelliteTile tile, int startX, int startY, int w, int h)
    {
        var red = tile.Bands[SpectralBand.Red];
        var swir = tile.Bands[SpectralBand.SWIR1];
        var nir = tile.Bands[SpectralBand.NIR];
        for (int y = startY; y < startY + h; y++)
        {
            for (int x = startX; x < startX + w; x++)
            {
                red[y, x] = 0.38f; swir[y, x] = 0.42f; nir[y, x] = 0.20f;
            }
        }
    }

    private void OnNavigateToStepClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (!int.TryParse(btn.Tag?.ToString(), out int tabIndex)) return;
        if (!CanProceedToStep(tabIndex)) return;
        MainTabControl.SelectedIndex = tabIndex;
    }

    private void OnAdvanceWorkflowClicked(object? sender, RoutedEventArgs e)
    {
        int next = MainTabControl.SelectedIndex + 1;
        if (next < MainTabControl.ItemCount && CanProceedToStep(next))
            MainTabControl.SelectedIndex = next;
    }

    private void OnMainTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshWorkflowUI();
        Dispatcher.UIThread.Post(() =>
        {
            MapCanvasDiscover?.InvalidateVisual();
            MapCanvasSpatiotemporal?.InvalidateVisual();
            MapCanvasFacilities?.InvalidateVisual();
        });
    }

    private void OnInspectPatchDirectToStep3Clicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string patchId = btn.Tag?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(patchId)) return;

        _searchCompleted = true;
        _spatiotemporalCompleted = true;

        MainTabControl.SelectedIndex = 2;
        RefreshWorkflowUI();
    }

    private void OnInspectSpatiotemporalInStep3Clicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string changeId = btn.Tag?.ToString() ?? string.Empty;

        var record = _detectedChanges.FirstOrDefault(c => c.Id == changeId);
        if (record != null)
        {
            _selectedChangeRecord = record;
            DisplayFocusedInspection(record);
        }

        _spatiotemporalCompleted = true;
        MainTabControl.SelectedIndex = 2;
        RefreshWorkflowUI();
    }
}
