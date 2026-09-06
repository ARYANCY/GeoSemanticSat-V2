using System;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.Processing;

/// <summary>
/// Spectral index computation.
///
/// When NIR or SWIR is absent the classic indices are undefined, and the previous behaviour,
/// letting GetBandOrFallback substitute Red, collapsed them to a constant zero across the
/// whole scene (NDVI = (R-R)/(R+R) = 0, and NDBI = (S-N)/(S+N) with both falling back to Red).
/// Every downstream consumer then compared zeros with no indication anything was wrong.
/// These methods now fall back to documented visible-band proxies, so 3-band RGB imagery
/// carries real signal. The proxies are weaker than the true indices;
/// SatelliteTile.RequiresVisibleBandProxies reports when they are in use.
/// </summary>
public static class SpectralIndices
{
    /// <summary>
    /// NDVI = (NIR - Red) / (NIR + Red).
    /// Without NIR, falls back to normalized Excess Green, (2G - R - B) / (2G + R + B),
    /// the standard visible-band greenness index.
    /// </summary>
    public static float[,] ComputeNDVI(SatelliteTile tile)
    {
        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        if (tile.HasBand(SpectralBand.NIR))
        {
            return ComputeNormalizedDifference(tile.Bands[SpectralBand.NIR], red, tile.Width, tile.Height);
        }

        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        return Combine(tile, red, green, blue, (r, g, b) => Nd(2f * g, r + b));
    }

    /// <summary>
    /// NDWI = (Green - NIR) / (Green + NIR).
    /// Without NIR, falls back to blue dominance (B - R) / (B + R): water absorbs strongly
    /// in red and reflects comparatively more blue.
    /// </summary>
    public static float[,] ComputeNDWI(SatelliteTile tile)
    {
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        if (tile.HasBand(SpectralBand.NIR))
        {
            return ComputeNormalizedDifference(green, tile.Bands[SpectralBand.NIR], tile.Width, tile.Height);
        }

        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        return Combine(tile, red, green, blue, (r, g, b) => Nd(b, r));
    }

    /// <summary>
    /// MNDWI = (Green - SWIR) / (Green + SWIR). Without SWIR, reuses the NDWI proxy.
    /// </summary>
    public static float[,] ComputeMNDWI(SatelliteTile tile)
    {
        if (tile.HasBand(SpectralBand.SWIR1))
        {
            var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
            return ComputeNormalizedDifference(green, tile.Bands[SpectralBand.SWIR1], tile.Width, tile.Height);
        }
        return ComputeNDWI(tile);
    }

    /// <summary>
    /// NDBI = (SWIR - NIR) / (SWIR + NIR).
    /// Without SWIR or NIR, falls back to normalized Excess Red, (2R - G - B) / (2R + G + B).
    /// Concrete, asphalt and bare construction surfaces are red-shifted relative to
    /// vegetation in the visible spectrum.
    /// </summary>
    public static float[,] ComputeNDBI(SatelliteTile tile)
    {
        if (tile.HasBand(SpectralBand.SWIR1) && tile.HasBand(SpectralBand.NIR))
        {
            return ComputeNormalizedDifference(
                tile.Bands[SpectralBand.SWIR1], tile.Bands[SpectralBand.NIR], tile.Width, tile.Height);
        }

        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        return Combine(tile, red, green, blue, (r, g, b) => Nd(2f * r, g + b));
    }

    /// <summary>
    /// BSI = ((SWIR + Red) - (NIR + Blue)) / ((SWIR + Red) + (NIR + Blue)).
    /// Without SWIR or NIR, falls back to ((R + G) - 2B) / ((R + G) + 2B): exposed soil has
    /// markedly low blue reflectance relative to red and green.
    /// </summary>
    public static float[,] ComputeBSI(SatelliteTile tile)
    {
        int w = tile.Width;
        int h = tile.Height;
        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);

        if (!tile.HasBand(SpectralBand.SWIR1) || !tile.HasBand(SpectralBand.NIR))
        {
            return Combine(tile, red, green, blue, (r, g, b) => Nd(r + g, 2f * b));
        }

        var swir = tile.Bands[SpectralBand.SWIR1];
        var nir = tile.Bands[SpectralBand.NIR];

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

    /// <summary>Bounded normalized difference of two scalars, always in [-1, 1].</summary>
    public static float Nd(float a, float b)
    {
        float den = a + b;
        return Math.Abs(den) > 1e-5f ? Math.Clamp((a - b) / den, -1.0f, 1.0f) : 0.0f;
    }

    public static float[,] ComputeSobelGradient(float[,] input, int w, int h)
    {
        var grad = new float[h, w];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                float gx = (-1 * input[y - 1, x - 1] + 1 * input[y - 1, x + 1])
                         + (-2 * input[y, x - 1]     + 2 * input[y, x + 1])
                         + (-1 * input[y + 1, x - 1] + 1 * input[y + 1, x + 1]);

                float gy = (-1 * input[y - 1, x - 1] - 2 * input[y - 1, x] - 1 * input[y - 1, x + 1])
                         + ( 1 * input[y + 1, x - 1] + 2 * input[y + 1, x] + 1 * input[y + 1, x + 1]);

                grad[y, x] = (float)Math.Sqrt(gx * gx + gy * gy);
            }
        }
        return grad;
    }

    private static float[,] Combine(SatelliteTile tile, float[,] red, float[,] green, float[,] blue,
                                    Func<float, float, float, float> f)
    {
        var result = new float[tile.Height, tile.Width];
        for (int y = 0; y < tile.Height; y++)
            for (int x = 0; x < tile.Width; x++)
                result[y, x] = f(red[y, x], green[y, x], blue[y, x]);
        return result;
    }

    private static float[,] ComputeNormalizedDifference(float[,] bandA, float[,] bandB, int w, int h)
    {
        var result = new float[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                result[y, x] = Nd(bandA[y, x], bandB[y, x]);
            }
        }
        return result;
    }
}
