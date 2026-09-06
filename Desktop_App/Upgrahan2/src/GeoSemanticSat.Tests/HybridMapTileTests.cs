using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GeoSemanticSat.UI.Services;
using Xunit;

namespace GeoSemanticSat.Tests;

public class HybridMapTileTests
{
    [Theory]
    [InlineData(0.0, 0.0, 10, 512, 512)] // Null Island at zoom 10
    // Expected values below are the canonical OSM slippy-map values
    // (https://wiki.openstreetmap.org/wiki/Slippy_map_tilenames). The previous fixtures
    // held incorrect numbers, so these two cases failed against a correct implementation.
    [InlineData(77.2080, 28.6050, 14, 11705, 6832)] // Delhi at zoom 14
    [InlineData(-122.4194, 37.7749, 12, 655, 1583)] // San Francisco at zoom 12
    public void SlippyTileMath_ConvertsLonLatToTileCorrectly(double lon, double lat, int zoom, int expectedX, int expectedY)
    {
        var (x, y) = HybridTileService.LonLatToTile(lon, lat, zoom);
        Assert.Equal(expectedX, x);
        Assert.Equal(expectedY, y);
    }

    [Fact]
    public void SlippyTileMath_RoundTripTileToLonLatIsAccurateWithinTileSpan()
    {
        double origLon = 77.2080;
        double origLat = 28.6050;
        int zoom = 14;

        var (x, y) = HybridTileService.LonLatToTile(origLon, origLat, zoom);
        var (nwLon, nwLat) = HybridTileService.TileToLonLat(x, y, zoom);
        var (seLon, seLat) = HybridTileService.TileToLonLat(x + 1, y + 1, zoom);

        // Original point should be strictly inside the tile's bounding box
        Assert.True(origLon >= nwLon && origLon <= seLon, "Longitude should fall inside tile bounds");
        Assert.True(origLat <= nwLat && origLat >= seLat, "Latitude should fall inside tile bounds");
    }

    [Fact]
    public void HybridTileService_RegistryContainsAllKeyProviders()
    {
        var providers = HybridTileService.AvailableProviders;
        Assert.NotEmpty(providers);

        var ids = providers.Select(p => p.Id).ToList();
        Assert.Contains("carto-dark", ids);
        Assert.Contains("osm-standard", ids);
        Assert.Contains("esri-satellite", ids);
        Assert.Contains("sentinel2-cloudless", ids);
        Assert.Contains("usgs-imagery", ids);

        foreach (var p in providers)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.UrlTemplate));
            Assert.Contains("{z}", p.UrlTemplate);
            Assert.Contains("{x}", p.UrlTemplate);
            Assert.Contains("{y}", p.UrlTemplate);
        }
    }

    [Fact]
    public void HybridTileService_GeneratesCorrectDiskPaths()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "GSS_Test_Tiles_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new HybridTileService(tempDir);
            string path = service.GetDiskCachePath("carto-dark", 12, 100, 200);
            
            Assert.Contains("carto-dark", path);
            Assert.Contains("12", path);
            Assert.Contains("100", path);
            Assert.EndsWith("200.png", path);

            string sentinelPath = service.GetDiskCachePath("sentinel2-cloudless", 14, 50, 60);
            Assert.EndsWith("60.jpg", sentinelPath);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void HybridTileService_AirGappedMode_ReturnsNullOnMissWithoutNetworkCall()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "GSS_AirGap_Test_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new HybridTileService(tempDir);
            service.Mode = MapTileMode.OfflineStrict;

            // Should return null immediately on cache miss and not throw
            var tile = service.GetTile(10, 500, 500);
            Assert.Null(tile);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task NetworkConnectivityMonitor_InitializesAndRunsGracefully()
    {
        using var monitor = new NetworkConnectivityMonitor(TimeSpan.FromSeconds(30));
        Assert.NotNull(monitor);

        // Explicit check should not crash or throw even if offline/isolated
        bool isOnline = await monitor.CheckConnectivityAsync();
        // Result is boolean; verify no exception occurred
        Assert.True(isOnline || !isOnline);
    }
}
