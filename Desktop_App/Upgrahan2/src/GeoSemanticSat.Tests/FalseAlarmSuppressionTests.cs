using System;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.Raster;
using Xunit;

namespace GeoSemanticSat.Tests;

public class FalseAlarmSuppressionTests
{
    [Fact]
    public void RegistrationJitterFilter_DetectsSubPixelShift()
    {
        int size = 16;
        int w = 32, h = 32;
        var bandA = new float[h, w];
        var bandB = new float[h, w];

        // Create high-contrast edge at x = 10
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bandA[y, x] = x < 10 ? 0.1f : 0.8f;
                // bandB shifted 1 pixel right (x < 11)
                bandB[y, x] = x < 11 ? 0.1f : 0.8f;
            }
        }

        bool isJitter = RegistrationJitterFilter.IsRegistrationJitter(bandA, bandB, 5, 5, size, w, h);
        Assert.True(isJitter, "1-pixel edge registration shift should be identified as jitter false alarm.");
    }

    [Fact]
    public void RadiometricNormalizer_EliminatesAtmosphericIlluminationScale()
    {
        int w = 40, h = 40;
        var refTile = new SatelliteTile { Width = w, Height = h, Platform = SensorPlatform.Sentinel2_Optical };
        var targetTile = new SatelliteTile { Width = w, Height = h, Platform = SensorPlatform.Sentinel2_Optical };

        var refRed = new float[h, w];
        var targetRed = new float[h, w];

        // Target tile has uniform atmospheric haze (+0.10) and illumination attenuation (* 0.80)
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float val = (x + y) / 100.0f;
                refRed[y, x] = val;
                targetRed[y, x] = val * 0.80f + 0.10f;
            }
        }

        refTile.Bands[SpectralBand.Red] = refRed;
        targetTile.Bands[SpectralBand.Red] = targetRed;

        var mask = new QualityMaskFlags[h, w];
        var normalized = RadiometricNormalizer.NormalizeTo(targetTile, refTile, mask, mask);

        var normRed = normalized.Bands[SpectralBand.Red];

        // Normalized target should be much closer to reference
        double errorBefore = Math.Abs(targetRed[20, 20] - refRed[20, 20]);
        double errorAfter = Math.Abs(normRed[20, 20] - refRed[20, 20]);

        Assert.True(errorAfter < errorBefore, $"Normalized error ({errorAfter}) should be less than unnormalized error ({errorBefore}).");
    }
}
