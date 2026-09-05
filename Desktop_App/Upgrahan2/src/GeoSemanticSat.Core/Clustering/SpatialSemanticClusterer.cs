using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.Model;
using VectorIndexStore = GeoSemanticSat.Core.VectorIndex.VectorIndex;

namespace GeoSemanticSat.Core.Clustering;

public record ClusterGroup(
    int ClusterId,
    string Label,
    List<TilePatch> Members,
    float[] Centroid,
    BoundingBox EnclosingBounds,
    double CohesionScore
);

/// <summary>
/// Unsupervised Discovery and Clustering Engine.
/// Groups similar sites across the AOI based on semantic embedding distance and spatial proximity.
/// Enables analysts to discover analogous installations without manually authoring new queries.
/// </summary>
public static class SpatialSemanticClusterer
{
    /// <summary>
    /// Performs DBSCAN clustering in multimodal embedding space with spatial bounding constraints.
    /// eps: maximum cosine distance (e.g. 0.25) to consider neighbors.
    /// minPts: minimum points to form a dense cluster.
    /// </summary>
    public static List<ClusterGroup> ClusterSites(
        IReadOnlyList<TilePatch> patches,
        double epsCosineDistance = 0.22,
        int minPts = 2,
        double maxSpatialDistanceKm = 50.0)
    {
        if (patches == null || patches.Count == 0)
            return new List<ClusterGroup>();

        int n = patches.Count;
        int[] labels = new int[n]; // 0 = unvisited, -1 = noise, > 0 = clusterId
        int currentClusterId = 0;

        // Build spatial hash grid buckets for sub-millisecond neighbor candidate pruning
        double cellSizeDegrees = Math.Max(0.1, maxSpatialDistanceKm / 111.32);
        var spatialGrid = new Dictionary<(long, long), List<int>>();

        for (int i = 0; i < n; i++)
        {
            long gx = (long)Math.Floor(patches[i].Bounds.Center.Longitude / cellSizeDegrees);
            long gy = (long)Math.Floor(patches[i].Bounds.Center.Latitude / cellSizeDegrees);
            var key = (gx, gy);

            if (!spatialGrid.TryGetValue(key, out var bucket))
            {
                bucket = new List<int>();
                spatialGrid[key] = bucket;
            }
            bucket.Add(i);
        }

        for (int i = 0; i < n; i++)
        {
            if (labels[i] != 0) continue;

            var neighbors = FindNeighbors(i, patches, spatialGrid, cellSizeDegrees, epsCosineDistance, maxSpatialDistanceKm);
            if (neighbors.Count < minPts)
            {
                labels[i] = -1; // noise candidate
            }
            else
            {
                currentClusterId++;
                labels[i] = currentClusterId;

                var queue = new Queue<int>(neighbors);
                var visitedInExpansion = new HashSet<int>(neighbors) { i };

                while (queue.Count > 0)
                {
                    int neighborIdx = queue.Dequeue();
                    if (labels[neighborIdx] == -1)
                        labels[neighborIdx] = currentClusterId;

                    if (labels[neighborIdx] != 0)
                        continue;

                    labels[neighborIdx] = currentClusterId;
                    var subNeighbors = FindNeighbors(neighborIdx, patches, spatialGrid, cellSizeDegrees, epsCosineDistance, maxSpatialDistanceKm);
                    if (subNeighbors.Count >= minPts)
                    {
                        foreach (var sn in subNeighbors)
                        {
                            if (visitedInExpansion.Add(sn))
                            {
                                queue.Enqueue(sn);
                            }
                        }
                    }
                }
            }
        }

        // Group into ClusterGroup objects
        var result = new List<ClusterGroup>();
        var grouped = patches.Select((p, idx) => (Patch: p, ClusterId: labels[idx]))
                             .Where(x => x.ClusterId > 0)
                             .GroupBy(x => x.ClusterId);

        foreach (var grp in grouped)
        {
            var members = grp.Select(g => g.Patch).ToList();
            if (members.Count == 0) continue;

            int dim = members[0].EmbeddingVector.Length;
            float[] centroid = new float[dim];

            double minLon = double.MaxValue, minLat = double.MaxValue;
            double maxLon = double.MinValue, maxLat = double.MinValue;

            foreach (var m in members)
            {
                minLon = Math.Min(minLon, m.Bounds.MinLon);
                minLat = Math.Min(minLat, m.Bounds.MinLat);
                maxLon = Math.Max(maxLon, m.Bounds.MaxLon);
                maxLat = Math.Max(maxLat, m.Bounds.MaxLat);

                for (int d = 0; d < dim; d++)
                {
                    centroid[d] += m.EmbeddingVector[d];
                }
            }

            VectorIndexStore.NormalizeInPlace(centroid);

            // Compute cohesion (average similarity to centroid)
            double simSum = 0.0;
            foreach (var m in members)
            {
                simSum += VectorIndexStore.DotProduct(centroid, m.EmbeddingVector);
            }
            double cohesion = simSum / members.Count;

            string label = InferClusterLabel(centroid, members);

            result.Add(new ClusterGroup(
                grp.Key,
                label,
                members,
                centroid,
                new BoundingBox(minLon, minLat, maxLon, maxLat),
                Math.Round(cohesion, 3)
            ));
        }

        return result.OrderByDescending(c => c.Members.Count).ToList();
    }

    private static List<int> FindNeighbors(
        int targetIdx,
        IReadOnlyList<TilePatch> patches,
        Dictionary<(long, long), List<int>> spatialGrid,
        double cellSizeDegrees,
        double eps,
        double maxSpatialKm)
    {
        var neighbors = new List<int>();
        var target = patches[targetIdx];

        long gx = (long)Math.Floor(target.Bounds.Center.Longitude / cellSizeDegrees);
        long gy = (long)Math.Floor(target.Bounds.Center.Latitude / cellSizeDegrees);

        // Search only target cell and 8 adjacent neighboring spatial cells
        for (long dy = -1; dy <= 1; dy++)
        {
            for (long dx = -1; dx <= 1; dx++)
            {
                if (spatialGrid.TryGetValue((gx + dx, gy + dy), out var candidates))
                {
                    foreach (int j in candidates)
                    {
                        if (targetIdx == j) continue;
                        var other = patches[j];

                        // Spatial distance filter
                        double spatialDistKm = target.Bounds.Center.DistanceToKm(other.Bounds.Center);
                        if (spatialDistKm > maxSpatialKm) continue;

                        // Cosine distance filter in embedding space
                        double sim = VectorIndexStore.DotProduct(target.EmbeddingVector, other.EmbeddingVector);
                        double dist = 1.0 - sim;
                        if (dist <= eps)
                        {
                            neighbors.Add(j);
                        }
                    }
                }
            }
        }

        return neighbors;
    }

    private static string InferClusterLabel(float[] centroid, List<TilePatch> members)
    {
        // Infer archetype from dominant embedding features
        // In our 128-dim multimodal space:
        // Indices 0-15: Built-up / structural features
        // Indices 16-31: Water / moisture features
        // Indices 32-47: Vegetation / forestry
        // Indices 48-63: Linear / road / runway features
        // Indices 64-79: Transient / vehicular activity
        double structural = 0, water = 0, vegetation = 0, linear = 0, activity = 0;

        for (int i = 0; i < 16 && i < centroid.Length; i++) structural += Math.Abs(centroid[i]);
        for (int i = 16; i < 32 && i < centroid.Length; i++) water += Math.Abs(centroid[i]);
        for (int i = 32; i < 48 && i < centroid.Length; i++) vegetation += Math.Abs(centroid[i]);
        for (int i = 48; i < 64 && i < centroid.Length; i++) linear += Math.Abs(centroid[i]);
        for (int i = 64; i < 80 && i < centroid.Length; i++) activity += Math.Abs(centroid[i]);

        double maxScore = Math.Max(structural, Math.Max(water, Math.Max(vegetation, Math.Max(linear, activity))));

        if (maxScore == structural) return $"Built-up / Compound Group ({members.Count} sites)";
        if (maxScore == linear) return $"Corridor / Linear Track Assets ({members.Count} sites)";
        if (maxScore == water) return $"Hydrological / Inundation Zone ({members.Count} sites)";
        if (maxScore == activity) return $"High-Activity / Material Staging Sites ({members.Count} sites)";
        return $"Vegetation / Terrain Clearing Cluster ({members.Count} sites)";
    }
}
