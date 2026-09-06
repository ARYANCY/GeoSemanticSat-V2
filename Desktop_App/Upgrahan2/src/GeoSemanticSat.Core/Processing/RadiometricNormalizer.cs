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

        // Initial OLS Regression: y = m*x + c
        double meanX = xVals.Average();
        double meanY = yVals.Average();

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

        // Iteratively Reweighted Least Squares (IRLS) with Tukey Biweight to reject change outliers
        int maxIters = 3;
        for (int iter = 0; iter < maxIters; iter++)
        {
            // Compute residuals
            var residuals = new double[xVals.Count];
            for (int i = 0; i < xVals.Count; i++)
            {
                residuals[i] = Math.Abs(yVals[i] - (slope * xVals[i] + intercept));
            }

            // Estimate robust scale from the residual MAD.
            // sigma_hat = median(|r|) / 0.6745 makes the MAD a consistent estimator of the
            // standard deviation under normal errors; omitting the divisor made the Tukey
            // tuning constant ~1.48x too small and rejected far more PIF pixels than intended.
            var sortedRes = residuals.OrderBy(r => r).ToArray();
            double medRes = sortedRes[sortedRes.Length / 2];
            double sigmaHat = (medRes + 1e-6) / 0.6745;
            double tuningConstant = Math.Max(0.015, 4.685 * sigmaHat);

            // Weighted regression
            double wSum = 0, wMeanX = 0, wMeanY = 0;
            var weights = new double[xVals.Count];
            for (int i = 0; i < xVals.Count; i++)
            {
                double u = residuals[i] / tuningConstant;
                weights[i] = u < 1.0 ? Math.Pow(1.0 - u * u, 2) : 0.0;
                wSum += weights[i];
                wMeanX += weights[i] * xVals[i];
                wMeanY += weights[i] * yVals[i];
            }

            if (wSum > 10)
            {
                wMeanX /= wSum;
                wMeanY /= wSum;

                double wNum = 0, wDenom = 0;
                for (int i = 0; i < xVals.Count; i++)
                {
                    if (weights[i] <= 0) continue;
                    double dx = xVals[i] - wMeanX;
                    double dy = yVals[i] - wMeanY;
                    wNum += weights[i] * dx * dy;
                    wDenom += weights[i] * dx * dx;
                }

                if (wDenom > 1e-7)
                {
                    slope = wNum / wDenom;
                    intercept = wMeanY - slope * wMeanX;
                }
            }
        }

        // Bound realistic gain adjustments. The intercept was fitted jointly with the
        // rejected slope, so it must be reset too - keeping it produced an affine transform
        // that matched neither the fit nor the identity.
        if (slope < 0.4 || slope > 2.5)
        {
            slope = 1.0;
            intercept = 0.0;
        }

        // Coefficient of determination of the final fit over the PIF sample.
        // Previously this was returned as a hardcoded 0.92 regardless of fit quality.
        double ssTot = 0, ssRes = 0;
        double finalMeanY = yVals.Average();
        for (int i = 0; i < xVals.Count; i++)
        {
            double predicted = slope * xVals[i] + intercept;
            ssRes += (yVals[i] - predicted) * (yVals[i] - predicted);
            ssTot += (yVals[i] - finalMeanY) * (yVals[i] - finalMeanY);
        }
        double r2 = ssTot > 1e-12 ? 1.0 - (ssRes / ssTot) : 0.0;

        return new NormalizationParams(slope, intercept, r2);
    }
}
