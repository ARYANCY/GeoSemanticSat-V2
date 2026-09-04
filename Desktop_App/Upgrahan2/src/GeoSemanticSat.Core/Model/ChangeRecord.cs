using System;
using System.Collections.Generic;

namespace GeoSemanticSat.Core.Model;

public class ChangeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TileId { get; set; } = string.Empty;
    public BoundingBox Bounds { get; set; }
    public GeoCoordinate Center => Bounds.Center;
    public DateTime TimestampT1 { get; set; }
    public DateTime TimestampT2 { get; set; }
    public DateTime EarliestObservationTimestamp { get; set; }
    public ChangeType Type { get; set; } = ChangeType.NoChange;
    public double Confidence { get; set; }
    public int AffectedPixels { get; set; }
    public double AreaSqMeters { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    public string ProcessingNotes { get; set; } = string.Empty;
    public bool ConfirmedByAnalyst { get; set; }
    public bool RejectedByAnalyst { get; set; }
    public string AnalystNotes { get; set; } = string.Empty;
}
