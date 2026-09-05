using System;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.Raster;

[Flags]
public enum QualityMaskFlags : byte
{
    Valid = 0,
    Cloud = 1 << 0,
    CloudShadow = 1 << 1,
    Snow = 1 << 2,
    Water = 1 << 3,
    HighHaze = 1 << 4,
    Saturated = 1 << 5
}

/// <summary>
/// Quality Handling and False-Alarm Suppression Mask Engine.
/// Detects clouds using multi-spectral Blue/NIR/SWIR thresholds,
/// projects directional cloud shadows using solar angles (elevation + azimuth ray-casting),
/// and detects snow vs. cloud using NDSI (Normalized Difference Snow Index).
/// </summary>
public class QualityMaskEngine
{
    public const float CloudBlueThreshold = 0.22f;
    public const float CloudCirrusThreshold = 0.18f;
    public const float ShadowNirMaxThreshold = 0.14f;

    /// <summary>
    /// Generates pixel-level quality flags for the satellite tile.
    /// </summary>
    public static QualityMaskFlags[,] GenerateQualityMask(SatelliteTile tile)
    {
        int w = tile.Width;
        int h = tile.Height;
        var mask = new QualityMaskFlags[h, w];

        if (tile.Platform == SensorPlatform.Sentinel1_SAR)
        {
            // SAR has all-weather penetration; flag extreme speckle/saturation only
            var vv = tile.GetBandOrFallback(SpectralBand.SAR_VV, SpectralBand.SAR_VV);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (vv[y, x] >= 0.98f) mask[y, x] |= QualityMaskFlags.Saturated;
                }
            }
            return mask;
        }

        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        var swir = tile.GetBandOrFallback(SpectralBand.SWIR1, SpectralBand.NIR);

        // Step 1: Detect Clouds and Snow
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float b = blue[y, x];
                float g = green[y, x];
                float r = red[y, x];
                float n = nir[y, x];
                float s = swir[y, x];

                // Check saturation
                if (b > 0.99f && g > 0.99f && r > 0.99f)
                {
                    mask[y, x] |= QualityMaskFlags.Saturated;
                }

                // NDSI = (Green - SWIR) / (Green + SWIR)
                float ndsi = (g + s) > 0.001f ? (g - s) / (g + s) : 0.0f;
                // NDVI = (NIR - Red) / (NIR + Red)
                float ndvi = (n + r) > 0.001f ? (n - r) / (n + r) : 0.0f;

                // Thick dense clouds (high reflectance across visible & NIR)
                if (b > 0.50f && g > 0.45f && r > 0.40f && n > 0.45f)
                {
                    mask[y, x] |= QualityMaskFlags.Cloud;
                    continue;
                }

                // Snow: high NDSI, cold, low SWIR (< 0.08) and lower NIR
                if (ndsi > 0.45f && s < 0.08f && n < 0.40f && b > 0.25f)
                {
                    mask[y, x] |= QualityMaskFlags.Snow;
                    continue;
                }

                // Standard Cloud / Cirrus condition: High blue reflectance, flat visible spectrum, low NDVI
                bool isBrightVisible = b > CloudBlueThreshold && g > 0.20f && r > 0.18f;
                bool isColdCirrus = s < 0.25f && b > 0.20f;
                if (isBrightVisible && isColdCirrus && ndvi < 0.20f)
                {
                    mask[y, x] |= QualityMaskFlags.Cloud;
                }
                else if (b > 0.18f && ndvi < 0.10f && s > 0.15f)
                {
                    mask[y, x] |= QualityMaskFlags.HighHaze;
                }
            }
        }

        // Step 2: Geometric Ray-Casting for Cloud Shadows & Edge Dilation
        // Direction opposite to solar azimuth:
        double shadowAngleRad = ((tile.SunAzimuthDegrees + 180.0) % 360.0) * (Math.PI / 180.0);
        double sunElevRad = Math.Max(12.0, Math.Min(85.0, tile.SunElevationDegrees)) * (Math.PI / 180.0);

        // Typical cloud base altitude 1500m - 3000m. In pixels (accounting for GSD):
        double gsd = Math.Max(1.0, tile.GroundSamplingDistanceMeters);
        double avgCloudAltMeters = 2000.0;
        double shadowDistPixels = (avgCloudAltMeters / Math.Tan(sunElevRad)) / gsd;
        shadowDistPixels = Math.Clamp(shadowDistPixels, 4.0, 75.0);

        int dx = (int)Math.Round(Math.Sin(shadowAngleRad) * shadowDistPixels);
        int dy = (int)Math.Round(-Math.Cos(shadowAngleRad) * shadowDistPixels);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if ((mask[y, x] & QualityMaskFlags.Cloud) != 0)
                {
                    // Cast shadow ray with directional spread along the solar track
                    for (int step = -3; step <= 3; step++)
                    {
                        int sx = x + dx + (int)Math.Round(step * Math.Cos(shadowAngleRad));
                        int sy = y + dy + (int)Math.Round(step * Math.Sin(shadowAngleRad));
                        if (sx >= 0 && sx < w && sy >= 0 && sy < h)
                        {
                            // Target pixel is dark in NIR/SWIR and not thick cloud
                            if (nir[sy, sx] < ShadowNirMaxThreshold && (mask[sy, sx] & QualityMaskFlags.Cloud) == 0)
                            {
                                mask[sy, sx] |= QualityMaskFlags.CloudShadow;
                            }
                        }
                    }
                }
            }
        }

        // Step 3: Morphological Cloud Fringe Dilation (1-pixel dilation around cloud edges)
        var dilated = (QualityMaskFlags[,])mask.Clone();
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                if ((mask[y, x] & QualityMaskFlags.Cloud) != 0)
                {
                    dilated[y - 1, x] |= QualityMaskFlags.HighHaze;
                    dilated[y + 1, x] |= QualityMaskFlags.HighHaze;
                    dilated[y, x - 1] |= QualityMaskFlags.HighHaze;
                    dilated[y, x + 1] |= QualityMaskFlags.HighHaze;
                }
            }
        }

        return dilated;
    }

    /// <summary>
    /// Calculates the usable, cloud-free and shadow-free fraction of the tile (0.0 to 1.0).
    /// </summary>
    public static double CalculateUsabilityScore(QualityMaskFlags[,] mask, int w, int h)
    {
        int validCount = 0;
        int total = w * h;
        if (total == 0) return 0.0;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var flag = mask[y, x];
                if ((flag & (QualityMaskFlags.Cloud | QualityMaskFlags.CloudShadow | QualityMaskFlags.Snow | QualityMaskFlags.Saturated)) == 0)
                {
                    validCount++;
                }
            }
        }
        return (double)validCount / total;
    }
}
