using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.ChangeDetection;

public record ChangeSearchCriteria(
    GeoCoordinate? Center = null,
    double? RadiusKm = null,
    BoundingBox? Bounds = null,
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    ChangeType? TargetChangeType = null,
    double MinConfidence = 0.50,
    string? Keyword = null
);

public record ChangeSearchResult(
    ChangeRecord Record,
    double DistanceKm,
    double RelevanceScore
);

/// <summary>
/// Advanced Spatiotemporal & Change-Type Search Engine.
/// Allows analysts to query detected changes by precise Geographic Coordinates (Lat, Lon, Radius),
/// Observation Time Range, Earliest Onset Date, and specific Change Class (Construction, Clearance, etc.).
/// </summary>
public class ChangeSearchEngine
{
    private readonly List<ChangeRecord> _repository = new();
    private readonly object _lock = new();

    public ChangeSearchEngine(IEnumerable<ChangeRecord>? initialChanges = null)
    {
        if (initialChanges != null)
        {
            _repository.AddRange(initialChanges);
        }
    }

    public void AddChange(ChangeRecord change)
    {
        lock (_lock)
        {
            if (!_repository.Any(c => c.Id == change.Id))
            {
                _repository.Add(change);
            }
        }
    }

    public void AddRange(IEnumerable<ChangeRecord> changes)
    {
        foreach (var c in changes) AddChange(c);
    }

    public IReadOnlyList<ChangeRecord> GetAll()
    {
        lock (_lock) return _repository.ToList();
    }

    /// <summary>
    /// Executes multi-criteria search over change records.
    /// </summary>
    public List<ChangeSearchResult> Search(ChangeSearchCriteria criteria, int topK = 50)
    {
        List<ChangeRecord> pool;
        lock (_lock)
        {
            pool = _repository.ToList();
        }

        var matches = new List<ChangeSearchResult>();

        foreach (var record in pool)
        {
            // 1. Change Type Filter
            if (criteria.TargetChangeType.HasValue && criteria.TargetChangeType.Value != ChangeType.NoChange)
            {
                if (record.Type != criteria.TargetChangeType.Value)
                    continue;
            }

            // 2. Minimum Confidence
            if (record.Confidence < criteria.MinConfidence)
                continue;

            // 3. Temporal Window (Observation or Earliest Observation)
            if (criteria.StartDate.HasValue)
            {
                var start = criteria.StartDate.Value;
                // If an onset date is identified, check if it occurred before the StartDate
                if (record.EarliestObservationTimestamp != default && record.EarliestObservationTimestamp < start)
                    continue;
                // If no onset date was identified, check if the change observation T2 is before StartDate
                else if (record.EarliestObservationTimestamp == default && record.TimestampT2 != default && record.TimestampT2 < start)
                    continue;
            }

            if (criteria.EndDate.HasValue)
            {
                var end = criteria.EndDate.Value;
                if (end.TimeOfDay == TimeSpan.Zero)
                {
                    end = end.Date.AddDays(1).AddTicks(-1);
                }

                // If an onset date is identified, check if it occurred after the EndDate
                if (record.EarliestObservationTimestamp != default && record.EarliestObservationTimestamp > end)
                    continue;
                // If the baseline T1 itself started after EndDate
                else if (record.TimestampT1 != default && record.TimestampT1 > end)
                    continue;
            }

            // 4. Geographic Bounds Filter
            if (criteria.Bounds.HasValue && !criteria.Bounds.Value.Intersects(record.Bounds))
                continue;

            // 5. Geographic Coordinate & Radial Proximity
            double distanceKm = 0.0;
            double effectiveRadius = (criteria.RadiusKm.HasValue && criteria.RadiusKm.Value > 0) ? criteria.RadiusKm.Value : 15.0;
            if (criteria.Center.HasValue)
            {
                distanceKm = record.Center.DistanceToKm(criteria.Center.Value);
                if (distanceKm > effectiveRadius)
                    continue;
            }

            // 6. Keyword Filter in processing / analyst notes
            if (!string.IsNullOrWhiteSpace(criteria.Keyword))
            {
                string kw = criteria.Keyword.ToLowerInvariant();
                bool hasMatch = record.ProcessingNotes.ToLowerInvariant().Contains(kw) ||
                                record.AnalystNotes.ToLowerInvariant().Contains(kw) ||
                                record.Type.ToString().ToLowerInvariant().Contains(kw);
                if (!hasMatch)
                    continue;
            }

            // Calculate overall composite relevance score
            // Prioritizes higher confidence and closer spatial proximity
            double proximityScore = criteria.Center.HasValue && effectiveRadius > 0
                ? Math.Clamp(1.0 - (distanceKm / effectiveRadius), 0.0, 1.0)
                : 1.0;

            double relevance = 0.60 * record.Confidence + 0.40 * proximityScore;

            matches.Add(new ChangeSearchResult(record, Math.Round(distanceKm, 3), Math.Round(relevance, 4)));
        }

        return matches.OrderByDescending(m => m.RelevanceScore).Take(topK).ToList();
    }
}
