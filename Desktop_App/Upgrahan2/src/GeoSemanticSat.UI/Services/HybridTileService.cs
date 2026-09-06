using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace GeoSemanticSat.UI.Services;

public enum MapTileMode
{
    /// <summary>
    /// Checks local disk cache first; fetches from open-source network provider if missing and saves to local disk.
    /// </summary>
    Auto,

    /// <summary>
    /// Strict 100% air-gapped mode. Never issues network requests; only loads from local disk or offline bundles.
    /// </summary>
    OfflineStrict,

    /// <summary>
    /// Checks network first to obtain newest satellite/basemap tiles, falling back to local disk cache.
    /// </summary>
    OnlinePreferred
}

public class BasemapProvider
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string UrlTemplate { get; set; }
    public string Attribution { get; set; } = "Open-Source Map Provider";
    public int MaxZoom { get; set; } = 19;
    public int MinZoom { get; set; } = 0;
    public bool IsSatellite { get; set; } = false;

    /// <summary>
    /// Name of the .env variable holding this provider's API key, or null when the
    /// provider is keyless. Keys are never stored in source.
    /// </summary>
    public string? ApiKeyEnvVar { get; set; }

    /// <summary>True when the provider cannot serve the whole globe.</summary>
    public bool IsRegionLimited { get; set; }
}

public class PrecacheProgressEventArgs : EventArgs
{
    public int CompletedTiles { get; }
    public int TotalTiles { get; }
    public double ProgressPercentage => TotalTiles > 0 ? (double)CompletedTiles / TotalTiles * 100.0 : 0;
    public string StatusMessage { get; }

    public PrecacheProgressEventArgs(int completed, int total, string status)
    {
        CompletedTiles = completed;
        TotalTiles = total;
        StatusMessage = status;
    }
}

/// <summary>
/// High-Performance Hybrid Map Tile Engine.
/// Provides a 4-tier tile caching architecture:
/// 1. In-Memory L1 LRU Cache (Fastest 60 FPS rendering, bounded capacity)
/// 2. Local Disk L2 Cache (Persistent offline storage)
/// 3. Asynchronous Non-Blocking Network Tile Fetcher (OpenStreetMap, CartoDB Dark Matter, ESRI, Sentinel-2)
/// 4. Procedural Tactical Graticule Fallback when completely offline without cached tiles.
/// </summary>
public class HybridTileService : IDisposable
{
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(3)
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    static HybridTileService()
    {
        try
        {
            HttpClient.DefaultRequestHeaders.Add("User-Agent", "GeoSemanticSat-Analyst-Platform/3.0 (Defense/Intelligence Hybrid Map Engine)");
        }
        catch
        {
            // header already set
        }
    }

    // Standard Basemap Registry
    public static readonly BasemapProvider CartoDark = new()
    {
        Id = "carto-dark",
        Name = "CartoDB Dark Matter (Tactical)",
        Description = "Minimalist dark theme optimized for high-contrast intelligence overlay",
        UrlTemplate = "https://a.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",
        Attribution = "(C) OpenStreetMap contributors, (C) CARTO",
        MaxZoom = 19,
        IsSatellite = false,
        // CartoDB serves an "API KEY REQUIRED" watermark tile with HTTP 200, so a missing
        // key cannot be detected as a failure. Supply one via .env to use it.
        ApiKeyEnvVar = "CARTO_API_KEY"
    };

    public static readonly BasemapProvider OpenStreetMap = new()
    {
        Id = "osm-standard",
        Name = "OpenStreetMap Standard",
        Description = "Global crowdsourced topographic and street map",
        UrlTemplate = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
        Attribution = "(C) OpenStreetMap contributors",
        MaxZoom = 19,
        IsSatellite = false
    };

    public static readonly BasemapProvider EsriSatellite = new()
    {
        Id = "esri-satellite",
        Name = "ESRI World Imagery (Satellite)",
        Description = "High-resolution optical satellite and aerial orthophoto basemap",
        UrlTemplate = "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
        Attribution = "Esri, Maxar, Earthstar Geographics",
        MaxZoom = 19,
        IsSatellite = true
    };

    public static readonly BasemapProvider Sentinel2Cloudless = new()
    {
        Id = "sentinel2-cloudless",
        Name = "Sentinel-2 Cloudless 2024 (EOX 10m)",
        Description = "Global seamless 10-meter multi-spectral composite by EOX",
        UrlTemplate = "https://tiles.maps.eox.at/wmts/1.0.0/s2cloudless-2024_3857/default/g/{z}/{y}/{x}.jpg",
        Attribution = "EOxCloudless https://cloudless.eox.at by EOX IT Services GmbH (Contains modified Copernicus Sentinel data 2024). CC BY-NC-SA 4.0, non-commercial use only",
        MaxZoom = 16,
        IsSatellite = true
    };

    public static readonly BasemapProvider UsgsImagery = new()
    {
        Id = "usgs-imagery",
        Name = "USGS Imagery (United States only)",
        Description = "Public domain USGS imagery. UNITED STATES COVERAGE ONLY - returns no tiles outside the US",
        UrlTemplate = "https://basemap.nationalmap.gov/arcgis/rest/services/USGSImageryOnly/MapServer/tile/{z}/{y}/{x}",
        Attribution = "USGS The National Map / US Department of the Interior",
        MaxZoom = 16,
        IsSatellite = true,
        // Verified: Delhi returns HTTP 404, Denver returns 200. US coverage only.
        IsRegionLimited = true
    };

    public static readonly IReadOnlyList<BasemapProvider> AvailableProviders = new List<BasemapProvider>
    {
        CartoDark,
        EsriSatellite,
        Sentinel2Cloudless,
        OpenStreetMap,
        UsgsImagery
    };

    // L1 LRU Cache
    private readonly int _maxMemoryCacheCount;
    private readonly ConcurrentDictionary<string, Bitmap> _l1MemoryCache = new();
    private readonly LinkedList<string> _lruOrder = new();
    private readonly object _lruLock = new();

    // In-flight fetch tracking & concurrency limiter
    private readonly ConcurrentDictionary<string, bool> _activeRequests = new();
    private readonly SemaphoreSlim _downloadThrottle = new(4, 4);

    public string LocalCacheDirectory { get; set; }
    public MapTileMode Mode { get; set; } = MapTileMode.Auto;
    private BasemapProvider _activeProvider = EsriSatellite;

    /// <summary>
    /// Default is ESRI satellite imagery: the only free, keyless provider with global
    /// coverage and no watermark. CartoDB returns HTTP 200 for its "API KEY REQUIRED"
    /// watermark tile, so the app cannot detect that failure.
    /// </summary>
    public BasemapProvider ActiveProvider
    {
        get => _activeProvider;
        set
        {
            if (ReferenceEquals(_activeProvider, value)) return;
            _activeProvider = value;
            LastProviderError = null;
        }
    }

    public event Action? TileAvailable;

    /// <summary>Raised when a basemap provider returns a non-success status.</summary>
    public event EventHandler<string>? ProviderFailed;

    /// <summary>Most recent provider failure, or null while the basemap is healthy.</summary>
    public string? LastProviderError { get; private set; }
    public event EventHandler<PrecacheProgressEventArgs>? PrecacheProgress;

    public HybridTileService(string? customCacheDir = null, int maxMemoryCacheCount = 256)
    {
        _maxMemoryCacheCount = maxMemoryCacheCount;

        if (!string.IsNullOrEmpty(customCacheDir))
        {
            LocalCacheDirectory = customCacheDir;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            LocalCacheDirectory = Path.Combine(appData, "GeoSemanticSat", "MapTileCache");
        }

        try
        {
            if (!Directory.Exists(LocalCacheDirectory))
            {
                Directory.CreateDirectory(LocalCacheDirectory);
            }
        }
        catch
        {
            LocalCacheDirectory = Path.Combine(Path.GetTempPath(), "GeoSemanticSat", "MapTileCache");
            Directory.CreateDirectory(LocalCacheDirectory);
        }
    }

    /// <summary>
    /// Retrieves a tile bitmap from memory L1 or disk L2 synchronously, or queues background fetch.
    /// Returns null immediately if tile is not currently ready, avoiding UI thread stalls.
    /// </summary>
    public Bitmap? GetTile(int z, int x, int y)
    {
        // Snapshot the provider ONCE. Reading ActiveProvider again inside the async fetch
        // let a mid-flight basemap switch write one provider's tiles into another
        // provider's cache folder, and the poisoned entry then persisted on disk.
        var provider = ActiveProvider;
        string key = $"{provider.Id}_{z}_{x}_{y}";

        // 1. Check L1 Memory Cache
        if (_l1MemoryCache.TryGetValue(key, out var cachedBitmap))
        {
            TouchLruKey(key);
            return cachedBitmap;
        }

        // 2. Check L2 Disk Cache
        string diskPath = GetDiskCachePath(provider.Id, z, x, y);
        if (File.Exists(diskPath))
        {
            try
            {
                using var fs = File.OpenRead(diskPath);
                var bmp = new Bitmap(fs);
                PutL1Cache(key, bmp);
                return bmp;
            }
            catch
            {
                // Corrupt file, allow re-fetch
            }
        }

        // 3. Trigger Async Fetch if not strictly offline
        if (Mode != MapTileMode.OfflineStrict)
        {
            if (_activeRequests.TryAdd(key, true))
            {
                _ = FetchTileWithRetryAsync(provider, z, x, y, key, diskPath);
            }
        }

        return null;
    }

    private async Task FetchTileWithRetryAsync(BasemapProvider provider, int z, int x, int y, string key, string diskPath)
    {
        int maxRetries = 2;
        int delayMs = 150;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _downloadThrottle.WaitAsync();

                string url = provider.UrlTemplate
                    .Replace("{z}", z.ToString())
                    .Replace("{x}", x.ToString())
                    .Replace("{y}", y.ToString());

                // Keys come from .env, never from source. Every default provider is
                // keyless, so this is a no-op unless one is configured.
                if (!string.IsNullOrEmpty(provider.ApiKeyEnvVar))
                {
                    string? apiKey = EnvironmentConfig.Get(provider.ApiKeyEnvVar);
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        url += (url.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(apiKey);
                    }
                }

                using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    // A 404 does not throw, so the old loop retried the same failing request
                    // three times and gave up without telling anyone. A dead basemap looked
                    // identical to a deliberately dark one.
                    LastProviderError = $"{provider.Name}: HTTP {(int)response.StatusCode}";
                    ProviderFailed?.Invoke(this, LastProviderError);
                    break;
                }

                {
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();

                    // Save to L2 Disk Cache
                    string? dir = Path.GetDirectoryName(diskPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    await File.WriteAllBytesAsync(diskPath, bytes);

                    // Put in L1 Memory Cache
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    PutL1Cache(key, bmp);

                    // Notify UI to re-render tile
                    TileAvailable?.Invoke();
                    break;
                }
            }
            catch
            {
                if (attempt < maxRetries)
                {
                    await Task.Delay(delayMs);
                    delayMs *= 2;
                }
            }
            finally
            {
                _downloadThrottle.Release();
            }
        }

        _activeRequests.TryRemove(key, out _);
    }

    private void PutL1Cache(string key, Bitmap bmp)
    {
        lock (_lruLock)
        {
            if (_l1MemoryCache.Count >= _maxMemoryCacheCount)
            {
                var oldest = _lruOrder.First;
                if (oldest != null)
                {
                    _lruOrder.RemoveFirst();
                    _l1MemoryCache.TryRemove(oldest.Value, out _);
                }
            }

            _l1MemoryCache[key] = bmp;
            _lruOrder.Remove(key);
            _lruOrder.AddLast(key);
        }
    }

    private void TouchLruKey(string key)
    {
        lock (_lruLock)
        {
            _lruOrder.Remove(key);
            _lruOrder.AddLast(key);
        }
    }

    public string GetDiskCachePath(string providerId, int z, int x, int y)
    {
        string ext = (providerId == Sentinel2Cloudless.Id) ? "jpg" : "png";
        return Path.Combine(LocalCacheDirectory, providerId, z.ToString(), x.ToString(), $"{y}.{ext}");
    }

    /// <summary>
    /// Pre-caches an entire Area of Interest (AOI) bounding box across specified zoom levels.
    /// </summary>
    public async Task PrecacheRegionAsync(double minLat, double minLon, double maxLat, double maxLon, int minZoom, int maxZoom, CancellationToken ct = default)
    {
        var tileList = new List<(int z, int x, int y)>();

        for (int z = minZoom; z <= maxZoom; z++)
        {
            var (minX, maxY) = LonLatToTile(minLon, minLat, z);
            var (maxX, minY) = LonLatToTile(maxLon, maxLat, z);

            for (int x = Math.Min(minX, maxX); x <= Math.Max(minX, maxX); x++)
            {
                for (int y = Math.Min(minY, maxY); y <= Math.Max(minY, maxY); y++)
                {
                    tileList.Add((z, x, y));
                }
            }
        }

        int total = tileList.Count;
        int completed = 0;

        PrecacheProgress?.Invoke(this, new PrecacheProgressEventArgs(0, total, $"Starting pre-cache of {total} tiles..."));

        foreach (var (z, x, y) in tileList)
        {
            if (ct.IsCancellationRequested) break;

            GetTile(z, x, y);
            completed++;

            if (completed % 10 == 0 || completed == total)
            {
                PrecacheProgress?.Invoke(this, new PrecacheProgressEventArgs(completed, total, $"Caching: {completed}/{total} tiles"));
            }

            await Task.Delay(30, ct); // Fair-use rate limiter
        }

        PrecacheProgress?.Invoke(this, new PrecacheProgressEventArgs(completed, total, $"Pre-cache completed ({completed} tiles)."));
    }

    public long GetDiskCacheSizeBytes()
    {
        if (!Directory.Exists(LocalCacheDirectory)) return 0;
        try
        {
            return Directory.GetFiles(LocalCacheDirectory, "*.*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    public void ClearMemoryCache()
    {
        lock (_lruLock)
        {
            _l1MemoryCache.Clear();
            _lruOrder.Clear();
        }
    }

    public void ClearDiskCache()
    {
        ClearMemoryCache();
        try
        {
            if (Directory.Exists(LocalCacheDirectory))
            {
                Directory.Delete(LocalCacheDirectory, true);
                Directory.CreateDirectory(LocalCacheDirectory);
            }
        }
        catch
        {
            // Ignore if files locked
        }
    }

    public static (int x, int y) LonLatToTile(double lon, double lat, int zoom)
    {
        double clampedLat = Math.Clamp(lat, -85.05112878, 85.05112878);
        double clampedLon = Math.Clamp(lon, -180.0, 180.0);

        double n = Math.Pow(2.0, zoom);
        int x = (int)Math.Floor((clampedLon + 180.0) / 360.0 * n);
        
        double latRad = clampedLat * Math.PI / 180.0;
        double yVal = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        int y = (int)Math.Floor(yVal);

        x = Math.Clamp(x, 0, (int)n - 1);
        y = Math.Clamp(y, 0, (int)n - 1);

        return (x, y);
    }

    public static (double lon, double lat) TileToLonLat(int x, int y, int zoom)
    {
        double n = Math.Pow(2.0, zoom);
        double lon = (double)x / n * 360.0 - 180.0;
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * (double)y / n)));
        double lat = latRad * 180.0 / Math.PI;

        return (lon, lat);
    }

    public void Dispose()
    {
        _downloadThrottle.Dispose();
        ClearMemoryCache();
    }
}
