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

    [Fact]
    public void UtmCoordinateReprojection_ConvertsAccuratelyToWgs84()
    {
        // New Delhi region: UTM Zone 43N, Easting: ~716000 m, Northing: ~3167000 m
        double easting = 716000.0;
        double northing = 3167000.0;
        int utm43n = 32643;

        var geo = CoordinateReprojection.ProjectedToWgs84(easting, northing, utm43n);

        // Expected Latitude ~ 28.61° N, Longitude ~ 77.21° E
        Assert.InRange(geo.Latitude, 28.5, 28.7);
        Assert.InRange(geo.Longitude, 77.1, 77.3);

        // Round-trip verification
        var (roundEasting, roundNorthing) = CoordinateReprojection.Wgs84ToProjected(geo, utm43n);
        Assert.Equal(easting, roundEasting, 1);
        Assert.Equal(northing, roundNorthing, 1);
    }

    [Fact]
    public void NoDataMasking_CorrectlySuppressesBorderZerosAndNoData()
    {
        int w = 16, h = 16;
        var tile = new SatelliteTile
        {
            TileId = "TEST_NODATA",
            Platform = SensorPlatform.Sentinel2_Optical,
            Width = w,
            Height = h,
            NoDataValue = -9999.0
        };

        var red = new float[h, w];
        var green = new float[h, w];
        var blue = new float[h, w];
        var nir = new float[h, w];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (x == 0 || y == 0)
                {
                    // Border zero-padding
                    red[y, x] = 0.0f;
                    green[y, x] = 0.0f;
                    blue[y, x] = 0.0f;
                    nir[y, x] = 0.0f;
                }
                else if (x == 1)
                {
                    // Explicit NoData value
                    red[y, x] = -9999.0f;
                    green[y, x] = -9999.0f;
                    blue[y, x] = -9999.0f;
                    nir[y, x] = -9999.0f;
                }
                else
                {
                    red[y, x] = 0.15f;
                    green[y, x] = 0.20f;
                    blue[y, x] = 0.10f;
                    nir[y, x] = 0.45f;
                }
            }
        }

        tile.Bands[SpectralBand.Red] = red;
        tile.Bands[SpectralBand.Green] = green;
        tile.Bands[SpectralBand.Blue] = blue;
        tile.Bands[SpectralBand.NIR] = nir;

        var mask = QualityMaskEngine.GenerateQualityMask(tile);

        // Border pixel (0,0) must be flagged as NoData and unusable
        Assert.True((mask[0, 0] & QualityMaskFlags.NoData) != 0);
        Assert.False(QualityMaskEngine.IsUsable(mask[0, 0]));

        // Explicit NoData pixel (5,1) must be flagged as NoData
        Assert.True((mask[5, 1] & QualityMaskFlags.NoData) != 0);
        Assert.False(QualityMaskEngine.IsUsable(mask[5, 1]));

        // Valid interior pixel (5,5) must be valid
        Assert.True(QualityMaskEngine.IsUsable(mask[5, 5]));
    }

    [Fact]
    public void ProvenanceAuditTrail_IncludesSha256InGeoJson()
    {
        var changes = new List<ChangeRecord>
        {
            new ChangeRecord
            {
                Id = "CHG_001",
                TileId = "S2_NCR_2026",
                Type = ChangeType.Construction,
                Confidence = 0.92,
                Bounds = new BoundingBox(77.20, 28.60, 77.25, 28.65),
                TimestampT1 = DateTime.UtcNow.AddDays(-10),
                TimestampT2 = DateTime.UtcNow
            }
        };

        string testHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        string geoJson = GeoSemanticSat.Core.Workflow.ProvenanceAuditTrail.ExportToGeoJson(changes, "2.0.0-TEST", testHash, 0.15);

        Assert.Contains(testHash, geoJson);
        Assert.Contains("sourceImageSha256", geoJson);
        Assert.Contains("cvaThreshold", geoJson);
    }
}
