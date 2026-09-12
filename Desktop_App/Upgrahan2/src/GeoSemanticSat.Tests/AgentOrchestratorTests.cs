using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Engine.Services;
using Xunit;

namespace GeoSemanticSat.Tests;

public class AgentOrchestratorTests
{
    private static ChangeRecord CreateTestCandidate(string id, ChangeType type, double confidence)
    {
        return new ChangeRecord
        {
            Id = id,
            TileId = "sentinel2-delhi-01",
            Bounds = new BoundingBox(28.59, 77.20, 28.62, 77.23),
            TimestampT1 = new DateTime(2024, 1, 10),
            TimestampT2 = new DateTime(2024, 4, 15),
            EarliestObservationTimestamp = new DateTime(2024, 3, 14),
            Type = type,
            Confidence = confidence,
            AreaSqMeters = 54000.0,
            Metrics = new Dictionary<string, double>
            {
                ["SpectralDifference"] = 0.74,
                ["DeltaNDVI"] = -0.32,
                ["DeltaNDBI"] = 0.28,
            }
        };
    }

    [Fact]
    public async Task ExecuteTaskAsync_RoadsPrompt_PlansChangeAnalysisAndSelectsBeforeAfter()
    {
        var service = new AgentClientService();
        var candidate = CreateTestCandidate("chg-road-01", ChangeType.RoadDevelopment, 0.91);
        var detected = new List<ChangeRecord> { candidate };

        var result = await service.ExecuteTaskAsync(
            prompt: "Show newly constructed roads in this area and display before and after imagery",
            activeCandidate: candidate,
            detectedChanges: detected
        );

        Assert.Equal("completed", result.Status);
        Assert.Equal("BEFORE_AFTER_REQUEST", result.Intent);
        Assert.NotEmpty(result.Plan);
        Assert.Contains(result.Plan, p => p.Contains("change_detection") || p.Contains("before_after"));
        Assert.NotNull(result.BeforeAfter);
        Assert.Equal("ROADDEVELOPMENT", result.BeforeAfter.ChangeType);
        Assert.Equal("2024-01-10", result.BeforeAfter.BeforeDate);
        Assert.Equal("2024-04-15", result.BeforeAfter.AfterDate);
        Assert.NotEmpty(result.Evidence);
    }

    [Fact]
    public async Task ExecuteTaskAsync_SitrepPrompt_ReturnsMilitaryStandardIntelligence()
    {
        var service = new AgentClientService();
        var candidate = CreateTestCandidate("chg-sitrep-01", ChangeType.Construction, 0.88);

        var result = await service.ExecuteTaskAsync(
            prompt: "Generate a military-standard SITREP brief for this site",
            activeCandidate: candidate
        );

        Assert.Equal("completed", result.Status);
        Assert.Equal("SITREP_REPORT", result.Intent);
        Assert.Contains("SITREP", result.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Centroid", result.Answer);
        Assert.NotNull(result.TargetCenter);
        Assert.True(result.TargetCenter.Value.Latitude > 28.0);
    }

    [Fact]
    public async Task ExecuteTaskAsync_EmptyPrompt_FailsGracefully()
    {
        var service = new AgentClientService();

        var result = await service.ExecuteTaskAsync(string.Empty);

        Assert.Equal("failed", result.Status);
        Assert.Empty(result.Plan);
        Assert.Null(result.BeforeAfter);
    }

    [Fact]
    public async Task ExecuteTaskAsync_SimilarSitesPrompt_SelectsDiscoveryIntent()
    {
        var service = new AgentClientService();
        var candidate = CreateTestCandidate("chg-sim-01", ChangeType.Construction, 0.85);

        var result = await service.ExecuteTaskAsync(
            prompt: "Find similar sites and facility clusters",
            activeCandidate: candidate
        );

        Assert.Equal("completed", result.Status);
        Assert.Equal("SIMILAR_SITE_DISCOVERY", result.Intent);
        Assert.Contains(result.Plan, p => p.Contains("similar") || p.Contains("search"));
    }
}
