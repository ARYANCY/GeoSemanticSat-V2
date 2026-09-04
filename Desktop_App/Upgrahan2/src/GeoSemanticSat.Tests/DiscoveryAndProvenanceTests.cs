using System;
using System.Collections.Generic;
using System.Text.Json;
using GeoSemanticSat.Core.Clustering;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Workflow;
using GeoSemanticSat.Engine.Embeddings;
using Xunit;

namespace GeoSemanticSat.Tests;

public class DiscoveryAndProvenanceTests
{
    [Fact]
    public void SpatialSemanticClusterer_GroupsAnalogousInstallations()
    {
        var patches = new List<TilePatch>();

        // 4 airfield runway patches in close proximity
        for (int i = 0; i < 4; i++)
        {
            patches.Add(new TilePatch
            {
                PatchId = $"runway_{i}",
                Bounds = new BoundingBox(77.10 + i * 0.002, 28.40, 77.102 + i * 0.002, 28.402),
                EmbeddingVector = TextQueryEncoder.Encode("airfield runway asphalt corridor")
            });
        }

        // 4 water reservoir patches
        for (int i = 0; i < 4; i++)
        {
            patches.Add(new TilePatch
            {
                PatchId = $"water_{i}",
                Bounds = new BoundingBox(77.50 + i * 0.002, 28.80, 77.502 + i * 0.002, 28.802),
                EmbeddingVector = TextQueryEncoder.Encode("water reservoir river inundation")
            });
        }

        var clusters = SpatialSemanticClusterer.ClusterSites(patches, epsCosineDistance: 0.20, minPts: 2);

        Assert.True(clusters.Count >= 2, $"Expected at least 2 distinct clusters, got {clusters.Count}");
        Assert.All(clusters, c => Assert.True(c.CohesionScore > 0.80));
    }

    [Fact]
    public void ReviewQueue_ConfirmAndRejectWorkflow_MaintainsAuditState()
    {
        var queue = new ReviewQueue();
        var change1 = new ChangeRecord { Id = "CHG_001", Type = ChangeType.Construction, Confidence = 0.91 };
        var change2 = new ChangeRecord { Id = "CHG_002", Type = ChangeType.Clearance, Confidence = 0.78 };

        queue.Enqueue(change1);
        queue.Enqueue(change2);

        Assert.Equal(2, queue.Count);
        var pending = queue.GetPendingRanked();
        Assert.Equal("CHG_001", pending[0].Record.Id); // Ranked by confidence

        queue.Confirm("CHG_001", "Confirmed forward runway construction.");
        queue.Reject("CHG_002", "Seasonal agricultural crop rotation.");

        var all = queue.GetAll();
        var item1 = all.Find(i => i.Record.Id == "CHG_001");
        var item2 = all.Find(i => i.Record.Id == "CHG_002");

        Assert.NotNull(item1);
        Assert.Equal("Confirmed", item1.Status);
        Assert.True(item1.Record.ConfirmedByAnalyst);

        Assert.NotNull(item2);
        Assert.Equal("Rejected", item2.Status);
        Assert.True(item2.Record.RejectedByAnalyst);
    }

    [Fact]
    public void ProvenanceAuditTrail_ExportsValidGeoJsonFeatureCollection()
    {
        var changes = new List<ChangeRecord>
        {
            new ChangeRecord
            {
                Id = "CHG_PROV_1",
                TileId = "S2_20240320",
                Bounds = new BoundingBox(77.20, 28.60, 77.21, 28.61),
                Type = ChangeType.Construction,
                Confidence = 0.94,
                TimestampT1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                TimestampT2 = new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc),
                EarliestObservationTimestamp = new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc),
                AreaSqMeters = 2500,
                ProcessingNotes = "Validated change candidate."
            }
        };

        string geoJson = ProvenanceAuditTrail.ExportToGeoJson(changes);
        Assert.False(string.IsNullOrEmpty(geoJson));

        // Validate JSON structure
        using var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;
        Assert.Equal("FeatureCollection", root.GetProperty("type").GetString());

        var features = root.GetProperty("features");
        Assert.Equal(1, features.GetArrayLength());

        var f0 = features[0];
        Assert.Equal("Feature", f0.GetProperty("type").GetString());
        Assert.Equal("Polygon", f0.GetProperty("geometry").GetProperty("type").GetString());

        var props = f0.GetProperty("properties");
        Assert.Equal("CHG_PROV_1", props.GetProperty("changeId").GetString());
        Assert.Equal("Construction", props.GetProperty("changeType").GetString());
        Assert.True(props.TryGetProperty("provenance", out var prov));
        Assert.True(prov.TryGetProperty("prov:wasGeneratedBy", out _));
    }
}
