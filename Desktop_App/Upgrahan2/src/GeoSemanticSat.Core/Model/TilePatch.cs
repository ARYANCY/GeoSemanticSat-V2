using System;
using System.Collections.Generic;

namespace GeoSemanticSat.Core.Model;

public class TilePatch
{
    public string PatchId { get; set; } = string.Empty;
    public string ParentTileId { get; set; } = string.Empty;
    public SensorPlatform Platform { get; set; }
    public DateTime Timestamp { get; set; }
    public BoundingBox Bounds { get; set; }
    public int PixelX { get; set; }
    public int PixelY { get; set; }
    public int PatchWidth { get; set; }
    public int PatchHeight { get; set; }
    public float[] EmbeddingVector { get; set; } = Array.Empty<float>();
    public Dictionary<string, double> StatisticalFeatures { get; set; } = new();
    public double QualityScore { get; set; } = 1.0;
    public bool HasCloudOrShadow { get; set; }
    public double CloudCoverPercentage { get; set; }
    public double SunElevationDegrees { get; set; } = 45.0;
    public double? ViewZenithDegrees { get; set; }
    public string SourceFilePath { get; set; } = string.Empty;
}
