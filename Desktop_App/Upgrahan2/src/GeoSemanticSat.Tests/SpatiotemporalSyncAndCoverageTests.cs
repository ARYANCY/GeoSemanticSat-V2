using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Engine.Embeddings;
using Xunit;

namespace GeoSemanticSat.Tests;

public class SpatiotemporalSyncAndCoverageTests
{
    private static ChangeRecord CreateDummyRecord(string id, double lat, double lon, DateTime timestamp, ChangeType type = ChangeType.Construction)
    {
        return new ChangeRecord
        {
            Id = id,
            TileId = "tile_test_001",
            Type = type,
            Bounds = new BoundingBox(lon - 0.005, lat - 0.005, lon + 0.005, lat + 0.005),
            AreaSqMeters = 5000,
            Confidence = 0.92,
            EarliestObservationTimestamp = timestamp,
            Metrics = new Dictionary<string, double> { { "CVA_Magnitude", 0.45 } },
            ProcessingNotes = "Test candidate"
        };
    }

    [Fact]
    public void OutOfCoverage_CoordinateQuery_ReturnsZeroResults()
    {
        // Active test scene is located around latitude 34.05, longitude -118.25 (Los Angeles)
        var records = new List<ChangeRecord>
        {
            CreateDummyRecord("c1", 34.0522, -118.2437, new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc)),
            CreateDummyRecord("c2", 34.0535, -118.2410, new DateTime(2024, 4, 10, 0, 0, 0, DateTimeKind.Utc))
        };

        var engine = new ChangeSearchEngine(records);

        // Query London, UK (51.5074, -0.1278) with 15km radius
        var criteriaLondon = new ChangeSearchCriteria(
            Center: new GeoCoordinate(51.5074, -0.1278),
            RadiusKm: 15.0
        );

        var resultsLondon = engine.Search(criteriaLondon);
        Assert.Empty(resultsLondon);

        // Query Tokyo, Japan (35.6762, 139.6503) with 50km radius
        var criteriaTokyo = new ChangeSearchCriteria(
            Center: new GeoCoordinate(35.6762, 139.6503),
            RadiusKm: 50.0
        );

        var resultsTokyo = engine.Search(criteriaTokyo);
        Assert.Empty(resultsTokyo);
    }

    [Fact]
    public void OutOfCoverage_DateQuery_ReturnsZeroResults()
    {
        var records = new List<ChangeRecord>
        {
            CreateDummyRecord("c1", 34.0522, -118.2437, new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc)),
            CreateDummyRecord("c2", 34.0535, -118.2410, new DateTime(2024, 4, 10, 0, 0, 0, DateTimeKind.Utc))
        };

        var engine = new ChangeSearchEngine(records);

        // Query year 2022 (outside data coverage of 2024)
        var criteriaHistorical = new ChangeSearchCriteria(
            Center: new GeoCoordinate(34.0522, -118.2437),
            RadiusKm: 20.0,
            StartDate: new DateTime(2022, 1, 1),
            EndDate: new DateTime(2022, 12, 31)
        );

        var results = engine.Search(criteriaHistorical);
        Assert.Empty(results);
    }

    [Fact]
    public void InCoverage_CoordinateAndDateQuery_ReturnsRankedMatches()
    {
        var records = new List<ChangeRecord>
        {
            CreateDummyRecord("c1", 34.0522, -118.2437, new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc)),
            CreateDummyRecord("c2", 34.0535, -118.2410, new DateTime(2024, 4, 10, 0, 0, 0, DateTimeKind.Utc)),
            CreateDummyRecord("c3", 34.1500, -118.2437, new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc)) // ~10.8 km away
        };

        var engine = new ChangeSearchEngine(records);

        var criteria = new ChangeSearchCriteria(
            Center: new GeoCoordinate(34.0522, -118.2437),
            RadiusKm: 5.0, // Should include c1 and c2, but exclude c3 (~10.8km away)
            StartDate: new DateTime(2024, 3, 1),
            EndDate: new DateTime(2024, 4, 30)
        );

        var results = engine.Search(criteria);
        Assert.Equal(2, results.Count);
        Assert.Equal("c1", results[0].Record.Id); // Closest record ranked highest
        Assert.True(results[0].DistanceKm < 0.1);
        Assert.True(results[0].RelevanceScore > results[1].RelevanceScore);
    }

    [Fact]
    public void VectorIndex_InclusiveEndDate_MatchesSameDayMidnightFilter()
    {
        var index = new VectorIndex(128);
        var patch = new TilePatch
        {
            PatchId = "patch_1",
            ParentTileId = "tile_1",
            Bounds = new BoundingBox(-118.0, 34.0, -117.9, 34.1),
            EmbeddingVector = TextQueryEncoder.Encode("military warehouse depot"),
            Timestamp = new DateTime(2024, 5, 20, 14, 30, 0, DateTimeKind.Utc) // 2:30 PM on May 20
        };
        index.Add(patch);

        // Filter with EndDate set to May 20 at midnight (as typically provided by date pickers without time)
        var filterSameDay = new SearchFilter(
            StartDate: new DateTime(2024, 5, 20, 0, 0, 0),
            EndDate: new DateTime(2024, 5, 20, 0, 0, 0),
            MinQuality: 0.0
        );

        float[] queryVec = TextQueryEncoder.Encode("military warehouse depot");
        var results = index.Search(queryVec, topK: 10, filter: filterSameDay);
        Assert.Single(results);
        Assert.Equal("patch_1", results[0].Patch.PatchId);

        // Filter with EndDate before May 20 should yield nothing
        var filterPrior = new SearchFilter(
            EndDate: new DateTime(2024, 5, 19, 0, 0, 0),
            MinQuality: 0.0
        );
        var resultsPrior = index.Search(queryVec, topK: 10, filter: filterPrior);
        Assert.Empty(resultsPrior);
    }

    [Fact]
    public void QualityMaskEngine_UsabilityScore_ConsistentWithFlags()
    {
        int w = 10, h = 10;
        var mask = new QualityMaskFlags[h, w];

        // Valid data everywhere initially
        double initialScore = QualityMaskEngine.CalculateUsabilityScore(mask, w, h);
        Assert.Equal(1.0, initialScore, precision: 2);

        // Mark 50 pixels as Cloud (unusable)
        for (int i = 0; i < 50; i++)
        {
            mask[i / 10, i % 10] = QualityMaskFlags.Cloud;
        }

        double updatedScore = QualityMaskEngine.CalculateUsabilityScore(mask, w, h);
        Assert.Equal(0.5, updatedScore, precision: 2);
    }

    [Fact]
    public void VectorIndex_CenterCoordinateWithoutExplicitRadius_AppliesDefaultRadius()
    {
        var index = new VectorIndex(128);

        // Near patch at center (34.05, -118.25)
        var nearPatch = new TilePatch
        {
            PatchId = "near_patch",
            ParentTileId = "tile_1",
            Bounds = new BoundingBox(-118.251, 34.049, -118.249, 34.051),
            EmbeddingVector = TextQueryEncoder.Encode("coastal port facility"),
            Timestamp = DateTime.UtcNow
        };

        // Far patch ~100 km away at (34.05, -117.15)
        var farPatch = new TilePatch
        {
            PatchId = "far_patch",
            ParentTileId = "tile_1",
            Bounds = new BoundingBox(-117.151, 34.049, -117.149, 34.051),
            EmbeddingVector = TextQueryEncoder.Encode("coastal port facility"),
            Timestamp = DateTime.UtcNow
        };

        index.Add(nearPatch);
        index.Add(farPatch);

        // Filter with CenterCoordinate but NO explicit RadiusKm (should default to 15.0 km)
        var filter = new SearchFilter(
            CenterCoordinate: new GeoCoordinate(34.05, -118.25),
            RadiusKm: null,
            MinQuality: 0.0
        );

        float[] queryVec = TextQueryEncoder.Encode("coastal port facility");
        var results = index.Search(queryVec, topK: 10, filter: filter);

        Assert.Single(results);
        Assert.Equal("near_patch", results[0].Patch.PatchId);
    }

    [Fact]
    public void RadiometricNormalizer_PIF_AcceptsInformationalWaterAndHazeFlags()
    {
        int w = 20, h = 20;
        var t1 = new SatelliteTile
        {
            TileId = "t1",
            Width = w,
            Height = h,
            Platform = SensorPlatform.Sentinel2_Optical,
            Transform = AffineGeoTransform.NorthUp(77.0, 28.0, 0.0001, 0.0001)
        };
        var t2 = new SatelliteTile
        {
            TileId = "t2",
            Width = w,
            Height = h,
            Platform = SensorPlatform.Sentinel2_Optical,
            Transform = AffineGeoTransform.NorthUp(77.0, 28.0, 0.0001, 0.0001)
        };

        var red1 = new float[h, w];
        var red2 = new float[h, w];
        var mask1 = new QualityMaskFlags[h, w];
        var mask2 = new QualityMaskFlags[h, w];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                red1[y, x] = 0.30f;
                red2[y, x] = 0.35f; // Slight atmospheric gain
                // Set informational flags (Water, HighHaze) which are NOT clouds or shadows
                mask1[y, x] = QualityMaskFlags.Water;
                mask2[y, x] = QualityMaskFlags.HighHaze;
            }
        }

        t1.Bands[SpectralBand.Red] = red1;
        t2.Bands[SpectralBand.Red] = red2;

        var normalized = RadiometricNormalizer.NormalizeTo(t2, t1, mask2, mask1);

        // Pixels with Water/HighHaze flags should NOT be rejected from PIF regression
        Assert.NotNull(normalized);
        Assert.True(normalized.Bands.ContainsKey(SpectralBand.Red));
        float normalizedVal = normalized.Bands[SpectralBand.Red][5, 5];
        // The normalized value should be pulled towards t1 (0.30f) rather than staying at raw t2 (0.35f)
        Assert.True(Math.Abs(normalizedVal - 0.30f) < 0.03f);
    }

    [Fact]
    public void AffineGeoTransform_WorldfileMapping_CalculatesCorrectCoordinates()
    {
        // Standard GDAL / ESRI Worldfile:
        // Line 1: dx = 0.0001
        // Line 2: rotY = 0.0
        // Line 3: rotX = 0.0
        // Line 4: dy = -0.0001
        // Line 5: x0 = 77.20
        // Line 6: y0 = 28.60
        var transform = new AffineGeoTransform(77.20, 0.0001, 0.0, 28.60, 0.0, -0.0001);

        var geo0 = transform.PixelToGeo(0, 0);
        Assert.Equal(77.20, geo0.Longitude, precision: 5);
        Assert.Equal(28.60, geo0.Latitude, precision: 5);

        var geo100 = transform.PixelToGeo(100, 100);
        Assert.Equal(77.21, geo100.Longitude, precision: 5);
        Assert.Equal(28.59, geo100.Latitude, precision: 5);

        var (px, py) = transform.GeoToPixel(new GeoCoordinate(28.59, 77.21));
        Assert.Equal(100.0, px, precision: 2);
        Assert.Equal(100.0, py, precision: 2);
    }
}
