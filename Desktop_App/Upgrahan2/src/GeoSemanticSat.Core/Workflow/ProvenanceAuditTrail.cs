using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.Workflow;

/// <summary>
/// W3C PROV-O Aligned Provenance & Geospatial Export.
/// Retains source-scene IDs, sensors, timestamps, algorithm versions, confidence scores,
/// and analyst feedback into standard GeoJSON and JSON-LD audit reports.
/// </summary>
public static class ProvenanceAuditTrail
{
    public static string ExportToGeoJson(IEnumerable<ChangeRecord> changes, string systemVersion = "1.0.0-PROV", string? sourceImageSha256 = null, double? cvaThreshold = null)
    {
        var features = new List<object>();

        foreach (var c in changes)
        {
            var coordinates = new double[][][]
            {
                new double[][]
                {
                    new double[] { c.Bounds.MinLon, c.Bounds.MinLat },
                    new double[] { c.Bounds.MaxLon, c.Bounds.MinLat },
                    new double[] { c.Bounds.MaxLon, c.Bounds.MaxLat },
                    new double[] { c.Bounds.MinLon, c.Bounds.MaxLat },
                    new double[] { c.Bounds.MinLon, c.Bounds.MinLat }
                }
            };

            var provDict = new Dictionary<string, string>
            {
                ["prov:wasGeneratedBy"] = $"GeoSemanticSat-Engine-{systemVersion}",
                ["prov:generatedAtTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["prov:primarySource"] = c.TileId
            };
            if (!string.IsNullOrEmpty(sourceImageSha256))
            {
                provDict["prov:sourceImageSha256"] = sourceImageSha256;
            }

            var properties = new Dictionary<string, object>
            {
                ["changeId"] = c.Id,
                ["tileId"] = c.TileId,
                ["changeType"] = c.Type.ToString(),
                ["confidence"] = c.Confidence,
                ["timestampT1"] = c.TimestampT1.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["timestampT2"] = c.TimestampT2.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["earliestObservation"] = c.EarliestObservationTimestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["areaSqMeters"] = c.AreaSqMeters,
                ["affectedPixels"] = c.AffectedPixels,
                ["processingNotes"] = c.ProcessingNotes,
                ["confirmedByAnalyst"] = c.ConfirmedByAnalyst,
                ["rejectedByAnalyst"] = c.RejectedByAnalyst,
                ["analystNotes"] = c.AnalystNotes,
                ["provenance"] = provDict
            };

            foreach (var kvp in c.Metrics)
            {
                properties[kvp.Key] = kvp.Value;
            }

            features.Add(new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Polygon",
                    coordinates = coordinates
                },
                properties = properties
            });
        }

        var metadata = new Dictionary<string, object>
        {
            ["systemVersion"] = systemVersion,
            ["generatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["sourceImageSha256"] = sourceImageSha256 ?? "N/A",
            ["cvaThreshold"] = cvaThreshold ?? 0.05
        };

        var root = new Dictionary<string, object>
        {
            ["type"] = "FeatureCollection",
            ["crs"] = new
            {
                type = "name",
                properties = new { name = "urn:ogc:def:crs:OGC:1.3:CRS84" }
            },
            ["metadata"] = metadata,
            ["features"] = features
        };

        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void SaveGeoJson(string filePath, IEnumerable<ChangeRecord> changes, string? sourceImageSha256 = null, double? cvaThreshold = null)
    {
        string json = ExportToGeoJson(changes, "1.0.0-PROV", sourceImageSha256, cvaThreshold);
        File.WriteAllText(filePath, json, Encoding.UTF8);
    }
}
