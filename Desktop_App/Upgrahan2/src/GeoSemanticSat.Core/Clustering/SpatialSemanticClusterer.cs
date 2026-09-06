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
        // Archetype scores must read the dimensions MultiSpectralVisionEncoder actually writes:
        //   0-10  band means / std devs (brightness, not semantic)
        //   16-23 spectral indices: 16 NDVI, 17 NDWI, 18 NDBI, 19 MNDWI, 20 BSI,
        //         21 built-near-water, 22 cleared soil, 23 man-made contrast
        //   32-37 spatial gradients, 37 = linear continuity (road / runway)
        //   64-65 texture energy and high-frequency peak density
        //   80-94 per-quadrant layout
        // The previous 16-wide contiguous buckets did not match this layout: indices 48-63
        // are never written by the encoder, so the "linear" score was always zero and that
        // label was unreachable; gradients were counted as "vegetation"; and the raw band
        // means in 0-15 have the largest magnitudes, so "structural" won almost every time.
        // Bands 0-10 are deliberately excluded - scene brightness is not an archetype.
        float At(int i) => i < centroid.Length ? centroid[i] : 0f;

        double vegetation = Math.Abs(At(16));
        double water = Math.Abs(At(17)) + Math.Abs(At(19));
        double structural = Math.Abs(At(18)) + Math.Abs(At(21)) + Math.Abs(At(23));
        double clearance = Math.Abs(At(20)) + Math.Abs(At(22));
        double linear = Math.Abs(At(37)) + Math.Abs(At(33)) + Math.Abs(At(34)) + Math.Abs(At(35)) + Math.Abs(At(36));
        double activity = Math.Abs(At(64)) + Math.Abs(At(65));

        var ranked = new (double Score, string Label)[]
        {
            (structural, $"Built-up / Compound Group ({members.Count} sites)"),
            (linear,     $"Corridor / Linear Track Assets ({members.Count} sites)"),
            (water,      $"Hydrological / Inundation Zone ({members.Count} sites)"),
            (activity,   $"High-Activity / Material Staging Sites ({members.Count} sites)"),
            (clearance,  $"Cleared Ground / Earthworks Cluster ({members.Count} sites)"),
            (vegetation, $"Vegetation / Terrain Clearing Cluster ({members.Count} sites)")
        };

        var best = ranked[0];
        foreach (var candidate in ranked)
        {
            if (candidate.Score > best.Score) best = candidate;
        }
        return best.Label;
    }
}
