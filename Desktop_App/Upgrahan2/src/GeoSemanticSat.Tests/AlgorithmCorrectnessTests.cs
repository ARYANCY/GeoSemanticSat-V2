using System;
using System.Collections.Generic;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Engine.Embeddings;
using Xunit;

namespace GeoSemanticSat.Tests;

/// <summary>
/// Regression tests for the algorithm-audit fixes. Each test fails against the
/// pre-fix behaviour, so they pin the corrections rather than restate them.
/// </summary>
public class AlgorithmCorrectnessTests
{
    // --- A1: text query determinism -------------------------------------------------

    [Fact]
    public void StableHash_IsIndependentOfProcessSeed()
    {
        // Known FNV-1a 32-bit values, masked to 31 bits. These are fixed constants:
        // if the hash ever changes, every persisted index silently becomes invalid.
        Assert.Equal(1678518572, TextQueryEncoder.StableHash("a"));       // FNV-1a("a")  = 0xE40C292C
        Assert.Equal(2075376851, TextQueryEncoder.StableHash("zorblax"));  // 0x7BB3BCD3
        Assert.Equal(TextQueryEncoder.StableHash("zorblax"), TextQueryEncoder.StableHash("zorblax"));
        Assert.NotEqual(TextQueryEncoder.StableHash("zorblax"), TextQueryEncoder.StableHash("quandrium"));
    }

    [Fact]
    public void Encode_OutOfLexiconQuery_IsDeterministic()
    {
        // The old GetHashCode() path produced a different vector on every process launch.
        var first = TextQueryEncoder.Encode("zorblax quandrium");
        var second = TextQueryEncoder.Encode("zorblax quandrium");
        Assert.Equal(first, second);
        Assert.Contains(first, v => v != 0f); // fallback actually populated something
    }

    [Fact]
    public void StableHash_DoesNotOverflowOnPathologicalToken()
    {
        // Math.Abs(int.MinValue) threw OverflowException; the masked hash cannot.
        foreach (var token in new[] { "", "￿￿", new string('z', 512) })
        {
            int h = TextQueryEncoder.StableHash(token);
            Assert.InRange(h, 0, int.MaxValue);
        }
    }

    // --- C1 / C2: CUSUM onset -------------------------------------------------------

    [Fact]
    public void Onset_FlatSeries_ReportsNoChangeDetected()
    {
        var tiles = BuildSeries(new[] { 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f });
        var bounds = new BoundingBox(0.0, 0.0, 0.02, 0.02);

        OnsetEstimator.EstimateEarliestObservation(tiles, bounds, ChangeType.NoChange, out bool detected);

        // Previously indistinguishable from "change at the final pass".
        Assert.False(detected);
    }

    [Fact]
    public void Onset_StepChange_IsDetectedAfterTheBaselineWindow()
    {
        // Flat baseline then a sustained step. Onset must land on or after the step,
        // never inside the baseline window the mean was computed from.
        // Levels stay below 0.18 so QualityMaskEngine does not flag the uniform bright
        // frames as cloud (b > 0.50 && g > 0.45 && r > 0.40 && n > 0.45) and drop them
        // from the usable series before CUSUM ever sees them.
        var tiles = BuildSeries(new[] { 0.03f, 0.03f, 0.03f, 0.18f, 0.18f, 0.18f });
        var bounds = new BoundingBox(0.0, 0.0, 0.02, 0.02);

        var onset = OnsetEstimator.EstimateEarliestObservation(tiles, bounds, ChangeType.NoChange, out bool detected);

        Assert.True(detected);
        Assert.True(onset >= tiles[0].AcquisitionTimestamp.AddDays(3),
            $"Onset {onset:O} fell inside the baseline window - CUSUM is testing baseline points against their own mean.");
    }

    // --- C3 / C4 / B4: radiometric normalizer ---------------------------------------

    [Fact]
    public void Normalizer_RecoversAKnownGainAndOffset()
    {
        // reference = 0.8 * target + 0.05, so the fit must invert exactly that.
        var target = BuildTile("t", (x, y) => 0.10f + 0.008f * ((x * 7 + y * 3) % 90));
        var reference = BuildTile("r", (x, y) =>
        {
            float t = 0.10f + 0.008f * ((x * 7 + y * 3) % 90);
            return 0.8f * t + 0.05f;
        });

        var mask = new QualityMaskFlags[32, 32];
        var result = RadiometricNormalizer.NormalizeTo(target, reference, mask, mask);
        var corrected = result.Bands[SpectralBand.Red];
        var expected = reference.Bands[SpectralBand.Red];

        for (int y = 0; y < 32; y += 7)
        {
            for (int x = 0; x < 32; x += 7)
            {
                Assert.InRange(corrected[y, x], expected[y, x] - 0.02f, expected[y, x] + 0.02f);
            }
        }
    }

    [Fact]
    public void Normalizer_IdenticalTiles_AreLeftUnchanged()
    {
        // With an over-tight Tukey constant the IRLS pass could drift on a perfect fit.
        var tile = BuildTile("t", (x, y) => 0.10f + 0.008f * ((x * 7 + y * 3) % 90));
        var same = BuildTile("r", (x, y) => 0.10f + 0.008f * ((x * 7 + y * 3) % 90));
        var mask = new QualityMaskFlags[32, 32];

        var result = RadiometricNormalizer.NormalizeTo(tile, same, mask, mask);
        var corrected = result.Bands[SpectralBand.Red];
        var original = tile.Bands[SpectralBand.Red];

        for (int y = 0; y < 32; y += 5)
            for (int x = 0; x < 32; x += 5)
                Assert.InRange(corrected[y, x], original[y, x] - 0.01f, original[y, x] + 0.01f);
    }

    // --- helpers ---------------------------------------------------------------------

    private static SatelliteTile BuildTile(string id, Func<int, int, float> value)
    {
        var band = new float[32, 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
                band[y, x] = value(x, y);

        var tile = new SatelliteTile
        {
            TileId = id,
            Platform = SensorPlatform.Sentinel2_Optical,
            AcquisitionTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Width = 32,
            Height = 32,
            GroundSamplingDistanceMeters = 10.0,
            Transform = AffineGeoTransform.NorthUp(0.0, 0.02, 0.000625, 0.000625),
            Bounds = new BoundingBox(0.0, 0.0, 0.02, 0.02)
        };
        tile.Bands[SpectralBand.Red] = band;
        return tile;
    }

    private static List<SatelliteTile> BuildSeries(float[] levels)
    {
        var tiles = new List<SatelliteTile>();
        for (int i = 0; i < levels.Length; i++)
        {
            float level = levels[i];
            var tile = BuildTile($"t{i}", (_, _) => level);
            tile.AcquisitionTimestamp = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i);
            tiles.Add(tile);
        }
        return tiles;
    }
}
