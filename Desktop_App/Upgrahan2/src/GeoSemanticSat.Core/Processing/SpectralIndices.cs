using System;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.Processing;

public static class SpectralIndices
{
    public static float[,] ComputeNDVI(SatelliteTile tile)
    {
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        return ComputeNormalizedDifference(nir, red, tile.Width, tile.Height);
    }

    public static float[,] ComputeNDWI(SatelliteTile tile)
    {
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        return ComputeNormalizedDifference(green, nir, tile.Width, tile.Height);
    }

    public static float[,] ComputeMNDWI(SatelliteTile tile)
    {
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var swir = tile.GetBandOrFallback(SpectralBand.SWIR1, SpectralBand.NIR);
        return ComputeNormalizedDifference(green, swir, tile.Width, tile.Height);
    }

    public static float[,] ComputeNDBI(SatelliteTile tile)
    {
        var swir = tile.GetBandOrFallback(SpectralBand.SWIR1, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        return ComputeNormalizedDifference(swir, nir, tile.Width, tile.Height);
    }

    public static float[,] ComputeBSI(SatelliteTile tile)
    {
        int w = tile.Width;
        int h = tile.Height;
        var swir = tile.GetBandOrFallback(SpectralBand.SWIR1, SpectralBand.Red);
        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);

        var bsi = new float[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float num = (swir[y, x] + red[y, x]) - (nir[y, x] + blue[y, x]);
                float den = (swir[y, x] + red[y, x]) + (nir[y, x] + blue[y, x]);
                bsi[y, x] = Math.Abs(den) > 1e-4f ? Math.Clamp(num / den, -1.0f, 1.0f) : 0.0f;
            }
        }
        return bsi;
    }

    public static float[,] ComputeSobelGradient(float[,] input, int w, int h)
    {
        var grad = new float[h, w];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                // Sobel X
                float gx = (-1 * input[y - 1, x - 1] + 1 * input[y - 1, x + 1])
                         + (-2 * input[y, x - 1]     + 2 * input[y, x + 1])
                         + (-1 * input[y + 1, x - 1] + 1 * input[y + 1, x + 1]);

                // Sobel Y
                float gy = (-1 * input[y - 1, x - 1] - 2 * input[y - 1, x] - 1 * input[y - 1, x + 1])
                         + ( 1 * input[y + 1, x - 1] + 2 * input[y + 1, x] + 1 * input[y + 1, x + 1]);

                grad[y, x] = (float)Math.Sqrt(gx * gx + gy * gy);
            }
        }
        return grad;
    }

    private static float[,] ComputeNormalizedDifference(float[,] bandA, float[,] bandB, int w, int h)
    {
        var result = new float[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = bandA[y, x];
                float b = bandB[y, x];
                float denom = a + b;
                if (Math.Abs(denom) > 1e-5f)
                {
                    result[y, x] = Math.Clamp((a - b) / denom, -1.0f, 1.0f);
                }
                else
                {
                    result[y, x] = 0.0f;
                }
            }
        }
        return result;
    }
}
