using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Clustering;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Engine.Embeddings;
using Xunit;

namespace GeoSemanticSat.Tests;

/// <summary>
/// Regression tests for the second audit pass (A2/A3/B1/B2/B3/C6-C15).
/// Each test fails against the pre-fix behaviour.
/// </summary>
public class OpenIssueRegressionTests
{
    // --- A3: phrasing must not silently drop the highest-weight concept ---

    [Fact]
    public void TextQuery_IndefiniteArticle_DoesNotChangeTheEncoding()
    {
        var withArticle = TextQueryEncoder.Encode("structures near a river");
        var without = TextQueryEncoder.Encode("structures near river");

        Assert.Equal(without, withArticle);
        // The co-occurrence concept must actually have fired.
        Assert.True(withArticle[SemanticEmbeddingLayout.StructureNearWater] > 0.1f,
            "The 'near river' concept did not fire for the phrasing the UI itself suggests.");
    }

    [Fact]
    public void TextQuery_MatchesWholeWordsOnly()
    {
        // "planet" must not trigger the aircraft concept via a substring match on "plane",
        // and "guardrail" must not trigger the rail/road concept.
        var planet = TextQueryEncoder.Encode("planet surface survey");
        var plane = TextQueryEncoder.Encode("plane");

        Assert.True(plane[SemanticEmbeddingLayout.ActivityPeaks] > 0.1f);
        Assert.NotEqual(plane, planet);
    }

    // --- A2/D: text and image must share one comparable space ---

    [Fact]
    public void TextEncoder_WritesOnlyToSemanticAxes()
    {
        var v = TextQueryEncoder.Encode("newly built structures near a river");
        var semantic = new HashSet<int>(SemanticEmbeddingLayout.SemanticAxes);

        for (int i = 0; i < v.Length; i++)
        {
            if (!semantic.Contains(i))
            {
                Assert.True(Math.Abs(v[i]) < 1e-6f,
                    $"Text encoder wrote {v[i]} into appearance dimension {i}, which no text query can assert.");
            }
        }
    }

    [Fact]
    public void SemanticCosine_IsSymmetricAndBounded()
    {
        var a = TextQueryEncoder.Encode("airfield runway");
        var b = TextQueryEncoder.Encode("river water");

        double ab = SemanticEmbeddingLayout.CosineOnSemanticAxes(a, b);
        double ba = SemanticEmbeddingLayout.CosineOnSemanticAxes(b, a);

        Assert.Equal(ab, ba, 6);
        Assert.InRange(ab, -1.0, 1.0);
        Assert.Equal(1.0, SemanticEmbeddingLayout.CosineOnSemanticAxes(a, a), 5);
    }

    [Fact]
    public void RgbOnlyImagery_ProducesNonZeroSemanticAxes()
    {
        // A 3-band RGB tile previously yielded NDVI = 0 and NDBI = 0 for every pixel,
        // because a missing NIR silently fell back to Red.
        var tile = RgbTile(64, 64);
        var ndvi = SpectralIndices.ComputeNDVI(tile);
        var ndbi = SpectralIndices.ComputeNDBI(tile);

        Assert.True(tile.RequiresVisibleBandProxies);
        Assert.NotEqual(0.0f, ndvi[10, 10]);
        Assert.NotEqual(0.0f, ndbi[10, 10]);

        var embedding = MultiSpectralVisionEncoder.EncodePatch(tile, 0, 0, 32, 32);
        Assert.NotEqual(0.0f, embedding[SemanticEmbeddingLayout.Vegetation]);
        Assert.NotEqual(0.0f, embedding[SemanticEmbeddingLayout.BuiltUp]);
    }

    [Fact]
    public void TrueMultispectralTile_StillUsesRealIndices()
    {
        var tile = RgbTile(32, 32);
        var nir = new float[32, 32];
        var swir = new float[32, 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++) { nir[y, x] = 0.80f; swir[y, x] = 0.30f; }
        tile.Bands[SpectralBand.NIR] = nir;
        tile.Bands[SpectralBand.SWIR1] = swir;

        Assert.False(tile.RequiresVisibleBandProxies);
        // NDBI = (0.30 - 0.80) / (0.30 + 0.80) = -0.454545...
        Assert.InRange(SpectralIndices.ComputeNDBI(tile)[5, 5], -0.4546f, -0.4545f);
    }

    // --- B3: WGS84 ellipsoidal area ---

    [Fact]
    public void EllipsoidalArea_MatchesKnownWgs84Values()
    {
        // Published WGS84 total surface area is 510,065,621.7 km^2.
        var whole = new BoundingBox(-180, -90, 180, 90);
        Assert.InRange(whole.AreaSquareMetres(), 5.0996e14, 5.1017e14);

        // One degree square at the equator is close to 111.32 x 110.57 km.
        var equator = new BoundingBox(0, 0, 1, 1);
        double km2 = equator.AreaSquareMetres() / 1e6;
        Assert.InRange(km2, 12000, 12400);
    }

    [Fact]
    public void EllipsoidalArea_ShrinksWithLatitude()
    {
        double atEquator = new BoundingBox(0, 0, 1, 1).AreaSquareMetres();
        double atSixty = new BoundingBox(0, 60, 1, 61).AreaSquareMetres();
        double atEighty = new BoundingBox(0, 80, 1, 81).AreaSquareMetres();

        Assert.True(atSixty < atEquator);
        // Beyond 78.5 degrees the old cos-floor of 0.2 stopped shrinking entirely.
        Assert.True(atEighty < atSixty);
        Assert.InRange(atSixty / atEquator, 0.45, 0.55); // ~cos(60) = 0.5
    }

    // --- B2 / C12 / C13 / C14: jitter filter ---

    [Fact]
    public void Jitter_IdenticalPatches_ReportNoDifferenceNotJitter()
    {
        var band = EdgeBand(32, 32, 10);
        var result = RegistrationJitterFilter.Analyze(band, band, 4, 4, 16, 32, 32);

        // The old code returned true ("is jitter") for identical input.
        Assert.Equal(RegistrationJitterFilter.AlignmentVerdict.NoDifference, result.Verdict);
        Assert.False(RegistrationJitterFilter.IsRegistrationJitter(band, band, 4, 4, 16, 32, 32));
    }

    [Fact]
    public void Jitter_OnePixelShift_IsDetectedWithSubPixelOffset()
    {
        var a = EdgeBand(32, 32, 10);
        var b = EdgeBand(32, 32, 11);
        var result = RegistrationJitterFilter.Analyze(a, b, 4, 4, 16, 32, 32);

        Assert.Equal(RegistrationJitterFilter.AlignmentVerdict.RegistrationJitter, result.Verdict);
        Assert.InRange(Math.Abs(result.ShiftX), 0.5, 2.0);
        Assert.True(result.AlignedDifference < result.BaseDifference);
    }

    [Fact]
    public void Jitter_GenuineNewObject_IsNotSuppressed()
    {
        var a = new float[32, 32];
        var b = new float[32, 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++) { a[y, x] = 0.15f; b[y, x] = 0.15f; }
        // A compact bright object appears in B only. No shift can explain it away.
        for (int y = 8; y < 16; y++)
            for (int x = 8; x < 16; x++) b[y, x] = 0.90f;

        var result = RegistrationJitterFilter.Analyze(a, b, 4, 4, 20, 32, 32);
        Assert.Equal(RegistrationJitterFilter.AlignmentVerdict.GenuineDifference, result.Verdict);
    }

    [Fact]
    public void Ssim_SinglePixelPatch_DoesNotDivideByZero()
    {
        var one = new float[1, 1];
        one[0, 0] = 0.5f;
        double ssim = RegistrationJitterFilter.ComputeSSIM(one, one, 1);
        Assert.False(double.IsNaN(ssim) || double.IsInfinity(ssim));
    }

    // --- C9: quality flags are a bit set, not an equality test ---

    [Fact]
    public void QualityFlags_InformationalBitsDoNotInvalidateAPixel()
    {
        Assert.True(QualityMaskEngine.IsUsable(QualityMaskFlags.Valid));
        // Water must stay usable: dropping it would blind water-extent detection to the
        // very pixels it exists to measure.
        Assert.True(QualityMaskEngine.IsUsable(QualityMaskFlags.Water));
        Assert.True(QualityMaskEngine.IsUsable(QualityMaskFlags.HighHaze));

        Assert.False(QualityMaskEngine.IsUsable(QualityMaskFlags.Cloud));
        Assert.False(QualityMaskEngine.IsUsable(QualityMaskFlags.CloudShadow));
        Assert.False(QualityMaskEngine.IsUsable(QualityMaskFlags.Water | QualityMaskFlags.Cloud));
    }

    // --- C6 / C7: DBSCAN ---

    [Fact]
    public void Dbscan_MinPtsCountsThePointItself()
    {
        // Two mutually-similar co-located patches must form a cluster at minPts = 2.
        // Excluding self meant minPts = 2 silently required three points.
        var patches = new List<TilePatch>
        {
            PatchAt(28.60, 77.20, 0.9f),
            PatchAt(28.6001, 77.2001, 0.9f)
        };

        var clusters = SpatialSemanticClusterer.ClusterSites(patches, epsCosineDistance: 0.25, minPts: 2);
        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].Members.Count);
    }

    [Fact]
    public void Dbscan_FindsNeighboursNearTheRadiusEdgeAtHighLatitude()
    {
        // At 60 degrees north a degree of longitude is ~55 km, so the old fixed 3x3 cell
        // search covered barely half the requested radius and dropped this pair.
        var patches = new List<TilePatch>
        {
            PatchAt(60.0, 10.0, 0.9f),
            PatchAt(60.0, 10.70, 0.9f)   // ~39 km east, well inside a 50 km radius
        };

        var clusters = SpatialSemanticClusterer.ClusterSites(
            patches, epsCosineDistance: 0.25, minPts: 2, maxSpatialDistanceKm: 50.0);

        Assert.Single(clusters);
        Assert.Equal(2, clusters[0].Members.Count);
    }

    // --- B1 / C8: CVA and rule arbitration ---

    [Fact]
    public void Cva_EmitsMagnitudeAndTrajectoryMetrics()
    {
        var (t1, t2) = ConstructionPair();
        var changes = MultiTemporalChangeDetector.DetectChanges(t1, t2,
            new MultiTemporalChangeDetector.ChangeDetectionOptions(PatchSize: 16, MinConfidence: 0.5));

        Assert.NotEmpty(changes);
        var c = changes[0];
        Assert.True(c.Metrics.ContainsKey("CvaMagnitude"), "CVA magnitude was never computed.");
        Assert.True(c.Metrics.ContainsKey("CvaTrajectoryRadians"), "CVA trajectory angle was never computed.");
        Assert.True(c.Metrics["CvaMagnitude"] > 0);
        Assert.InRange(c.Metrics["CvaTrajectoryRadians"], -Math.PI, Math.PI);
    }

    [Fact]
    public void Cva_UnchangedScene_ProducesNoDetections()
    {
        var t1 = ConstructionPair().Item1;
        var same = ConstructionPair().Item1;
        same.TileId = "same";

        var changes = MultiTemporalChangeDetector.DetectChanges(t1, same,
            new MultiTemporalChangeDetector.ChangeDetectionOptions(PatchSize: 16, MinConfidence: 0.5));

        Assert.Empty(changes);
    }

    [Fact]
    public void Cva_AreaUsesTheEllipsoidalBoxArea()
    {
        var (t1, t2) = ConstructionPair();
        var changes = MultiTemporalChangeDetector.DetectChanges(t1, t2,
            new MultiTemporalChangeDetector.ChangeDetectionOptions(PatchSize: 16, MinConfidence: 0.5));

        Assert.NotEmpty(changes);
        var c = changes[0];
        Assert.Equal(c.Bounds.AreaSquareMetres(), c.AreaSqMeters, 3);
        Assert.True(c.AreaSqMeters > 0);
    }

    [Fact]
    public void TextSearch_DoesNotReturnAntiCorrelatedPatchesAsCandidates()
    {
        var index = new VectorIndex(SemanticEmbeddingLayout.Dimension);

        var builtUp = PatchAt(28.60, 77.20, 0.9f);
        var opposite = PatchAt(28.61, 77.21, 0.0f);
        opposite.EmbeddingVector[SemanticEmbeddingLayout.BuiltUp] = -0.9f;
        opposite.EmbeddingVector[SemanticEmbeddingLayout.ManMadeContrast] = -0.4f;

        index.Add(builtUp);
        index.Add(opposite);

        var engine = new GeoSemanticSat.Engine.Retrieval.SemanticSearchEngine(index);
        var results = engine.SearchByText("built structures", topK: 10);

        // The anti-correlated patch used to be ranked and shown as a negative-percent candidate.
        Assert.All(results, r => Assert.True(r.SimilarityScore > 0));
        Assert.DoesNotContain(results, r => r.Patch.PatchId == opposite.PatchId);
    }

    // --- helpers ---

    private static SatelliteTile RgbTile(int w, int h)
    {
        var tile = new SatelliteTile
        {
            TileId = "rgb",
            Platform = SensorPlatform.Sentinel2_Optical,
            Width = w,
            Height = h,
            GroundSamplingDistanceMeters = 10.0,
            AcquisitionTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Transform = AffineGeoTransform.NorthUp(77.20, 28.61, 0.0001, 0.0001),
            Bounds = new BoundingBox(77.20, 28.60, 77.21, 28.61)
        };

        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Deliberately non-neutral: 2R != G+B and 2G != R+B, so neither the
                // Excess Red nor the Excess Green proxy is degenerate.
                r[y, x] = 0.12f;
                g[y, x] = 0.16f;
                b[y, x] = 0.05f;
            }
        }
        tile.Bands[SpectralBand.Red] = r;
        tile.Bands[SpectralBand.Green] = g;
        tile.Bands[SpectralBand.Blue] = b;
        return tile;
    }

    private static float[,] EdgeBand(int w, int h, int edgeX)
    {
        var band = new float[h, w];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                band[y, x] = x < edgeX ? 0.10f : 0.80f;
        return band;
    }

    private static TilePatch PatchAt(double lat, double lon, float builtUp)
    {
        var v = new float[SemanticEmbeddingLayout.Dimension];
        v[SemanticEmbeddingLayout.BuiltUp] = builtUp;
        v[SemanticEmbeddingLayout.ManMadeContrast] = 0.4f;

        return new TilePatch
        {
            PatchId = $"p_{lat}_{lon}",
            ParentTileId = "t",
            Platform = SensorPlatform.Sentinel2_Optical,
            Timestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Bounds = new BoundingBox(lon, lat, lon + 0.001, lat + 0.001),
            EmbeddingVector = v,
            QualityScore = 1.0
        };
    }

    /// <summary>T1 vegetated, T2 with a bright high-contrast built surface in one corner.</summary>
    private static (SatelliteTile, SatelliteTile) ConstructionPair()
    {
        SatelliteTile Make(string id, bool built)
        {
            var tile = new SatelliteTile
            {
                TileId = id,
                Platform = SensorPlatform.Sentinel2_Optical,
                Width = 64,
                Height = 64,
                GroundSamplingDistanceMeters = 10.0,
                AcquisitionTimestamp = new DateTime(2024, built ? 6 : 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Transform = AffineGeoTransform.NorthUp(77.20, 28.61, 0.0001, 0.0001),
                Bounds = new BoundingBox(77.20, 28.6036, 77.2064, 28.61)
            };

            var red = new float[64, 64];
            var green = new float[64, 64];
            var blue = new float[64, 64];
            var nir = new float[64, 64];
            var swir = new float[64, 64];

            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    // Vegetated background
                    red[y, x] = 0.06f; green[y, x] = 0.10f; blue[y, x] = 0.05f;
                    nir[y, x] = 0.45f; swir[y, x] = 0.18f;

                    if (built && x >= 16 && x < 48 && y >= 16 && y < 48)
                    {
                        // Concrete: bright, high SWIR, collapsed NIR, chequered for edge energy
                        bool alt = ((x / 2) + (y / 2)) % 2 == 0;
                        red[y, x] = alt ? 0.38f : 0.30f;
                        green[y, x] = alt ? 0.36f : 0.29f;
                        blue[y, x] = alt ? 0.34f : 0.28f;
                        nir[y, x] = 0.16f;
                        swir[y, x] = 0.42f;
                    }
                }
            }

            tile.Bands[SpectralBand.Red] = red;
            tile.Bands[SpectralBand.Green] = green;
            tile.Bands[SpectralBand.Blue] = blue;
            tile.Bands[SpectralBand.NIR] = nir;
            tile.Bands[SpectralBand.SWIR1] = swir;
            return tile;
        }

        return (Make("T1", built: false), Make("T2", built: true));
    }
}
