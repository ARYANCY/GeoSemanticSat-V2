using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Engine.Services;
using Xunit;

namespace GeoSemanticSat.Tests;

public class AnalystChatAndInsightTests
{
    private static ChangeRecord CreateTestCandidate(ChangeType type, double confidence, double spectralDiff)
    {
        return new ChangeRecord
        {
            Id = "candidate-test-01",
            TileId = "sentinel2-tile-01",
            Bounds = new BoundingBox(28.60, 77.20, 28.62, 77.22),
            TimestampT1 = new DateTime(2024, 1, 10),
            TimestampT2 = new DateTime(2024, 4, 15),
            EarliestObservationTimestamp = new DateTime(2024, 3, 14),
            Type = type,
            Confidence = confidence,
            AreaSqMeters = 45000.0,
            Metrics = new Dictionary<string, double>
            {
                ["SpectralDifference"] = spectralDiff,
                ["DeltaNDVI"] = -0.35,
                ["DeltaNDBI"] = 0.29,
            }
        };
    }

    [Fact]
    public void GenerateInsight_HighConfidenceConstruction_ReturnsCriticalSeverity()
    {
        var service = new AnalystChatService();
        var candidate = CreateTestCandidate(ChangeType.Construction, confidence: 0.92, spectralDiff: 0.78);

        var insight = service.GenerateInsight(candidate);

        Assert.Equal("CRITICAL", insight.Severity);
        Assert.Equal("CONSTRUCTION", insight.ChangeClass);
        Assert.True(insight.Confidence > 0.90);
        Assert.Contains(insight.PhysicalEvidence, e => e.Contains("NDBI") || e.Contains("built-up"));
        Assert.Contains("2024-03-14", insight.Timeline);
        Assert.NotEmpty(insight.Recommendations);
    }

    [Fact]
    public void GenerateInsight_ClearanceCandidate_DetectsVegetationBiomassLoss()
    {
        var service = new AnalystChatService();
        var candidate = CreateTestCandidate(ChangeType.Clearance, confidence: 0.82, spectralDiff: 0.65);

        var insight = service.GenerateInsight(candidate);

        Assert.Equal("HIGH", insight.Severity);
        Assert.Contains(insight.PhysicalEvidence, e => e.Contains("vegetation") || e.Contains("NDVI"));
    }

    [Fact]
    public async Task AskAnalystAsync_ReportQuery_GeneratesMilitaryStandardSitrep()
    {
        var service = new AnalystChatService();
        var candidate = CreateTestCandidate(ChangeType.Construction, confidence: 0.89, spectralDiff: 0.72);

        string response = await service.AskAnalystAsync("Generate intelligence brief for this location", candidate);

        Assert.Contains("SITREP", response);
        Assert.Contains("Centroid", response);
        Assert.Contains("RECOMMENDED ACTIONS", response);
    }

    [Fact]
    public async Task AskAnalystAsync_EtiologyQuery_ExplainsSpectralDynamics()
    {
        var service = new AnalystChatService();
        var candidate = CreateTestCandidate(ChangeType.Construction, confidence: 0.89, spectralDiff: 0.72);

        string response = await service.AskAnalystAsync("Why was this classified as construction?", candidate);

        Assert.Contains("Spectral Divergence", response);
        Assert.Contains("CVA", response);
    }

    [Fact]
    public async Task AskAnalystAsync_SarQuery_ExplainsRadarDoubleBounce()
    {
        var service = new AnalystChatService();
        var candidate = CreateTestCandidate(ChangeType.Construction, confidence: 0.89, spectralDiff: 0.72);

        string response = await service.AskAnalystAsync("Does SAR radar confirm this?", candidate);

        Assert.Contains("Sentinel-1", response);
        Assert.Contains("double-bounce", response, StringComparison.OrdinalIgnoreCase);
    }
}
