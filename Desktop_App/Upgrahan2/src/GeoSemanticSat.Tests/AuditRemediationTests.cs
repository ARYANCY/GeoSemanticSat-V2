using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using GeoSemanticSat.Engine.Services;
using GeoSemanticSat.UI.Controls;

namespace GeoSemanticSat.Tests;

public class AuditRemediationTests
{
    [Fact]
    public async Task AgentClientService_SupportsCancellationTokenGracefully()
    {
        // Test BUG-0010: CancellationToken support in ExecuteTaskAsync
        var client = new AgentClientService("http://127.0.0.1:9999"); // Unused port
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled token

        // When cancelled, should gracefully return in-process fallback without unhandled exception
        var result = await client.ExecuteTaskAsync("Test prompt under cancellation", null, null, null, cts.Token);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.Intent));
    }

    [Fact]
    public void InteractiveMapCanvas_ComputesConformalLonScaleFactorCorrectly()
    {
        // Test BUG-0012: Conformal latitude cosine scaling
        var canvas = new InteractiveMapCanvas();

        // Pan to New Delhi test latitude 28.6050
        canvas.PanTo(28.6050, 77.2080);
        double factorDelhi = canvas.GetLonScaleFactor();

        // Cos(28.605 deg) = ~0.8779
        Assert.InRange(factorDelhi, 0.85, 0.90);

        // At Equator (0.0 deg), factor should be ~1.0
        canvas.PanTo(0.0, 0.0);
        double factorEquator = canvas.GetLonScaleFactor();
        Assert.InRange(factorEquator, 0.99, 1.0);
    }

    [Fact]
    public async Task AnalystChatService_GracefullyRecoversFromUnreachableBackend()
    {
        // Test in-process deterministic fallback when HTTP backend is offline
        var chatService = new AnalystChatService("http://127.0.0.1:9999");
        string response = await chatService.AskAnalystAsync("Why was this change flagged?", null);

        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response));
        Assert.Contains("analysis", response.ToLowerInvariant());
    }

    [Fact]
    public void VectorIndex_UpsertUnlocked_DeduplicatesAndUpdatesInPlace()
    {
        var index = new GeoSemanticSat.Core.VectorIndex.VectorIndex(128);
        var p1 = new GeoSemanticSat.Core.Model.TilePatch
        {
            PatchId = "patch_dup_test",
            QualityScore = 0.5,
            EmbeddingVector = new float[128]
        };
        p1.EmbeddingVector[0] = 1.0f;
        index.Add(p1);
        Assert.Equal(1, index.Count);

        var p2 = new GeoSemanticSat.Core.Model.TilePatch
        {
            PatchId = "patch_dup_test",
            QualityScore = 0.95,
            EmbeddingVector = new float[128]
        };
        p2.EmbeddingVector[0] = 0.8f;
        p2.EmbeddingVector[1] = 0.6f;
        index.Add(p2);

        // Count must still be 1 (deduplicated)
        Assert.Equal(1, index.Count);
        var retrieved = index.GetAllPatches()[0];
        Assert.Equal(0.95, retrieved.QualityScore);
    }

    [Fact]
    public void VectorIndex_Version2_PreservesOpticalMetadata()
    {
        string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"v2_test_{Guid.NewGuid():N}.bin");
        try
        {
            var index = new GeoSemanticSat.Core.VectorIndex.VectorIndex(128);
            var patch = new GeoSemanticSat.Core.Model.TilePatch
            {
                PatchId = "patch_v2_metadata",
                CloudCoverPercentage = 12.5,
                SunElevationDegrees = 52.3,
                ViewZenithDegrees = 7.8,
                SourceFilePath = "/data/scenes/sentinel2_test.tif",
                QualityScore = 0.92,
                EmbeddingVector = new float[128]
            };
            patch.EmbeddingVector[0] = 1.0f;
            index.Add(patch);

            index.SaveIndex(tempFile);
            Assert.True(System.IO.File.Exists(tempFile));

            var loaded = GeoSemanticSat.Core.VectorIndex.VectorIndex.LoadIndex(tempFile);
            Assert.Equal(1, loaded.Count);
            var loadedPatch = loaded.GetAllPatches()[0];

            Assert.Equal("patch_v2_metadata", loadedPatch.PatchId);
            Assert.Equal(12.5, loadedPatch.CloudCoverPercentage, 3);
            Assert.Equal(52.3, loadedPatch.SunElevationDegrees, 3);
            Assert.True(loadedPatch.ViewZenithDegrees.HasValue);
            Assert.Equal(7.8, loadedPatch.ViewZenithDegrees!.Value, 3);
            Assert.Equal("/data/scenes/sentinel2_test.tif", loadedPatch.SourceFilePath);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
        }
    }
}
