using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.Raster;

namespace GeoSemanticSat.Core.ChangeDetection;

/// <summary>
/// Multi-Temporal Satellite Change Detection Engine with False-Alarm Suppression.
/// Classifies changes into Construction, Clearance, Water Extent Variation, Road Development, and Activity.
/// Filters out seasonal phenology, illumination differences, clouds, shadows, and co-registration jitter.
/// </summary>
public class MultiTemporalChangeDetector
{
    public record ChangeDetectionOptions(
        int PatchSize = 16,
        double MinConfidence = 0.65,
        bool EnableRadiometricNormalization = true,
        bool EnableJitterSuppression = true,
        bool EnableQualityMasking = true
    );

    /// <summary>
    /// Detects changes between baseline tile T1 and target tile T2.
    /// </summary>
    public static List<ChangeRecord> DetectChanges(SatelliteTile t1, SatelliteTile t2, ChangeDetectionOptions? options = null)
    {
        options ??= new ChangeDetectionOptions();

        int w = Math.Min(t1.Width, t2.Width);
        int h = Math.Min(t1.Height, t2.Height);
        int patchSize = options.PatchSize;

        // 1. Generate Quality Masks
        var mask1 = options.EnableQualityMasking ? QualityMaskEngine.GenerateQualityMask(t1) : new QualityMaskFlags[h, w];
        var mask2 = options.EnableQualityMasking ? QualityMaskEngine.GenerateQualityMask(t2) : new QualityMaskFlags[h, w];

        // 2. Relative Radiometric Normalization
        var targetTile = options.EnableRadiometricNormalization
            ? RadiometricNormalizer.NormalizeTo(t2, t1, mask2, mask1)
            : t2;

        // 3. Compute Spectral Indices for both epochs
        var ndvi1 = SpectralIndices.ComputeNDVI(t1);
        var ndvi2 = SpectralIndices.ComputeNDVI(targetTile);

        var ndbi1 = SpectralIndices.ComputeNDBI(t1);
        var ndbi2 = SpectralIndices.ComputeNDBI(targetTile);

        var ndwi1 = SpectralIndices.ComputeNDWI(t1);
        var ndwi2 = SpectralIndices.ComputeNDWI(targetTile);

        var bsi1 = SpectralIndices.ComputeBSI(t1);
        var bsi2 = SpectralIndices.ComputeBSI(targetTile);

        // Compute high-frequency spatial gradients (Sobel)
        var red1 = t1.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var red2 = targetTile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var grad1 = SpectralIndices.ComputeSobelGradient(red1, w, h);
        var grad2 = SpectralIndices.ComputeSobelGradient(red2, w, h);

        // Estimate scene-wide seasonal NDVI shift (to suppress false seasonal vegetation phenology alarms)
        double sceneNdviDeltaSum = 0.0;
        int sceneValidPixels = 0;
        for (int y = 0; y < h; y += 4)
        {
            for (int x = 0; x < w; x += 4)
            {
                if (mask1[y, x] == QualityMaskFlags.Valid && mask2[y, x] == QualityMaskFlags.Valid)
                {
                    sceneNdviDeltaSum += (ndvi2[y, x] - ndvi1[y, x]);
                    sceneValidPixels++;
                }
            }
        }
        double backgroundSeasonalNdviShift = sceneValidPixels > 0 ? sceneNdviDeltaSum / sceneValidPixels : 0.0;

        List<ChangeRecord> changes = new();

        // 4. Iterate by patches
        for (int py = 0; py <= h - patchSize; py += patchSize)
        {
            for (int px = 0; px <= w - patchSize; px += patchSize)
            {
                // Count valid unmasked pixels in patch
                int validPixels = 0;
                double sumD_Ndvi = 0, sumD_Ndbi = 0, sumD_Ndwi = 0, sumD_Bsi = 0;
                double sumD_Grad = 0, sumAbsRed = 0;

                for (int y = py; y < py + patchSize; y++)
                {
                    for (int x = px; x < px + patchSize; x++)
                    {
                        if (mask1[y, x] != QualityMaskFlags.Valid || mask2[y, x] != QualityMaskFlags.Valid)
                            continue;

                        validPixels++;
                        sumD_Ndvi += (ndvi2[y, x] - ndvi1[y, x]);
                        sumD_Ndbi += (ndbi2[y, x] - ndbi1[y, x]);
                        sumD_Ndwi += (ndwi2[y, x] - ndwi1[y, x]);
                        sumD_Bsi  += (bsi2[y, x] - bsi1[y, x]);
                        sumD_Grad += (grad2[y, x] - grad1[y, x]);
                        sumAbsRed += Math.Abs(red2[y, x] - red1[y, x]);
                    }
                }

                // If patch is predominantly covered by cloud or shadow, skip
                if (validPixels < (patchSize * patchSize) * 0.45)
                    continue;

                double avgD_Ndvi = sumD_Ndvi / validPixels;
                double avgD_Ndbi = sumD_Ndbi / validPixels;
                double avgD_Ndwi = sumD_Ndwi / validPixels;
                double avgD_Bsi  = sumD_Bsi  / validPixels;
                double avgD_Grad = sumD_Grad / validPixels;
                double avgAbsRed = sumAbsRed / validPixels;

                // Subtract background seasonal shift from NDVI delta
                double adjustedD_Ndvi = avgD_Ndvi - backgroundSeasonalNdviShift;

                // 5. Check Registration Jitter
                if (options.EnableJitterSuppression && avgAbsRed > 0.08)
                {
                    if (RegistrationJitterFilter.IsRegistrationJitter(red1, red2, px, py, patchSize, w, h))
                    {
                        // False alarm caused by sub-pixel misregistration!
                        continue;
                    }
                }

                // 6. Classification & Confidence Scoring
                ChangeType detectedType = ChangeType.NoChange;
                double confidence = 0.0;
                string notes = string.Empty;

                // Rule 1: Water Extent Variation (Inundation, lake/reservoir contraction or river channel shift)
                if (Math.Abs(avgD_Ndwi) > 0.20)
                {
                    detectedType = ChangeType.WaterExtentVariation;
                    confidence = Math.Clamp(0.75 + (Math.Abs(avgD_Ndwi) * 0.4), 0.0, 0.99);
                    notes = avgD_Ndwi > 0
                        ? $"Water expansion / inundation: ΔNDWI={avgD_Ndwi:F3}"
                        : $"Water contraction / drying: ΔNDWI={avgD_Ndwi:F3}";
                }
                // Rule 2: Clearance / Deforestation (Strong vegetation loss with low structural edge density)
                else if (adjustedD_Ndvi < -0.20 && avgD_Bsi > 0.10 && avgD_Grad < 0.09)
                {
                    detectedType = ChangeType.Clearance;
                    confidence = Math.Clamp(0.72 + (Math.Abs(adjustedD_Ndvi) * 0.4) + (avgD_Bsi * 0.3), 0.0, 0.98);
                    notes = $"Vegetation loss / land clearance: ΔNDVI_adj={adjustedD_Ndvi:F3}, ΔBSI={avgD_Bsi:F3}";
                }
                // Rule 3: Construction (New structures, high NDBI increase AND high structural edge gradients)
                else if (avgD_Ndbi > 0.18 && avgD_Grad > 0.08 && avgAbsRed > 0.14)
                {
                    detectedType = ChangeType.Construction;
                    confidence = Math.Clamp(0.75 + (avgD_Ndbi * 0.4) + (avgD_Grad * 0.4), 0.0, 0.99);
                    notes = $"New structural signature: ΔNDBI={avgD_Ndbi:F3}, ΔGrad={avgD_Grad:F3}";
                }
                // Rule 4: Road / Linear Infrastructure Development
                else if (avgD_Grad > 0.12 && avgAbsRed > 0.12 && Math.Abs(avgD_Ndwi) < 0.15)
                {
                    detectedType = ChangeType.RoadDevelopment;
                    confidence = Math.Clamp(0.70 + (avgD_Grad * 0.5), 0.0, 0.95);
                    notes = $"Linear infrastructure development: ΔGrad={avgD_Grad:F3}, ΔReflectance={avgAbsRed:F3}";
                }
                // Rule 5: Activity / Transient Object Concentration (vehicles, equipment staging)
                else if (avgAbsRed > 0.22 && Math.Abs(avgD_Ndbi) < 0.12 && Math.Abs(adjustedD_Ndvi) < 0.12 && avgD_Grad > 0.07)
                {
                    detectedType = ChangeType.ActivityConcentration;
                    confidence = Math.Clamp(0.68 + (avgAbsRed * 0.3), 0.0, 0.92);
                    notes = $"Localized transient activity / concentration: ΔReflectance={avgAbsRed:F3}";
                }

                if (detectedType != ChangeType.NoChange && confidence >= options.MinConfidence)
                {
                    // Compute geographic coordinates for this patch
                    var pGeoTopLeft = t1.Transform.PixelToGeo(px, py);
                    var pGeoBottomRight = t1.Transform.PixelToGeo(px + patchSize, py + patchSize);
                    var patchBounds = new BoundingBox(
                        Math.Min(pGeoTopLeft.Longitude, pGeoBottomRight.Longitude),
                        Math.Min(pGeoTopLeft.Latitude, pGeoBottomRight.Latitude),
                        Math.Max(pGeoTopLeft.Longitude, pGeoBottomRight.Longitude),
                        Math.Max(pGeoTopLeft.Latitude, pGeoBottomRight.Latitude)
                    );

                    double gsd = t1.GroundSamplingDistanceMeters;
                    double areaSqM = patchSize * patchSize * gsd * gsd;

                    changes.Add(new ChangeRecord
                    {
                        TileId = t2.TileId,
                        Bounds = patchBounds,
                        TimestampT1 = t1.AcquisitionTimestamp,
                        TimestampT2 = t2.AcquisitionTimestamp,
                        EarliestObservationTimestamp = t2.AcquisitionTimestamp,
                        Type = detectedType,
                        Confidence = Math.Round(confidence, 4),
                        AffectedPixels = validPixels,
                        AreaSqMeters = areaSqM,
                        ProcessingNotes = notes,
                        Metrics = new Dictionary<string, double>
                        {
                            ["DeltaNDVI"] = Math.Round(avgD_Ndvi, 4),
                            ["DeltaNDBI"] = Math.Round(avgD_Ndbi, 4),
                            ["DeltaNDWI"] = Math.Round(avgD_Ndwi, 4),
                            ["DeltaBSI"] = Math.Round(avgD_Bsi, 4),
                            ["DeltaGradient"] = Math.Round(avgD_Grad, 4)
                        }
                    });
                }
            }
        }

        return changes.OrderByDescending(c => c.Confidence).ToList();
    }
}
