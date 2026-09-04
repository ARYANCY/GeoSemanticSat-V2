using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.Workflow;

public class ReviewItem
{
    public ChangeRecord Record { get; }
    public DateTime AddedTimestamp { get; } = DateTime.UtcNow;
    public string Status { get; set; } = "Pending"; // Pending, Confirmed, Rejected, Flagged
    public string AnalystComments { get; set; } = string.Empty;
    public DateTime? DecisionTimestamp { get; set; }

    public ReviewItem(ChangeRecord record)
    {
        Record = record;
    }
}

/// <summary>
/// Ranked review queue prioritized by analytical confidence and change magnitude.
/// </summary>
public class ReviewQueue
{
    private readonly List<ReviewItem> _items = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _items.Count; } }

    public void Enqueue(ChangeRecord change)
    {
        lock (_lock)
        {
            if (!_items.Any(i => i.Record.Id == change.Id))
            {
                _items.Add(new ReviewItem(change));
            }
        }
    }

    public void EnqueueRange(IEnumerable<ChangeRecord> changes)
    {
        foreach (var c in changes) Enqueue(c);
    }

    public List<ReviewItem> GetPendingRanked()
    {
        lock (_lock)
        {
            return _items.Where(i => i.Status == "Pending")
                         .OrderByDescending(i => i.Record.Confidence)
                         .ToList();
        }
    }

    public List<ReviewItem> GetAll()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(i => i.Record.Confidence).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    public bool Confirm(string changeId, string notes = "")
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Record.Id == changeId);
            if (item == null) return false;
            item.Status = "Confirmed";
            item.Record.ConfirmedByAnalyst = true;
            item.Record.RejectedByAnalyst = false;
            item.AnalystComments = notes;
            item.Record.AnalystNotes = notes;
            item.DecisionTimestamp = DateTime.UtcNow;
            return true;
        }
    }

    public bool Reject(string changeId, string notes = "")
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Record.Id == changeId);
            if (item == null) return false;
            item.Status = "Rejected";
            item.Record.ConfirmedByAnalyst = false;
            item.Record.RejectedByAnalyst = true;
            item.AnalystComments = notes;
            item.Record.AnalystNotes = notes;
            item.DecisionTimestamp = DateTime.UtcNow;
            return true;
        }
    }

    public bool FlagForReview(string changeId, string notes = "")
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Record.Id == changeId);
            if (item == null) return false;
            item.Status = "Flagged";
            item.AnalystComments = notes;
            item.DecisionTimestamp = DateTime.UtcNow;
            return true;
        }
    }
}
