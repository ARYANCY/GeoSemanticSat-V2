using System;
using System.Collections.Generic;
using System.IO;
using GeoSemanticSat.Core.Model;

namespace GeoSemanticSat.Core.Raster;

public enum VisualRenderMode
{
    TrueColorRGB,
    FalseColorInfrared,       // NIR (B8), Red (B4), Green (B3)
    SWIR_GeologicalMoisture,  // SWIR1 (B11), NIR (B8), Red (B4)
    NDVI_Heatmap,             // Vegetation biomass density
    NDWI_WaterMap,            // Water delineation
    NDBI_BuiltUpUrban,        // Concrete, built structures
    SAR_MicrowaveSimulation,  // C-band radar roughness & corner reflections
    ThermalRadiance,          // Longwave thermal radiance heat signatures
    ChangeOverlay             // Visual diff overlay
}

/// <summary>
/// Cross-Platform Satellite Imagery & Change Heatmap Raster Visualizer.
/// Generates 32-bit BGRA pixel buffers and standard BMP streams directly from multi-spectral bands
/// for rendering in Avalonia UI without external dependencies.
/// </summary>
public static class RasterVisualizer
{
    /// <summary>
    /// Renders a patch or full tile into a standard BMP MemoryStream readable by Avalonia Bitmap.
    /// </summary>
    public static MemoryStream RenderTileToBmpStream(
        SatelliteTile tile,
        int startX = 0,
        int startY = 0,
        int width = -1,
        int height = -1,
        VisualRenderMode mode = VisualRenderMode.TrueColorRGB)
    {
        if (width <= 0) width = tile.Width - startX;
        if (height <= 0) height = tile.Height - startY;

        width = Math.Clamp(width, 1, tile.Width - startX);
        height = Math.Clamp(height, 1, tile.Height - startY);

        byte[] bgra = RenderToBgraBytes(tile, startX, startY, width, height, mode);
        return CreateBmpStream(bgra, width, height);
    }

    /// <summary>
    /// Generates a composite Change Heatmap visualizing baseline T1 overlaid with color-coded detected changes.
    /// </summary>
    public static MemoryStream RenderChangeHeatmapBmpStream(
        SatelliteTile t1,
        SatelliteTile t2,
        IReadOnlyList<ChangeRecord> changes,
        int width = -1,
        int height = -1)
    {
        if (width <= 0) width = t1.Width;
        if (height <= 0) height = t1.Height;

        width = Math.Clamp(width, 1, t1.Width);
        height = Math.Clamp(height, 1, t1.Height);

        // Start with True Color background of T1
        byte[] bgra = RenderToBgraBytes(t1, 0, 0, width, height, VisualRenderMode.TrueColorRGB);

        // Apply change highlights
        foreach (var change in changes)
        {
            var (px1, py1) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MaxLat, change.Bounds.MinLon));
            var (px2, py2) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MinLat, change.Bounds.MaxLon));

            int x0 = Math.Clamp((int)Math.Min(px1, px2), 0, width - 1);
            int y0 = Math.Clamp((int)Math.Min(py1, py2), 0, height - 1);
            int x1 = Math.Clamp((int)Math.Max(px1, px2), 0, width - 1);
            int y1 = Math.Clamp((int)Math.Max(py1, py2), 0, height - 1);

            var (r, g, b) = GetColorRgbForChangeType(change.Type);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int idx = (y * width + x) * 4;
                    bool isBorder = (x == x0 || x == x1 || y == y0 || y == y1);

                    if (isBorder)
                    {
                        // Solid border outline
                        bgra[idx + 0] = b;
                        bgra[idx + 1] = g;
                        bgra[idx + 2] = r;
                        bgra[idx + 3] = 255;
                    }
                    else
                    {
                        // Semi-transparent colored tint (alpha blend 40%)
                        bgra[idx + 0] = (byte)(bgra[idx + 0] * 0.60 + b * 0.40);
                        bgra[idx + 1] = (byte)(bgra[idx + 1] * 0.60 + g * 0.40);
                        bgra[idx + 2] = (byte)(bgra[idx + 2] * 0.60 + r * 0.40);
                    }
                }
            }
        }

        return CreateBmpStream(bgra, width, height);
    }

    public static byte[] RenderToBgraBytes(
        SatelliteTile tile,
        int startX,
        int startY,
        int w,
        int h,
        VisualRenderMode mode)
    {
        byte[] bgra = new byte[w * h * 4];

        var red = tile.GetBandOrFallback(SpectralBand.Red, SpectralBand.Red);
        var green = tile.GetBandOrFallback(SpectralBand.Green, SpectralBand.Red);
        var blue = tile.GetBandOrFallback(SpectralBand.Blue, SpectralBand.Red);
        var nir = tile.GetBandOrFallback(SpectralBand.NIR, SpectralBand.Red);
        var swir = tile.GetBandOrFallback(SpectralBand.SWIR1, SpectralBand.Red);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int srcX = Math.Clamp(startX + x, 0, tile.Width - 1);
                int srcY = Math.Clamp(startY + y, 0, tile.Height - 1);
                int outIdx = (y * w + x) * 4;

                byte rByte, gByte, bByte;

                switch (mode)
                {
                    case VisualRenderMode.FalseColorInfrared:
                        // Standard NIR false color: NIR -> Red, Red -> Green, Green -> Blue
                        rByte = (byte)Math.Clamp(nir[srcY, srcX] * 255.0f, 0, 255);
                        gByte = (byte)Math.Clamp(red[srcY, srcX] * 255.0f, 0, 255);
                        bByte = (byte)Math.Clamp(green[srcY, srcX] * 255.0f, 0, 255);
                        break;

                    case VisualRenderMode.SWIR_GeologicalMoisture:
                        // SWIR-NIR-Red: Penetrates haze/smoke, highlights disturbed soil and concrete
                        rByte = (byte)Math.Clamp(swir[srcY, srcX] * 255.0f, 0, 255);
                        gByte = (byte)Math.Clamp(nir[srcY, srcX] * 255.0f, 0, 255);
                        bByte = (byte)Math.Clamp(red[srcY, srcX] * 255.0f, 0, 255);
                        break;

                    case VisualRenderMode.NDVI_Heatmap:
                        // NDVI Color scale: green for vegetation, brown for soil, blue for water
                        float ndvi = (nir[srcY, srcX] + red[srcY, srcX]) > 1e-4f
                            ? (nir[srcY, srcX] - red[srcY, srcX]) / (nir[srcY, srcX] + red[srcY, srcX])
                            : 0f;
                        if (ndvi > 0.3f)
                        {
                            rByte = (byte)(40 + (1.0f - ndvi) * 100);
                            gByte = (byte)Math.Clamp(ndvi * 255.0f, 120, 255);
                            bByte = 40;
                        }
                        else if (ndvi < 0.0f)
                        {
                            rByte = 20; gByte = 80; bByte = 220; // Water
                        }
                        else
                        {
                            rByte = 180; gByte = 140; bByte = 90; // Bare earth
                        }
                        break;

                    case VisualRenderMode.NDWI_WaterMap:
                        // Normalized Difference Water Index
                        float ndwi = (green[srcY, srcX] + nir[srcY, srcX]) > 1e-4f
                            ? (green[srcY, srcX] - nir[srcY, srcX]) / (green[srcY, srcX] + nir[srcY, srcX])
                            : 0f;
                        if (ndwi > 0.05f)
                        {
                            rByte = 14;
                            gByte = (byte)Math.Clamp(140 + ndwi * 115, 140, 255);
                            bByte = 240; // Vibrant Cyan-Blue water
                        }
                        else
                        {
                            rByte = (byte)Math.Clamp(30 + red[srcY, srcX] * 40, 0, 70);
                            gByte = (byte)Math.Clamp(30 + green[srcY, srcX] * 40, 0, 70);
                            bByte = (byte)Math.Clamp(35 + blue[srcY, srcX] * 40, 0, 80);
                        }
                        break;

                    case VisualRenderMode.NDBI_BuiltUpUrban:
                        // Normalized Difference Built-up Index (Concrete, Asphalt, Structures)
                        float ndbi = (swir[srcY, srcX] + nir[srcY, srcX]) > 1e-4f
                            ? (swir[srcY, srcX] - nir[srcY, srcX]) / (swir[srcY, srcX] + nir[srcY, srcX])
                            : 0f;
                        if (ndbi > 0.02f)
                        {
                            // Fiery orange-red highlight for concrete/structures
                            rByte = (byte)Math.Clamp(210 + ndbi * 45, 210, 255);
                            gByte = (byte)Math.Clamp(50 + (1.0f - ndbi) * 130, 20, 180);
                            bByte = 20;
                        }
                        else
                        {
                            // Cool slate background
                            rByte = 16;
                            gByte = 22;
                            bByte = 38;
                        }
                        break;

                    case VisualRenderMode.SAR_MicrowaveSimulation:
                        // Simulates Sentinel-1 C-Band radar backscatter (roughness / metallic double-bounce)
                        float waterCheck = (green[srcY, srcX] - nir[srcY, srcX]);
                        if (waterCheck > 0.12f)
                        {
                            rByte = 0; gByte = 5; bByte = 10; // Specular water is radar black
                        }
                        else
                        {
                            float roughness = (swir[srcY, srcX] * 0.65f + red[srcY, srcX] * 0.35f);
                            if (roughness > 0.32f)
                            {
                                // Built concrete/metal corner reflectors: glowing radar white/cyan
                                rByte = (byte)Math.Clamp(roughness * 255.0f, 180, 255);
                                gByte = 255;
                                bByte = 240;
                            }
                            else
                            {
                                // Ground/vegetation diffuse return (CRT phosphor green)
                                rByte = 18;
                                gByte = (byte)Math.Clamp(roughness * 210.0f, 35, 150);
                                bByte = 28;
                            }
                        }
                        break;

                    case VisualRenderMode.ThermalRadiance:
                        // Simulates Thermal Infrared (TIR) longwave surface heat emission
                        float heat = swir[srcY, srcX] * 0.70f + red[srcY, srcX] * 0.30f;
                        if (heat > 0.40f)
                        {
                            // High thermal emission (active facility, metal, engine heat)
                            rByte = 255;
                            gByte = (byte)Math.Clamp(180 + (heat - 0.4f) * 125, 180, 255);
                            bByte = (byte)Math.Clamp((heat - 0.4f) * 200, 0, 255);
                        }
                        else if (heat > 0.20f)
                        {
                            // Moderate heat
                            rByte = (byte)Math.Clamp(120 + (heat - 0.2f) * 600, 120, 255);
                            gByte = (byte)Math.Clamp((heat - 0.2f) * 400, 0, 150);
                            bByte = (byte)Math.Clamp(150 - (heat - 0.2f) * 500, 20, 150);
                        }
                        else
                        {
                            // Cold / vegetative moisture absorption
                            rByte = (byte)Math.Clamp(heat * 300, 0, 60);
                            gByte = 10;
                            bByte = (byte)Math.Clamp(30 + heat * 400, 30, 110);
                        }
                        break;

                    case VisualRenderMode.TrueColorRGB:
                    default:
                        rByte = (byte)Math.Clamp(red[srcY, srcX] * 255.0f, 0, 255);
                        gByte = (byte)Math.Clamp(green[srcY, srcX] * 255.0f, 0, 255);
                        bByte = (byte)Math.Clamp(blue[srcY, srcX] * 255.0f, 0, 255);
                        break;
                }

                bgra[outIdx + 0] = bByte;
                bgra[outIdx + 1] = gByte;
                bgra[outIdx + 2] = rByte;
                bgra[outIdx + 3] = 255; // Alpha
            }
        }

        return bgra;
    }

    /// <summary>
    /// Creates a valid uncompressed 32-bit BMP stream from BGRA byte array.
    /// Works with zero external libraries and loads natively in Avalonia Bitmap.
    /// </summary>
    public static MemoryStream CreateBmpStream(byte[] bgra, int width, int height)
    {
        var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.Default, leaveOpen: true);

        int pixelDataSize = width * height * 4;
        int fileSize = 54 + pixelDataSize;

        // BITMAPFILEHEADER (14 bytes)
        bw.Write((byte)'B');
        bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((ushort)0);
        bw.Write((ushort)0);
        bw.Write(54); // Offset to pixel data

        // BITMAPINFOHEADER (40 bytes)
        bw.Write(40); // header size
        bw.Write(width);
        bw.Write(-height); // negative for top-down bitmap
        bw.Write((ushort)1); // planes
        bw.Write((ushort)32); // 32 bits per pixel (BGRA)
        bw.Write(0); // BI_RGB (uncompressed)
        bw.Write(pixelDataSize);
        bw.Write(2835); // horizontal resolution (~72 DPI)
        bw.Write(2835); // vertical resolution
        bw.Write(0);
        bw.Write(0);

        bw.Write(bgra);
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    private static (byte R, byte G, byte B) GetColorRgbForChangeType(ChangeType type) => type switch
    {
        ChangeType.Construction => (239, 68, 68),    // Red
        ChangeType.Clearance => (245, 158, 11),      // Amber
        ChangeType.WaterExtentVariation => (14, 165, 233), // Cyan / Water
        ChangeType.RoadDevelopment => (168, 85, 247),// Purple
        ChangeType.ActivityConcentration => (249, 115, 22), // Orange
        _ => (255, 255, 255)
    };

    public record FocusedChangeInspection(
        MemoryStream T1Stream,
        MemoryStream T2Stream,
        MemoryStream OverlayStream,
        int CropX,
        int CropY,
        int CropWidth,
        int CropHeight
    );

    /// <summary>
    /// Generates high-detail focused crops (T1, T2, and Change Overlay with target reticle)
    /// for a specific detected change record.
    /// </summary>
    public static FocusedChangeInspection RenderFocusedSite(
        SatelliteTile t1,
        SatelliteTile t2,
        ChangeRecord change,
        VisualRenderMode mode = VisualRenderMode.TrueColorRGB,
        int padding = 16)
    {
        var (px1, py1) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MaxLat, change.Bounds.MinLon));
        var (px2, py2) = t1.Transform.GeoToPixel(new GeoCoordinate(change.Bounds.MinLat, change.Bounds.MaxLon));

        int minX = (int)Math.Min(px1, px2);
        int minY = (int)Math.Min(py1, py2);
        int maxX = (int)Math.Max(px1, px2);
        int maxY = (int)Math.Max(py1, py2);

        int cropX = Math.Clamp(minX - padding, 0, t1.Width - 1);
        int cropY = Math.Clamp(minY - padding, 0, t1.Height - 1);
        int cropW = Math.Clamp((maxX + padding) - cropX, 16, t1.Width - cropX);
        int cropH = Math.Clamp((maxY + padding) - cropY, 16, t1.Height - cropY);

        byte[] bgraT1 = RenderToBgraBytes(t1, cropX, cropY, cropW, cropH, mode);
        byte[] bgraT2 = RenderToBgraBytes(t2, cropX, cropY, cropW, cropH, mode);

        byte[] bgraOverlay = (byte[])bgraT2.Clone();
        var (r, g, b) = GetColorRgbForChangeType(change.Type);

        int relX0 = Math.Clamp(minX - cropX, 0, cropW - 1);
        int relY0 = Math.Clamp(minY - cropY, 0, cropH - 1);
        int relX1 = Math.Clamp(maxX - cropX, 0, cropW - 1);
        int relY1 = Math.Clamp(maxY - cropY, 0, cropH - 1);

        for (int y = relY0; y <= relY1; y++)
        {
            for (int x = relX0; x <= relX1; x++)
            {
                int idx = (y * cropW + x) * 4;
                bool isBorder = (x == relX0 || x == relX1 || y == relY0 || y == relY1);
                if (isBorder)
                {
                    bgraOverlay[idx + 0] = b;
                    bgraOverlay[idx + 1] = g;
                    bgraOverlay[idx + 2] = r;
                    bgraOverlay[idx + 3] = 255;
                }
                else
                {
                    bgraOverlay[idx + 0] = (byte)(bgraOverlay[idx + 0] * 0.40 + b * 0.60);
                    bgraOverlay[idx + 1] = (byte)(bgraOverlay[idx + 1] * 0.40 + g * 0.60);
                    bgraOverlay[idx + 2] = (byte)(bgraOverlay[idx + 2] * 0.40 + r * 0.60);
                }
            }
        }

        // Draw tactical crosshair on center
        int midX = (relX0 + relX1) / 2;
        int midY = (relY0 + relY1) / 2;
        for (int i = -4; i <= 4; i++)
        {
            int cx = Math.Clamp(midX + i, 0, cropW - 1);
            int cy = Math.Clamp(midY + i, 0, cropH - 1);

            int idxH = (midY * cropW + cx) * 4;
            bgraOverlay[idxH + 0] = 255; bgraOverlay[idxH + 1] = 255; bgraOverlay[idxH + 2] = 255;

            int idxV = (cy * cropW + midX) * 4;
            bgraOverlay[idxV + 0] = 255; bgraOverlay[idxV + 1] = 255; bgraOverlay[idxV + 2] = 255;
        }

        return new FocusedChangeInspection(
            CreateBmpStream(bgraT1, cropW, cropH),
            CreateBmpStream(bgraT2, cropW, cropH),
            CreateBmpStream(bgraOverlay, cropW, cropH),
            cropX, cropY, cropW, cropH
        );
    }
}
