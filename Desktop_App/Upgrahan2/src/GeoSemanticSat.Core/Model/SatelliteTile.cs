using System;
using System.Collections.Generic;

namespace GeoSemanticSat.Core.Model;

public class SatelliteTile
{
    public string TileId { get; set; } = string.Empty;
    public SensorPlatform Platform { get; set; }
    public DateTime AcquisitionTimestamp { get; set; }
    public BoundingBox Bounds { get; set; }
    public AffineGeoTransform Transform { get; set; } = null!;
    public double GroundSamplingDistanceMeters { get; set; } = 10.0;
    public double CloudCoverPercentage { get; set; }
    public double SunElevationDegrees { get; set; } = 45.0;
    public double SunAzimuthDegrees { get; set; } = 135.0;
    /// <summary>Optional sensor off-nadir / view zenith in degrees when present in metadata. Not estimated.</summary>
    public double? ViewZenithDegrees { get; set; }
    public bool AcquisitionTimestampIsKnown { get; set; } = true;
    public int Width { get; set; }
    public int Height { get; set; }
    public int EpsgCode { get; set; } = 4326;
    public double? NoDataValue { get; set; }
    public string? SourceImageSha256 { get; set; }
    public string SourceFilePath { get; set; } = string.Empty;
    public Dictionary<SpectralBand, float[,]> Bands { get; set; } = new();

    public float[]? EmbeddingVector { get; set; }

    /// <summary>True when the band is genuinely present, not substituted by GetBandOrFallback.</summary>
    public bool HasBand(SpectralBand band) => Bands.ContainsKey(band);

    /// <summary>
    /// True when the tile lacks NIR or SWIR, so NDVI/NDBI/NDWI/BSI cannot be computed as
    /// defined and visible-band proxies are used instead. GetBandOrFallback silently
    /// substitutes Red for a missing NIR, which made NDVI = (R-R)/(R+R) = 0 and NDBI = 0
    /// for every pixel of every RGB scene, with no indication to the caller.
    /// </summary>
    public bool RequiresVisibleBandProxies => !HasBand(SpectralBand.NIR) || !HasBand(SpectralBand.SWIR1);

    public float[,] GetBandOrFallback(SpectralBand band, SpectralBand fallback)
    {
        if (Bands.TryGetValue(band, out var data)) return data;
        if (Bands.TryGetValue(fallback, out var fallbackData)) return fallbackData;
        return new float[Height, Width];
    }
}
