using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;
using GeoSemanticSat.Engine.Retrieval;

namespace GeoSemanticSat.Engine.Services;

public record DaemonCommandRequest(
    string Command,
    string? MissionId = null,
    string? Query = null,
    int? TopK = null,
    string? T1Path = null,
    string? T2Path = null,
    double? MinConfidence = null,
    string? Domain = null,
    string? OutputDir = null
);

public record DaemonCommandResponse(
    bool Success,
    string Message,
    object? Data = null
);

/// <summary>
/// Feature 1: Sovereign EO Intelligence API / Local Intelligence-as-a-Service (LIaaS).
/// Provides a controlled on-premises daemon interface executing semantic retrieval,
/// change detection, and mission monitoring without cloud dependencies.
/// </summary>
public class LocalIntelligenceDaemon
{
    private readonly SemanticSearchEngine _searchEngine;
    private readonly MissionMonitoringEngine _monitoringEngine;

    public LocalIntelligenceDaemon(SemanticSearchEngine searchEngine, MissionMonitoringEngine monitoringEngine)
    {
        _searchEngine = searchEngine;
        _monitoringEngine = monitoringEngine;
    }

    public DaemonCommandResponse ProcessCommand(string commandJson)
    {
        try
        {
            var req = JsonSerializer.Deserialize<DaemonCommandRequest>(commandJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null)
                return new DaemonCommandResponse(false, "Invalid or empty command payload.");

            return req.Command.ToUpperInvariant() switch
            {
                "HEALTH" => HandleHealth(),
                "GET_DOMAIN_PACKS" => HandleGetDomainPacks(),
                "DETECT_CHANGES" => HandleDetectChanges(req),
                "GET_MISSIONS" => HandleGetMissions(),
                "GET_ALERTS" => HandleGetAlerts(req),
                "EXPORT_EVIDENCE" => HandleExportEvidence(req),
                _ => new DaemonCommandResponse(false, $"Unknown command '{req.Command}'.")
            };
        }
        catch (Exception ex)
        {
            return new DaemonCommandResponse(false, $"Command execution error: {ex.Message}");
        }
    }

    private DaemonCommandResponse HandleHealth()
    {
        return new DaemonCommandResponse(true, "UPAGRAHA Sovereign Daemon Operational", new
        {
            status = "HEALTHY",
            airGapped = true,
            indexedTiles = _searchEngine.Index.Count,
            activeMissions = _monitoringEngine.GetActiveMissions().Count,
            pendingAlerts = _monitoringEngine.GetAlerts().Count(a => a.AnalystVerdict == "PENDING"),
            timestamp = DateTime.UtcNow
        });
    }

    private DaemonCommandResponse HandleGetDomainPacks()
    {
        var packs = DomainPackRegistry.GetAllPacks().Select(p => new
        {
            domain = p.Domain.ToString(),
            name = p.Name,
            description = p.Description,
            minConfidence = p.MinConfidenceThreshold,
            patchSize = p.RecommendedPatchSize,
            queryPresets = p.SemanticQueryTemplates,
            indices = p.KeySpectralIndices
        });

        return new DaemonCommandResponse(true, "Domain packs retrieved successfully.", packs);
    }

    private DaemonCommandResponse HandleDetectChanges(DaemonCommandRequest req)
    {
        if (string.IsNullOrEmpty(req.T1Path) || !File.Exists(req.T1Path))
            return new DaemonCommandResponse(false, "Baseline T1 raster path missing or not found.");

        if (string.IsNullOrEmpty(req.T2Path) || !File.Exists(req.T2Path))
            return new DaemonCommandResponse(false, "Target T2 raster path missing or not found.");

        var t1 = GeoTiffReader.Read(req.T1Path);
        var t2 = GeoTiffReader.Read(req.T2Path);

        var options = new MultiTemporalChangeDetector.ChangeDetectionOptions(
            MinConfidence: req.MinConfidence ?? 0.65
        );

        if (!string.IsNullOrEmpty(req.Domain) && Enum.TryParse<VerticalDomain>(req.Domain, true, out var domain))
        {
            options = DomainPackRegistry.GetPack(domain).CreateDetectionOptions();
        }

        var changes = MultiTemporalChangeDetector.DetectChanges(t1, t2, options);

        return new DaemonCommandResponse(true, $"Detected {changes.Count} changes between T1 and T2.", new
        {
            t1Id = t1.TileId,
            t2Id = t2.TileId,
            totalChanges = changes.Count,
            changes = changes.Select(c => new
            {
                id = c.Id,
                type = c.Type.ToString(),
                confidence = c.Confidence,
                areaSqMeters = c.AreaSqMeters,
                bounds = new { c.Bounds.MinLon, c.Bounds.MinLat, c.Bounds.MaxLon, c.Bounds.MaxLat }
            })
        });
    }

    private DaemonCommandResponse HandleGetMissions()
    {
        var missions = _monitoringEngine.GetActiveMissions().Select(m => new
        {
            id = m.MissionId,
            name = m.Name,
            minConfidence = m.MinConfidence,
            targetTypes = m.TargetChangeTypes.Select(t => t.ToString()),
            aoi = new { m.Aoi.MinLon, m.Aoi.MinLat, m.Aoi.MaxLon, m.Aoi.MaxLat }
        });

        return new DaemonCommandResponse(true, "Active missions retrieved.", missions);
    }

    private DaemonCommandResponse HandleGetAlerts(DaemonCommandRequest req)
    {
        var alerts = _monitoringEngine.GetAlerts(req.MissionId).Select(a => new
        {
            alertId = a.AlertId,
            missionId = a.MissionId,
            missionName = a.MissionName,
            type = a.PrimaryChangeType.ToString(),
            priority = a.PriorityScore,
            confidence = a.PeakConfidence,
            totalArea = a.TotalAreaSqMeters,
            verdict = a.AnalystVerdict,
            triggeredAt = a.TriggeredAt
        });

        return new DaemonCommandResponse(true, "Alerts retrieved.", alerts);
    }

    private DaemonCommandResponse HandleExportEvidence(DaemonCommandRequest req)
    {
        string outDir = req.OutputDir ?? Path.Combine(Directory.GetCurrentDirectory(), "evidence_export");
        string missionName = req.MissionId ?? "OPERATIONAL_MISSION";

        var alerts = _monitoringEngine.GetAlerts(req.MissionId);
        var changes = alerts.SelectMany(a => a.Changes).ToList();

        var result = EvidenceReportService.GenerateEvidencePackage(outDir, missionName, changes);

        return new DaemonCommandResponse(true, "Evidence package generated.", result);
    }
}
