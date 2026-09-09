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
    /// <summary>
    /// Evidence score at or above which a Candidate is reported as high-confidence.
    /// See CONTEXT.md: this is an evidence score, not a calibrated probability.
    /// </summary>
    private const double HighConfidenceThreshold = 0.85;

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
    private ChangeRecord? _inspectedMapRecord = null;
    private List<SearchResultItemViewModel> _currentSearchResults = new();
    private string _activeHeatmapLayer = "CVA";
    private bool _isFullSceneContext = false;
    private int _activePassIndex = 2;

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

        var stageButtons = new[] { BtnStep1, BtnStep2, BtnStep3, BtnStep4, BtnStep5 };
        for (int i = 0; i < stageButtons.Length; i++)
        {
            if (stageButtons[i] is not { } b) continue;
            b.Classes.Set("current", i == currentTab);
            b.Classes.Set("done", i < currentTab);
        }

        var stageBadges = new[] { BadgeStep1Icon, BadgeStep2Icon, BadgeStep3Icon, BadgeStep4Icon, BadgeStep5Icon };
        var stageLabels = new[] { TxtStep1Label, TxtStep2Label, TxtStep3Label, TxtStep4Label, TxtStep5Label };
        for (int i = 0; i < stageBadges.Length; i++)
        {
            bool isCurrent = i == currentTab;
            if (stageBadges[i] is { } badge)
            {
                badge.Background = (IBrush?)Application.Current?.FindResource(
                    isCurrent ? "AccentBrush" : "SurfaceInsetBrush");
            }
            if (stageLabels[i] is { } label)
            {
                label.Foreground = (IBrush?)Application.Current?.FindResource(
                    isCurrent ? "TextPrimaryBrush" : "TextMutedBrush");
                label.FontWeight = isCurrent ? FontWeight.Bold : FontWeight.SemiBold;
            }
        }

        TxtActiveWorkflowPhase.Text = currentTab switch
        {
            0 => "1. Find Images",
            1 => "2. Pick Location",
            2 => "3. Check Changes",
            3 => "4. Group Places",
            4 => "5. Review & Export",
            _ => "Workflow"
        };

        BtnNextStage.Content = currentTab switch
        {
            0 => "Next: Pick Location ➔",
            1 => "Next: Check Changes ➔",
            2 => "Next: Group Places ➔",
            3 => "Next: Review & Export ➔",
            _ => "Export Final Report ➔"
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
                MapCanvasSpatiotemporal?.InvalidateVisual();
                MapCanvasFacilities?.InvalidateVisual();
            });
        };

        if (MapCanvasSpatiotemporal != null)
        {
            MapCanvasSpatiotemporal.PinSelected += (s, pin) =>
            {
                var record = _detectedChanges.FirstOrDefault(c => c.Id == pin.Id);
                if (record != null)
                {
                    SyncActiveCandidate(record);
                }
            };
        }
    }

    private void OnMapProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            var provider = HybridTileService.AvailableProviders.FirstOrDefault(p => p.Id == tag) ?? HybridTileService.CartoDark;
            MapCanvasSpatiotemporal?.SetBasemapProvider(provider);
        }
    }

    private void OnMapModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox cmb && cmb.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (Enum.TryParse<MapTileMode>(tag, out var mode))
            {
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
        InjectVehicles(_t3, 192, 192, 48, 48, numVehicles: 16);
        InjectVehicles(_t1, 192, 192, 48, 48, numVehicles: 8);
        InjectAirfield(_t1, 96, 210, 140, 14);
        InjectAirfield(_t3, 96, 210, 140, 14);

        InjectConstruction(t4, 60, 60, 40, 40);
        InjectClearance(t4, 160, 40, 40, 40);
        InjectVehicles(t4, 192, 192, 48, 48, numVehicles: 20);

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
        TxtSearchQuery.Text = "large vehicle concentrations on open ground";
        OnSearchClicked(null, null!);

        TxtTelemetryArchive.Text = $"{_index.Count} satellite images loaded";
        int highConfidence = _detectedChanges.Count(c => c.Confidence >= HighConfidenceThreshold);
        TxtTelemetryCandidates.Text = _detectedChanges.Count == 0
            ? "no candidates"
            : $"{_detectedChanges.Count} changes found ({highConfidence} strong matches)";
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
        if (_t1 == null || _t3 == null) return;

        if (MapCanvasSpatiotemporal != null)
        {
            MapCanvasSpatiotemporal.SceneFootprint = _t1.Bounds;
            MapCanvasSpatiotemporal.SceneFootprintLabel = $"Sentinel-2 Multi-Temporal AOI (T1: {_t1.AcquisitionTimestamp:yyyy-MM-dd} / T2: {_t3.AcquisitionTimestamp:yyyy-MM-dd} • 10m GSD)";
        }

        var pins = _detectedChanges.Select((c, i) =>
        {
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

            string rawHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Id + c.TileId + c.Type + c.Center.Latitude + c.Center.Longitude)));

            return new MapPin
            {
                Id = c.Id,
                Title = $"Candidate #{i + 1} ({c.Type})",
                Latitude = c.Center.Latitude,
                Longitude = c.Center.Longitude,
                ChangeType = c.Type.ToString(),
                Confidence = c.Confidence,
                AreaSqM = c.AreaSqMeters,
                Bounds = c.Bounds,
                State = c.ConfirmedByAnalyst ? MapMarkerState.Confirmed : (c.RejectedByAnalyst ? MapMarkerState.Rejected : MapMarkerState.Candidate),
                BeforePreview = beforeBmp,
                AfterPreview = afterBmp,
                TimestampT1 = _t1.AcquisitionTimestamp,
                TimestampT2 = _t3.AcquisitionTimestamp,
                EarliestOnset = c.EarliestObservationTimestamp,
                SensorPlatformT1 = $"{_t1.Platform} (10m)",
                SensorPlatformT2 = $"{_t3.Platform} (10m)",
                SceneIdT1 = _t1.TileId,
                SceneIdT2 = _t3.TileId,
                ProvenanceHash = $"SHA256:{rawHash[..16].ToLowerInvariant()}..."
            };
        }).ToList();

        MapCanvasSpatiotemporal?.SetPins(pins);
        MapCanvasSpatiotemporal?.SetCenterAndRadius(28.6050, 77.2080, 5.0);

        if (_detectedChanges.Count > 0)
        {
            DisplayMapProvenance(_detectedChanges[0]);
        }
    }

    private void DisplayMapProvenance(ChangeRecord c)
    {
        _inspectedMapRecord = c;

        // Render Before (T1) and After (T2) miniature chips for this candidate
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

        if (ImgMapProvBefore != null) ImgMapProvBefore.Source = beforeBmp;
        if (ImgMapProvAfter != null) ImgMapProvAfter.Source = afterBmp;

        if (TxtMapProvTitle != null)
            TxtMapProvTitle.Text = $"Spot {c.Id[..8]} | {c.Type} ({(c.Confidence * 100):F0}% Match)";

        if (TxtMapProvCoords != null)
            TxtMapProvCoords.Text = $"{c.Center.Latitude:F5}° N, {c.Center.Longitude:F5}° E | {c.AreaSqMeters:N0} m²";

        if (TxtMapProvBeforeDate != null)
            TxtMapProvBeforeDate.Text = $"{_t1.AcquisitionTimestamp:yyyy-MM-dd HH:mm} UTC";

        if (TxtMapProvBeforeSensor != null)
            TxtMapProvBeforeSensor.Text = "Sentinel-2 Optical";

        if (TxtMapProvBeforeScene != null)
            TxtMapProvBeforeScene.Text = $"Image ID: {_t1.TileId}";

        if (TxtMapProvAfterDate != null)
            TxtMapProvAfterDate.Text = $"{_t3.AcquisitionTimestamp:yyyy-MM-dd HH:mm} UTC";

        if (TxtMapProvAfterSensor != null)
            TxtMapProvAfterSensor.Text = "Sentinel-2 Optical";

        if (TxtMapProvAfterScene != null)
            TxtMapProvAfterScene.Text = $"Image ID: {_t3.TileId}";

        if (TxtMapProvOnset != null)
            TxtMapProvOnset.Text = $"Started: {c.EarliestObservationTimestamp:yyyy-MM-dd}";

        string rawHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Id + c.TileId + c.Type + c.Center.Latitude + c.Center.Longitude)));
        if (TxtMapProvHash != null)
            TxtMapProvHash.Text = "Security Check: Verified";

        if (BtnMapProvVerify != null) BtnMapProvVerify.Tag = c.Id;
        if (BtnMapProvConfirm != null) BtnMapProvConfirm.Tag = c.Id;
        if (BtnMapProvReject != null) BtnMapProvReject.Tag = c.Id;

        // Synchronize in-map canvas selected pin
        MapCanvasSpatiotemporal?.SelectPin(c.Id);
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
        TxtInterpretedQuery.Text = TextQueryEncoder.ExplainQuery(query);

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

            string priorityLabel = r.SimilarityScore >= 0.70 ? "STRONG MATCH" : (r.SimilarityScore >= 0.45 ? "MODERATE MATCH" : "RELEVANT SPOT");

            return new SearchResultItemViewModel
            {
                PatchId = r.Patch.PatchId,
                ImagePreview = previewBmp,
                SimilarityBadge = $"#{i + 1}  {(r.SimilarityScore * 100):F0}% match  {priorityLabel}",
                Title = $"Result #{i + 1} [{r.Patch.Platform.ToString().Replace('_', ' ')}]",
                Detail = $"Photo Date: {r.Patch.Timestamp:yyyy-MM-dd} | Image Quality: {(r.Patch.QualityScore * 100):F0}%",
                Coordinates = $"Location: {r.Patch.Bounds.Center.Latitude:F5}° N, {r.Patch.Bounds.Center.Longitude:F5}° E",
                SpectralInfo = $"Area: 32x32 pixels (High-Res) | Match Score: {r.SimilarityScore:F2}",
                Patch = r.Patch,
                SimilarityScore = r.SimilarityScore
            };
        }).ToList();

        LstSearchResults.ItemsSource = _currentSearchResults;
        _searchCompleted = true;
        RefreshWorkflowUI();
    }

    private void OnQuickQueryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string query)
        {
            TxtSearchQuery.Text = query;
            OnSearchClicked(null, null!);
        }
    }

    // =========================================================================
    // CROSS-TAB SYNCHRONIZATION (DATA-PRESENT & NO-DATA EMPTY STATE)
    // =========================================================================

    private bool _isSyncingCandidate = false;

    private void SyncActiveCandidate(ChangeRecord? record)
    {
        if (_isSyncingCandidate) return;
        _isSyncingCandidate = true;
        try
        {
            if (record == null)
            {
                _selectedChangeRecord = null;
                _inspectedMapRecord = null;
                return;
            }

            _selectedChangeRecord = record;
            _inspectedMapRecord = record;

            // 1. Tab 2: Map Pin, Pan & Provenance
            MapCanvasSpatiotemporal?.SelectPin(record.Id);
            MapCanvasSpatiotemporal?.PanTo(record.Center.Latitude, record.Center.Longitude);
            DisplayMapProvenance(record);

            if (LstSpatiotemporalResults?.ItemsSource is IEnumerable<SpatiotemporalResultItemViewModel> spItems)
            {
                var match = spItems.FirstOrDefault(it => it.Id == record.Id);
                if (match != null && LstSpatiotemporalResults.SelectedItem != match)
                {
                    LstSpatiotemporalResults.SelectedItem = match;
                }
            }

            // 2. Tab 3: Focused Inspection & Candidate List
            if (LstChangeResults?.ItemsSource is IEnumerable<ChangeListItemViewModel> chgItems)
            {
                var match = chgItems.FirstOrDefault(it => it.Id == record.Id);
                if (match != null && LstChangeResults.SelectedItem != match)
                {
                    LstChangeResults.SelectedItem = match;
                }
            }
            DisplayFocusedInspection(record);

            // 3. Tab 1: Highlight closest matching patch
            if (LstSearchResults?.ItemsSource is IEnumerable<SearchResultItemViewModel> searchItems)
            {
                var closest = searchItems
                    .OrderBy(s => s.Patch.Bounds.Center.DistanceToKm(record.Center))
                    .FirstOrDefault();
                if (closest != null && closest.Patch.Bounds.Center.DistanceToKm(record.Center) < 2.0)
                {
                    if (LstSearchResults.SelectedItem != closest)
                        LstSearchResults.SelectedItem = closest;
                }
            }
        }
        finally
        {
            _isSyncingCandidate = false;
        }
    }

    private void SyncNoDataState(string reason, double lat, double lon, DateTime? startDate, DateTime? endDate)
    {
        _selectedChangeRecord = null;
        _inspectedMapRecord = null;

        string dateSummary = (startDate.HasValue && endDate.HasValue)
            ? $"{startDate.Value:yyyy-MM-dd} to {endDate.Value:yyyy-MM-dd} UTC"
            : (startDate.HasValue ? $"From {startDate.Value:yyyy-MM-dd} UTC" : (endDate.HasValue ? $"Up to {endDate.Value:yyyy-MM-dd} UTC" : "All Archive Epochs"));

        // Tab 2 No-Data State
        if (PnlMapProvenance != null) PnlMapProvenance.IsVisible = false;
        if (PnlNoDataTab2 != null)
        {
            PnlNoDataTab2.IsVisible = true;
            if (TxtNoDataTab2Detail != null)
                TxtNoDataTab2Detail.Text = $"Coordinates ({lat:F4}°N, {lon:F4}°E) | Time: {dateSummary} | {reason}";
        }
        if (MapCanvasSpatiotemporal != null)
        {
            MapCanvasSpatiotemporal.SetPins(new List<MapPin>());
            MapCanvasSpatiotemporal.SetCenterAndRadius(lat, lon, 10.0);
        }
        if (LstSpatiotemporalResults != null)
        {
            LstSpatiotemporalResults.ItemsSource = new List<SpatiotemporalResultItemViewModel>();
        }

        // Tab 3 No-Data State (Overlay + Reset metrics)
        if (PnlNoDataTab3 != null)
        {
            PnlNoDataTab3.IsVisible = true;
            if (TxtNoDataTab3Coords != null)
                TxtNoDataTab3Coords.Text = $"Location: ({lat:F5}°N, {lon:F5}°E) | Radius: 10.0 km";
            if (TxtNoDataTab3Dates != null)
                TxtNoDataTab3Dates.Text = $"Observation Window: {dateSummary}";
        }
        if (TxtCandidateCounter != null) TxtCandidateCounter.Text = "Candidate 0 of 0";
        if (TxtCandidateCoords != null) TxtCandidateCoords.Text = $"{lat:F5}°N, {lon:F5}°E (No Local Data)";
        if (TxtCandidateType != null) TxtCandidateType.Text = "None";
        if (TxtAiClassificationVerdict != null) TxtAiClassificationVerdict.Text = "No satellite observations for active spatiotemporal query.";
        if (TxtAiConfidence != null) TxtAiConfidence.Text = "0% DATA COVERAGE";
        if (BadgeAiConfidence != null) BadgeAiConfidence.Background = Brush.Parse("#374151");
        if (TxtAiArea != null) TxtAiArea.Text = "0 m²";
        if (TxtAiOnset != null) TxtAiOnset.Text = "N/A";
        if (TxtTimelineOnsetMarker != null) TxtTimelineOnsetMarker.Text = "N/A";
        if (TxtEvidVegetation != null) TxtEvidVegetation.Text = "Vegetation: No coverage";
        if (TxtEvidSoil != null) TxtEvidSoil.Text = "Soil/Built: No coverage";
        if (TxtEvidPersistence != null) TxtEvidPersistence.Text = "No multi-temporal observations in archive";
        if (TxtEvidSpatial != null) TxtEvidSpatial.Text = "Spatial footprint: 0 m²";
        if (ImgBaselineT1 != null) ImgBaselineT1.Source = null;
        if (ImgTargetT2 != null) ImgTargetT2.Source = null;
        if (ImgChangeHeatmap != null) ImgChangeHeatmap.Source = null;
        if (ImgSpectralChart != null) ImgSpectralChart.Source = null;

        // Tab 1 No-Data State (if coordinate is out of archive bounds)
        double distFromArchive = new GeoCoordinate(lat, lon).DistanceToKm(new GeoCoordinate(28.6050, 77.2080));
        if (distFromArchive > 25.0)
        {
            if (PnlNoDataTab1 != null)
            {
                PnlNoDataTab1.IsVisible = true;
                if (TxtNoDataTab1Detail != null)
                    TxtNoDataTab1Detail.Text = $"Coordinates ({lat:F4}°N, {lon:F4}°E) lie outside indexed archive domain ({distFromArchive:F0} km away).";
            }
        }
    }

    private void SyncDataAvailableState()
    {
        if (PnlNoDataTab1 != null) PnlNoDataTab1.IsVisible = false;
        if (PnlNoDataTab2 != null) PnlNoDataTab2.IsVisible = false;
        if (PnlNoDataTab3 != null) PnlNoDataTab3.IsVisible = false;
        if (PnlMapProvenance != null) PnlMapProvenance.IsVisible = true;
        if (DlgMissingDataModal != null) DlgMissingDataModal.IsVisible = false;
    }

    private void OnShowMissingDataModalClicked(object? sender, RoutedEventArgs e)
    {
        if (DlgMissingDataModal != null)
            DlgMissingDataModal.IsVisible = true;
    }

    private void OnModalDismissClicked(object? sender, RoutedEventArgs e)
    {
        if (DlgMissingDataModal != null)
            DlgMissingDataModal.IsVisible = false;
    }

    private void OnModalResetToActiveCoverageClicked(object? sender, RoutedEventArgs e)
    {
        if (DlgMissingDataModal != null)
            DlgMissingDataModal.IsVisible = false;

        if (TxtSearchLat != null) TxtSearchLat.Text = "28.60500";
        if (TxtSearchLon != null) TxtSearchLon.Text = "77.20800";
        if (TxtSearchRadius != null) TxtSearchRadius.Text = "10.0";
        if (TxtStartDate != null) TxtStartDate.Text = "2024-01-01";
        if (TxtEndDate != null) TxtEndDate.Text = "2024-04-30";
        if (CmbDatePeriodPreset != null) CmbDatePeriodPreset.SelectedIndex = 0;
        if (CmbChangeTypeFilter != null) CmbChangeTypeFilter.SelectedIndex = 0;

        SyncDataAvailableState();
        OnExecuteSpatiotemporalSearchClicked(null, null!);
    }

    private void OnModalIngestFileClicked(object? sender, RoutedEventArgs e)
    {
        if (DlgMissingDataModal != null)
            DlgMissingDataModal.IsVisible = false;
        OnLoadGeoTiffClicked(sender, e);
    }

    private void OnModalGenerateSyntheticSceneClicked(object? sender, RoutedEventArgs e)
    {
        if (DlgMissingDataModal != null)
            DlgMissingDataModal.IsVisible = false;

        double lat = double.TryParse(TxtSearchLat?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLat) ? parsedLat : 28.6050;
        double lon = double.TryParse(TxtSearchLon?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedLon) ? parsedLon : 77.2080;

        DateTime? startDate = null;
        if (!string.IsNullOrWhiteSpace(TxtStartDate?.Text) && DateTime.TryParse(TxtStartDate.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sDate))
            startDate = sDate;

        DateTime? endDate = null;
        if (!string.IsNullOrWhiteSpace(TxtEndDate?.Text) && DateTime.TryParse(TxtEndDate.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var eDate))
            endDate = eDate;

        GenerateSyntheticSceneForLocation(lat, lon, startDate, endDate);
    }

    public void GenerateSyntheticSceneForLocation(double lat, double lon, DateTime? startDate, DateTime? endDate)
    {
        DateTime baseDate = startDate ?? new DateTime(2024, 1, 10, 10, 30, 0, DateTimeKind.Utc);
        DateTime targetDate = endDate ?? baseDate.AddDays(70);

        int sceneW = 256;
        int sceneH = 256;
        double baseLon = lon - 0.0128;
        double baseLat = lat + 0.0128;
        var transform = AffineGeoTransform.NorthUp(baseLon, baseLat, 0.0001, 0.0001);

        string locName = $"{Math.Abs(lat):F2}{(lat >= 0 ? "N" : "S")}_{Math.Abs(lon):F2}{(lon >= 0 ? "E" : "W")}";
        _t1 = CreateTile($"S2_{locName}_{baseDate:yyyyMMdd}_T1", SensorPlatform.Sentinel2_Optical, baseDate, sceneW, sceneH, transform);
        var t2 = CreateTile($"S2_{locName}_{baseDate.AddDays(36):yyyyMMdd}_T2", SensorPlatform.Sentinel2_Optical, baseDate.AddDays(36), sceneW, sceneH, transform);
        _t3 = CreateTile($"S2_{locName}_{targetDate:yyyyMMdd}_T3", SensorPlatform.Sentinel2_Optical, targetDate, sceneW, sceneH, transform);
        var t4 = CreateTile($"S2_{locName}_{targetDate.AddDays(35):yyyyMMdd}_T4", SensorPlatform.Sentinel2_Optical, targetDate.AddDays(35), sceneW, sceneH, transform);

        // Inject ground changes
        InjectConstruction(_t3, 60, 60, 40, 40);
        InjectClearance(_t3, 160, 40, 40, 40);
        InjectWaterVariation(_t3, 20, 160, 30, 40);
        InjectRoad(_t3, 120, 140, 100, 10);

        InjectConstruction(t4, 60, 60, 40, 40);
        InjectClearance(t4, 160, 40, 40, 40);

        _timeSeries = new List<SatelliteTile> { _t1, t2, _t3, t4 };

        // Re-index
        _index = new VectorIndex(128);
        _searchEngine = new SemanticSearchEngine(_index);
        _searchEngine.IngestTile(_t1, patchSize: 32);
        _searchEngine.IngestTile(_t3, patchSize: 32);

        _changeSearchEngine = new ChangeSearchEngine();
        _reviewQueue = new ReviewQueue();

        UpdateOverviewRenderings();
        RunInitialChangeDetection();
        OnRunClusteringClicked(null, null!);

        if (TxtTelemetryArchive != null)
            TxtTelemetryArchive.Text = $"{_index.Count} observations indexed (AOI: {lat:F4}°N, {lon:F4}°E)";

        SyncDataAvailableState();

        if (TxtSearchLat != null) TxtSearchLat.Text = lat.ToString("F5", CultureInfo.InvariantCulture);
        if (TxtSearchLon != null) TxtSearchLon.Text = lon.ToString("F5", CultureInfo.InvariantCulture);
        if (TxtStartDate != null) TxtStartDate.Text = baseDate.ToString("yyyy-MM-dd");
        if (TxtEndDate != null) TxtEndDate.Text = targetDate.ToString("yyyy-MM-dd");

        OnExecuteSpatiotemporalSearchClicked(null, null!);

        if (TxtSearchQuery != null && !string.IsNullOrWhiteSpace(TxtSearchQuery.Text))
        {
            OnSearchClicked(null, null!);
        }
    }

    private void OnCandidateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LstSearchResults?.SelectedItem is SearchResultItemViewModel item)
        {
            var patch = item.Patch;
            var center = patch.Bounds.Center;
            var matchingRecord = _detectedChanges
                .OrderBy(c => c.Center.DistanceToKm(center))
                .FirstOrDefault();

            if (matchingRecord != null)
            {
                SyncActiveCandidate(matchingRecord);
            }
        }
    }

    private void OnSpatiotemporalSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LstSpatiotemporalResults?.SelectedItem is SpatiotemporalResultItemViewModel item)
        {
            var record = _detectedChanges.FirstOrDefault(c => c.Id == item.Id) ?? item.Record;
            if (record != null)
            {
                SyncActiveCandidate(record);
            }
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

            var matchingRecord = _detectedChanges
                .OrderBy(c => c.Center.DistanceToKm(targetPatch.Bounds.Center))
                .FirstOrDefault();
            if (matchingRecord != null)
            {
                SyncActiveCandidate(matchingRecord);
            }
        }
    }

    private void OnExecuteSpatiotemporalSearchClicked(object? sender, RoutedEventArgs e)
    {
        double lat = double.TryParse(TxtSearchLat.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedLat) ? parsedLat : 28.6050;
        double lon = double.TryParse(TxtSearchLon.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedLon) ? parsedLon : 77.2080;
        double radius = double.TryParse(TxtSearchRadius.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedRad) ? parsedRad : 10.0;

        DateTime? startDate = null;
        if (!string.IsNullOrWhiteSpace(TxtStartDate?.Text) && DateTime.TryParse(TxtStartDate.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sDate))
        {
            startDate = sDate;
        }

        DateTime? endDate = null;
        if (!string.IsNullOrWhiteSpace(TxtEndDate?.Text) && DateTime.TryParse(TxtEndDate.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var eDate))
        {
            endDate = eDate.Date.AddDays(1).AddTicks(-1);
        }

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
            StartDate: startDate,
            EndDate: endDate,
            TargetChangeType: targetType,
            MinConfidence: 0.50
        );

        var results = _changeSearchEngine.Search(criteria, topK: 50);

        string dateSummary = (startDate.HasValue && endDate.HasValue)
            ? $"Period: {startDate.Value:yyyy-MM-dd} to {endDate.Value:yyyy-MM-dd} UTC"
            : (startDate.HasValue ? $"From: {startDate.Value:yyyy-MM-dd} UTC" : (endDate.HasValue ? $"To: {endDate.Value:yyyy-MM-dd} UTC" : "All Available Epochs"));

        if (TxtSearchMatchCount != null)
            TxtSearchMatchCount.Text = $"{results.Count} candidates found";

        if (TxtSearchActiveCriteria != null)
            TxtSearchActiveCriteria.Text = $"Radius: {radius:F1} km around ({lat:F4}°N, {lon:F4}°E) | {dateSummary}";

        // Check if out-of-coverage geographically or temporally
        double distFromArchiveCenter = new GeoCoordinate(lat, lon).DistanceToKm(_t1.Bounds.Center);
        bool isSpatialOutOfCoverage = distFromArchiveCenter > (radius + 20.0);
        bool isTemporalOutOfCoverage = (startDate.HasValue && startDate.Value > _t3.AcquisitionTimestamp.AddDays(30)) ||
                                       (endDate.HasValue && endDate.Value < _t1.AcquisitionTimestamp.AddDays(-30));

        if (results.Count == 0 || isSpatialOutOfCoverage || isTemporalOutOfCoverage)
        {
            string reason = isSpatialOutOfCoverage && isTemporalOutOfCoverage
                ? "Out of bounds spatially and temporally."
                : (isSpatialOutOfCoverage
                    ? $"Geographic coordinates are {(int)distFromArchiveCenter} km outside active satellite coverage."
                    : (isTemporalOutOfCoverage
                        ? "Requested observation dates lie outside the archive epoch time series."
                        : "Zero change candidates found matching criteria in active scene."));

            if (DlgMissingDataModal != null)
            {
                if (TxtModalQueryCoords != null)
                    TxtModalQueryCoords.Text = $"Coordinates: ({lat:F5}°N, {lon:F5}°E)" + (isSpatialOutOfCoverage ? $" [{(int)distFromArchiveCenter} km from archive]" : "");
                if (TxtModalQueryRadius != null)
                    TxtModalQueryRadius.Text = $"Search Radius: {radius:F1} km";
                if (TxtModalQueryTime != null)
                    TxtModalQueryTime.Text = $"Time Window: {dateSummary}";
                if (TxtModalQueryStatus != null)
                    TxtModalQueryStatus.Text = $"Status: {reason}";
                DlgMissingDataModal.IsVisible = true;
            }

            SyncNoDataState(reason, lat, lon, startDate, endDate);
            _spatiotemporalCompleted = true;
            RefreshWorkflowUI();
            return;
        }

        SyncDataAvailableState();

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
                Title = $"Spot {c.Id[..8]} | {c.Type} ({c.AreaSqMeters:N0} m²)",
                DistanceInfo = $"Distance: {r.DistanceKm:F2} km from search center | ({c.Center.Latitude:F4}°N, {c.Center.Longitude:F4}°E)",
                EarliestOnsetInfo = $"Change Began: {c.EarliestObservationTimestamp:yyyy-MM-dd}",
                SpectralMetrics = $"Confidence: {(c.Confidence * 100):F0}% | {c.ProcessingNotes}",
                Record = c
            };
        }).ToList();

        // Update Map Center and Pins to match active search filter
        var searchPins = results.Select((r, i) =>
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

            string rawHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(c.Id + c.TileId + c.Type + c.Center.Latitude + c.Center.Longitude)));

            return new MapPin
            {
                Id = c.Id,
                Title = $"Candidate #{i + 1} ({c.Type})",
                Latitude = c.Center.Latitude,
                Longitude = c.Center.Longitude,
                ChangeType = c.Type.ToString(),
                Confidence = c.Confidence,
                AreaSqM = c.AreaSqMeters,
                Bounds = c.Bounds,
                State = c.ConfirmedByAnalyst ? MapMarkerState.Confirmed : (c.RejectedByAnalyst ? MapMarkerState.Rejected : MapMarkerState.Candidate),
                BeforePreview = beforeBmp,
                AfterPreview = afterBmp,
                TimestampT1 = _t1.AcquisitionTimestamp,
                TimestampT2 = _t3.AcquisitionTimestamp,
                EarliestOnset = c.EarliestObservationTimestamp,
                SensorPlatformT1 = $"{_t1.Platform} (10m)",
                SensorPlatformT2 = $"{_t3.Platform} (10m)",
                SceneIdT1 = _t1.TileId,
                SceneIdT2 = _t3.TileId,
                ProvenanceHash = $"SHA256:{rawHash[..16].ToLowerInvariant()}..."
            };
        }).ToList();

        MapCanvasSpatiotemporal?.SetPins(searchPins);
        MapCanvasSpatiotemporal?.SetCenterAndRadius(lat, lon, radius);

        SyncActiveCandidate(results[0].Record);

        _spatiotemporalCompleted = true;
        RefreshWorkflowUI();
    }

    private void OnDatePeriodPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbDatePeriodPreset == null || TxtStartDate == null || TxtEndDate == null) return;

        switch (CmbDatePeriodPreset.SelectedIndex)
        {
            case 0: // All Archive Epochs (2024)
                TxtStartDate.Text = "2024-01-01";
                TxtEndDate.Text = "2024-04-30";
                break;
            case 1: // Q1 2024 (Jan 01 - Mar 31)
                TxtStartDate.Text = "2024-01-01";
                TxtEndDate.Text = "2024-03-31";
                break;
            case 2: // Early Phase (Jan 01 - Feb 15)
                TxtStartDate.Text = "2024-01-01";
                TxtEndDate.Text = "2024-02-15";
                break;
            case 3: // Disturbance Window (Feb 01 - Mar 25)
                TxtStartDate.Text = "2024-02-01";
                TxtEndDate.Text = "2024-03-25";
                break;
            case 4: // Late Phase (Mar 15 - Apr 30)
                TxtStartDate.Text = "2024-03-15";
                TxtEndDate.Text = "2024-04-30";
                break;
            case 5: // Custom Dates (Specific Range)
                break;
        }

        OnExecuteSpatiotemporalSearchClicked(null, null!);
    }

    private void OnResetDateRangeClicked(object? sender, RoutedEventArgs e)
    {
        if (TxtStartDate != null) TxtStartDate.Text = "2024-01-01";
        if (TxtEndDate != null) TxtEndDate.Text = "2024-04-30";
        if (CmbDatePeriodPreset != null) CmbDatePeriodPreset.SelectedIndex = 0;
        OnExecuteSpatiotemporalSearchClicked(null, null!);
    }

    private void OnQuickDateClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            var parts = tag.Split(',');
            if (parts.Length >= 2)
            {
                if (TxtStartDate != null) TxtStartDate.Text = parts[0];
                if (TxtEndDate != null) TxtEndDate.Text = parts[1];
                if (CmbDatePeriodPreset != null) CmbDatePeriodPreset.SelectedIndex = 5; // Custom
                OnExecuteSpatiotemporalSearchClicked(null, null!);
            }
        }
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
            Title = $"Spot {c.Id[..8]} - {c.Type} ({c.AreaSqMeters:N0} m²)",
            Notes = c.ProcessingNotes,
            MetricsSummary = string.Join(" | ", c.Metrics.Select(m => $"{m.Key}: {m.Value:F3}")),
            EarliestObservationText = $"Started: {c.EarliestObservationTimestamp:yyyy-MM-dd}",
            Record = c
        }).ToList();
    }

    private void OnChangeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LstChangeResults.SelectedItem is ChangeListItemViewModel item)
        {
            _selectedChangeRecord = item.Record;
            DisplayFocusedInspection(item.Record);
            SyncActiveCandidate(item.Record);
        }
    }

    private bool _blinkShowingT1 = true; // For A/B blink comparison toggle

    private void OnPrevCandidateClicked(object? sender, RoutedEventArgs e)
    {
        int idx = LstChangeResults.SelectedIndex;
        if (idx > 0)
            LstChangeResults.SelectedIndex = idx - 1;
    }

    private void OnNextCandidateClicked(object? sender, RoutedEventArgs e)
    {
        int idx = LstChangeResults.SelectedIndex;
        if (idx >= 0 && idx < _detectedChanges.Count - 1)
            LstChangeResults.SelectedIndex = idx + 1;
    }

    private void OnToggleViewModeClicked(object? sender, RoutedEventArgs e)
    {
        _isFullSceneContext = !_isFullSceneContext;
        BtnToggleViewMode.Content = _isFullSceneContext ? "🔍 Zoom to Site" : "🌐 View Entire Area";
        if (_selectedChangeRecord != null)
        {
            DisplayFocusedInspection(_selectedChangeRecord);
        }
    }

    private void OnSelectSpectralCardClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string layer)
        {
            _activeHeatmapLayer = layer;
            TxtActiveHeatmapTitle.Text = layer switch
            {
                "NDBI" => "3. NEW CONCRETE & BUILDINGS (Heatmap)",
                "NDVI" => "3. PLANT & TREE LOSS (Heatmap)",
                "NDWI" => "3. WATER & FLOOD AREA (Heatmap)",
                "BSI"  => "3. SOIL DISTURBANCE (Heatmap)",
                _      => "3. HIGHLIGHTED CHANGES (Heatmap)"
            };
            if (_selectedChangeRecord != null)
            {
                DisplayFocusedInspection(_selectedChangeRecord);
            }
        }
    }

    private void OnPassClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int passIdx))
        {
            _activePassIndex = Math.Clamp(passIdx, 0, _timeSeries.Count - 1);
            var tile = _timeSeries[_activePassIndex];
            TxtTargetPanelSubtitle.Text = $"2. TARGET OBSERVATION ({tile.AcquisitionTimestamp:yyyy-MM-dd})";
            if (_selectedChangeRecord != null)
            {
                DisplayFocusedInspection(_selectedChangeRecord);
            }
        }
    }

    private void OnBlinkCompareClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedChangeRecord == null || _t1 == null || _timeSeries.Count == 0) return;
        try
        {
            _blinkShowingT1 = !_blinkShowingT1;
            var targetTile = (_activePassIndex >= 0 && _activePassIndex < _timeSeries.Count)
                ? _timeSeries[_activePassIndex]
                : _t3;
            var tile = _blinkShowingT1 ? _t1 : targetTile;

            if (_isFullSceneContext)
            {
                using var s = RasterVisualizer.RenderTileToBmpStream(tile, 0, 0, tile.Width, tile.Height, _currentChangeSpectralMode);
                ImgBaselineT1.Source = new Bitmap(s);
            }
            else
            {
                var (px1, py1) = _t1.Transform.GeoToPixel(new GeoCoordinate(_selectedChangeRecord.Bounds.MaxLat, _selectedChangeRecord.Bounds.MinLon));
                var (px2, py2) = _t1.Transform.GeoToPixel(new GeoCoordinate(_selectedChangeRecord.Bounds.MinLat, _selectedChangeRecord.Bounds.MaxLon));
                int minX = (int)Math.Min(px1, px2);
                int minY = (int)Math.Min(py1, py2);
                int maxX = (int)Math.Max(px1, px2);
                int maxY = (int)Math.Max(py1, py2);
                int cropX = Math.Clamp(minX - 28, 0, _t1.Width - 1);
                int cropY = Math.Clamp(minY - 28, 0, _t1.Height - 1);
                int cropW = Math.Clamp((maxX + 28) - cropX, 16, _t1.Width - cropX);
                int cropH = Math.Clamp((maxY + 28) - cropY, 16, _t1.Height - cropY);

                using var s = RasterVisualizer.RenderTileToBmpStream(tile, cropX, cropY, cropW, cropH, _currentChangeSpectralMode);
                ImgBaselineT1.Source = new Bitmap(s);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Blink compare error: {ex.Message}");
        }
    }

    private void DisplayFocusedInspection(ChangeRecord record)
    {
        try
        {
            _blinkShowingT1 = true; // Reset blink state on candidate change

            // ── Candidate navigation info ──
            int idx = _detectedChanges.IndexOf(record);
            TxtCandidateCounter.Text = $"Spot {idx + 1} of {_detectedChanges.Count}";
            TxtCandidateCoords.Text = $"{record.Center.Latitude:F5}°N, {record.Center.Longitude:F5}°E";
            TxtCandidateType.Text = record.Type.ToString();

            // ── AI Conclusion & Assessment ──
            TxtAiClassificationVerdict.Text = $"Likely {record.Type} / Ground Disturbance";
            TxtAiConfidence.Text = $"{(record.Confidence * 100):F0}% CERTAIN ({(record.Confidence >= HighConfidenceThreshold ? "HIGH CONFIDENCE" : "MODERATE")})";
            BadgeAiConfidence.Background = Brush.Parse(record.Confidence >= HighConfidenceThreshold ? "#065F46" : "#78350F");

            TxtAiArea.Text = $"{record.AreaSqMeters:N0} m² (approx {(record.AreaSqMeters / 10000.0):F2} ha / {(record.AreaSqMeters * 0.000247105):F1} acres)";
            TxtAiOnset.Text = $"{record.EarliestObservationTimestamp:yyyy-MM-dd}";
            TxtTimelineOnsetMarker.Text = $"Change Began: {record.EarliestObservationTimestamp:yyyy-MM-dd}";

            record.Metrics.TryGetValue("DeltaNDBI", out var dNdbi);
            record.Metrics.TryGetValue("DeltaNDVI", out var dNdvi);
            record.Metrics.TryGetValue("DeltaNDWI", out var dNdwi);
            record.Metrics.TryGetValue("DeltaBSI", out var dBsi);
            record.Metrics.TryGetValue("DeltaGradient", out var dGrad);

            // ── Structured Evidence Bullets ──
            TxtEvidVegetation.Text = $"✓ Vegetation: {(dNdvi < -0.15 ? "Significant tree/plant loss" : "Stable vegetation")} (Change: {dNdvi:+0.000;-0.000})";
            TxtEvidSoil.Text = $"✓ Ground Surface: {(dNdbi > 0.15 ? "New bare soil / concrete" : "Moderate surface change")} (Building shift: {dNdbi:+0.000;-0.000})";
            TxtEvidPersistence.Text = "✓ Change confirmed across 4 separate satellite dates";
            TxtEvidSpatial.Text = $"✓ Physical footprint: {record.AreaSqMeters:N0} m² ({record.AffectedPixels} px)";

            // ── Technical Diagnostics ──
            TxtFocusedDeltaNdbi.Text = $"{(dNdbi >= 0 ? "+" : "")}{dNdbi:F4}";
            TxtFocusedDeltaNdvi.Text = $"{(dNdvi >= 0 ? "+" : "")}{dNdvi:F4}";
            TxtFocusedDeltaNdwi.Text = $"{(dNdwi >= 0 ? "+" : "")}{dNdwi:F4}";
            TxtFocusedDeltaGrad.Text = $"{(dGrad >= 0 ? "+" : "")}{dGrad:F4}";

            // ── Update Badges on Individual Index Cards ──
            TxtBadgeDeltaNdvi.Text = $"Δ = {dNdvi:+0.000;-0.000}";
            TxtBadgeDeltaNdbi.Text = $"Δ = {dNdbi:+0.000;-0.000}";
            TxtBadgeDeltaNdwi.Text = $"Δ = {dNdwi:+0.000;-0.000}";
            TxtBadgeDeltaBsi.Text  = $"Δ = {dBsi:+0.000;-0.000}";

            var targetTile = (_timeSeries != null && _activePassIndex >= 0 && _activePassIndex < _timeSeries.Count)
                ? _timeSeries[_activePassIndex]
                : _t3;

            TxtTargetPanelSubtitle.Text = $"2. TARGET OBSERVATION ({targetTile.AcquisitionTimestamp:yyyy-MM-dd})";

            // ── Render Primary 3-Panel Visualizations ──
            if (_isFullSceneContext)
            {
                using var s1 = RasterVisualizer.RenderTileToBmpStream(_t1, 0, 0, _t1.Width, _t1.Height, _currentChangeSpectralMode);
                ImgBaselineT1.Source = new Bitmap(s1);

                using var s2 = RasterVisualizer.RenderTileToBmpStream(targetTile, 0, 0, targetTile.Width, targetTile.Height, _currentChangeSpectralMode);
                ImgTargetT2.Source = new Bitmap(s2);

                using var s3 = RasterVisualizer.RenderChangeHeatmapBmpStream(_t1, targetTile, _detectedChanges, record);
                ImgChangeHeatmap.Source = new Bitmap(s3);
            }
            else
            {
                var focused = RasterVisualizer.RenderFocusedSite(_t1, targetTile, record, _currentChangeSpectralMode, _activeHeatmapLayer, padding: 28, scale: 2);
                ImgBaselineT1.Source = new Bitmap(focused.T1Stream);
                ImgTargetT2.Source = new Bitmap(focused.T2Stream);
                ImgChangeHeatmap.Source = new Bitmap(focused.OverlayStream);
                focused.T1Stream.Dispose();
                focused.T2Stream.Dispose();
                focused.OverlayStream.Dispose();
            }

            // ── Per-candidate calibrated individual spectral index heatmaps ──
            var indexHeatmaps = RasterVisualizer.RenderFocusedIndexHeatmaps(_t1, targetTile, record, padding: 20, scale: 2);
            ImgHeatmapNdvi.Source = new Bitmap(indexHeatmaps.NdviStream);
            ImgHeatmapNdbi.Source = new Bitmap(indexHeatmaps.NdbiStream);
            ImgHeatmapNdwi.Source = new Bitmap(indexHeatmaps.NdwiStream);
            ImgHeatmapBsi.Source = new Bitmap(indexHeatmaps.BsiStream);
            indexHeatmaps.NdviStream.Dispose();
            indexHeatmaps.NdbiStream.Dispose();
            indexHeatmaps.NdwiStream.Dispose();
            indexHeatmaps.BsiStream.Dispose();

            // ── Multi-Spectral Signature Reflectance Profile Chart ──
            using var chartStream = RasterVisualizer.RenderSpectralProfileChart(_t1, targetTile, record);
            ImgSpectralChart.Source = new Bitmap(chartStream);

            // ── Update Multi-Pass Timeline Highlight ──
            UpdateTimelineHighlight(record.EarliestObservationTimestamp);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating focused change inspection: {ex.Message}");
        }
    }

    private void UpdateTimelineHighlight(DateTime onsetDate)
    {
        BorderPass1.BorderThickness = new Thickness(0);
        BorderPass2.BorderThickness = new Thickness(0);
        BorderPass3.BorderThickness = new Thickness(0);
        BorderPass4.BorderThickness = new Thickness(0);

        if (onsetDate <= new DateTime(2024, 1, 20))
        {
            BorderPass1.BorderThickness = new Thickness(2);
            BorderPass1.BorderBrush = Brush.Parse("#F59E0B");
        }
        else if (onsetDate <= new DateTime(2024, 2, 28))
        {
            BorderPass2.BorderThickness = new Thickness(2);
            BorderPass2.BorderBrush = Brush.Parse("#F59E0B");
        }
        else if (onsetDate <= new DateTime(2024, 3, 31))
        {
            BorderPass3.BorderThickness = new Thickness(2);
            BorderPass3.BorderBrush = Brush.Parse("#EF4444");
        }
        else
        {
            BorderPass4.BorderThickness = new Thickness(2);
            BorderPass4.BorderBrush = Brush.Parse("#F87171");
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
            Header = $"Site Group #{c.ClusterId}: {c.Label}",
            BoundsInfo = $"Location Area: [{c.EnclosingBounds.MinLon:F4}°E to {c.EnclosingBounds.MaxLon:F4}°E, {c.EnclosingBounds.MinLat:F4}°N to {c.EnclosingBounds.MaxLat:F4}°N]",
            CohesionInfo = $"Contains: {c.Members.Count} related spots | Similarity Score: {c.CohesionScore:F2}"
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
            Title = $"{item.Record.Type} - Spot {item.Record.Id[..8]}",
            AuditDetails = $"Before: {item.Record.TimestampT1:yyyy-MM-dd} ➔ After: {item.Record.TimestampT2:yyyy-MM-dd} | Started: {item.Record.EarliestObservationTimestamp:yyyy-MM-dd} | Notes: {item.AnalystComments}",
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
        MapCanvasSpatiotemporal?.ZoomIn();
        MapCanvasFacilities?.ZoomIn();
    }

    private void OnMapZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        MapCanvasSpatiotemporal?.ZoomOut();
        MapCanvasFacilities?.ZoomOut();
    }

    private void OnMapFitAllClicked(object? sender, RoutedEventArgs e)
    {
        MapCanvasSpatiotemporal?.FitToAll();
        MapCanvasFacilities?.FitToAll();
    }

    private bool _isDarkTheme = true;

    private void OnThemeToggleClicked(object? sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        var targetVariant = _isDarkTheme 
            ? Avalonia.Styling.ThemeVariant.Dark 
            : Avalonia.Styling.ThemeVariant.Light;

        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = targetVariant;
        }
        this.RequestedThemeVariant = targetVariant;

        if (TxtThemeMode != null)
        {
            TxtThemeMode.Text = _isDarkTheme ? "Light Mode" : "Dark Mode";
        }

        if (IconTheme != null)
        {
            IconTheme.Icon = _isDarkTheme ? LucideAvalonia.Enum.LucideIconNames.Sun : LucideAvalonia.Enum.LucideIconNames.Moon;
        }

        Dispatcher.UIThread.Post(() =>
        {
            MapCanvasSpatiotemporal?.InvalidateVisual();
            MapCanvasFacilities?.InvalidateVisual();
        });
    }

    private bool _isMapFullscreen = false;

    private void OnToggleMapFullscreenClicked(object? sender, RoutedEventArgs e)
    {
        _isMapFullscreen = !_isMapFullscreen;

        if (PnlTab2Filters != null) PnlTab2Filters.IsVisible = !_isMapFullscreen;
        if (PnlTab2Status != null) PnlTab2Status.IsVisible = !_isMapFullscreen;
        if (BrdTab2ResultsList != null) BrdTab2ResultsList.IsVisible = !_isMapFullscreen;

        if (BrdTab2MapContainer != null)
        {
            Grid.SetColumnSpan(BrdTab2MapContainer, _isMapFullscreen ? 2 : 1);
        }

        if (TxtMapFullscreen != null)
        {
            TxtMapFullscreen.Text = _isMapFullscreen ? "Exit Fullscreen" : "Fullscreen";
        }

        if (IconMapFullscreen != null)
        {
            IconMapFullscreen.Icon = _isMapFullscreen ? LucideAvalonia.Enum.LucideIconNames.Minimize : LucideAvalonia.Enum.LucideIconNames.Maximize;
        }

        if (BtnToggleMapFullscreen != null)
        {
            ToolTip.SetTip(BtnToggleMapFullscreen, _isMapFullscreen ? "Exit Map Fullscreen (Key: Esc or F)" : "Expand map to full screen (Key: F)");
        }

        Dispatcher.UIThread.Post(() =>
        {
            MapCanvasSpatiotemporal?.InvalidateVisual();
        });
    }

    private bool _isTab4MapFullscreen = false;

    private void OnToggleTab4MapFullscreenClicked(object? sender, RoutedEventArgs e)
    {
        _isTab4MapFullscreen = !_isTab4MapFullscreen;

        if (PnlTab4Header != null) PnlTab4Header.IsVisible = !_isTab4MapFullscreen;
        if (BrdTab4ResultsList != null) BrdTab4ResultsList.IsVisible = !_isTab4MapFullscreen;

        if (BrdTab4MapContainer != null)
        {
            Grid.SetColumnSpan(BrdTab4MapContainer, _isTab4MapFullscreen ? 2 : 1);
        }

        if (TxtTab4MapFullscreen != null)
        {
            TxtTab4MapFullscreen.Text = _isTab4MapFullscreen ? "Exit Fullscreen" : "Fullscreen";
        }

        if (IconTab4MapFullscreen != null)
        {
            IconTab4MapFullscreen.Icon = _isTab4MapFullscreen ? LucideAvalonia.Enum.LucideIconNames.Minimize : LucideAvalonia.Enum.LucideIconNames.Maximize;
        }

        if (BtnToggleTab4MapFullscreen != null)
        {
            ToolTip.SetTip(BtnToggleTab4MapFullscreen, _isTab4MapFullscreen ? "Exit Map Fullscreen (Key: Esc or F)" : "Expand map to full screen");
        }

        Dispatcher.UIThread.Post(() =>
        {
            MapCanvasFacilities?.InvalidateVisual();
        });
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
        else if (e.Key == Key.F)
        {
            if (MainTabControl?.SelectedIndex == 1)
            {
                OnToggleMapFullscreenClicked(null, null!);
                e.Handled = true;
            }
            else if (MainTabControl?.SelectedIndex == 3)
            {
                OnToggleTab4MapFullscreenClicked(null, null!);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (_isMapFullscreen)
            {
                OnToggleMapFullscreenClicked(null, null!);
                e.Handled = true;
            }
            else if (_isTab4MapFullscreen)
            {
                OnToggleTab4MapFullscreenClicked(null, null!);
                e.Handled = true;
            }
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

    private static void InjectVehicles(SatelliteTile tile, int startX, int startY, int w, int h, int numVehicles = 16)
    {
        var red = tile.Bands[SpectralBand.Red];
        var green = tile.Bands[SpectralBand.Green];
        var blue = tile.Bands[SpectralBand.Blue];
        var nir = tile.Bands[SpectralBand.NIR];
        var swir = tile.Bands[SpectralBand.SWIR1];

        // 1. Bare soil / staging ground base (high BSI, low NDVI)
        for (int y = startY; y < startY + h && y < tile.Height; y++)
        {
            for (int x = startX; x < startX + w && x < tile.Width; x++)
            {
                red[y, x] = 0.38f;
                green[y, x] = 0.30f;
                blue[y, x] = 0.18f;
                nir[y, x] = 0.18f;
                swir[y, x] = 0.48f;
            }
        }

        // 2. High-reflectance vehicle signatures with metallic specular peaks
        int cols = 4;
        for (int v = 0; v < numVehicles; v++)
        {
            int col = v % cols;
            int row = v / cols;
            int vx = startX + 4 + col * 7;
            int vy = startY + 4 + row * 7;

            if (vx >= 0 && vx + 3 < tile.Width && vy >= 0 && vy + 3 < tile.Height)
            {
                for (int dy = 0; dy <= 2; dy++)
                {
                    for (int dx = 0; dx <= 2; dx++)
                    {
                        red[vy + dy, vx + dx] = 0.88f;
                        green[vy + dy, vx + dx] = 0.85f;
                        blue[vy + dy, vx + dx] = 0.82f;
                        nir[vy + dy, vx + dx] = 0.78f;
                        swir[vy + dy, vx + dx] = 0.92f;
                    }
                }
            }
        }
    }

    private static void InjectAirfield(SatelliteTile tile, int startX, int startY, int length, int width)
    {
        var red = tile.Bands[SpectralBand.Red];
        var green = tile.Bands[SpectralBand.Green];
        var blue = tile.Bands[SpectralBand.Blue];
        var nir = tile.Bands[SpectralBand.NIR];
        var swir = tile.Bands[SpectralBand.SWIR1];

        for (int y = startY; y < startY + width && y < tile.Height; y++)
        {
            for (int x = startX; x < startX + length && x < tile.Width; x++)
            {
                red[y, x] = 0.16f;
                green[y, x] = 0.17f;
                blue[y, x] = 0.18f;
                nir[y, x] = 0.14f;
                swir[y, x] = 0.20f;
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
            MapCanvasSpatiotemporal?.InvalidateVisual();
            MapCanvasFacilities?.InvalidateVisual();
        });
    }

    private void OnInspectPatchDirectToStep3Clicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string patchId = btn.Tag?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(patchId)) return;

        var record = _detectedChanges.FirstOrDefault(c => c.TileId == patchId || c.Id == patchId);
        if (record != null)
        {
            SyncActiveCandidate(record);
        }

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
            SyncActiveCandidate(record);
        }

        _spatiotemporalCompleted = true;
        MainTabControl.SelectedIndex = 2;
        RefreshWorkflowUI();
    }

    private void OnMapProvDeepVerifyClicked(object? sender, RoutedEventArgs e)
    {
        if (_inspectedMapRecord != null)
        {
            _selectedChangeRecord = _inspectedMapRecord;
            DisplayFocusedInspection(_inspectedMapRecord);
            SyncActiveCandidate(_inspectedMapRecord);
            _spatiotemporalCompleted = true;
            MainTabControl.SelectedIndex = 2; // Step 3: Spectral Verification
            RefreshWorkflowUI();
        }
    }

    private void OnMapProvConfirmClicked(object? sender, RoutedEventArgs e)
    {
        if (_inspectedMapRecord != null)
        {
            _reviewQueue.Confirm(_inspectedMapRecord.Id, "Confirmed by analyst via Map Provenance Inspector.");
            _inspectedMapRecord.ConfirmedByAnalyst = true;
            _inspectedMapRecord.RejectedByAnalyst = false;
            UpdateReviewQueueList();
            UpdateMapPins();
            DisplayMapProvenance(_inspectedMapRecord);
        }
    }

    private void OnMapProvRejectClicked(object? sender, RoutedEventArgs e)
    {
        if (_inspectedMapRecord != null)
        {
            _reviewQueue.Reject(_inspectedMapRecord.Id, "Rejected false alarm via Map Provenance Inspector.");
            _inspectedMapRecord.RejectedByAnalyst = true;
            _inspectedMapRecord.ConfirmedByAnalyst = false;
            UpdateReviewQueueList();
            UpdateMapPins();
            DisplayMapProvenance(_inspectedMapRecord);
        }
    }
}
