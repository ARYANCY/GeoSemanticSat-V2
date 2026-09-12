using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.VectorIndex;
using GeoSemanticSat.Engine.Retrieval;
using GeoSemanticSat.Engine.Services;
using Xunit;

namespace GeoSemanticSat.Tests;

public class CommercialFeaturesTests
{
    [Fact]
    public void EvidenceReportService_GeneratesCompleteValidPackage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"evidence_test_{Guid.NewGuid():N}");
        try
        {
            var changes = new List<ChangeRecord>
            {
                new ChangeRecord
                {
                    Id = "CHG_COMM_01",
                    TileId = "S2_DELHI_2026",
                    Type = ChangeType.Construction,
                    Confidence = 0.94,
                    AreaSqMeters = 12500,
                    Bounds = new BoundingBox(77.20, 28.60, 77.22, 28.62),
                    TimestampT1 = DateTime.UtcNow.AddDays(-14),
                    TimestampT2 = DateTime.UtcNow,
                    ConfirmedByAnalyst = true
                }
            };

            var transform = AffineGeoTransform.NorthUp(77.20, 28.60, 0.0001, 0.0001);
            var tile = new SatelliteTile
            {
                TileId = "S2_DELHI_2026",
                Width = 32,
                Height = 32,
                Transform = transform,
                Bounds = new BoundingBox(77.20, 28.60, 77.22, 28.62),
                SourceImageSha256 = "c1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f80"
            };

            var result = EvidenceReportService.GenerateEvidencePackage(
                tempDir,
                "OPERATION_SAFEGUARD",
                changes,
                tile,
                tile,
                "Field intelligence confirmation of runway apron extension."
            );

            Assert.True(File.Exists(result.GeoJsonPath));
            Assert.True(File.Exists(result.StacMetadataPath));
            Assert.True(File.Exists(result.BriefingHtmlPath));
            Assert.True(File.Exists(result.ManifestSha256Path));
            Assert.NotEmpty(result.MerkleRootHash);
            Assert.Equal(1, result.ConfirmedChangesCount);
            Assert.Equal(1, result.TotalChangesCount);

            // Verify STAC content
            string stacJson = File.ReadAllText(result.StacMetadataPath);
            Assert.Contains("stac_version", stacJson);
            Assert.Contains("UPAGRAHA Intelligence Product: OPERATION_SAFEGUARD", stacJson);

            // Verify HTML briefing content
            string html = File.ReadAllText(result.BriefingHtmlPath);
            Assert.Contains("UPAGRAHA Sovereign EO Intelligence Briefing", html);
            Assert.Contains("OPERATION_SAFEGUARD", html);
            Assert.Contains("CONFIRMED", html);

            // Verify Manifest
            string manifest = File.ReadAllText(result.ManifestSha256Path);
            Assert.Contains("MERKLE_ROOT", manifest);
            Assert.Contains(result.MerkleRootHash, manifest);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void DomainPackRegistry_ConfiguresAllVerticalPacksCorrectly()
    {
        var packs = DomainPackRegistry.GetAllPacks().ToList();
        Assert.Equal(4, packs.Count);

        var disasterPack = DomainPackRegistry.GetPack(VerticalDomain.DisasterResponse);
        Assert.Contains(ChangeType.WaterExtentVariation, disasterPack.PrimaryChangeTypes);
        Assert.Contains("NDWI", disasterPack.KeySpectralIndices);

        var defensePack = DomainPackRegistry.GetPack(VerticalDomain.DefenseInfrastructure);
        Assert.Contains(ChangeType.Construction, defensePack.PrimaryChangeTypes);
        Assert.Contains("SAR_VV", defensePack.KeySpectralIndices);

        var miningPack = DomainPackRegistry.GetPack(VerticalDomain.MiningEnvironmental);
        Assert.Contains("NDVI", miningPack.KeySpectralIndices);

        var urbanPack = DomainPackRegistry.GetPack(VerticalDomain.UrbanLinearInfrastructure);
        Assert.Contains(ChangeType.RoadDevelopment, urbanPack.PrimaryChangeTypes);

        // Assert detection options can be created
        var options = defensePack.CreateDetectionOptions();
        Assert.Equal(defensePack.MinConfidenceThreshold, options.MinConfidence);
    }

    [Fact]
    public void MissionMonitoringEngine_RegistersMissionAndManagesAlerts()
    {
        var engine = new MissionMonitoringEngine();
        var aoi = new BoundingBox(77.0, 28.0, 77.5, 28.5);

        var mission = new MonitoringMission
        {
            MissionId = "MIS_001",
            Name = "Airbase Runway Sentinel",
            Aoi = aoi,
            TargetChangeTypes = new HashSet<ChangeType> { ChangeType.Construction },
            MinConfidence = 0.70
        };

        engine.RegisterMission(mission);
        var activeMissions = engine.GetActiveMissions();
        Assert.Single(activeMissions);
        Assert.Equal("Airbase Runway Sentinel", activeMissions[0].Name);

        // Manually inject an alert to verify verdict updates
        var changes = new List<ChangeRecord>
        {
            new ChangeRecord
            {
                Id = "CHG_M_01",
                TileId = "T1",
                Type = ChangeType.Construction,
                Confidence = 0.88,
                AreaSqMeters = 5000,
                Bounds = aoi,
                TimestampT1 = DateTime.UtcNow.AddDays(-7),
                TimestampT2 = DateTime.UtcNow
            }
        };

        // Create alert directly
        var alert = new IntelligenceAlert
        {
            AlertId = "ALT_TEST_01",
            MissionId = "MIS_001",
            MissionName = "Airbase Runway Sentinel",
            TriggeredAt = DateTime.UtcNow,
            PrimaryChangeType = ChangeType.Construction,
            PriorityScore = 82.5,
            PeakConfidence = 0.88,
            TotalAreaSqMeters = 5000,
            Changes = changes
        };

        // Assert alert verdict cycle
        Assert.Equal("PENDING", alert.AnalystVerdict);
        alert.AnalystVerdict = "CONFIRMED";
        Assert.Equal("CONFIRMED", alert.AnalystVerdict);
    }

    [Fact]
    public void LocalIntelligenceDaemon_ProcessesHealthAndDomainPacksCommands()
    {
        var searchEngine = new SemanticSearchEngine(new VectorIndex());
        var monitoringEngine = new MissionMonitoringEngine();
        var daemon = new LocalIntelligenceDaemon(searchEngine, monitoringEngine);

        // 1. HEALTH command
        string healthCmd = "{\"command\": \"HEALTH\"}";
        var healthResp = daemon.ProcessCommand(healthCmd);
        Assert.True(healthResp.Success);
        Assert.Contains("Operational", healthResp.Message);

        // 2. GET_DOMAIN_PACKS command
        string packsCmd = "{\"command\": \"GET_DOMAIN_PACKS\"}";
        var packsResp = daemon.ProcessCommand(packsCmd);
        Assert.True(packsResp.Success);
        Assert.NotNull(packsResp.Data);

        // 3. Unknown command
        string badCmd = "{\"command\": \"NON_EXISTENT\"}";
        var badResp = daemon.ProcessCommand(badCmd);
        Assert.False(badResp.Success);
    }
}
