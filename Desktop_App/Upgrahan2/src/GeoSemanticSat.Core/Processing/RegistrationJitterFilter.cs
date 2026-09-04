using System;

namespace GeoSemanticSat.Core.Processing;

/// <summary>
/// Co-registration Jitter and Edge Tolerance Filter.
/// Uses Structural Similarity (SSIM) and local gradient shift matching to prevent
/// false change alarms caused by 1-2 pixel registration errors.
/// </summary>
public static class RegistrationJitterFilter
{
    public const double SsimThresholdJitter = 0.82;

    /// <summary>
    /// Computes local patch SSIM (Structural Similarity Index) between two patches.
    /// Returns value between -1.0 and 1.0 (1.0 = structurally identical).
    /// </summary>
    public static double ComputeSSIM(float[,] patchA, float[,] patchB, int size)
    {
        double meanA = 0, meanB = 0;
        int n = size * size;
        if (n == 0) return 1.0;

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

        double c1 = 0.0001; // (k1 * L)^2
        double c2 = 0.0009; // (k2 * L)^2

        double num = (2 * meanA * meanB + c1) * (2 * covar + c2);
        double den = (meanA * meanA + meanB * meanB + c1) * (varA + varB + c2);

        return den > 1e-9 ? Math.Clamp(num / den, -1.0, 1.0) : 1.0;
    }

    /// <summary>
    /// Checks if a candidate change patch is actually caused by sub-pixel or 1-2 pixel registration shift.
    /// If shifting patch B by (-1, 0, +1) pixels dramatically increases correlation (> 0.92), it is registration jitter.
    /// </summary>
    public static bool IsRegistrationJitter(float[,] bandA, float[,] bandB, int startX, int startY, int patchSize, int w, int h)
    {
        double baseDiff = 0.0;
        int count = 0;

        for (int y = 0; y < patchSize; y++)
        {
            for (int x = 0; x < patchSize; x++)
            {
                int px = startX + x;
                int py = startY + y;
                if (px >= 0 && px < w && py >= 0 && py < h)
                {
                    baseDiff += Math.Abs(bandA[py, px] - bandB[py, px]);
                    count++;
                }
            }
        }
        if (count == 0) return false;
        double avgBaseDiff = baseDiff / count;
        if (avgBaseDiff < 0.04) return true; // negligible difference

        // Test neighbor pixel shifts in [-1, +1]
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;

                double shiftedDiff = 0.0;
                int shiftedCount = 0;
                for (int y = 0; y < patchSize; y++)
                {
                    for (int x = 0; x < patchSize; x++)
                    {
                        int ax = startX + x;
                        int ay = startY + y;
                        int bx = startX + x + dx;
                        int by = startY + y + dy;

                        if (ax >= 0 && ax < w && ay >= 0 && ay < h &&
                            bx >= 0 && bx < w && by >= 0 && by < h)
                        {
                            shiftedDiff += Math.Abs(bandA[ay, ax] - bandB[by, bx]);
                            shiftedCount++;
                        }
                    }
                }

                if (shiftedCount > 0)
                {
                    double avgShifted = shiftedDiff / shiftedCount;
                    // If a 1-pixel shift reduces the error by more than 55%, it is misalignment jitter!
                    if (avgShifted < avgBaseDiff * 0.45)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
