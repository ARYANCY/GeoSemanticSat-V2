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
    public int Width { get; set; }
    public int Height { get; set; }
    public string SourceFilePath { get; set; } = string.Empty;
    public Dictionary<SpectralBand, float[,]> Bands { get; set; } = new();

    public float[]? EmbeddingVector { get; set; }

    public float[,] GetBandOrFallback(SpectralBand band, SpectralBand fallback)
    {
        if (Bands.TryGetValue(band, out var data)) return data;
        if (Bands.TryGetValue(fallback, out var fallbackData)) return fallbackData;
        return new float[Height, Width];
    }
}
