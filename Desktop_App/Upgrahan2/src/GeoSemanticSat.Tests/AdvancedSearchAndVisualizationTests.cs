using System;
using System.Collections.Generic;
using System.IO;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Engine.Embeddings;
using Xunit;

namespace GeoSemanticSat.Tests;

public class AdvancedSearchAndVisualizationTests
{
    [Fact]
    public void RasterVisualizer_CreatesValidBmpStreamWithHeader()
    {
        int w = 32, h = 32;
        byte[] bgra = new byte[w * h * 4];
        Array.Fill(bgra, (byte)128);

        using var ms = RasterVisualizer.CreateBmpStream(bgra, w, h);
        Assert.NotNull(ms);
        Assert.True(ms.Length > 54);

        using var br = new BinaryReader(ms);
        byte b1 = br.ReadByte();
        byte b2 = br.ReadByte();
        Assert.Equal((byte)'B', b1);
        Assert.Equal((byte)'M', b2);

        int fileSize = br.ReadInt32();
        Assert.Equal(54 + (w * h * 4), fileSize);
    }

    [Fact]
    public void ChangeSearchEngine_FiltersByLatLonRadiusTimeAndType()
    {
        var engine = new ChangeSearchEngine();

        // 1. Change A at (28.605, 77.208), Construction, 2024-03-20
        var chgA = new ChangeRecord
        {
            Id = "CHANGE_A",
            Type = ChangeType.Construction,
            Confidence = 0.92,
            Bounds = new BoundingBox(77.2070, 28.6040, 77.2090, 28.6060),
            TimestampT1 = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            TimestampT2 = new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            EarliestObservationTimestamp = new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            ProcessingNotes = "New bunker structure."
        };

        // 2. Change B at (28.592, 77.203), WaterExtentVariation, 2024-03-20
        var chgB = new ChangeRecord
        {
            Id = "CHANGE_B",
            Type = ChangeType.WaterExtentVariation,
            Confidence = 0.85,
            Bounds = new BoundingBox(77.2020, 28.5910, 77.2040, 28.5930),
            TimestampT1 = new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            TimestampT2 = new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            EarliestObservationTimestamp = new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc),
            ProcessingNotes = "Riverbank inundation."
        };

        // 3. Change C far away at (29.500, 78.500), Construction, 2024-05-15
        var chgC = new ChangeRecord
        {
            Id = "CHANGE_C",
            Type = ChangeType.Construction,
            Confidence = 0.90,
            Bounds = new BoundingBox(78.490, 29.490, 78.510, 29.510),
            TimestampT1 = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            TimestampT2 = new DateTime(2024, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            EarliestObservationTimestamp = new DateTime(2024, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            ProcessingNotes = "Remote airbase pad."
        };

        engine.AddRange(new[] { chgA, chgB, chgC });

        // Query 1: Search within 5 km of (28.605, 77.208), Construction only
        var criteria1 = new ChangeSearchCriteria(
            Center: new GeoCoordinate(28.605, 77.208),
            RadiusKm: 5.0,
            TargetChangeType: ChangeType.Construction
        );

        var res1 = engine.Search(criteria1);
        Assert.Single(res1);
        Assert.Equal("CHANGE_A", res1[0].Record.Id);
        Assert.True(res1[0].DistanceKm < 1.0);

        // Query 2: Search with Time range [2024-01-01, 2024-04-01]
        var criteria2 = new ChangeSearchCriteria(
            StartDate: new DateTime(2024, 1, 1),
            EndDate: new DateTime(2024, 4, 1)
        );
        var res2 = engine.Search(criteria2);
        Assert.Equal(2, res2.Count);
        Assert.Contains(res2, r => r.Record.Id == "CHANGE_A");
        Assert.Contains(res2, r => r.Record.Id == "CHANGE_B");
        Assert.DoesNotContain(res2, r => r.Record.Id == "CHANGE_C");
    }

    [Fact]
    public void VectorIndex_RadialProximitySearch_FiltersCorrectly()
    {
        var index = new VectorIndex(128);

        var nearPatch = new TilePatch
        {
            PatchId = "near_patch",
            Bounds = new BoundingBox(77.205, 28.604, 77.207, 28.606),
            Timestamp = DateTime.UtcNow,
            EmbeddingVector = TextQueryEncoder.Encode("structure near river")
        };

        var farPatch = new TilePatch
        {
            PatchId = "far_patch",
            Bounds = new BoundingBox(78.500, 29.500, 78.510, 29.510), // ~150 km away
            Timestamp = DateTime.UtcNow,
            EmbeddingVector = TextQueryEncoder.Encode("structure near river")
        };

        index.Add(nearPatch);
        index.Add(farPatch);

        var filter = new SearchFilter(
            CenterCoordinate: new GeoCoordinate(28.605, 77.206),
            RadiusKm: 10.0 // 10 km search circle
        );

        float[] queryVec = TextQueryEncoder.Encode("structure near river");
        var results = index.Search(queryVec, topK: 10, filter: filter);

        Assert.Single(results);
        Assert.Equal("near_patch", results[0].Patch.PatchId);
    }
}
