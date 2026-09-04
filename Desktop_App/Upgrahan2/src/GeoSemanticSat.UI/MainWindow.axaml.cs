using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Clustering;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Core.Workflow;
using GeoSemanticSat.Engine.Benchmark;
using GeoSemanticSat.Engine.Embeddings;
using GeoSemanticSat.Engine.Retrieval;

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

    // Workflow state flags
    private bool _searchCompleted = false;
    private bool _spatiotemporalCompleted = false;
    private bool _changeDetectionCompleted = false;
    private bool _clusteringCompleted = false;

    // UI‑bound properties (INotifyPropertyChanged is implemented in the generated partial class)
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    // UI‑bound badge count properties
    public int Step1Count => _searchResultsCount();
    public int Step2Count => _spatiotemporalResultsCount();
    public int Step3Count => _detectedChanges?.Count ?? 0; // Change detection results
    public int Step4Count => _clustersCount();
    public int Step5Count => _reviewQueue?.GetAll().Count ?? 0;

    private int _searchResultsCount()
    {
        // The search results are stored in the ItemsSource of LstSearchResults; count them lazily.
        return (LstSearchResults?.Items?.Count) ?? 0;
    }

    private int _spatiotemporalResultsCount()
    {
        return (LstSpatiotemporalResults?.Items?.Count) ?? 0;
    }

    private int _clustersCount()
    {
        return (LstClusters?.Items?.Count) ?? 0;
    }

    // Helper to evaluate if a step can be entered
    private bool CanProceedToStep(int step)
    {
        return step switch
        {
            0 => true, // Step 1 always reachable
            1 => _searchCompleted,
            2 => _spatiotemporalCompleted,
            3 => _changeDetectionCompleted,
            4 => _clusteringCompleted,
            _ => false,
        };
    }

    // Refresh button enablement and badge texts
    private void RefreshWorkflowUI()
    {
        // BtnStep2 is null while InitializeComponent() is still running; skip until fully loaded.
        if (BtnStep2 == null) return;

        // Buttons are named BtnStep1‑5; we update their IsEnabled property directly.
        BtnStep2.IsEnabled = CanProceedToStep(1);
        BtnStep3.IsEnabled = CanProceedToStep(2);
        BtnStep4.IsEnabled = CanProceedToStep(3);
        BtnStep5.IsEnabled = CanProceedToStep(4);
        // Update badge TextBlocks – they are bound to the properties above via DataContext = this.
        // Force Avalonia to refresh bindings.
        this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step1Count)));
        this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step2Count)));
        this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step3Count)));
        this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step4Count)));
        this.PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Step5Count)));
    }

    // Reset downstream data when an earlier step is changed
    private void ResetDownstreamSteps(int fromStep)
    {
        switch (fromStep)
        {
            case 0:
                // Reset everything after search
                _spatiotemporalCompleted = false;
                _detectedChanges.Clear();
                _changeDetectionCompleted = false;
                _reviewQueue.Clear();
                _clusteringCompleted = false;
                break;
            case 1:
                _changeDetectionCompleted = false;
                _detectedChanges.Clear();
                _reviewQueue.Clear();
                _clusteringCompleted = false;
                break;
            case 2:
                _clusteringCompleted = false;
                break;
        }
        RefreshWorkflowUI();
    }

    public MainWindow()
    {
        InitializeComponent();
        InitializeArchive();
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

        StatusText.Text = $"Status: Online | Indexed {_index.Count} Patches | {_detectedChanges.Count} Change Candidates Staged";
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

        if (_detectedChanges.Count > 0)
        {
            LstChangeResults.SelectedIndex = 0;
        }
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

    private void OnSearchClicked(object? sender, RoutedEventArgs e)
    {
        string query = TxtSearchQuery.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query)) return;

        int topK = (int)(NumTopK.Value ?? 10m);
        double minQuality = SldMinQuality.Value / 100.0;

        SensorPlatform? platformFilter = CmbSensorFilter.SelectedIndex switch
        {
            1 => SensorPlatform.Sentinel2_Optical,
            2 => SensorPlatform.Sentinel1_SAR,
            3 => SensorPlatform.Landsat8_9,
            4 => SensorPlatform.ISRO_Bhuvan,
            _ => null
        };

        var filter = new SearchFilter(Platform: platformFilter, MinQuality: minQuality);
        var results = _searchEngine.SearchByText(query, topK, filter);

        LstSearchResults.ItemsSource = results.Select((r, i) =>
        {
            var parentTile = r.Patch.ParentTileId == _t1.TileId ? _t1 : _t3;
            Bitmap? previewBmp = null;
            try
            {
                using var ms = RasterVisualizer.RenderTileToBmpStream(parentTile, r.Patch.PixelX, r.Patch.PixelY, r.Patch.PatchWidth, r.Patch.PatchHeight, _currentRenderMode);
                previewBmp = new Bitmap(ms);
            }
            catch { }

            return new
            {
                PatchId = r.Patch.PatchId,
                ImagePreview = previewBmp,
                SimilarityBadge = $"🎯 Match #{i + 1} • {(r.SimilarityScore * 100):F1}%",
                Title = $"Patch {r.Patch.PatchId} [{r.Patch.Platform.ToString().Replace('_', ' ')}]",
                Detail = $"📅 Acquisition: {r.Patch.Timestamp:yyyy-MM-dd HH:mm} UTC | Sensor Quality: {(r.Patch.QualityScore * 100):F0}%",
                Coordinates = $"📍 Location: ({r.Patch.Bounds.Center.Latitude:F5}°N, {r.Patch.Bounds.Center.Longitude:F5}°E)",
                SpectralInfo = $"📐 AOI: [{r.Patch.Bounds.MinLon:F4}° to {r.Patch.Bounds.MaxLon:F4}°E, {r.Patch.Bounds.MinLat:F4}° to {r.Patch.Bounds.MaxLat:F4}°N] | 10m GSD"
            };
        }).ToList();
    }

    private void OnQuickQueryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string query)
        {
            TxtSearchQuery.Text = query;
            OnSearchClicked(null, null!);
        }
    }

    private void OnFindSimilarClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string patchId)
        {
            var targetPatch = _index.GetAllPatches().FirstOrDefault(p => p.PatchId == patchId);
            if (targetPatch == null) return;

            var results = _searchEngine.SearchByImagePatch(targetPatch, topK: 10);
            LstSearchResults.ItemsSource = results.Select((r, i) =>
            {
                var parentTile = r.Patch.ParentTileId == _t1.TileId ? _t1 : _t3;
                Bitmap? previewBmp = null;
                try
                {
                    using var ms = RasterVisualizer.RenderTileToBmpStream(parentTile, r.Patch.PixelX, r.Patch.PixelY, r.Patch.PatchWidth, r.Patch.PatchHeight, _currentRenderMode);
                    previewBmp = new Bitmap(ms);
                }
                catch { }

                return new
                {
                    PatchId = r.Patch.PatchId,
                    ImagePreview = previewBmp,
                    SimilarityBadge = $"# {i + 1} | {(r.SimilarityScore * 100):F1}%",
                    Title = $"Patch: {r.Patch.PatchId} [{r.Patch.Platform}]",
                    Detail = $"Visually & Semantically Similar to target patch {patchId}",
                    Coordinates = $"AOI: {r.Patch.Bounds.MinLon:F4}°E to {r.Patch.Bounds.MaxLon:F4}°E, {r.Patch.Bounds.MinLat:F4}°N to {r.Patch.Bounds.MaxLat:F4}°N",
                    SpectralInfo = $"Cosine Similarity: {r.SimilarityScore:F4} (Distance: {r.Distance:F4})"
                };
            }).ToList();
        }
    }

    private void OnInspectPatchChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string patchId)
        {
            var targetPatch = _index.GetAllPatches().FirstOrDefault(p => p.PatchId == patchId);
            if (targetPatch == null) return;

            // Fill Advanced Spatiotemporal Search with patch coordinates
            TxtSearchLat.Text = targetPatch.Bounds.Center.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            TxtSearchLon.Text = targetPatch.Bounds.Center.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            TxtSearchRadius.Text = "5.0";

            // Switch to the Advanced Spatiotemporal Search tab (index 1) so the user can see the results
            MainTabControl.SelectedIndex = 1;

            OnExecuteSpatiotemporalSearchClicked(null, null!);
            StatusText.Text = $"Pivoted to Advanced Change Search for location ({TxtSearchLat.Text}, {TxtSearchLon.Text})";
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

            return new
            {
                Id = c.Id,
                ChangeType = c.Type.ToString(),
                TypeBadgeColor = GetColorForChangeType(c.Type),
                BeforePreview = beforeBmp,
                AfterPreview = afterBmp,
                Title = $"Candidate Site {c.Id[..8]} • {c.Type} ({c.AreaSqMeters:N0} m² modified)",
                DistanceInfo = $"📍 Proximity: {r.DistanceKm:F2} km from query center | Center: ({c.Center.Latitude:F5}°N, {c.Center.Longitude:F5}°E)",
                EarliestOnsetInfo = $"⏱ Earliest Verified Onset: {c.EarliestObservationTimestamp:yyyy-MM-dd} UTC",
                SpectralMetrics = $"AI Confidence: {(c.Confidence * 100):F1}% | Relevance Rank: {r.RelevanceScore:F3} | {c.ProcessingNotes}"
            };
        }).ToList();

        StatusText.Text = $"Advanced Search: Found {results.Count} matching change sites within {radius} km of ({lat:F4}°N, {lon:F4}°E)";
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

        if (_detectedChanges.Count > 0)
        {
            LstChangeResults.SelectedIndex = 0;
        }

        StatusText.Text = $"Change Analysis Complete: Identified {_detectedChanges.Count} candidate change sites";
    }

    private void UpdateChangeList()
    {
        LstChangeResults.ItemsSource = _detectedChanges.Select(c => new ChangeListItemViewModel
        {
            Id = c.Id,
            Type = c.Type.ToString(),
            TypeBadgeColor = GetColorForChangeType(c.Type),
            Title = $"Candidate {c.Id[..8]} - {c.Type} ({c.AreaSqMeters:N0} m²)",
            Notes = c.ProcessingNotes,
            MetricsSummary = string.Join(" | ", c.Metrics.Select(m => $"{m.Key}: {m.Value:F3}")),
            EarliestObservationText = $"⏱ Earliest Usable Observation Onset: {c.EarliestObservationTimestamp:yyyy-MM-dd} (Confirmed by usable imagery)",
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
        else if (LstChangeResults.SelectedIndex >= 0 && LstChangeResults.SelectedIndex < _detectedChanges.Count)
        {
            _selectedChangeRecord = _detectedChanges[LstChangeResults.SelectedIndex];
            DisplayFocusedInspection(_selectedChangeRecord);
        }
    }

    private void DisplayFocusedInspection(ChangeRecord record)
    {
        try
        {
            TxtFocusedTitle.Text = $"Site #{record.Id[..8]} ({record.AreaSqMeters:N0} m² modified)";
            TxtFocusedType.Text = record.Type.ToString().ToUpperInvariant();
            BadgeFocusedType.Background = Avalonia.Media.Brush.Parse(GetColorForChangeType(record.Type));

            var inspection = RasterVisualizer.RenderFocusedSite(_t1, _t3, record, _currentChangeSpectralMode, padding: 16);
            using (inspection.T1Stream)
            {
                ImgFocusedT1.Source = new Bitmap(inspection.T1Stream);
            }
            using (inspection.T2Stream)
            {
                ImgFocusedT2.Source = new Bitmap(inspection.T2Stream);
            }
            using (inspection.OverlayStream)
            {
                ImgFocusedOverlay.Source = new Bitmap(inspection.OverlayStream);
            }

            record.Metrics.TryGetValue("DeltaNDBI", out var dNdbi);
            record.Metrics.TryGetValue("DeltaNDVI", out var dNdvi);
            record.Metrics.TryGetValue("DeltaNDWI", out var dNdwi);
            record.Metrics.TryGetValue("DeltaGradient", out var dGrad);

            TxtFocusedDeltaNdbi.Text = $"{(dNdbi >= 0 ? "+" : "")}{dNdbi:F4}";
            TxtFocusedDeltaNdvi.Text = $"{(dNdvi >= 0 ? "+" : "")}{dNdvi:F4}";
            TxtFocusedDeltaNdwi.Text = $"{(dNdwi >= 0 ? "+" : "")}{dNdwi:F4}";
            TxtFocusedDeltaGrad.Text = $"{(dGrad >= 0 ? "+" : "")}{dGrad:F4}";

            TxtFocusedOnset.Text = $"⏱ Earliest Usable Observation: {record.EarliestObservationTimestamp:yyyy-MM-dd} UTC | Footprint: {record.AreaSqMeters:N0} m² ({record.AffectedPixels} px) | Conf: {(record.Confidence * 100):F1}%";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating focused change inspection: {ex.Message}");
        }
    }

    private void OnFocusedPivotClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedChangeRecord == null) return;

        TxtSearchLat.Text = _selectedChangeRecord.Center.Latitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        TxtSearchLon.Text = _selectedChangeRecord.Center.Longitude.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
        TxtSearchRadius.Text = "5.0";

        CmbChangeTypeFilter.SelectedIndex = _selectedChangeRecord.Type switch
        {
            ChangeType.Construction => 1,
            ChangeType.Clearance => 2,
            ChangeType.WaterExtentVariation => 3,
            ChangeType.RoadDevelopment => 4,
            ChangeType.ActivityConcentration => 5,
            _ => 0
        };

        MainTabControl.SelectedIndex = 1;
        OnExecuteSpatiotemporalSearchClicked(null, null!);
        StatusText.Text = $"Pivoted to Tab 2 for site {_selectedChangeRecord.Id[..8]} at ({TxtSearchLat.Text}, {TxtSearchLon.Text})";
    }

    private void OnFocusedConfirmClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedChangeRecord == null) return;
        _reviewQueue.Confirm(_selectedChangeRecord.Id, "Confirmed by analyst via Focused Inspection Station.");
        UpdateReviewQueueList();
        StatusText.Text = $"Analyst confirmed candidate {_selectedChangeRecord.Id[..8]}";
    }

    private void OnFocusedRejectClicked(object? sender, RoutedEventArgs e)
    {
        if (_selectedChangeRecord == null) return;
        _reviewQueue.Reject(_selectedChangeRecord.Id, "Rejected false alarm via Focused Inspection Station.");
        UpdateReviewQueueList();
        StatusText.Text = $"Analyst rejected candidate {_selectedChangeRecord.Id[..8]}";
    }

    private void OnConfirmChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _reviewQueue.Confirm(id, "Confirmed by analyst in review console.");
            UpdateReviewQueueList();
            StatusText.Text = $"Analyst confirmed candidate {id[..8]}";
        }
    }

    private void OnRejectChangeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _reviewQueue.Reject(id, "Rejected false alarm.");
            UpdateReviewQueueList();
            StatusText.Text = $"Analyst rejected candidate {id[..8]}";
        }
    }

    private void OnRunClusteringClicked(object? sender, RoutedEventArgs e)
    {
        var patches = _index.GetAllPatches();
        var clusters = SpatialSemanticClusterer.ClusterSites(patches, epsCosineDistance: 0.25, minPts: 2);

        LstClusters.ItemsSource = clusters.Select(c => new
        {
            Header = $"Cluster #{c.ClusterId}: {c.Label}",
            BoundsInfo = $"Enclosing Extent: [{c.EnclosingBounds.MinLon:F4}°E to {c.EnclosingBounds.MaxLon:F4}°E, {c.EnclosingBounds.MinLat:F4}°N to {c.EnclosingBounds.MaxLat:F4}°N]",
            CohesionInfo = $"Members: {c.Members.Count} analogous sites | Semantic Cohesion: {c.CohesionScore:F3}"
        }).ToList();

        StatusText.Text = $"Discovered {clusters.Count} unsupervised spatial-semantic clusters across the AOI";
    }

    private void OnRerankClicked(object? sender, RoutedEventArgs e)
    {
        UpdateReviewQueueList();
        StatusText.Text = "Review queue reranked with analyst active learning feedback.";
    }

    private void UpdateReviewQueueList()
    {
        var items = _reviewQueue.GetAll();
        LstReviewQueue.ItemsSource = items.Select(item => new
        {
            StatusBadge = $"[{item.Status.ToUpperInvariant()}]",
            StatusColor = item.Status switch
            {
                "Confirmed" => "#4ADE80",
                "Rejected" => "#F87171",
                "Flagged" => "#FBBF24",
                _ => "#60A5FA"
            },
            Title = $"{item.Record.Type} - Candidate {item.Record.Id[..8]}",
            AuditDetails = $"T1: {item.Record.TimestampT1:yyyy-MM-dd} -> T2: {item.Record.TimestampT2:yyyy-MM-dd} | Earliest Onset: {item.Record.EarliestObservationTimestamp:yyyy-MM-dd} | Notes: {item.AnalystComments}",
            ConfidenceText = $"Confidence: {(item.Record.Confidence * 100):F1}%"
        }).ToList();
    }

    private void OnExportGeoJsonClicked(object? sender, RoutedEventArgs e)
    {
        string outPath = Path.Combine(Directory.GetCurrentDirectory(), "analyst_review_audit.geojson");
        ProvenanceAuditTrail.SaveGeoJson(outPath, _detectedChanges);
        StatusText.Text = $"Exported W3C PROV-O GeoJSON to: {outPath}";
    }

    private async void OnRunBenchmarkClicked(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Running Full Automated Evaluation Benchmark in Background...";
        await Task.Run(() =>
        {
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "benchmark_results");
            BenchmarkRunner.Run(outDir);
        });
        StatusText.Text = "Benchmark Completed! Full reproducible evaluation report generated.";
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
                int totalPatches = 0;
                int totalFiles = 0;
                StatusText.Text = $"Ingesting {files.Count} external GeoTIFF raster(s)...";

                foreach (var file in files)
                {
                    string localPath = file.Path.LocalPath;
                    if (File.Exists(localPath))
                    {
                        try
                        {
                            var tile = GeoTiffReader.Read(localPath);
                            int patches = _searchEngine.IngestTile(tile);
                            totalPatches += patches;
                            totalFiles++;
                        }
                        catch (Exception ex)
                        {
                            StatusText.Text = $"Error ingesting {Path.GetFileName(localPath)}: {ex.Message}";
                        }
                    }
                }

                StatusText.Text = $"Successfully ingested {totalFiles} GeoTIFF(s), extracted & indexed {totalPatches} patches.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"File selection error: {ex.Message}";
        }
    }

    private static string GetColorForChangeType(ChangeType type) => type switch
    {
        ChangeType.Construction => "#DC2626", // Red
        ChangeType.Clearance => "#D97706",    // Amber
        ChangeType.WaterExtentVariation => "#0284C7", // Cyan
        ChangeType.RoadDevelopment => "#9333EA", // Purple
        ChangeType.ActivityConcentration => "#F59E0B", // Orange
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
    // ── Navigation ─────────────────────────────────────────────────────────────

    // Handles the numbered step buttons in the workflow header bar.
    // Each button carries a Tag that equals the zero-based tab index to navigate to.
    private void OnNavigateToStepClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (!int.TryParse(btn.Tag?.ToString(), out int tabIndex)) return;
        if (!CanProceedToStep(tabIndex))
        {
            StatusText.Text = $"⚠ Complete Step {tabIndex} before advancing to Step {tabIndex + 1}.";
            return;
        }
        MainTabControl.SelectedIndex = tabIndex;
    }

    // Generic "Next Step" advance button – moves to the next sequential tab.
    private void OnAdvanceWorkflowClicked(object? sender, RoutedEventArgs e)
    {
        int next = MainTabControl.SelectedIndex + 1;
        if (next < MainTabControl.ItemCount && CanProceedToStep(next))
            MainTabControl.SelectedIndex = next;
        else
            StatusText.Text = "⚠ Complete the current step before advancing.";
    }

    // Back to Step 1 (Semantic Search tab, index 0).
    private void OnBackToStep1Clicked(object? sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 0;
    }

    // Fired whenever the user switches the main tab – refreshes workflow UI state.
    private void OnMainTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshWorkflowUI();
    }

    // ── Step 1 → Step 3 shortcuts ──────────────────────────────────────────────

    // "Direct to Step 3" from the search results panel:
    // selects the chosen patch and jumps straight to Change Analysis (tab 2).
    private void OnInspectPatchDirectToStep3Clicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string patchId = btn.Tag?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(patchId)) return;

        // Mark steps 1 & 2 done so navigation guard allows step 3.
        _searchCompleted = true;
        _spatiotemporalCompleted = true;

        MainTabControl.SelectedIndex = 2;
        StatusText.Text = $"Jumped to Change Analysis for patch {patchId}.";
        RefreshWorkflowUI();
    }

    // "Direct to Step 3" button at the bottom of Step 1 panel.
    private void OnDirectToStep3Clicked(object? sender, RoutedEventArgs e)
    {
        _searchCompleted = true;
        _spatiotemporalCompleted = true;
        MainTabControl.SelectedIndex = 2;
        RefreshWorkflowUI();
        StatusText.Text = "Navigated directly to Change Analysis (Step 3).";
    }

    // ── Step 2 → Step 3 shortcuts ──────────────────────────────────────────────

    // Inspect a spatiotemporal result directly in Step 3 Change Analysis tab.
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
        StatusText.Text = $"Inspecting change {changeId[..Math.Min(8, changeId.Length)]} in Step 3.";
    }

    // Pin a spatiotemporal result to the Clustering step (Step 4).
    private void OnPinSpatiotemporalToStep4Clicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string changeId = btn.Tag?.ToString() ?? string.Empty;

        _spatiotemporalCompleted = true;
        _changeDetectionCompleted = true;
        MainTabControl.SelectedIndex = 3;
        RefreshWorkflowUI();
        StatusText.Text = $"Pinned change {changeId[..Math.Min(8, changeId.Length)]} – navigated to Step 4 Clustering.";
    }
}

