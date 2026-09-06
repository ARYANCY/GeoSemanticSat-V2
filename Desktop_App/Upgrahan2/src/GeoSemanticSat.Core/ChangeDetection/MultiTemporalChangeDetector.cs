using System;
using System.Collections.Generic;
using System.Linq;
using GeoSemanticSat.Core.Model;
using GeoSemanticSat.Core.Processing;
using GeoSemanticSat.Core.Raster;

namespace GeoSemanticSat.Core.ChangeDetection;

/// <summary>
/// Multi-Temporal Change Vector Analysis (CVA) with false-alarm suppression.
///
/// Implements the method documented in documentation/02_algorithms_and_mathematics.md, which
/// the previous revision did not: it computed neither the spectral change magnitude nor the
/// trajectory angle, and classified with a fixed if/else-if cascade.
///
///   Spectral delta   d_rho(x,y) = rho_T2(x,y) - rho_T1(x,y) over all shared bands
///   Magnitude        M(x,y)     = ||d_rho(x,y)||_2
///   Trajectory angle theta      = atan2(d_NDBI, d_NDVI)
///
/// Magnitude gates whether a patch changed at all; the per-type evidence rules then decide
/// what kind of change it is. All candidate types are scored and the strongest wins, so a
/// construction site is no longer forced into WaterExtentVariation purely because the water
/// rule happened to be evaluated first.
/// </summary>
public class MultiTemporalChangeDetector
{
    public record ChangeDetectionOptions(
        int PatchSize = 16,
        double MinConfidence = 0.65,
        bool EnableRadiometricNormalization = true,
        bool EnableJitterSuppression = true,
        bool EnableQualityMasking = true,
        double MinChangeMagnitude = 0.05
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

        var mask1 = options.EnableQualityMasking ? QualityMaskEngine.GenerateQualityMask(t1) : new QualityMaskFlags[h, w];
        var mask2 = options.EnableQualityMasking ? QualityMaskEngine.GenerateQualityMask(t2) : new QualityMaskFlags[h, w];

        var targetTile = options.EnableRadiometricNormalization
            ? RadiometricNormalizer.NormalizeTo(t2, t1, mask2, mask1)
            : t2;

        var ndvi1 = SpectralIndices.ComputeNDVI(t1);
        var ndvi2 = SpectralIndices.ComputeNDVI(targetTile);
        var ndbi1 = SpectralIndices.ComputeNDBI(t1);
        var ndbi2 = SpectralIndices.ComputeNDBI(targetTile);
        var ndwi1 = SpectralIndices.ComputeNDWI(t1);
        var ndwi2 = SpectralIndices.ComputeNDWI(targetTile);
        var bsi1 = SpectralIndices.ComputeBSI(t1);
        var bsi2 = SpectralIndices.ComputeBSI(targetTile);

        // MNDWI discriminates open water from built-up surfaces; NDWI alone cannot.
        // NDWI = (G - NIR)/(G + NIR) rises for ANY collapse in NIR, and replacing vegetation
        // with concrete collapses NIR hard: the synthetic concrete slab in the benchmark
        // scores NDWI = +0.31, indistinguishable from water by that index.
        var mndwi1 = SpectralIndices.ComputeMNDWI(t1);
        var mndwi2 = SpectralIndices.ComputeMNDWI(targetTile);

        var red1 = t1.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var red2 = targetTile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var grad1 = SpectralIndices.ComputeSobelGradient(red1, w, h);
        var grad2 = SpectralIndices.ComputeSobelGradient(red2, w, h);

        // Bands shared by both epochs, used for the true CVA magnitude.
        var sharedBands = t1.Bands.Keys.Where(b => targetTile.Bands.ContainsKey(b)).ToList();

        // Scene-wide drift estimated with the MEDIAN, not the mean: a mean is dragged by the
        // very changes it is supposed to be robust against.
        var drift = EstimateSceneDrift(mask1, mask2, w, h, ndvi1, ndvi2, ndbi1, ndbi2, ndwi1, ndwi2, bsi1, bsi2);

        List<ChangeRecord> changes = new();

        for (int py = 0; py <= h - patchSize; py += patchSize)
        {
            for (int px = 0; px <= w - patchSize; px += patchSize)
            {
                int validPixels = 0;
                double sumD_Ndvi = 0, sumD_Ndbi = 0, sumD_Ndwi = 0, sumD_Bsi = 0;
                double sumD_Grad = 0, sumAbsRed = 0, sumMagnitude = 0;
                double sumMndwi1 = 0, sumMndwi2 = 0;

                for (int y = py; y < py + patchSize; y++)
                {
                    for (int x = px; x < px + patchSize; x++)
                    {
                        // Bit-test the flags. The previous "!= QualityMaskFlags.Valid" also
                        // discarded pixels carrying only an informational Water or HighHaze bit.
                        if (!QualityMaskEngine.IsUsable(mask1[y, x]) || !QualityMaskEngine.IsUsable(mask2[y, x]))
                            continue;

                        validPixels++;
                        sumD_Ndvi += (ndvi2[y, x] - ndvi1[y, x]);
                        sumD_Ndbi += (ndbi2[y, x] - ndbi1[y, x]);
                        sumD_Ndwi += (ndwi2[y, x] - ndwi1[y, x]);
                        sumD_Bsi  += (bsi2[y, x] - bsi1[y, x]);
                        sumD_Grad += (grad2[y, x] - grad1[y, x]);
                        sumAbsRed += Math.Abs(red2[y, x] - red1[y, x]);
                        sumMndwi1 += mndwi1[y, x];
                        sumMndwi2 += mndwi2[y, x];

                        // CVA magnitude: Euclidean norm of the multi-band spectral delta.
                        double sumSq = 0.0;
                        foreach (var band in sharedBands)
                        {
                            double d = targetTile.Bands[band][y, x] - t1.Bands[band][y, x];
                            sumSq += d * d;
                        }
                        sumMagnitude += Math.Sqrt(sumSq);
                    }
                }

                if (validPixels < (patchSize * patchSize) * 0.45)
                    continue;

                double avgD_Ndvi = sumD_Ndvi / validPixels;
                double avgD_Ndbi = sumD_Ndbi / validPixels;
                double avgD_Ndwi = sumD_Ndwi / validPixels;
                double avgD_Bsi  = sumD_Bsi  / validPixels;
                double avgD_Grad = sumD_Grad / validPixels;
                double avgAbsRed = sumAbsRed / validPixels;
                double magnitude = sumMagnitude / validPixels;
                double avgMndwi1 = sumMndwi1 / validPixels;
                double avgMndwi2 = sumMndwi2 / validPixels;

                // Seasonal phenology correction applies to the vegetation index, which is what
                // the seasonal cycle actually drives. The other indices are left alone:
                // RadiometricNormalizer has already removed scene-level gain and offset, and
                // subtracting a scene median from NDBI/BSI as well measurably destroyed real
                // signal whenever the change of interest covered a large share of the scene
                // (a median is only a drift estimate while changes stay a scene minority).
                // The other drifts are still reported in Metrics for the analyst.
                double adjustedD_Ndvi = avgD_Ndvi - drift.Ndvi;
                double adjustedD_Ndbi = avgD_Ndbi;
                double adjustedD_Ndwi = avgD_Ndwi;
                double adjustedD_Bsi  = avgD_Bsi;

                // CVA magnitude gate: below this the patch has not physically changed.
                if (magnitude < options.MinChangeMagnitude)
                    continue;

                // Trajectory angle as documented: atan2(dNDBI, dNDVI).
                double trajectoryRad = Math.Atan2(adjustedD_Ndbi, adjustedD_Ndvi);

                if (options.EnableJitterSuppression && avgAbsRed > 0.08)
                {
                    if (RegistrationJitterFilter.IsRegistrationJitter(red1, red2, px, py, patchSize, w, h))
                        continue;
                }

                var best = ClassifyByEvidence(
                    adjustedD_Ndvi, adjustedD_Ndbi, adjustedD_Ndwi, adjustedD_Bsi,
                    avgD_Grad, avgAbsRed, magnitude, avgMndwi1, avgMndwi2);

                if (best.Type == ChangeType.NoChange || best.Score < options.MinConfidence)
                    continue;

                var pGeoTopLeft = t1.Transform.PixelToGeo(px, py);
                var pGeoBottomRight = t1.Transform.PixelToGeo(px + patchSize, py + patchSize);
                var patchBounds = new BoundingBox(
                    Math.Min(pGeoTopLeft.Longitude, pGeoBottomRight.Longitude),
                    Math.Min(pGeoTopLeft.Latitude, pGeoBottomRight.Latitude),
                    Math.Max(pGeoTopLeft.Longitude, pGeoBottomRight.Longitude),
                    Math.Max(pGeoTopLeft.Latitude, pGeoBottomRight.Latitude)
                );

                changes.Add(new ChangeRecord
                {
                    TileId = t2.TileId,
                    Bounds = patchBounds,
                    TimestampT1 = t1.AcquisitionTimestamp,
                    TimestampT2 = t2.AcquisitionTimestamp,
                    EarliestObservationTimestamp = t2.AcquisitionTimestamp,
                    Type = best.Type,
                    Confidence = Math.Round(best.Score, 4),
                    AffectedPixels = validPixels,
                    AreaSqMeters = patchBounds.AreaSquareMetres(),
                    ProcessingNotes = best.Notes,
                    Metrics = new Dictionary<string, double>
                    {
                        ["DeltaNDVI"] = Math.Round(avgD_Ndvi, 4),
                        ["DeltaNDBI"] = Math.Round(avgD_Ndbi, 4),
                        ["DeltaNDWI"] = Math.Round(avgD_Ndwi, 4),
                        ["DeltaBSI"] = Math.Round(avgD_Bsi, 4),
                        ["DeltaGradient"] = Math.Round(avgD_Grad, 4),
                        ["CvaMagnitude"] = Math.Round(magnitude, 4),
                        ["CvaTrajectoryRadians"] = Math.Round(trajectoryRad, 4),
                        ["SceneDriftNDVI"] = Math.Round(drift.Ndvi, 4),
                        ["SceneDriftNDBI"] = Math.Round(drift.Ndbi, 4),
                        ["SceneDriftNDWI"] = Math.Round(drift.Ndwi, 4),
                        ["SceneDriftBSI"] = Math.Round(drift.Bsi, 4),
                        ["MNDWI_T1"] = Math.Round(avgMndwi1, 4),
                        ["MNDWI_T2"] = Math.Round(avgMndwi2, 4)
                    }
                });
            }
        }

        return changes.OrderByDescending(c => c.Confidence).ToList();
    }

    private record SceneDrift(double Ndvi, double Ndbi, double Ndwi, double Bsi);

    private record Classification(ChangeType Type, double Score, string Notes);

    /// <summary>
    /// Scores every change type that meets its necessary conditions and returns the strongest.
    ///
    /// The previous if/else-if chain made classification order-dependent: the water rule was
    /// tested first, so any patch with |dNDWI| > 0.20 became WaterExtentVariation even when
    /// construction evidence was far stronger, and replacing vegetation with buildings moves
    /// NIR hard, which moves NDWI hard.
    ///
    /// Score is a bounded evidence strength in [0, 1], NOT a calibrated probability. It ranks
    /// candidates against each other and against MinConfidence; it must not be read as
    /// "75 percent chance this is real" without a labelled validation set.
    /// </summary>
    private static Classification ClassifyByEvidence(
        double dNdvi, double dNdbi, double dNdwi, double dBsi,
        double dGrad, double absRed, double magnitude,
        double mndwi1, double mndwi2)
    {
        var candidates = new List<Classification>();

        // A stronger spectral move is stronger evidence regardless of which rule fires.
        double magBonus = Math.Clamp(magnitude * 0.30, 0.0, 0.15);

        // Water requires BOTH a large NDWI move AND an endpoint that actually looks like open
        // water under MNDWI. Testing the delta alone made every new concrete surface a water
        // event, because NDWI cannot separate built-up from water.
        bool openWaterAfter = mndwi2 > 0.0;
        bool openWaterBefore = mndwi1 > 0.0;
        bool waterEndpointConsistent = dNdwi > 0 ? openWaterAfter : openWaterBefore;

        if (Math.Abs(dNdwi) > 0.20 && waterEndpointConsistent)
        {
            double score = Math.Clamp(0.70 + Math.Abs(dNdwi) * 0.40 + magBonus, 0.0, 0.99);
            string dir = dNdwi > 0 ? "Water expansion / inundation" : "Water contraction / drying";
            candidates.Add(new Classification(ChangeType.WaterExtentVariation, score,
                $"{dir}: dNDWI={dNdwi:F3}, MNDWI {mndwi1:F2}->{mndwi2:F2}, CVA magnitude={magnitude:F3}"));
        }

        if (dNdvi < -0.20 && dBsi > 0.10 && dGrad < 0.09)
        {
            double score = Math.Clamp(0.70 + Math.Abs(dNdvi) * 0.40 + dBsi * 0.30 + magBonus, 0.0, 0.98);
            candidates.Add(new Classification(ChangeType.Clearance, score,
                $"Vegetation loss / land clearance: dNDVI_adj={dNdvi:F3}, dBSI={dBsi:F3}, CVA magnitude={magnitude:F3}"));
        }

        // New structural edges are supporting evidence for construction, not a precondition:
        // a large uniform slab has almost no internal edge energy. Vegetation replaced by a
        // bright built surface is equally valid evidence, so either satisfies the rule.
        if (dNdbi > 0.18 && absRed > 0.14 && (dGrad > 0.08 || dNdvi < -0.10))
        {
            double score = Math.Clamp(0.72 + dNdbi * 0.40 + dGrad * 0.40 + magBonus, 0.0, 0.99);
            candidates.Add(new Classification(ChangeType.Construction, score,
                $"New structural signature: dNDBI={dNdbi:F3}, dGrad={dGrad:F3}, CVA magnitude={magnitude:F3}"));
        }

        if (dGrad > 0.12 && absRed > 0.12 && Math.Abs(dNdwi) < 0.15)
        {
            double score = Math.Clamp(0.68 + dGrad * 0.50 + magBonus, 0.0, 0.95);
            candidates.Add(new Classification(ChangeType.RoadDevelopment, score,
                $"Linear infrastructure development: dGrad={dGrad:F3}, dReflectance={absRed:F3}"));
        }

        if (absRed > 0.22 && Math.Abs(dNdbi) < 0.12 && Math.Abs(dNdvi) < 0.12 && dGrad > 0.07)
        {
            double score = Math.Clamp(0.66 + absRed * 0.30 + magBonus, 0.0, 0.92);
            candidates.Add(new Classification(ChangeType.ActivityConcentration, score,
                $"Localized transient activity / concentration: dReflectance={absRed:F3}"));
        }

        if (candidates.Count == 0)
            return new Classification(ChangeType.NoChange, 0.0, string.Empty);

        var best = candidates[0];
        foreach (var c in candidates)
        {
            if (c.Score > best.Score) best = c;
        }

        if (candidates.Count > 1)
        {
            var others = candidates.Where(c => c.Type != best.Type)
                                   .Select(c => $"{c.Type}={c.Score:F2}");
            best = best with { Notes = best.Notes + $" | runner-up: {string.Join(", ", others)}" };
        }

        return best;
    }

    /// <summary>Median scene-wide delta per index, over pixels usable in both epochs.</summary>
    private static SceneDrift EstimateSceneDrift(
        QualityMaskFlags[,] mask1, QualityMaskFlags[,] mask2, int w, int h,
        float[,] ndvi1, float[,] ndvi2, float[,] ndbi1, float[,] ndbi2,
        float[,] ndwi1, float[,] ndwi2, float[,] bsi1, float[,] bsi2)
    {
        var dNdvi = new List<double>();
        var dNdbi = new List<double>();
        var dNdwi = new List<double>();
        var dBsi = new List<double>();

        for (int y = 0; y < h; y += 4)
        {
            for (int x = 0; x < w; x += 4)
            {
                if (!QualityMaskEngine.IsUsable(mask1[y, x]) || !QualityMaskEngine.IsUsable(mask2[y, x]))
                    continue;

                dNdvi.Add(ndvi2[y, x] - ndvi1[y, x]);
                dNdbi.Add(ndbi2[y, x] - ndbi1[y, x]);
                dNdwi.Add(ndwi2[y, x] - ndwi1[y, x]);
                dBsi.Add(bsi2[y, x] - bsi1[y, x]);
            }
        }

        return new SceneDrift(Median(dNdvi), Median(dNdbi), Median(dNdwi), Median(dBsi));
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : 0.5 * (values[mid - 1] + values[mid]);
    }
}
