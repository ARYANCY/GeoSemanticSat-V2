using System;
using System.IO;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Core.Workflow;
using GeoSemanticSat.Engine.Embeddings;
using GeoSemanticSat.Engine.Retrieval;
using Xunit;

namespace GeoSemanticSat.Tests;

public class VectorIndexAndRetrievalTests
{
    [Fact]
    public void VectorIndex_IncrementalAddAndSearch_ReturnsTopRanked()
    {
        var index = new VectorIndex(128);

        // Add 3 distinct synthetic patches
        var p1 = new TilePatch
        {
            PatchId = "patch_water",
            Bounds = new BoundingBox(77.0, 28.0, 77.1, 28.1),
            Timestamp = DateTime.UtcNow,
            EmbeddingVector = TextQueryEncoder.Encode("river waterbody")
        };

        var p2 = new TilePatch
        {
            PatchId = "patch_construction",
            Bounds = new BoundingBox(77.2, 28.0, 77.3, 28.1),
            Timestamp = DateTime.UtcNow,
            EmbeddingVector = TextQueryEncoder.Encode("concrete building facility")
        };

        var p3 = new TilePatch
        {
            PatchId = "patch_forest",
            Bounds = new BoundingBox(77.4, 28.0, 77.5, 28.1),
            Timestamp = DateTime.UtcNow,
            EmbeddingVector = TextQueryEncoder.Encode("dense green forest vegetation")
        };

        index.Add(p1);
        index.Add(p2);
        index.Add(p3);

        Assert.Equal(3, index.Count);

        var engine = new SemanticSearchEngine(index);

        // Query: "facility building" should rank patch_construction #1
        var results = engine.SearchByText("building structure facility", topK: 1);
        Assert.Single(results);
        Assert.Equal("patch_construction", results[0].Patch.PatchId);

        // Query: "river water" should rank patch_water #1
        var waterResults = engine.SearchByText("river stream water", topK: 1);
        Assert.Single(waterResults);
        Assert.Equal("patch_water", waterResults[0].Patch.PatchId);
    }

    [Fact]
    public void VectorIndex_BinaryPersistence_SavesAndLoadsWithoutLoss()
    {
        string tempIndexFile = Path.Combine(Path.GetTempPath(), $"index_{Guid.NewGuid():N}.bin");
        try
        {
            var original = new VectorIndex(128);
            for (int i = 0; i < 10; i++)
            {
                original.Add(new TilePatch
                {
                    PatchId = $"test_patch_{i}",
                    Bounds = new BoundingBox(77.0 + i * 0.01, 28.0, 77.01 + i * 0.01, 28.01),
                    Timestamp = DateTime.UtcNow,
                    EmbeddingVector = TextQueryEncoder.Encode($"query {i}")
                });
            }

            original.SaveIndex(tempIndexFile);
            Assert.True(File.Exists(tempIndexFile));

            var loaded = VectorIndex.LoadIndex(tempIndexFile);
            Assert.Equal(original.Count, loaded.Count);
            Assert.Equal(original.VectorDimension, loaded.VectorDimension);

            var query = TextQueryEncoder.Encode("query 5");
            var origRes = original.Search(query, topK: 1);
            var loadRes = loaded.Search(query, topK: 1);

            Assert.Equal(origRes[0].Patch.PatchId, loadRes[0].Patch.PatchId);
            Assert.Equal(origRes[0].SimilarityScore, loadRes[0].SimilarityScore, 4);
        }
        finally
        {
            if (File.Exists(tempIndexFile)) File.Delete(tempIndexFile);
        }
    }

    [Fact]
    public void RelevanceFeedback_ReranksBasedOnAnalystConfirmedPositives()
    {
        float[] baseQuery = TextQueryEncoder.Encode("structure");
        float[] positivePatch = TextQueryEncoder.Encode("river waterbody near building");
        float[] negativePatch = TextQueryEncoder.Encode("open desert sand");

        float[] adjusted = RelevanceFeedbackReranker.AdjustQueryVector(
            baseQuery,
            new[] { positivePatch },
            new[] { negativePatch }
        );

        // Adjusted query should have higher similarity to water than base query
        float[] waterProbe = TextQueryEncoder.Encode("river water");
        double baseSim = VectorIndex.DotProduct(baseQuery, waterProbe);
        double adjSim = VectorIndex.DotProduct(adjusted, waterProbe);

        Assert.True(adjSim > baseSim, "Relevance feedback should boost dimensions of confirmed positive features.");
    }
}
