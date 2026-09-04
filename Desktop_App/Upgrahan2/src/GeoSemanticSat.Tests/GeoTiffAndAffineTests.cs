using System;
using System.Collections.Generic;
using System.IO;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;
using Xunit;

namespace GeoSemanticSat.Tests;

public class GeoTiffAndAffineTests
{
    [Fact]
    public void AffineTransform_PixelToGeoAndInverse_RoundTripsAccurately()
    {
        double originLon = 77.2000;
        double originLat = 28.6100;
        double pixelSize = 0.0001;

        var transform = AffineGeoTransform.NorthUp(originLon, originLat, pixelSize, pixelSize);

        int testPx = 45;
        int testPy = 80;

        var geo = transform.PixelToGeo(testPx, testPy);
        var (px, py) = transform.GeoToPixel(geo);

        Assert.Equal(testPx, px, 4);
        Assert.Equal(testPy, py, 4);
        Assert.Equal(originLon + testPx * pixelSize, geo.Longitude, 5);
        Assert.Equal(originLat - testPy * pixelSize, geo.Latitude, 5);
    }

    [Fact]
    public void GeoCoordinate_HaversineDistance_ComputesCorrectly()
    {
        // New Delhi: 28.6139° N, 77.2090° E
        // Agra: 27.1767° N, 78.0081° E (Approx ~180 km)
        var delhi = new GeoCoordinate(28.6139, 77.2090);
        var agra = new GeoCoordinate(27.1767, 78.0081);

        double distKm = delhi.DistanceToKm(agra);
        Assert.InRange(distKm, 175.0, 190.0);
    }

    [Fact]
    public void BoundingBox_IntersectsAndContains_WorksExpectedly()
    {
        var box1 = new BoundingBox(77.0, 28.0, 77.5, 28.5);
        var box2 = new BoundingBox(77.3, 28.2, 77.8, 28.7);
        var box3 = new BoundingBox(78.0, 29.0, 78.5, 29.5);

        Assert.True(box1.Intersects(box2));
        Assert.False(box1.Intersects(box3));
        Assert.True(box1.Contains(new GeoCoordinate(28.2, 77.2)));
        Assert.False(box1.Contains(new GeoCoordinate(28.6, 77.2)));
    }

    [Fact]
    public void GeoTiffWriterAndReader_RoundTrip_PreservesMetadataAndBands()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"test_geotiff_{Guid.NewGuid():N}.tif");
        try
        {
            int w = 64, h = 64;
            var transform = AffineGeoTransform.NorthUp(77.100, 28.500, 0.0001, 0.0001);
            var tile = new SatelliteTile
            {
                TileId = "TEST_ROUNDTRIP",
                Platform = SensorPlatform.Sentinel2_Optical,
                AcquisitionTimestamp = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc),
                Width = w,
                Height = h,
                Transform = transform,
                Bounds = new BoundingBox(77.100, 28.500 - h * 0.0001, 77.100 + w * 0.0001, 28.500)
            };

            var red = new float[h, w];
            var nir = new float[h, w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    red[y, x] = 0.25f;
                    nir[y, x] = 0.75f;
                }
            }
            tile.Bands[SpectralBand.Red] = red;
            tile.Bands[SpectralBand.NIR] = nir;

            var bands = new List<SpectralBand> { SpectralBand.Red, SpectralBand.NIR };
            GeoTiffWriter.WriteGeoTiff(tempFile, tile, bands);

            Assert.True(File.Exists(tempFile));

            var loaded = GeoTiffReader.Read(tempFile, SensorPlatform.Sentinel2_Optical);
            Assert.Equal(w, loaded.Width);
            Assert.Equal(h, loaded.Height);
            Assert.Equal(transform.A, loaded.Transform.A, 5);
            Assert.Equal(transform.D, loaded.Transform.D, 5);

            var loadedRed = loaded.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
            Assert.InRange(loadedRed[10, 10], 0.23f, 0.27f);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
