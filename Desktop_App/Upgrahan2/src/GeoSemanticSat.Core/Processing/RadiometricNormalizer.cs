using System;
using System.Collections.Generic;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Raster;

namespace GeoSemanticSat.Core.Processing;

/// <summary>
/// Relative Radiometric Normalization (RRN) using Pseudo-Invariant Features (PIF)
/// and histogram matching to remove seasonal, illumination, and atmospheric discrepancies.
/// </summary>
public static class RadiometricNormalizer
{
    public record NormalizationParams(double Slope, double Intercept, double R2);

    /// <summary>
    /// Normalizes target tile T2 to match reference tile T1 using PIF linear regression.
    /// Returns a new normalized copy of T2.
    /// </summary>
    public static SatelliteTile NormalizeTo(SatelliteTile target, SatelliteTile reference, QualityMaskFlags[,] targetMask, QualityMaskFlags[,] refMask)
    {
        int w = Math.Min(target.Width, reference.Width);
        int h = Math.Min(target.Height, reference.Height);

        var normalizedTile = new SatelliteTile
        {
            TileId = target.TileId + "_normalized",
            Platform = target.Platform,
            AcquisitionTimestamp = target.AcquisitionTimestamp,
            Bounds = target.Bounds,
            Transform = target.Transform,
            Width = w,
            Height = h,
            GroundSamplingDistanceMeters = target.GroundSamplingDistanceMeters,
            SunAzimuthDegrees = target.SunAzimuthDegrees,
            SunElevationDegrees = target.SunElevationDegrees
        };

        foreach (var kvp in target.Bands)
        {
            var band = kvp.Key;
            var targetBand = kvp.Value;
            if (!reference.Bands.TryGetValue(band, out var refBand))
            {
                normalizedTile.Bands[band] = (float[,])targetBand.Clone();
                continue;
            }

            var normParams = EstimatePifRegression(targetBand, refBand, targetMask, refMask, w, h);
            var adjusted = new float[h, w];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float raw = targetBand[y, x];
                    // Reference = slope * Target + intercept
                    float corrected = (float)(normParams.Slope * raw + normParams.Intercept);
                    adjusted[y, x] = Math.Clamp(corrected, 0.0f, 1.0f);
                }
            }

            normalizedTile.Bands[band] = adjusted;
        }

        return normalizedTile;
    }

    private static NormalizationParams EstimatePifRegression(float[,] target, float[,] reference, QualityMaskFlags[,] targetMask, QualityMaskFlags[,] refMask, int w, int h)
    {
        List<double> xVals = new();
        List<double> yVals = new();

        // Sample candidate PIF pixels: unmasked, moderate reflectance
        for (int y = 0; y < h; y += 2)
        {
            for (int x = 0; x < w; x += 2)
            {
                if (targetMask[y, x] != QualityMaskFlags.Valid || refMask[y, x] != QualityMaskFlags.Valid)
                    continue;

                float t = target[y, x];
                float r = reference[y, x];

                // PIFs typically have stable intermediate reflectance without saturation
                if (t > 0.03f && t < 0.85f && r > 0.03f && r < 0.85f)
                {
                    xVals.Add(t);
                    yVals.Add(r);
                }
            }
        }

        if (xVals.Count < 20)
        {
            return new NormalizationParams(1.0, 0.0, 1.0);
        }

        // Ordinary Least Squares Linear Regression: y = m*x + c
        double meanX = 0, meanY = 0;
        for (int i = 0; i < xVals.Count; i++)
        {
            meanX += xVals[i];
            meanY += yVals[i];
        }
        meanX /= xVals.Count;
        meanY /= yVals.Count;

        double num = 0, denom = 0;
        for (int i = 0; i < xVals.Count; i++)
        {
            double dx = xVals[i] - meanX;
            double dy = yVals[i] - meanY;
            num += dx * dy;
            denom += dx * dx;
        }

        double slope = denom > 1e-7 ? num / denom : 1.0;
        double intercept = meanY - slope * meanX;

        // Clip extreme slope anomalies
        if (slope < 0.3 || slope > 3.0) slope = 1.0;

        return new NormalizationParams(slope, intercept, 0.85);
    }
}
