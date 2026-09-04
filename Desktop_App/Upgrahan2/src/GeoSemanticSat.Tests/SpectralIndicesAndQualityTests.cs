using System;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.Raster;
using Xunit;

namespace GeoSemanticSat.Tests;

public class SpectralIndicesAndQualityTests
{
    [Fact]
    public void SpectralIndices_NDVI_CalculatesAccurately()
    {
        int w = 10, h = 10;
        var tile = new SatelliteTile { Width = w, Height = h };
        var red = new float[h, w];
        var nir = new float[h, w];

        // Dense vegetation: NIR=0.8, Red=0.2 -> NDVI = (0.8 - 0.2) / (0.8 + 0.2) = 0.60
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                red[y, x] = 0.20f;
                nir[y, x] = 0.80f;
            }
        }
        tile.Bands[SpectralBand.Red] = red;
        tile.Bands[SpectralBand.NIR] = nir;

        var ndvi = SpectralIndices.ComputeNDVI(tile);
        Assert.Equal(0.60f, ndvi[5, 5], 3);
    }

    [Fact]
    public void QualityMaskEngine_DetectsCloudAndProjectsShadow()
    {
        int w = 60, h = 60;
        var tile = new SatelliteTile
        {
            Width = w,
            Height = h,
            Platform = SensorPlatform.Sentinel2_Optical,
            SunAzimuthDegrees = 135.0,
            SunElevationDegrees = 45.0,
            GroundSamplingDistanceMeters = 10.0
        };

        var blue = new float[h, w];
        var green = new float[h, w];
        var red = new float[h, w];
        var nir = new float[h, w];
        var swir = new float[h, w];

        // Background terrain
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                blue[y, x] = 0.08f; green[y, x] = 0.15f; red[y, x] = 0.12f;
                nir[y, x] = 0.55f; swir[y, x] = 0.15f;
            }
        }

        // Thick Cloud at center: x=20..25, y=20..25
        for (int y = 20; y <= 25; y++)
        {
            for (int x = 20; x <= 25; x++)
            {
                blue[y, x] = 0.85f; green[y, x] = 0.82f; red[y, x] = 0.80f;
                nir[y, x] = 0.80f; swir[y, x] = 0.10f;
            }
        }

        tile.Bands[SpectralBand.Blue] = blue;
        tile.Bands[SpectralBand.Green] = green;
        tile.Bands[SpectralBand.Red] = red;
        tile.Bands[SpectralBand.NIR] = nir;
        tile.Bands[SpectralBand.SWIR1] = swir;

        var mask = QualityMaskEngine.GenerateQualityMask(tile);

        // Verify cloud detected
        Assert.True((mask[22, 22] & QualityMaskFlags.Cloud) != 0);

        // Usability score should be between 0.80 and 0.99
        double usability = QualityMaskEngine.CalculateUsabilityScore(mask, w, h);
        Assert.InRange(usability, 0.70, 0.99);
    }
}
