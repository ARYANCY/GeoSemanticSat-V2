using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.Raster;

namespace GeoSemanticSat.Core.ChangeDetection;

/// <summary>
/// Multi-Temporal Earliest Observation Onset Estimator.
/// Given an AOI and a chronological series of satellite passes T_1, T_2, ..., T_k,
/// statistically identifies the earliest observation where change onset is supported by usable imagery.
/// </summary>
public static class OnsetEstimator
{
    public record ObservationPoint(DateTime Timestamp, string TileId, double MetricValue, bool IsUsable);

    /// <summary>
    /// Analyzes the temporal time-series at a specific geographic bounding box across all available observations,
    /// filtering out cloud-obscured scenes, and detecting the change-point transition.
    /// </summary>
    public static DateTime EstimateEarliestObservation(List<SatelliteTile> chronologicalTiles, BoundingBox bounds, ChangeType targetType)
    {
        if (chronologicalTiles.Count <= 1)
        {
            return chronologicalTiles.FirstOrDefault()?.AcquisitionTimestamp ?? DateTime.UtcNow;
        }

        var sorted = chronologicalTiles.OrderBy(t => t.AcquisitionTimestamp).ToList();
        var series = new List<ObservationPoint>();

        foreach (var tile in sorted)
        {
            // Find pixel region in tile
            var (pxMin, pyMin) = tile.Transform.GeoToPixel(new GeoCoordinate(bounds.MaxLat, bounds.MinLon));
            var (pxMax, pyMax) = tile.Transform.GeoToPixel(new GeoCoordinate(bounds.MinLat, bounds.MaxLon));

            int x0 = Math.Clamp((int)Math.Min(pxMin, pxMax), 0, tile.Width - 1);
            int y0 = Math.Clamp((int)Math.Min(pyMin, pyMax), 0, tile.Height - 1);
            int x1 = Math.Clamp((int)Math.Max(pxMin, pxMax), 0, tile.Width - 1);
            int y1 = Math.Clamp((int)Math.Max(pyMin, pyMax), 0, tile.Height - 1);

            int patchW = Math.Max(1, x1 - x0);
            int patchH = Math.Max(1, y1 - y0);

            // Check quality mask
            var mask = QualityMaskEngine.GenerateQualityMask(tile);
            int validCount = 0;
            double metricSum = 0.0;

            float[,] targetBand = targetType switch
            {
                ChangeType.Construction => SpectralIndices.ComputeNDBI(tile),
                ChangeType.Clearance => SpectralIndices.ComputeNDVI(tile),
                ChangeType.WaterExtentVariation => SpectralIndices.ComputeNDWI(tile),
                _ => tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red)
            };

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if ((mask[y, x] & (QualityMaskFlags.Cloud | QualityMaskFlags.CloudShadow)) == 0)
                    {
                        validCount++;
                        metricSum += targetBand[y, x];
                    }
                }
            }

            int total = patchW * patchH;
            bool isUsable = (double)validCount / Math.Max(1, total) >= 0.40;
            double metricAvg = validCount > 0 ? metricSum / validCount : 0.0;

            series.Add(new ObservationPoint(tile.AcquisitionTimestamp, tile.TileId, metricAvg, isUsable));
        }

        // Robust Sequential CUSUM / Change-Point Detection over usable observations
        var usableSeries = series.Where(s => s.IsUsable).ToList();
        if (usableSeries.Count < 2)
        {
            return series.Last().Timestamp;
        }

        // Establish moving baseline statistical envelope from the initial 1/3 observations
        int baselineCount = Math.Max(1, Math.Min(3, usableSeries.Count / 3));
        double baselineMean = usableSeries.Take(baselineCount).Average(s => s.MetricValue);
        double baselineVar = usableSeries.Take(baselineCount).Average(s => Math.Pow(s.MetricValue - baselineMean, 2));
        double sigma = Math.Max(0.025, Math.Sqrt(baselineVar));

        // CUSUM parameters: Slack allowance K and Decision threshold H
        double slackK = 0.5 * sigma;
        double thresholdH = Math.Max(0.12, 3.5 * sigma);

        double cusumPos = 0.0;
        double cusumNeg = 0.0;
        int onsetIndex = usableSeries.Count - 1;

        for (int i = 1; i < usableSeries.Count; i++)
        {
            double val = usableSeries[i].MetricValue;
            cusumPos = Math.Max(0.0, cusumPos + (val - baselineMean) - slackK);
            cusumNeg = Math.Max(0.0, cusumNeg - (val - baselineMean) - slackK);

            // Trigger change onset upon statistically significant cumulative deviation
            if (cusumPos > thresholdH || cusumNeg > thresholdH)
            {
                onsetIndex = i;
                break;
            }
        }

        return usableSeries[onsetIndex].Timestamp;
    }
}
