using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GeoSemanticSat.Core.ChangeDetection;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;

namespace GeoSemanticSat.Engine.Services;

public record MonitoringMission
{
    public required string MissionId { get; init; }
    public required string Name { get; init; }
    public required BoundingBox Aoi { get; init; }
    public HashSet<ChangeType> TargetChangeTypes { get; init; } = new();
    public double MinConfidence { get; init; } = 0.65;
    public double MinMagnitude { get; init; } = 0.06;
    public string? BaselineTilePath { get; init; }
    public SatelliteTile? LoadedBaselineTile { get; set; }
    public bool IsActive { get; set; } = true;
}

public class IntelligenceAlert
{
    public required string AlertId { get; init; }
    public required string MissionId { get; init; }
    public required string MissionName { get; init; }
    public required DateTime TriggeredAt { get; init; }
    public required ChangeType PrimaryChangeType { get; init; }
    public required double PriorityScore { get; init; } // 0.0 to 100.0
    public required double PeakConfidence { get; init; }
    public required double TotalAreaSqMeters { get; init; }
    public required List<ChangeRecord> Changes { get; init; }
    public string AnalystVerdict { get; set; } = "PENDING"; // PENDING, CONFIRMED, REJECTED
    public string? AnalystComments { get; set; }
}

/// <summary>
/// Feature 2: Mission Monitoring and Intelligence Alert Engine.
/// Executes automated recurring or scheduled batch monitoring over defined AOIs,
/// processes incoming scenes against baseline epochs, and triggers prioritized alerts.
/// </summary>
public class MissionMonitoringEngine
{
    private readonly ConcurrentDictionary<string, MonitoringMission> _missions = new();
    private readonly List<IntelligenceAlert> _alerts = new();
    private readonly object _alertLock = new();

    public event Action<IntelligenceAlert>? OnAlertTriggered;

    public void RegisterMission(MonitoringMission mission)
    {
        if (mission.BaselineTilePath != null && File.Exists(mission.BaselineTilePath) && mission.LoadedBaselineTile == null)
        {
            mission.LoadedBaselineTile = GeoTiffReader.Read(mission.BaselineTilePath);
        }
        _missions[mission.MissionId] = mission;
    }

    public bool RemoveMission(string missionId) => _missions.TryRemove(missionId, out _);

    public IReadOnlyList<MonitoringMission> GetActiveMissions() =>
        _missions.Values.Where(m => m.IsActive).ToList();

    public IReadOnlyList<IntelligenceAlert> GetAlerts(string? missionId = null)
    {
        lock (_alertLock)
        {
            return missionId == null
                ? _alerts.ToList()
                : _alerts.Where(a => a.MissionId == missionId).ToList();
        }
    }

    /// <summary>
    /// Evaluates a newly ingested satellite tile against an active mission.
    /// </summary>
    public IntelligenceAlert? EvaluateNewScene(string missionId, SatelliteTile newScene)
    {
        if (!_missions.TryGetValue(missionId, out var mission) || !mission.IsActive)
            return null;

        if (mission.LoadedBaselineTile == null)
            throw new InvalidOperationException($"Mission '{mission.Name}' has no loaded baseline tile.");

        // Check if the new scene intersects the mission AOI
        if (!newScene.Bounds.Intersects(mission.Aoi))
            return null;

        var options = new MultiTemporalChangeDetector.ChangeDetectionOptions(
            PatchSize: 16,
            MinConfidence: mission.MinConfidence,
            MinChangeMagnitude: mission.MinMagnitude
        );

        var detected = MultiTemporalChangeDetector.DetectChanges(mission.LoadedBaselineTile, newScene, options);

        // Filter by mission AOI and target change types
        var filteredChanges = detected.Where(c =>
            mission.Aoi.Intersects(c.Bounds) &&
            (mission.TargetChangeTypes.Count == 0 || mission.TargetChangeTypes.Contains(c.Type))
        ).ToList();

        if (filteredChanges.Count == 0)
            return null;

        // Group into highest-confidence dominant change type
        var dominantType = filteredChanges
            .GroupBy(c => c.Type)
            .OrderByDescending(g => g.Count())
            .First().Key;

        double peakConf = filteredChanges.Max(c => c.Confidence);
        double totalArea = filteredChanges.Sum(c => c.AreaSqMeters);

        // Priority formula: Confidence (0-60 pts) + Logarithmic Area Scale (0-40 pts)
        double areaPts = Math.Min(40.0, Math.Log10(Math.Max(10.0, totalArea)) * 8.0);
        double priority = (peakConf * 60.0) + areaPts;

        var alert = new IntelligenceAlert
        {
            AlertId = $"ALT-{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant(),
            MissionId = mission.MissionId,
            MissionName = mission.Name,
            TriggeredAt = DateTime.UtcNow,
            PrimaryChangeType = dominantType,
            PriorityScore = Math.Round(priority, 1),
            PeakConfidence = peakConf,
            TotalAreaSqMeters = totalArea,
            Changes = filteredChanges
        };

        lock (_alertLock)
        {
            _alerts.Insert(0, alert);
        }

        OnAlertTriggered?.Invoke(alert);
        return alert;
    }

    public bool UpdateAlertVerdict(string alertId, bool confirmed, string? comments = null)
    {
        lock (_alertLock)
        {
            var alert = _alerts.FirstOrDefault(a => a.AlertId == alertId);
            if (alert == null) return false;

            alert.AnalystVerdict = confirmed ? "CONFIRMED" : "REJECTED";
            alert.AnalystComments = comments;

            foreach (var c in alert.Changes)
            {
                c.ConfirmedByAnalyst = confirmed;
                c.RejectedByAnalyst = !confirmed;
                c.AnalystNotes = comments ?? string.Empty;
            }
            return true;
        }
    }
}
