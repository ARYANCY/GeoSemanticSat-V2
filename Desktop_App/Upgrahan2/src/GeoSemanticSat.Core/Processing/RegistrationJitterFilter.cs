using System;

namespace GeoSemanticSat.Core.Processing;

/// <summary>
/// Co-registration jitter and edge tolerance filter.
/// Suppresses false change alarms caused by sub-pixel to 2-pixel registration error.
/// </summary>
public static class RegistrationJitterFilter
{
    public const double SsimThresholdJitter = 0.82;

    /// <summary>Outcome of the jitter test, distinguishing states the boolean conflated.</summary>
    public enum AlignmentVerdict
    {
        /// <summary>The two patches differ, and the difference is not explained by a shift.</summary>
        GenuineDifference,

        /// <summary>The patches are effectively identical: nothing changed and nothing shifted.</summary>
        NoDifference,

        /// <summary>A sub-pixel or few-pixel shift explains most of the difference.</summary>
        RegistrationJitter
    }

    /// <summary>Sub-pixel alignment estimate produced by the parabolic peak fit.</summary>
    public readonly record struct AlignmentResult(
        AlignmentVerdict Verdict,
        double ShiftX,
        double ShiftY,
        double BaseDifference,
        double AlignedDifference);

    /// <summary>
    /// Computes local patch SSIM between two patches. Returns [-1, 1], 1 = identical.
    /// </summary>
    public static double ComputeSSIM(float[,] patchA, float[,] patchB, int size)
    {
        int n = size * size;
        // A single pixel has no sample variance; the old code divided by (n - 1) = 0 and
        // returned NaN/Infinity for size == 1.
        if (n <= 1) return 1.0;

        double meanA = 0, meanB = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                meanA += patchA[y, x];
                meanB += patchB[y, x];
            }
        }
        meanA /= n;
        meanB /= n;

        double varA = 0, varB = 0, covar = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double da = patchA[y, x] - meanA;
                double db = patchB[y, x] - meanB;
                varA += da * da;
                varB += db * db;
                covar += da * db;
            }
        }
        varA /= (n - 1);
        varB /= (n - 1);
        covar /= (n - 1);

        double c1 = 0.0001; // (k1 * L)^2 for L = 1 reflectance
        double c2 = 0.0009; // (k2 * L)^2

        double num = (2 * meanA * meanB + c1) * (2 * covar + c2);
        double den = (meanA * meanA + meanB * meanB + c1) * (varA + varB + c2);

        return den > 1e-9 ? Math.Clamp(num / den, -1.0, 1.0) : 1.0;
    }

    /// <summary>
    /// True when the patch difference is explained by misregistration.
    /// Kept for existing callers; see Analyze for the full verdict and sub-pixel offset.
    /// </summary>
    public static bool IsRegistrationJitter(float[,] bandA, float[,] bandB, int startX, int startY, int patchSize, int w, int h)
        => Analyze(bandA, bandB, startX, startY, patchSize, w, h).Verdict == AlignmentVerdict.RegistrationJitter;

    /// <summary>
    /// Builds a 3x3 dissimilarity surface over integer shifts, locates the minimum, and
    /// refines it to sub-pixel precision with a quadratic (parabolic) fit through the
    /// minimum and its two neighbours along each axis. That interpolation is what the
    /// documentation specifies and what the previous revision never implemented: it took an
    /// integer argmin plus one fixed (+0.5, +0.5) box average.
    ///
    /// Every shift is scored over the SAME pixel support, so the means are comparable. The
    /// old code averaged each shift over however many pixels happened to be in bounds, which
    /// biased the comparison near tile edges.
    /// </summary>
    public static AlignmentResult Analyze(float[,] bandA, float[,] bandB, int startX, int startY, int patchSize, int w, int h)
    {
        // Support shrunk by one pixel on every side so all nine shifts stay in bounds.
        int x0 = Math.Max(1, startX);
        int y0 = Math.Max(1, startY);
        int x1 = Math.Min(w - 2, startX + patchSize - 1);
        int y1 = Math.Min(h - 2, startY + patchSize - 1);

        if (x1 < x0 || y1 < y0)
            return new AlignmentResult(AlignmentVerdict.GenuineDifference, 0, 0, 0, 0);

        var surface = new double[3, 3];
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                double sum = 0;
                int n = 0;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        sum += Math.Abs(bandA[y, x] - bandB[y + dy, x + dx]);
                        n++;
                    }
                }
                surface[dy + 1, dx + 1] = n > 0 ? sum / n : double.MaxValue;
            }
        }

        double baseDiff = surface[1, 1];

        // Identical content is not misregistration, it is simply no change. Reporting it as
        // jitter conflated two different states for the caller.
        if (baseDiff < 0.02)
            return new AlignmentResult(AlignmentVerdict.NoDifference, 0, 0, baseDiff, baseDiff);

        int bestDx = 0, bestDy = 0;
        double bestDiff = double.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (surface[dy + 1, dx + 1] < bestDiff)
                {
                    bestDiff = surface[dy + 1, dx + 1];
                    bestDx = dx;
                    bestDy = dy;
                }
            }
        }

        // Parabolic refinement about the integer minimum. Only valid when the minimum has a
        // neighbour on each side, i.e. it is not on the edge of the 3x3 window.
        double subX = bestDx;
        double subY = bestDy;
        if (bestDx == 0)
            subX = ParabolicOffset(surface[bestDy + 1, 0], surface[bestDy + 1, 1], surface[bestDy + 1, 2]);
        if (bestDy == 0)
            subY = ParabolicOffset(surface[0, bestDx + 1], surface[1, bestDx + 1], surface[2, bestDx + 1]);

        // Bilinear resample of B at the refined offset gives the residual after alignment.
        double alignedDiff = ResampledDifference(bandA, bandB, x0, y0, x1, y1, subX, subY);

        // Misregistration if aligning at the refined offset removes most of the difference
        // and the required shift is small. A genuine new object cannot be shifted away.
        bool shiftIsSmall = Math.Sqrt(subX * subX + subY * subY) <= 2.0;
        bool residualCollapsed = alignedDiff < baseDiff * 0.50;

        var verdict = (shiftIsSmall && residualCollapsed)
            ? AlignmentVerdict.RegistrationJitter
            : AlignmentVerdict.GenuineDifference;

        return new AlignmentResult(verdict, subX, subY, baseDiff, alignedDiff);
    }

    /// <summary>
    /// Vertex of the parabola through (-1, left), (0, centre), (+1, right).
    /// Offset = 0.5 * (left - right) / (left - 2*centre + right), valid for a true minimum.
    /// </summary>
    private static double ParabolicOffset(double left, double centre, double right)
    {
        double denom = left - 2.0 * centre + right;
        if (Math.Abs(denom) < 1e-12) return 0.0;
        return Math.Clamp(0.5 * (left - right) / denom, -1.0, 1.0);
    }

    /// <summary>Mean absolute difference with B bilinearly resampled at a fractional offset.</summary>
    private static double ResampledDifference(float[,] bandA, float[,] bandB,
                                              int x0, int y0, int x1, int y1,
                                              double shiftX, double shiftY)
    {
        double sum = 0;
        int n = 0;

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double sx = x + shiftX;
                double sy = y + shiftY;

                int fx = (int)Math.Floor(sx);
                int fy = (int)Math.Floor(sy);
                double tx = sx - fx;
                double ty = sy - fy;

                if (fx < 0 || fy < 0 || fx + 1 >= bandB.GetLength(1) || fy + 1 >= bandB.GetLength(0))
                    continue;

                double top = bandB[fy, fx] * (1 - tx) + bandB[fy, fx + 1] * tx;
                double bottom = bandB[fy + 1, fx] * (1 - tx) + bandB[fy + 1, fx + 1] * tx;
                double interpolated = top * (1 - ty) + bottom * ty;

                sum += Math.Abs(bandA[y, x] - interpolated);
                n++;
            }
        }

        return n > 0 ? sum / n : double.MaxValue;
    }
}
